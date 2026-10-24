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
        try
        {
            foreach (var target in targets)
            {
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
            await ShowMessageAsync("Clone not started", ex.Message);
        }
        catch (OperationCanceledException)
        {
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
        }
        finally
        {
            foreach (var (disk, vhd) in images)
                await Task.Run(() => FinishImage(disk, vhd, jobs.Any(j => j.Disk == disk && j.Target.Succeeded)));
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

            try
            {
                bool success = await _cloneService.BackupAsync(sourceVolume, targetPath, progress, _cts.Token);
                if (success)
                {
                    progressItem.Status = "Completed";
                    progressItem.StatusColor = "#10B981"; // green
                    AppendLog($"[Disk {target.Index}] Backup completed successfully.");
                }
                else
                {
                    progressItem.Status = "Failed";
                    progressItem.StatusColor = "#EF4444"; // red
                    AppendLog($"[Disk {target.Index}] Backup finished with errors.");
                }
            }
            catch (OperationCanceledException)
            {
                progressItem.Status = "Cancelled";
                progressItem.StatusColor = "#71717A";
                AppendLog($"[Disk {target.Index}] Backup cancelled by user.");
            }
            catch (Exception ex)
            {
                progressItem.Status = "Error";
                progressItem.StatusColor = "#EF4444";
                AppendLog($"[Disk {target.Index}] ERROR: {ex.Message}");
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

    private async void LoadHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var target = GetSelectedTargets().FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.DriveLetters));
        if (target == null)
        {
            await ShowMessageAsync("Select a target disk",
                "On the Clone page, select a target disk that has a mounted drive letter, then scan it for backups here.");
            return;
        }

        string path = target.DriveLetters.Split(',')[0].Trim();
        AppendLog($"Scanning backup history on {path}...");

        try
        {
            string versionsText = await _cloneService.GetBackupVersionsAsync(path);
            var versions = _cloneService.ParseBackupVersions(versionsText);
            HistoryListView.ItemsSource = versions;

            AppendLog($"Scan completed. Found {versions.Count} backup version(s).");
            if (versions.Count == 0 && !string.IsNullOrWhiteSpace(versionsText))
            {
                AppendLog(versionsText);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
        }
    }

    private async void SaveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedTargets = GetSelectedTargets();
        var validTargets = selectedTargets.Where(t => !string.IsNullOrWhiteSpace(t.DriveLetters)).ToList();

        if (MasterComboBox.SelectedItem is not DiskInfo master || string.IsNullOrWhiteSpace(master.DriveLetters) ||
            validTargets.Count == 0)
        {
            await ShowMessageAsync("Select disks first",
                "To schedule a backup, select a source and at least one target drive that has a mounted volume (drive letter).");
            return;
        }

        string day = (ScheduleDayComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Sunday";
        string time = ScheduleTimeTextBox.Text.Trim();

        // Validate 24h format (HH:mm)
        if (!System.Text.RegularExpressions.Regex.IsMatch(time, @"^(0[0-9]|1[0-9]|2[0-3]):[0-5][0-9]$"))
        {
            await ShowMessageAsync("Invalid time", "Enter a valid time in 24-hour format, for example 02:00 or 23:30.");
            return;
        }

        string sourceDrive = master.DriveLetters.Split(',')[0].Trim();
        string dayCode = day.Substring(0, 3).ToUpper(); // SUN, MON, etc.

        bool allSuccess = true;
        List<string> errorMessages = new();

        foreach (var target in validTargets)
        {
            string targetDrive = target.DriveLetters.Split(',')[0].Trim();
            string taskName = $"Replicore_Backup_Disk{target.Index}";
            string arguments = $"/create /tn \"{taskName}\" /tr \"wbadmin start backup -backupTarget:{targetDrive} -include:{sourceDrive} -allCritical -quiet\" /sc weekly /d {dayCode} /st {time} /f /ru SYSTEM";

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();

                if (proc?.ExitCode == 0)
                {
                    AppendLog($"Successfully registered weekly backup task '{taskName}': {day}s at {time} to {targetDrive}");
                }
                else
                {
                    string err = proc?.StandardError.ReadToEnd() ?? "Unknown error";
                    AppendLog($"Failed to register task '{taskName}': {err}");
                    errorMessages.Add($"Target {targetDrive}: {err}");
                    allSuccess = false;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"ERROR scheduling task '{taskName}': {ex.Message}");
                errorMessages.Add($"Target {targetDrive}: {ex.Message}");
                allSuccess = false;
            }
        }

        if (allSuccess)
        {
            await ShowMessageAsync("Backup scheduled", $"Weekly backup scheduled for {validTargets.Count} drive(s): {day}s at {time}.");
        }
        else
        {
            string errors = string.Join("\n", errorMessages);
            await ShowMessageAsync("Some tasks weren't registered", $"Some scheduled backup tasks failed to register:\n{errors}");
        }
    }

    private async void DeleteScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Try to delete the old task format if it exists
            RunDeleteTask("Replicore_Backup");

            // Also delete potential disk-specific tasks
            for (int i = 0; i <= 24; i++)
            {
                RunDeleteTask($"Replicore_Backup_Disk{i}", silent: true);
            }

            AppendLog("Deleted scheduled backup task(s).");
            await ShowMessageAsync("Scheduled tasks removed", "Scheduled backup task(s) were removed.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR deleting task(s): {ex.Message}");
            await ShowMessageAsync("Couldn't remove tasks", $"Error: {ex.Message}");
        }
    }

    private void RunDeleteTask(string taskName, bool silent = false)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/delete /tn \"{taskName}\" /f",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit();
            
            if (proc?.ExitCode != 0 && !silent)
            {
                string err = proc?.StandardError.ReadToEnd() ?? "";
                if (!err.Contains("ERROR: The system cannot find the file specified."))
                {
                    AppendLog($"Failed to delete task '{taskName}': {err}");
                }
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                AppendLog($"ERROR deleting task '{taskName}': {ex.Message}");
            }
        }
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
