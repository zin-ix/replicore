using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Documents;
using Replicore.Models;
using Replicore.Services;
using Wpf.Ui.Appearance;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace Replicore.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    // Fluent theme brush keys, resolved dynamically so colors follow light/dark mode
    private const string TextPrimary = "TextFillColorPrimaryBrush";
    private const string TextTertiary = "TextFillColorTertiaryBrush";
    private const string AccentText = "AccentTextFillColorPrimaryBrush";
    private const string SuccessText = "SystemFillColorSuccessBrush";
    private const string CriticalText = "SystemFillColorCriticalBrush";
    private const string SuccessBackground = "SystemFillColorSuccessBackgroundBrush";
    private const string CriticalBackground = "SystemFillColorCriticalBackgroundBrush";
    private const string CautionText = "SystemFillColorCautionBrush";
    private const string CautionBackground = "SystemFillColorCautionBackgroundBrush";

    // Free space at the end of a larger target worth pointing out in the preview
    private const long MinimumShownGap = 16L * 1024 * 1024;

    private readonly DiskEnumerationService _diskService = new();
    private readonly CloneService _cloneService = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private int _logCount = 0;
    private readonly System.Collections.ObjectModel.ObservableCollection<CloneTaskProgress> _activeProgresses = new();
    private List<DiskInfo> _allDisks = new();
    // .vhdx files added as clone targets; kept across rescans. Negative indexes never match a real disk.
    private readonly List<DiskInfo> _imageTargets = new();
    private int _nextImageIndex = -1;

    /// <param name="initialScan">Disk scan already started by the splash screen; null to scan on open.</param>
    public MainWindow(Task<List<DiskInfo>>? initialScan = null)
    {
        InitializeComponent();
        // Keep Mica and light/dark theme in sync with Windows settings
        SystemThemeWatcher.Watch(this);

        // The remove button on image file cards lives in a shared template, so its click is handled here
        TargetsItemsControl.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(TargetCard_Click));

        MasterComboBox.SelectionChanged += (_, _) =>
        {
            UpdateTargetList();
            UpdateSourceDetails();
            UpdateJobSummary();
        };

        // Show the splash screen's scan result, or scan now if there isn't one
        Loaded += async (_, _) => await ScanDisksAsync(initialScan);
    }

    private void UpdateTargetList()
    {
        IEnumerable<DiskInfo> targets = _allDisks;
        if (MasterComboBox.SelectedItem is DiskInfo selectedMaster)
        {
            // Ensure the selected master is not checked as a target
            selectedMaster.IsSelected = false;
            targets = targets.Where(d => d.Index != selectedMaster.Index);
        }

        // Image files are clone targets only; a Windows image backup needs a real drive
        if (DirectMirrorRadio.IsChecked == true)
            targets = targets.Concat(_imageTargets);
        else
            foreach (var image in _imageTargets) image.IsSelected = false;

        var list = targets.ToList();
        TargetsItemsControl.ItemsSource = list;
        TargetsEmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"2 disks", "1 image file" or "1 disk and 1 image file".</summary>
    private static string DescribeTargets(List<DiskInfo> targets)
    {
        int images = targets.Count(t => t.IsImageFile);
        int disks = targets.Count - images;
        var parts = new List<string>();
        if (disks > 0) parts.Add($"{disks} disk{(disks == 1 ? "" : "s")}");
        if (images > 0) parts.Add($"{images} image file{(images == 1 ? "" : "s")}");
        return string.Join(" and ", parts);
    }

    private List<DiskInfo> GetSelectedTargets() =>
        TargetsItemsControl.Items.Cast<DiskInfo>().Where(d => d.IsSelected).ToList();

    private void Disk_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiskInfo.IsSelected)) UpdateJobSummary();
    }

    /// <summary>Refreshes the "N of M selected" label and the command bar summary.</summary>
    private void UpdateJobSummary()
    {
        var targets = GetSelectedTargets();
        int total = TargetsItemsControl.Items.Count;
        TargetCountText.Text = total == 0 ? "" : $"{targets.Count} of {total} selected";

        if (_isRunning) return;

        SelectAllButton.IsEnabled = total > 0;
        ClearSelectionButton.IsEnabled = targets.Count > 0;

        bool isMirror = DirectMirrorRadio.IsChecked == true;
        if (MasterComboBox.SelectedItem is not DiskInfo master)
        {
            JobSummaryTitle.Text = "Select a source disk";
            JobSummaryDetail.Text = "";
        }
        else if (targets.Count == 0)
        {
            JobSummaryTitle.Text = "Select one or more target disks";
            JobSummaryDetail.Text = isMirror
                ? "Each target becomes an exact, bootable copy of the source."
                : "A Windows system image of the source is saved on each target.";
        }
        else
        {
            string verb = isMirror ? "Clone" : "Back up";
            JobSummaryTitle.Text = $"{verb} Disk {master.Index} to {DescribeTargets(targets)}";
            JobSummaryDetail.Text = string.Join("   •   ", targets.Select(t =>
                t.IsImageFile ? $"Image  {t.Model}" : $"Disk {t.Index}  {t.Model} ({t.SizeFormatted})"));
        }

        StartBackupButton.IsEnabled = MasterComboBox.SelectedItem is DiskInfo && targets.Count > 0;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires once during InitializeComponent, before the pages exist
        if (ClonePage == null) return;
        ShowPage((NavList.SelectedItem as ListBoxItem)?.Tag as string ?? "Clone");
    }

    private void ShowPage(string page)
    {
        ClonePage.Visibility = page == "Clone" ? Visibility.Visible : Visibility.Collapsed;
        SchedulePage.Visibility = page == "Schedule" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = page == "Activity" ? Visibility.Visible : Visibility.Collapsed;

        // Scheduled runs happen outside the app, so both lists are reloaded each time they're shown
        if (page == "Schedule") _ = LoadSchedulesAsync();
        if (page == "History") _ = LoadRunsAsync();
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var disk in TargetsItemsControl.Items.Cast<DiskInfo>()) disk.IsSelected = true;
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var disk in TargetsItemsControl.Items.Cast<DiskInfo>()) disk.IsSelected = false;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog(RootContentDialogHost)
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Asks a yes/no question. Cancel is always the default button, so pressing Enter
    /// never confirms a destructive action by accident.
    /// </summary>
    /// <param name="content">The message text, or a custom element such as the partition preview.</param>
    private async Task<bool> ConfirmAsync(string title, object content, string confirmText, bool destructive = false)
    {
        var dialog = new ContentDialog(RootContentDialogHost)
        {
            Title = title,
            Content = content is string message ? new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } : content,
            PrimaryButtonText = confirmText,
            PrimaryButtonAppearance = destructive ? ControlAppearance.Danger : ControlAppearance.Primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (content is not string) dialog.DialogMaxWidth = 640;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Body of the erase confirmation: every target's partitions now and after the clone,
    /// so it's obvious what is about to be replaced.
    /// </summary>
    private static UIElement BuildClonePreview(DiskInfo master, List<DiskInfo> targets, string intro, string outro)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap });

        foreach (var target in targets)
        {
            panel.Children.Add(new TextBlock
            {
                Text = target.IsImageFile ? target.NameLine : $"{target.NameLine} ({target.SizeFormatted})",
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 16, 0, 8)
            });

            if (target.IsImageFile)
                panel.Children.Add(PreviewRow("File", new TextBlock { Text = target.ImagePath, TextWrapping = TextWrapping.Wrap, FontSize = 12 }));
            else
                panel.Children.Add(PreviewRow("Now", new Views.PartitionMapBar { Segments = target.Layout }));
            panel.Children.Add(PreviewRow("After", new Views.PartitionMapBar { Segments = LayoutAfterClone(master, target) }));
        }

        panel.Children.Add(new TextBlock { Text = outro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) });
        return new ScrollViewer { Content = panel, MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static Grid PreviewRow(string label, FrameworkElement content)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var caption = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(caption);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        return row;
    }

    /// <summary>The source's partitions, plus the unallocated space left at the end of a larger target.</summary>
    private static List<DiskSegment> LayoutAfterClone(DiskInfo master, DiskInfo target)
    {
        var layout = master.Layout.ToList();
        long extra = target.IsImageFile ? 0 : target.LayoutSizeBytes - master.LayoutSizeBytes;
        if (extra >= MinimumShownGap)
        {
            layout.Add(new DiskSegment
            {
                Offset = master.LayoutSizeBytes, Size = extra, Kind = SegmentKind.Unallocated, Title = "Free",
                Description = $"Unallocated\n{DiskCloneEngine.FormatGB(extra)}. Extend a partition in Disk Management to use it."
            });
        }
        return layout;
    }

    /// <summary>
    /// Enumerates physical disks while showing the scanning overlay. Used on startup,
    /// by the Rescan button and by "Try again" after a failed scan.
    /// </summary>
    /// <param name="pendingScan">A scan that is already running (from the splash screen) to use instead of starting one.</param>
    private async Task ScanDisksAsync(Task<List<DiskInfo>>? pendingScan = null)
    {
        ShowPage("");
        NavList.IsEnabled = false;
        ScanOverlay.Visibility = Visibility.Visible;
        ScanningPanel.Visibility = Visibility.Visible;
        ScanErrorPanel.Visibility = Visibility.Collapsed;

        List<DiskInfo> disks;
        try
        {
            disks = await (pendingScan ?? Task.Run(() => _diskService.GetPhysicalDisks()));
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR scanning disks: {ex.Message}");
            ScanErrorBar.Message = ex is System.Management.ManagementException
                ? "Administrator privileges are required to read physical drives, partition layouts and S.M.A.R.T. health data. Run Replicore as Administrator."
                : ex.Message;
            ScanningPanel.Visibility = Visibility.Collapsed;
            ScanErrorPanel.Visibility = Visibility.Visible;
            return;
        }

        foreach (var old in _allDisks) old.PropertyChanged -= Disk_PropertyChanged;
        _allDisks = disks;
        foreach (var disk in _allDisks) disk.PropertyChanged += Disk_PropertyChanged;

        MasterComboBox.ItemsSource = _allDisks;
        if (_allDisks.Count > 0)
        {
            // Default master to the system disk
            MasterComboBox.SelectedItem = _allDisks.FirstOrDefault(d => d.IsSystemDisk) ?? _allDisks[0];
        }

        // The schedule only backs up disks that have mounted volumes
        var schedulable = _allDisks.Where(d => !string.IsNullOrWhiteSpace(d.DriveLetters)).ToList();
        ScheduleSourceComboBox.ItemsSource = schedulable;
        ScheduleSourceComboBox.SelectedItem = schedulable.FirstOrDefault(d => d.IsSystemDisk) ?? schedulable.FirstOrDefault();

        UpdateTargetList();
        UpdateSourceDetails();
        UpdateJobSummary();
        AppendLog($"Found {_allDisks.Count} disk(s).");

        ScanOverlay.Visibility = Visibility.Collapsed;
        NavList.IsEnabled = true;
        ShowPage((NavList.SelectedItem as ListBoxItem)?.Tag as string ?? "Clone");
    }

    private void UpdateSourceDetails()
    {
        if (MasterComboBox.SelectedItem is DiskInfo disk)
        {
            MasterModelText.Text = disk.Model;
            MasterDetailText.Text = $"Disk {disk.Index}   •   {disk.SizeFormatted}   •   {disk.ConnectionLabel}" +
                                    (disk.IsSystemDisk ? "   •   Windows disk" : "");

            MasterLayoutBar.Segments = disk.Layout;
            MasterPartitionsControl.ItemsSource = disk.Partitions;
            MasterNoPartitionsText.Visibility = disk.Partitions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Health status pill, readings and any problems
            var health = disk.Health;
            var (textBrush, backgroundBrush) = HealthBrushes(health.Level);
            MasterHealthPill.Visibility = Visibility.Visible;
            MasterHealthPill.ToolTip = health.ToolTipText;
            MasterHealthText.Text = health.Label;
            MasterHealthText.SetResourceReference(TextBlock.ForegroundProperty, textBrush);
            MasterHealthPill.SetResourceReference(Border.BackgroundProperty, backgroundBrush);

            MasterHealthDetailsControl.ItemsSource = health.Details;
            MasterNoHealthText.Visibility = health.Details.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MasterHealthBar.IsOpen = health.Issues.Count > 0;
            if (health.Issues.Count > 0)
            {
                bool critical = health.Level == HealthLevel.Critical;
                MasterHealthBar.Severity = critical ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
                MasterHealthBar.Title = critical ? "This disk may fail soon" : "This disk reported a problem";
                MasterHealthBar.Message = string.Join("\n", health.Issues) + "\nBack it up or clone it to a healthy disk soon.";
            }
        }
        else
        {
            MasterModelText.Text = "No disk selected";
            MasterDetailText.Text = "";
            MasterLayoutBar.Segments = null;
            MasterPartitionsControl.ItemsSource = null;
            MasterNoPartitionsText.Visibility = Visibility.Collapsed;
            MasterHealthPill.Visibility = Visibility.Collapsed;
            MasterHealthDetailsControl.ItemsSource = null;
            MasterNoHealthText.Visibility = Visibility.Collapsed;
            MasterHealthBar.IsOpen = false;
        }
    }

    private static (string Text, string Background) HealthBrushes(HealthLevel level) => level switch
    {
        HealthLevel.Healthy => (SuccessText, SuccessBackground),
        HealthLevel.Warning => (CautionText, CautionBackground),
        HealthLevel.Critical => (CriticalText, CriticalBackground),
        _ => ("TextFillColorSecondaryBrush", "SubtleFillColorSecondaryBrush")
    };

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ScanDisksAsync();
    }

    private async void RetryScanButton_Click(object sender, RoutedEventArgs e)
    {
        await ScanDisksAsync();
    }

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent for the default-checked radio, before other controls exist
        if (DirectCloneWarningBanner == null || StartBackupButton == null || JobSummaryTitle == null ||
            CloneOptionsPanel == null || AddImageButton == null || TargetsEmptyState == null) return;

        bool isMirror = DirectMirrorRadio.IsChecked == true;
        DirectCloneWarningBanner.IsOpen = isMirror;
        CloneOptionsPanel.Visibility = isMirror ? Visibility.Visible : Visibility.Collapsed;
        AddImageButton.Visibility = isMirror ? Visibility.Visible : Visibility.Collapsed;
        StartBackupButton.Content = isMirror ? "Start clone" : "Start backup";
        UpdateTargetList();
        UpdateJobSummary();
    }

    private void AddImageButton_Click(object sender, RoutedEventArgs e)
    {
        int sourceIndex = (MasterComboBox.SelectedItem as DiskInfo)?.Index ?? 0;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the disk image as",
            Filter = "Virtual hard disk (*.vhdx)|*.vhdx",
            DefaultExt = ".vhdx",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"Replicore Disk {sourceIndex} {DateTime.Now:yyyy-MM-dd}.vhdx"
        };
        if (dialog.ShowDialog(this) != true) return;

        var existing = _imageTargets.FirstOrDefault(i => i.ImagePath.Equals(dialog.FileName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.IsSelected = true;
            return;
        }

        var image = new DiskInfo
        {
            IsImageFile = true,
            ImagePath = dialog.FileName,
            Model = Path.GetFileName(dialog.FileName),
            Index = _nextImageIndex--
        };
        image.PropertyChanged += Disk_PropertyChanged;
        _imageTargets.Add(image);
        UpdateTargetList();
        image.IsSelected = true;
    }

    private void TargetCard_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { Tag: "RemoveImage", DataContext: DiskInfo image }) return;
        e.Handled = true;
        image.PropertyChanged -= Disk_PropertyChanged;
        _imageTargets.Remove(image);
        UpdateTargetList();
        UpdateJobSummary();
    }

    private async void StartBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (MasterComboBox.SelectedItem is not DiskInfo master)
        {
            await ShowMessageAsync("No source disk", "Select a source disk first.");
            return;
        }

        var selectedTargets = GetSelectedTargets();
        if (selectedTargets.Count == 0)
        {
            await ShowMessageAsync("No target disk", "Select at least one target disk.");
            return;
        }

        var diskTargets = selectedTargets.Where(t => !t.IsImageFile).ToList();
        var imageTargets = selectedTargets.Where(t => t.IsImageFile).ToList();

        // --- Safety guard 1: master and target can't be the same physical disk ---
        foreach (var target in diskTargets)
        {
            if (master.Index == target.Index)
            {
                await ShowMessageAsync("Same disk selected", $"The source and target can't be the same disk (Disk {target.Index}).");
                return;
            }

            // --- Safety guard 2: warn before writing to a disk that holds Windows itself ---
            if (target.IsSystemDisk)
            {
                bool proceed = await ConfirmAsync("Target is your Windows disk",
                    $"'{target.NameLine}' appears to be the disk Windows is currently running from. " +
                    "Using it as a target is highly unusual and may fail or corrupt your running system.\n\nContinue anyway?",
                    "Continue", destructive: true);

                if (!proceed) return;
            }

            // --- Safety guard 3: warning if target health is failing ---
            if (!target.IsHealthy)
            {
                bool proceed = await ConfirmAsync(target.Health.Level == HealthLevel.Critical ? "Target disk is failing" : "Target disk reported a problem",
                    $"Target disk '{target.NameLine}' reports:\n\n{FormatIssues(target.Health)}\n\n" +
                    "Writing to a failing drive is risky and can lead to backup corruption or hardware failure.\n\nAre you sure you want to proceed?",
                    "Continue", destructive: true);

                if (!proceed) return;
            }
        }

        bool isDirectMirror = DirectMirrorRadio.IsChecked == true;

        if (isDirectMirror)
        {
            // --- Source health: worth cloning now, but an unreadable sector stops the clone ---
            if (!master.IsHealthy)
            {
                bool proceed = await ConfirmAsync("Source disk reported a problem",
                    $"'{master.NameLine}' reports:\n\n{FormatIssues(master.Health)}\n\n" +
                    "Copying its data to a healthy disk now is a good idea. If a sector can't be read, though, the clone stops " +
                    "with an error, so keep the computer otherwise idle while it runs.\n\nStart the clone?",
                    "Continue");

                if (!proceed) return;
            }

            // --- Mirror Safety Check: Size Mismatch ---
            foreach (var target in diskTargets)
            {
                if (target.SizeBytes < master.SizeBytes)
                {
                    await ShowMessageAsync("Target disk is too small",
                        $"The target disk '{target.NameLine}' ({target.SizeFormatted}) is smaller than the source disk ({master.SizeFormatted}).\n\n" +
                        "Disk cloning requires every target disk to be the same size as the source disk or larger.");
                    return;
                }
            }

            if (!await CheckImageTargetsAsync(master, diskTargets, imageTargets)) return;

            string snapshotNote = master.IsSystemDisk
                ? "Windows is running from the source disk. Replicore will take a Volume Shadow Copy snapshot so the copy is consistent.\n\n"
                : "";

            if (diskTargets.Count > 0)
            {
                // --- Mirror Safety Check: Permanent Wipe Confirmation, with each target's partitions now and after ---
                bool confirmWipe = await ConfirmAsync("Erase target disks?",
                    BuildClonePreview(master, selectedTargets,
                        "Disk cloning will completely OVERWRITE and WIPE all data, file systems and partitions on the target disk(s). " +
                        "All existing files on them will be permanently lost.",
                        "Once finished, the target drives will be exact, bootable duplicates of the source.\n\n" + snapshotNote +
                        "Are you sure you want to overwrite these drives?"),
                    "Erase and continue", destructive: true);

                if (!confirmWipe) return;

                string targetsListStr = string.Join("\n", diskTargets.Select(t => $"   • {t.NameLine} ({t.SizeFormatted})"));
                bool doubleConfirm = await ConfirmAsync("Final check",
                    $"You are about to wipe and overwrite the following drives:\n\n" +
                    $"{targetsListStr}\n\n" +
                    $"This action is permanent and CANNOT be undone.",
                    "Erase drives and clone", destructive: true);

                if (!doubleConfirm) return;
            }
            else
            {
                bool confirmImage = await ConfirmAsync("Create image file?",
                    BuildClonePreview(master, selectedTargets,
                        $"Replicore will copy {master.NameLine} into a VHDX image file. No disks are erased.",
                        snapshotNote + "Start creating the image?"),
                    "Create image");

                if (!confirmImage) return;
            }

            await RunMirrorCloneAsync(master, selectedTargets);
        }
        else
        {
            // --- Backup Safety Check: Backup Confirmation ---
            string targetsListStr = string.Join("\n", selectedTargets.Select(t => $"   • {t.NameLine}"));
            bool confirmBackup = await ConfirmAsync("Start backup?",
                $"This will create a backup image of:\n\n   {master.NameLine}\n\nto the following target drive(s):\n\n{targetsListStr}\n\n" +
                "Existing data on the target locations is not erased.",
                "Start backup");

            if (!confirmBackup) return;

            await RunBackupAsync(master, selectedTargets);
        }
    }

    private static string FormatIssues(DiskHealth health) =>
        string.Join("\n", health.Issues.Select(i => $"   • {i}"));

    /// <summary>Checks each image file's destination drive before anything is written.</summary>
    private async Task<bool> CheckImageTargetsAsync(DiskInfo master, List<DiskInfo> diskTargets, List<DiskInfo> imageTargets)
    {
        // Smart copy writes the used space of lettered volumes; other partitions are copied in full
        long estimate = SmartCopyCheckBox.IsChecked == true
            ? master.Partitions.Sum(p => p.SizeBytes - p.FreeSpaceBytes) +
              master.Layout.Where(s => s.Kind != SegmentKind.Unallocated && s.DriveLetter.Length == 0).Sum(s => s.Size)
            : master.LayoutSizeBytes;

        foreach (var image in imageTargets)
        {
            string root = Path.GetPathRoot(image.ImagePath) ?? "";
            string letter = root.TrimEnd('\\');

            var erased = diskTargets.FirstOrDefault(d => d.Partitions.Any(p => p.DriveLetter.Equals(letter, StringComparison.OrdinalIgnoreCase)));
            if (erased != null)
            {
                await ShowMessageAsync("Image is on a target disk",
                    $"'{image.Model}' would be saved on {letter}, which is on {erased.NameLine} — a disk this clone erases.\n\n" +
                    "Choose a folder on another drive.");
                return false;
            }

            DriveInfo drive;
            long free;
            try
            {
                drive = new DriveInfo(root);
                free = drive.AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Can't save the image there", $"'{image.ImagePath}' isn't on a local drive that's available: {ex.Message}");
                return false;
            }

            if (drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && estimate >= 4L * 1024 * 1024 * 1024)
            {
                await ShowMessageAsync("Drive can't hold the image",
                    $"{letter} is formatted as FAT32, which can't store files larger than 4 GB. Save the image on an NTFS or exFAT drive.");
                return false;
            }

            if (free < estimate)
            {
                bool proceed = await ConfirmAsync("Not enough free space?",
                    $"The image of {master.NameLine} may need up to about {DiskCloneEngine.FormatGB(estimate)}, but {letter} has " +
                    $"{DiskCloneEngine.FormatGB(free)} free. If it runs out of space, the clone fails and the partial image is deleted.\n\n" +
                    "Continue anyway?",
                    "Continue");
                if (!proceed) return false;
            }
        }
        return true;
    }

    private async Task RunMirrorCloneAsync(DiskInfo master, List<DiskInfo> targets)
    {
        SetRunningState(true);
        ClearLog();
        bool verify = VerifyCheckBox.IsChecked == true;
        bool smartCopy = SmartCopyCheckBox.IsChecked == true;
        AppendLog($"Starting clone of {master.NameLine} to {DescribeTargets(targets)}" +
                  $"{(smartCopy ? ", copying used space only" : "")}{(verify ? ", with verification" : "")}...");

        _cts = new CancellationTokenSource();
        _activeProgresses.Clear();
        foreach (var disk in _allDisks.Concat(_imageTargets)) disk.Progress = null;

        var jobs = new List<(DiskInfo Disk, CloneTaskProgress Item, CloneTarget Target)>();
        var images = new List<(DiskInfo Disk, VirtualDisk Vhd)>();
        // One history row per target; failures that happen before a target's job exists are noted here
        var runIds = new Dictionary<DiskInfo, long?>();
        var runErrors = new Dictionary<DiskInfo, string>();
        bool cancelled = false;
        try
        {
            foreach (var target in targets)
            {
                runIds[target] = TryStartRun("Clone", master.NameLine, target.IsImageFile ? target.ImagePath : target.NameLine);

                var progressItem = new CloneTaskProgress
                {
                    TargetDiskName = target.NameLine,
                    Percent = 0,
                    Status = "Preparing...",
                    StatusColor = "#3B82F6" // blue
                };
                _activeProgresses.Add(progressItem);
                target.Progress = progressItem;

                string label = target.LogLabel;
                var progress = new Progress<CloneProgressEventArgs>(e =>
                {
                    switch (e.Phase)
                    {
                        case "Erasing":
                            progressItem.Status = "Erasing...";
                            break;
                        case "Cloning":
                        case "Verifying":
                            if (e.PercentComplete.HasValue)
                            {
                                progressItem.Percent = e.PercentComplete.Value;
                                progressItem.Status = $"{e.Phase} ({e.PercentComplete.Value}%)";
                            }
                            if (e.SpeedMB.HasValue) progressItem.Speed = $"{e.SpeedMB.Value:F1} MB/s";
                            if (e.EtaSeconds.HasValue) progressItem.Eta = FormatEta(e.EtaSeconds.Value);
                            break;
                        case "Finalizing":
                            progressItem.Status = "Finalizing...";
                            break;
                        case "Failed":
                            progressItem.Status = "Failed";
                            progressItem.StatusColor = "#EF4444"; // red
                            break;
                    }
                    if (!string.IsNullOrWhiteSpace(e.Message))
                    {
                        AppendLog($"[{label}] {e.Message}");
                    }
                });

                if (!target.IsImageFile)
                {
                    jobs.Add((target, progressItem, new CloneTarget(target.Index, target.DeviceId, progress)));
                    continue;
                }

                // An image file becomes a blank disk: create the .vhdx and attach it without drive letters
                try
                {
                    progressItem.Status = "Creating image...";
                    AppendLog($"[{label}] Creating {target.ImagePath}...");
                    var vhd = await Task.Run(() =>
                    {
                        // The save dialog already asked before replacing an existing file
                        if (File.Exists(target.ImagePath)) File.Delete(target.ImagePath);
                        return VirtualDisk.CreateFor(master.DeviceId, target.ImagePath);
                    });
                    images.Add((target, vhd));
                    jobs.Add((target, progressItem, new CloneTarget(vhd.DiskIndex, vhd.PhysicalPath, progress, isBlank: true)));
                }
                catch (Exception ex)
                {
                    progressItem.Status = "Failed";
                    progressItem.StatusColor = "#EF4444"; // red
                    AppendLog($"[{label}] ERROR: Couldn't create the image file. {ex.Message}");
                    runErrors[target] = $"Couldn't create the image file. {ex.Message}";
                }
            }

            if (jobs.Count > 0)
            {
                var engine = new DiskCloneEngine(_cloneService);
                var sourceLetters = master.Partitions.Select(p => p.DriveLetter).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                await engine.CloneAsync(master.Index, master.DeviceId, sourceLetters, jobs.Select(j => j.Target).ToList(),
                    verify, smartCopy, new Progress<string>(AppendLog), _cts.Token);
            }

            foreach (var (disk, item, target) in jobs)
            {
                if (target.Succeeded)
                {
                    item.Percent = 100;
                    item.Status = target.Verified ? "Verified" : "Completed";
                    item.StatusColor = "#10B981"; // green
                    item.Eta = "done";
                    AppendLog($"[{disk.LogLabel}] Clone {(target.Verified ? "completed and verified" : "completed")} successfully.");
                }
                else
                {
                    item.Status = "Failed";
                    item.StatusColor = "#EF4444"; // red
                }
            }

            if (jobs.Count == targets.Count && jobs.All(j => j.Target.Succeeded))
            {
                AppendLog("All clones completed successfully!");
                ExecutePostBackupAction();
            }
            else
            {
                AppendLog("One or more clones failed. See the messages above for each disk.");
            }
        }
        catch (CloneBlockedException ex)
        {
            // Raised before any target was touched
            foreach (var (_, item, _) in jobs)
            {
                item.Status = "Not started";
                item.StatusColor = "#71717A"; // gray
            }
            AppendLog($"ERROR: Clone not started. {ex.Message}");
            foreach (var target in targets) runErrors.TryAdd(target, $"Not started. {ex.Message}");
            await ShowMessageAsync("Clone not started", ex.Message);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            foreach (var (disk, item, target) in jobs.Where(j => !j.Target.Succeeded && j.Target.Error == null))
            {
                item.Status = "Cancelled";
                item.StatusColor = "#71717A"; // gray
                AppendLog(disk.IsImageFile
                    ? $"[{disk.LogLabel}] Clone cancelled."
                    : $"[{disk.LogLabel}] Clone cancelled. The disk was left without a partition table.");
            }
        }
        catch (Exception ex)
        {
            foreach (var (_, item, target) in jobs.Where(j => !j.Target.Succeeded))
            {
                item.Status = "Error";
                item.StatusColor = "#EF4444"; // red
            }
            AppendLog($"ERROR: {ex.Message}");
            foreach (var target in targets) runErrors.TryAdd(target, ex.Message);
        }
        finally
        {
            foreach (var (disk, vhd) in images)
                await Task.Run(() => FinishImage(disk, vhd, jobs.Any(j => j.Disk == disk && j.Target.Succeeded)));

            foreach (var (disk, runId) in runIds)
            {
                var job = jobs.FirstOrDefault(j => j.Disk == disk);
                if (job.Target?.Succeeded == true)
                    TryFinishRun(runId, RunStatus.Succeeded, job.Target.Verified ? "Cloned and verified." : "Cloned.");
                else if (cancelled && job.Target?.Error == null)
                    TryFinishRun(runId, RunStatus.Cancelled, "Cancelled.");
                else
                    TryFinishRun(runId, RunStatus.Failed,
                        job.Target?.Error ?? runErrors.GetValueOrDefault(disk) ?? "Clone failed. See the Activity page.");
            }
            SetRunningState(false);
        }
    }

    /// <summary>Detaches a finished image; an image that didn't complete is deleted rather than left half-written.</summary>
    private void FinishImage(DiskInfo image, VirtualDisk vhd, bool succeeded)
    {
        void Log(string message) => Dispatcher.Invoke(() => AppendLog($"[{image.LogLabel}] {message}"));

        try { vhd.Dispose(); }
        catch (Exception ex) { Log($"WARNING: Couldn't detach the image: {ex.Message}"); }

        if (succeeded)
        {
            long size = new FileInfo(image.ImagePath).Length;
            Log($"Image saved to {image.ImagePath} ({DiskCloneEngine.FormatGB(size)} on disk).");
            return;
        }
        try
        {
            File.Delete(image.ImagePath);
            Log("The incomplete image file was deleted.");
        }
        catch (Exception ex)
        {
            Log($"WARNING: Couldn't delete the incomplete image file: {ex.Message}");
        }
    }

    private async Task RunBackupAsync(DiskInfo master, List<DiskInfo> targets)
    {
        SetRunningState(true);
        ClearLog();
        AppendLog($"Starting parallel backup of {master.NameLine} to {targets.Count} target(s)...");

        _cts = new CancellationTokenSource();
        _activeProgresses.Clear();
        foreach (var disk in _allDisks) disk.Progress = null;

        string sourceVolume = !string.IsNullOrWhiteSpace(master.DriveLetters)
            ? master.DriveLetters.Split(',')[0].Trim()
            : throw new InvalidOperationException("Master disk has no mounted volume to back up.");

        var tasks = targets.Select(async target =>
        {
            string targetPath = !string.IsNullOrWhiteSpace(target.DriveLetters)
                ? target.DriveLetters.Split(',')[0].Trim()
                : "";

            var progressItem = new CloneTaskProgress
            {
                TargetDiskName = target.NameLine,
                Percent = 0,
                Status = "Preparing...",
                StatusColor = "#3B82F6"
            };

            Dispatcher.Invoke(() => _activeProgresses.Add(progressItem));
            target.Progress = progressItem;

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                progressItem.Status = "No Drive Letter";
                progressItem.StatusColor = "#EF4444";
                AppendLog($"[Disk {target.Index}] ERROR: Target disk has no mounted volume to write to.");
                return;
            }

            var progress = new Progress<CloneProgressEventArgs>(e =>
            {
                if (e.PercentComplete.HasValue)
                {
                    progressItem.Percent = e.PercentComplete.Value;
                    progressItem.Status = $"Backing up ({e.PercentComplete.Value}%)";
                }
                if (!string.IsNullOrWhiteSpace(e.Message))
                {
                    AppendLog($"[Disk {target.Index}] {e.Message}");
                }
            });

            long? runId = TryStartRun("Image backup", $"{sourceVolume} on {master.NameLine}", targetPath);
            try
            {
                bool success = await _cloneService.BackupAsync(sourceVolume, targetPath, progress, _cts.Token);
                if (success)
                {
                    progressItem.Status = "Completed";
                    progressItem.StatusColor = "#10B981"; // green
                    AppendLog($"[Disk {target.Index}] Backup completed successfully.");
                    TryFinishRun(runId, RunStatus.Succeeded, $"Image of {sourceVolume} saved to {targetPath}.");
                }
                else
                {
                    progressItem.Status = "Failed";
                    progressItem.StatusColor = "#EF4444"; // red
                    AppendLog($"[Disk {target.Index}] Backup finished with errors.");
                    TryFinishRun(runId, RunStatus.Failed, "wbadmin reported errors. See the Activity page.");
                }
            }
            catch (OperationCanceledException)
            {
                progressItem.Status = "Cancelled";
                progressItem.StatusColor = "#71717A";
                AppendLog($"[Disk {target.Index}] Backup cancelled by user.");
                TryFinishRun(runId, RunStatus.Cancelled, "Cancelled.");
            }
            catch (Exception ex)
            {
                progressItem.Status = "Error";
                progressItem.StatusColor = "#EF4444";
                AppendLog($"[Disk {target.Index}] ERROR: {ex.Message}");
                TryFinishRun(runId, RunStatus.Failed, ex.Message);
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);

            bool anyFailed = _activeProgresses.Any(p => p.Status == "Failed" || p.Status == "Error" || p.Status == "No Drive Letter");
            bool allCompleted = _activeProgresses.All(p => p.Status == "Completed");

            if (allCompleted)
            {
                AppendLog("All parallel backups completed successfully!");
                ExecutePostBackupAction();
            }
            else if (anyFailed)
            {
                AppendLog("One or more backup operations finished with errors.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR running parallel backups: {ex.Message}");
        }
        finally
        {
            SetRunningState(false);
        }
    }

    private string FormatEta(double seconds)
    {
        if (seconds <= 0 || double.IsInfinity(seconds) || double.IsNaN(seconds)) return "estimating...";
        var time = TimeSpan.FromSeconds(seconds);
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}h {time.Minutes}m remaining";
        if (time.TotalMinutes >= 1)
            return $"{time.Minutes}m {time.Seconds}s remaining";
        return $"{time.Seconds}s remaining";
    }

    private void CancelBackupButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        AppendLog("Cancellation requested. Halting all operations...");
        CancelBackupButton.IsEnabled = false;
    }

    // ---------------- Backup history ----------------

    /// <summary>Opened on first use, so a database problem only affects history and schedules, not cloning.</summary>
    private HistoryDatabase Db => _db ??= new HistoryDatabase();
    private HistoryDatabase? _db;
    private readonly TaskSchedulerService _taskScheduler = new();

    /// <summary>Records a manual run in the history; null if the database can't be written.</summary>
    private long? TryStartRun(string kind, string source, string destination)
    {
        try { return Db.StartRun(null, kind, source, destination); }
        catch (Exception ex)
        {
            AppendLog($"WARNING: Couldn't save this run to backup history: {ex.Message}");
            return null;
        }
    }

    private void TryFinishRun(long? runId, RunStatus status, string details)
    {
        if (runId is not long id) return;
        try { Db.FinishRun(id, status, details); }
        catch (Exception ex) { AppendLog($"WARNING: Couldn't update backup history: {ex.Message}"); }
    }

    private async Task LoadRunsAsync()
    {
        try
        {
            var runs = await Task.Run(() => Db.GetRuns());
            RunsListView.ItemsSource = runs;
            RunsEmptyText.Visibility = runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            RunsEmptyText.Text = $"Couldn't read backup history: {ex.Message}";
            RunsEmptyText.Visibility = Visibility.Visible;
        }
    }

    private async void RefreshHistoryButton_Click(object sender, RoutedEventArgs e) => await LoadRunsAsync();

    private void OpenRunLogButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path || !File.Exists(path)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void LoadHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        // Default to the first target selected on the Clone page
        if (string.IsNullOrWhiteSpace(HistoryScanPathTextBox.Text))
        {
            var target = GetSelectedTargets().FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.DriveLetters));
            if (target != null) HistoryScanPathTextBox.Text = target.DriveLetters.Split(',')[0].Trim();
        }

        string? path = NormalizeBackupDestination(HistoryScanPathTextBox.Text);
        if (path == null)
        {
            await ShowMessageAsync("Enter a drive", "Enter the drive or network share that holds the backups, such as E: or \\\\server\\backups.");
            return;
        }

        AppendLog($"Scanning image versions on {path}...");
        try
        {
            string versionsText = await _cloneService.GetBackupVersionsAsync(path);
            var versions = _cloneService.ParseBackupVersions(versionsText);
            HistoryListView.ItemsSource = versions;
            HistoryListView.Visibility = Visibility.Visible;

            AppendLog($"Scan completed. Found {versions.Count} backup version(s).");
            if (versions.Count == 0)
            {
                await ShowMessageAsync("No image versions found",
                    $"wbadmin found no Windows image backups on {path}. Folder backups aren't images, so they only appear in the list above.");
                if (!string.IsNullOrWhiteSpace(versionsText)) AppendLog(versionsText);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
        }
    }

    // ---------------- Schedules ----------------

    /// <summary>One row in the Scheduled backups list.</summary>
    public sealed class ScheduleListItem
    {
        public required BackupSchedule Schedule { get; init; }
        public required string Title { get; init; }
        public required string When { get; init; }
        public required string Status { get; init; }
        public required Brush StatusBrush { get; init; }
    }

    private ScheduleKind SelectedScheduleKind => (ScheduleKind)Math.Max(0, ScheduleTypeComboBox.SelectedIndex);

    private async Task LoadSchedulesAsync()
    {
        try
        {
            var rows = await Task.Run(() =>
            {
                var lastRuns = Db.GetLastRunPerSchedule();
                return Db.GetSchedules()
                    .Select(s => (Schedule: s, Task: _taskScheduler.GetStatus(s), LastRun: lastRuns.GetValueOrDefault(s.Id)))
                    .ToList();
            });

            SchedulesItemsControl.ItemsSource = rows.Select(r => DescribeSchedule(r.Schedule, r.Task, r.LastRun)).ToList();
            SchedulesEmptyText.Text = "No scheduled backups yet. Add one above.";
            SchedulesEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            SchedulesItemsControl.ItemsSource = null;
            SchedulesEmptyText.Text = $"Couldn't load scheduled backups: {ex.Message}";
            SchedulesEmptyText.Visibility = Visibility.Visible;
        }
    }

    private ScheduleListItem DescribeSchedule(BackupSchedule schedule, ScheduledTaskStatus task, BackupRun? lastRun)
    {
        var parts = new List<string>();
        bool problem = false;

        if (!task.Exists)
        {
            parts.Add("Missing from Task Scheduler, so it won't run. Delete it and add it again.");
            problem = true;
        }
        else if (task.IsRunning) parts.Add("Running now");
        else if (task.IsDisabled)
        {
            parts.Add("Disabled in Task Scheduler");
            problem = true;
        }
        else if (task.NextRun is DateTime next) parts.Add($"Next run {next:ddd yyyy-MM-dd HH:mm}");

        if (lastRun == null) parts.Add("Hasn't run yet");
        else
        {
            parts.Add($"Last run {lastRun.StartedText}: {lastRun.StatusText}" +
                      (lastRun.Status == RunStatus.Failed && lastRun.Details.Length > 0 ? $" — {lastRun.Details}" : ""));
            problem |= lastRun.Status == RunStatus.Failed;
        }

        return new ScheduleListItem
        {
            Schedule = schedule,
            Title = $"{schedule.KindLabel}: {schedule.SourceSummary}",
            When = schedule.WhenSummary,
            Status = string.Join("   •   ", parts),
            StatusBrush = (Brush)FindResource(problem ? CriticalText : TextTertiary)
        };
    }

    private async void RefreshSchedulesButton_Click(object sender, RoutedEventArgs e) => await LoadSchedulesAsync();

    private async void RunScheduleNowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BackupSchedule schedule) return;
        try
        {
            _taskScheduler.RunNow(schedule);
            AppendLog($"Started scheduled backup {schedule.Id} ({schedule.KindLabel}) through Task Scheduler.");
            await ShowMessageAsync("Backup started",
                "The backup is running in the background under the SYSTEM account, just as it would on schedule. " +
                "It appears in Backup history when it finishes, even if you close Replicore.");
            await LoadSchedulesAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Couldn't start the backup", ex.Message);
        }
    }

    private async void DeleteOneScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BackupSchedule schedule) return;
        bool confirm = await ConfirmAsync("Delete this schedule?",
            $"{schedule.KindLabel}: {schedule.SourceSummary}\n{schedule.WhenSummary}\n\nIts past runs stay in Backup history.",
            "Delete");
        if (!confirm) return;

        try
        {
            _taskScheduler.Delete(schedule);
            Db.DeleteSchedule(schedule.Id);
            AppendLog($"Deleted scheduled backup {schedule.Id}.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Couldn't delete the schedule", ex.Message);
        }
        await LoadSchedulesAsync();
    }

    private void ScheduleSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // One checkbox per mounted volume on the source, for the Selected volumes type
        ScheduleVolumesPanel.Children.Clear();
        var volumes = (ScheduleSourceComboBox.SelectedItem as DiskInfo)?.Partitions
            .Where(p => !string.IsNullOrWhiteSpace(p.DriveLetter)).ToList() ?? new();

        foreach (var volume in volumes)
        {
            ScheduleVolumesPanel.Children.Add(new CheckBox
            {
                Content = $"{volume.DriveLetter}  {volume.DisplayLabel} ({volume.UsedSpaceFormatted} used)",
                Tag = volume.DriveLetter,
                IsChecked = true,
                Margin = new Thickness(0, 0, 20, 4)
            });
        }
        ScheduleNoVolumesText.Visibility = volumes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires once during InitializeComponent, before the rows below it exist
        if (ScheduleDestinationCaption == null) return;

        var kind = SelectedScheduleKind;
        bool folders = kind == ScheduleKind.Folders;
        ScheduleCustomPanel.Visibility = kind == ScheduleKind.Volumes ? Visibility.Visible : Visibility.Collapsed;
        ScheduleFoldersPanel.Visibility = folders ? Visibility.Visible : Visibility.Collapsed;
        ScheduleSourceSection.Visibility = folders ? Visibility.Collapsed : Visibility.Visible;

        ScheduleTypeCaption.Text = kind switch
        {
            ScheduleKind.FullImage => "Every volume on the source disk plus the boot files, so Windows can be restored from it.",
            ScheduleKind.Volumes => "An image of only the volumes you select.",
            _ => "Copies the folders you add. Each run copies new and changed files; files deleted from the source stay in the backup."
        };
        ScheduleDestinationCaption.Text = folders
            ? @"A folder on another drive, such as E:\Backups, or a network share. Each folder is copied into back_up_<folder name>, for example E:\Backups\back_up_Documents."
            : @"A drive such as E: or a network share such as \\server\backups. Windows saves the image in a WindowsImageBackup folder at the root of a drive.";
        ScheduleDestinationTextBox.PlaceholderText = folders ? @"E:\Backups" : @"E: or \\server\share";
    }

    private void ScheduleAddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose folders to back up", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;

        foreach (string folder in dialog.FolderNames)
        {
            string path = Path.TrimEndingDirectorySeparator(folder);
            if (Path.GetPathRoot(path) == path) path = folder; // keep "C:\" rather than "C:"
            if (!ScheduleFoldersListBox.Items.Cast<string>().Any(f => f.Equals(path, StringComparison.OrdinalIgnoreCase)))
                ScheduleFoldersListBox.Items.Add(path);
        }
        UpdateScheduleFolderState();
    }

    private void ScheduleRemoveFolderButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ScheduleFoldersListBox.SelectedItems.Cast<string>().ToList())
            ScheduleFoldersListBox.Items.Remove(item);
        UpdateScheduleFolderState();
    }

    private void ScheduleFoldersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateScheduleFolderState();

    private void UpdateScheduleFolderState()
    {
        ScheduleNoFoldersText.Visibility = ScheduleFoldersListBox.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleRemoveFolderButton.IsEnabled = ScheduleFoldersListBox.SelectedItems.Count > 0;
    }

    private async void ScheduleBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the backup destination" };
        if (dialog.ShowDialog(this) != true) return;

        string path = dialog.FolderName;
        if (path.StartsWith(@"\\") || SelectedScheduleKind == ScheduleKind.Folders)
        {
            ScheduleDestinationTextBox.Text = path.StartsWith(@"\\") ? path.TrimEnd('\\') : path;
            return;
        }

        // wbadmin only writes images to the root of a local drive, so a subfolder can't be used
        string root = Path.GetPathRoot(path)!.TrimEnd('\\');
        ScheduleDestinationTextBox.Text = root;
        if (!string.Equals(path.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync("Drive root used",
                $"Windows Backup can't save an image into a folder on a local drive, so the backup will be saved to {root}\\WindowsImageBackup.");
        }
    }

    /// <summary>Turns "e", "E:" or "E:\" into "E:" and trims a network share; null if it isn't a valid image destination.</summary>
    private static string? NormalizeBackupDestination(string input)
    {
        string value = input.Trim().Trim('"');
        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z]:?\\?$"))
            return char.ToUpperInvariant(value[0]) + ":";
        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^\\\\[^\\/:*?""<>|]+\\[^/:*?""<>|]+$"))
            return value.TrimEnd('\\');
        return null;
    }

    /// <summary>Full path of a folder destination on a drive or network share; null if it isn't one.</summary>
    private static string? NormalizeFolderDestination(string input)
    {
        string value = input.Trim().Trim('"');
        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z]:$")) value += "\\";
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^([A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)") ||
            value.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0)
            return null;
        return Path.GetFullPath(value);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
    private static bool IsSameOrInside(string path, string folder)
    {
        string a = Path.TrimEndingDirectorySeparator(path) + "\\";
        string b = Path.TrimEndingDirectorySeparator(folder) + "\\";
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    private async void SaveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        var day = (DayOfWeek)Math.Max(0, ScheduleDayComboBox.SelectedIndex); // Items run Sunday to Saturday, like DayOfWeek
        string time = ScheduleTimeTextBox.Text.Trim();

        // Validate 24h format (HH:mm)
        if (!System.Text.RegularExpressions.Regex.IsMatch(time, @"^(0[0-9]|1[0-9]|2[0-3]):[0-5][0-9]$"))
        {
            await ShowMessageAsync("Invalid time", "Enter a valid time in 24-hour format, for example 02:00 or 23:30.");
            return;
        }

        var schedule = new BackupSchedule { Kind = SelectedScheduleKind, Day = day, Time = time };

        if (schedule.Kind == ScheduleKind.Folders)
        {
            var folders = ScheduleFoldersListBox.Items.Cast<string>().ToList();
            if (folders.Count == 0)
            {
                await ShowMessageAsync("Add folders", "Add at least one folder to back up.");
                return;
            }

            var missing = folders.Where(f => !Directory.Exists(f)).ToList();
            if (missing.Count > 0)
            {
                await ShowMessageAsync("Folder not found", $"These folders don't exist:\n{string.Join("\n", missing)}");
                return;
            }

            string? destination = NormalizeFolderDestination(ScheduleDestinationTextBox.Text);
            if (destination == null)
            {
                await ShowMessageAsync("Invalid destination",
                    "Enter a folder such as E:\\Backups or a network share such as \\\\server\\backups.");
                return;
            }
            if (!destination.StartsWith(@"\\") && !Directory.Exists(Path.GetPathRoot(destination)))
            {
                await ShowMessageAsync("Destination not found",
                    $"Drive {Path.GetPathRoot(destination)} isn't connected. Connect it and try again.");
                return;
            }

            // A destination inside a source folder would copy the backup into itself on every run
            string? overlap = folders.FirstOrDefault(f => IsSameOrInside(destination, f));
            if (overlap != null)
            {
                await ShowMessageAsync("Destination is inside a source folder",
                    $"{destination} is inside {overlap}, which is being backed up. Choose a destination outside it.");
                return;
            }

            schedule.Folders = folders;
            schedule.Destination = destination;
        }
        else
        {
            if (ScheduleSourceComboBox.SelectedItem is not DiskInfo source)
            {
                await ShowMessageAsync("Choose a source disk", "Choose the disk to back up. Only disks with a drive letter can be scheduled.");
                return;
            }

            bool custom = schedule.Kind == ScheduleKind.Volumes;
            var volumes = custom
                ? ScheduleVolumesPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList()
                : source.Partitions.Select(p => p.DriveLetter).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            if (volumes.Count == 0)
            {
                await ShowMessageAsync("Select volumes", "Select at least one volume to back up.");
                return;
            }

            string? destination = NormalizeBackupDestination(ScheduleDestinationTextBox.Text);
            if (destination == null)
            {
                await ShowMessageAsync("Invalid destination",
                    "Enter a drive such as E: or a network share such as \\\\server\\backups. Windows Backup can't save an image into a folder on a local drive.");
                return;
            }

            if (!destination.StartsWith(@"\\"))
            {
                if (!Directory.Exists(destination + "\\"))
                {
                    await ShowMessageAsync("Destination not found", $"Drive {destination} isn't connected. Connect it and try again.");
                    return;
                }
                if (volumes.Any(v => v.Equals(destination, StringComparison.OrdinalIgnoreCase)))
                {
                    await ShowMessageAsync("Destination is being backed up",
                        $"{destination} is one of the volumes being backed up. Choose a destination on another disk.");
                    return;
                }
            }

            schedule.SourceLabel = source.NameLine;
            schedule.Volumes = volumes;
            schedule.Bootable = !custom || ScheduleBootableCheckBox.IsChecked == true;
            schedule.Destination = destination;
        }

        try
        {
            Db.AddSchedule(schedule);
            try
            {
                _taskScheduler.Register(schedule);
            }
            catch
            {
                // Don't keep a schedule that Task Scheduler will never run
                Db.DeleteSchedule(schedule.Id);
                throw;
            }

            // Tasks from earlier versions of Replicore would otherwise run alongside the new ones
            _taskScheduler.DeleteLegacyTasks();

            AppendLog($"Added scheduled backup {schedule.Id}: {schedule.KindLabel}, {schedule.SourceSummary}. {schedule.WhenSummary}.");
            await LoadSchedulesAsync();
            await ShowMessageAsync("Backup scheduled", $"{schedule.KindLabel}: {schedule.SourceSummary}\n{schedule.WhenSummary}.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR adding scheduled backup: {ex.Message}");
            await ShowMessageAsync("Schedule wasn't added", $"Error: {ex.Message}");
        }
    }

    private async void DeleteScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        bool confirm = await ConfirmAsync("Remove all scheduled backups?",
            "Every Replicore task is removed from Task Scheduler. Backup history is kept.", "Remove all");
        if (!confirm) return;

        try
        {
            foreach (var schedule in Db.GetSchedules())
            {
                _taskScheduler.Delete(schedule);
                Db.DeleteSchedule(schedule.Id);
            }
            _taskScheduler.DeleteLegacyTasks();

            AppendLog("Removed all scheduled backups.");
            await ShowMessageAsync("Scheduled backups removed", "All scheduled backups were removed.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR removing scheduled backups: {ex.Message}");
            await ShowMessageAsync("Couldn't remove scheduled backups", $"Error: {ex.Message}");
        }
        await LoadSchedulesAsync();
    }

    [System.Runtime.InteropServices.DllImport("Powrprof.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, ExactSpelling = true)]
    public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    private void ExecutePostBackupAction()
    {
        Dispatcher.Invoke(() =>
        {
            var selectedAction = (PostBackupActionComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (selectedAction == "Shut down PC")
            {
                AppendLog("Shutting down PC in 60 seconds (post-backup action)...");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/s /t 60 /c \"Replicore: Backup complete. Shutting down.\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                System.Diagnostics.Process.Start(psi);
            }
            else if (selectedAction == "Put PC to sleep")
            {
                AppendLog("Putting PC to sleep (post-backup action)...");
                SetSuspendState(false, false, false);
            }
        });
    }

    private void SetRunningState(bool running)
    {
        StartBackupButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        CancelBackupButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelBackupButton.IsEnabled = running;

        _isRunning = running;

        RefreshButton.IsEnabled = !running;
        MasterComboBox.IsEnabled = !running;
        ImageBackupRadio.IsEnabled = !running;
        DirectMirrorRadio.IsEnabled = !running;
        SelectAllButton.IsEnabled = !running;
        ClearSelectionButton.IsEnabled = !running;
        PostBackupActionComboBox.IsEnabled = !running;
        VerifyCheckBox.IsEnabled = !running;
        SmartCopyCheckBox.IsEnabled = !running;
        AddImageButton.IsEnabled = !running;
        SaveScheduleButton.IsEnabled = !running;
        DeleteScheduleButton.IsEnabled = !running;
        LoadHistoryButton.IsEnabled = !running;
        // Lock target selection, but keep the cards fully visible so their live progress stays readable
        TargetsItemsControl.IsHitTestVisible = !running;

        if (running)
        {
            string targets = DescribeTargets(GetSelectedTargets());
            JobSummaryTitle.Text = DirectMirrorRadio.IsChecked == true
                ? $"Cloning to {targets}..."
                : $"Backing up to {targets}...";
            JobSummaryDetail.Text = "Progress is shown on each target. Keep all drives connected until it finishes.";
        }
        else
        {
            UpdateJobSummary();
        }
    }

    private void AppendLog(string message)
    {
        _logCount++;

        var row = new Border
        {
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent
        };
        if (_logCount % 2 == 0)
        {
            row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        }

        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12
        };

        var timeRun = new Run($"{DateTime.Now:HH:mm:ss}  ");
        timeRun.SetResourceReference(TextElement.ForegroundProperty, TextTertiary);
        textBlock.Inlines.Add(timeRun);

        // Parse content for visual styling in log
        string msgBrushKey = TextPrimary;
        if (message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            msgBrushKey = CriticalText;
        }
        else if (message.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            msgBrushKey = SuccessText;
        }
        else if (message.Contains("Starting", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("Scanning", StringComparison.OrdinalIgnoreCase))
        {
            msgBrushKey = AccentText;
        }

        var messageRun = new Run(message);
        messageRun.SetResourceReference(TextElement.ForegroundProperty, msgBrushKey);
        textBlock.Inlines.Add(messageRun);

        row.Child = textBlock;
        LogPanel.Children.Add(row);
        LogCountText.Text = $"{_logCount} entries";
        LogScrollViewer.ScrollToEnd();
    }

    private void ClearLog()
    {
        LogPanel.Children.Clear();
        _logCount = 0;
        LogCountText.Text = "";
    }

}
