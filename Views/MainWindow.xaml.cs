using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Documents;
using Replicore.Models;
using Replicore.Services;
using Wpf.Ui.Appearance;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace Replicore.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    // Fluent theme brush keys, resolved dynamically so colors follow light/dark mode
    private const string TextPrimary = "TextFillColorPrimaryBrush";
    private const string TextTertiary = "TextFillColorTertiaryBrush";
    private const string AccentText = "AccentTextFillColorPrimaryBrush";
    private const string SuccessText = "SystemFillColorSuccessBrush";
    private const string CautionText = "SystemFillColorCautionBrush";
    private const string CriticalText = "SystemFillColorCriticalBrush";

    private readonly DiskEnumerationService _diskService = new();
    private readonly CloneService _cloneService = new();
    private CancellationTokenSource? _cts;
    private int _logCount = 0;
    private readonly System.Collections.ObjectModel.ObservableCollection<CloneTaskProgress> _activeProgresses = new();
    private List<DiskInfo> _allDisks = new();

    public MainWindow()
    {
        InitializeComponent();
        // Keep Mica and light/dark theme in sync with Windows settings
        SystemThemeWatcher.Watch(this);
        // Disks will be loaded asynchronously during the terminal-style boot-up sequence
        ActiveProgressesControl.ItemsSource = _activeProgresses;

        this.Loaded += (s, e) =>
        {
            if (FindResource("BlinkCursor") is System.Windows.Media.Animation.Storyboard blinkStoryboard)
            {
                blinkStoryboard.Begin(this);
            }
        };

        MasterComboBox.SelectionChanged += (_, _) =>
        {
            UpdateTargetList();
            UpdateDetailText(MasterComboBox, MasterDetailText, MasterPartitionsControl, MasterNoPartitionsText, MasterHealthPill, MasterHealthText);
        };
        TargetsListBox.SelectionChanged += (_, _) => UpdateDetailText(TargetsListBox, TargetDetailText, TargetPartitionsControl, TargetNoPartitionsText, TargetHealthPill, TargetHealthText);
    }

    private void UpdateTargetList()
    {
        if (MasterComboBox.SelectedItem is DiskInfo selectedMaster)
        {
            // Ensure the selected master is not checked as a target
            selectedMaster.IsSelected = false;
            
            var targets = _allDisks.Where(d => d.Index != selectedMaster.Index).ToList();
            TargetsListBox.ItemsSource = targets;
            TargetsEmptyState.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            TargetsListBox.ItemsSource = _allDisks;
            TargetsEmptyState.Visibility = _allDisks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
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
    private async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        var dialog = new ContentDialog(RootContentDialogHost)
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            PrimaryButtonAppearance = destructive ? ControlAppearance.Danger : ControlAppearance.Primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task LoadDisksAsync()
    {
        try
        {
            var disks = _diskService.GetPhysicalDisks();
            _allDisks = disks;

            MasterComboBox.ItemsSource = _allDisks;

            if (_allDisks.Count > 0)
            {
                // Default master to the system disk
                MasterComboBox.SelectedItem = _allDisks.FirstOrDefault(d => d.IsSystemDisk) ?? _allDisks[0];
            }

            UpdateTargetList();

            AppendLog($"Found {_allDisks.Count} disk(s).");
            UpdateDetailText(MasterComboBox, MasterDetailText, MasterPartitionsControl, MasterNoPartitionsText, MasterHealthPill, MasterHealthText);
            UpdateDetailText(TargetsListBox, TargetDetailText, TargetPartitionsControl, TargetNoPartitionsText, TargetHealthPill, TargetHealthText);
        }
        catch (System.Management.ManagementException ex)
        {
            AppendLog($"ERROR scanning disks: {ex.Message}");
            await ShowMessageAsync("Access denied",
                "Replicore couldn't query the physical drives.\n\n" +
                "Administrator privileges are required to read physical drives, partition layouts and S.M.A.R.T. health data.\n\n" +
                "Run this terminal (or the built executable) as Administrator.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR scanning disks: {ex.Message}");
            await ShowMessageAsync("Couldn't scan disks", $"An unexpected error occurred while scanning disks:\n{ex.Message}");
        }
    }

    private void UpdateDetailText(System.Windows.Controls.Primitives.Selector selector, TextBlock detailText, ItemsControl partitionsControl, TextBlock noPartitionsText, Border healthPill, TextBlock healthText)
    {
        if (selector.SelectedItem is DiskInfo disk)
        {
            string typeLabel = disk.IsSystemDisk ? "System Disk" : "Basic Disk";
            detailText.Text = $"{disk.InterfaceType} interface   •   {typeLabel}   •   {disk.SizeFormatted}";

            if (disk.Partitions.Count > 0)
            {
                partitionsControl.ItemsSource = disk.Partitions;
                partitionsControl.Visibility = Visibility.Visible;
                noPartitionsText.Visibility = Visibility.Collapsed;
            }
            else
            {
                partitionsControl.ItemsSource = null;
                partitionsControl.Visibility = Visibility.Collapsed;
                noPartitionsText.Visibility = Visibility.Visible;
            }

            // Health status pill
            healthPill.Visibility = Visibility.Visible;
            healthText.Text = disk.IsHealthy ? "Healthy" : $"Alert: {disk.HealthStatus}";
            healthText.SetResourceReference(TextBlock.ForegroundProperty, disk.IsHealthy ? SuccessText : CriticalText);
        }
        else
        {
            detailText.Text = "No disk selected";
            partitionsControl.ItemsSource = null;
            partitionsControl.Visibility = Visibility.Collapsed;
            noPartitionsText.Visibility = Visibility.Collapsed;
            healthPill.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadDisksAsync();
    }

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DirectCloneWarningBanner == null || ProgressTitleText == null || StartBackupButton == null) return;

        bool isMirror = DirectMirrorRadio.IsChecked == true;
        DirectCloneWarningBanner.IsOpen = isMirror;

        if (isMirror)
        {
            ProgressTitleText.Text = "Cloning progress";
            StartBackupButton.Content = "Start clone";
        }
        else
        {
            ProgressTitleText.Text = "Backup progress";
            StartBackupButton.Content = "Start backup";
        }
    }

    private async void StartBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (MasterComboBox.SelectedItem is not DiskInfo master)
        {
            await ShowMessageAsync("No source disk", "Select a source disk first.");
            return;
        }

        var selectedTargets = TargetsListBox.Items.Cast<DiskInfo>().Where(d => d.IsSelected).ToList();
        if (selectedTargets.Count == 0)
        {
            await ShowMessageAsync("No target disk", "Select at least one target disk using the checkboxes.");
            return;
        }

        // --- Safety guard 1: master and target can't be the same physical disk ---
        foreach (var target in selectedTargets)
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
                bool proceed = await ConfirmAsync("Target disk is failing",
                    $"Target disk '{target.NameLine}' reports a failing S.M.A.R.T. health status ({target.HealthStatus}). " +
                    "Writing to a failing drive is risky and can lead to backup corruption or hardware failure.\n\nAre you sure you want to proceed?",
                    "Continue", destructive: true);

                if (!proceed) return;
            }
        }

        bool isDirectMirror = DirectMirrorRadio.IsChecked == true;

        if (isDirectMirror)
        {
            // --- Mirror Safety Check: Size Mismatch ---
            foreach (var target in selectedTargets)
            {
                if (target.SizeBytes < master.SizeBytes)
                {
                    await ShowMessageAsync("Target disk is too small",
                        $"The target disk '{target.NameLine}' ({target.SizeFormatted}) is smaller than the source disk ({master.SizeFormatted}).\n\n" +
                        "Direct disk mirroring requires every target disk to be the same size as the source disk or larger.");
                    return;
                }
            }

            // --- Mirror Safety Check: Permanent Wipe Confirmation ---
            string targetsListStr = string.Join("\n", selectedTargets.Select(t => $"   • {t.NameLine} ({t.SizeFormatted})"));

            bool confirmWipe = await ConfirmAsync("Erase target disks?",
                $"Direct disk mirroring will completely OVERWRITE and WIPE all data, file systems and partitions on the following target disk(s):\n\n" +
                $"{targetsListStr}\n\n" +
                $"All existing files on these drives will be permanently lost.\n\n" +
                $"Once finished, these target drives will be exact, bootable duplicates of the source.\n\n" +
                $"Are you sure you want to overwrite these drives?",
                "Erase and continue", destructive: true);

            if (!confirmWipe) return;

            bool doubleConfirm = await ConfirmAsync("Final check",
                $"You are about to wipe and overwrite the following drives:\n\n" +
                $"{targetsListStr}\n\n" +
                $"This action is permanent and CANNOT be undone.",
                "Erase drives and clone", destructive: true);

            if (!doubleConfirm) return;

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

    private async Task RunMirrorCloneAsync(DiskInfo master, List<DiskInfo> targets)
    {
        SetRunningState(true);
        ClearLog();
        AppendLog($"Starting parallel direct sector-by-sector mirror clone of {master.NameLine} to {targets.Count} target(s)...");

        _cts = new CancellationTokenSource();
        _activeProgresses.Clear();

        var tasks = targets.Select(async target =>
        {
            var progressItem = new CloneTaskProgress
            {
                TargetDiskName = target.NameLine,
                Percent = 0,
                Status = "Cleaning...",
                StatusColor = "#3B82F6" // blue
            };

            Dispatcher.Invoke(() => _activeProgresses.Add(progressItem));

            var progress = new Progress<CloneProgressEventArgs>(e =>
            {
                if (e.PercentComplete.HasValue)
                {
                    progressItem.Percent = e.PercentComplete.Value;
                    progressItem.Status = $"Cloning ({e.PercentComplete.Value}%)";
                }
                if (e.SpeedMB.HasValue)
                {
                    progressItem.Speed = $"{e.SpeedMB.Value:F1} MB/s";
                }
                if (e.EtaSeconds.HasValue)
                {
                    progressItem.Eta = FormatEta(e.EtaSeconds.Value);
                }
                if (!string.IsNullOrWhiteSpace(e.Message))
                {
                    if (e.Message.StartsWith("Copied "))
                    {
                        // Throttled details
                    }
                    else
                    {
                        AppendLog($"[Disk {target.Index}] {e.Message}");
                    }
                }
            });

            try
            {
                bool success = await _cloneService.DirectSectorCloneAsync(master.DeviceId, target.DeviceId, target.Index, master.SizeBytes, progress, _cts.Token);
                if (success)
                {
                    progressItem.Status = "Completed";
                    progressItem.StatusColor = "#10B981"; // green
                    AppendLog($"[Disk {target.Index}] Direct Mirror Clone completed successfully!");
                }
                else
                {
                    progressItem.Status = "Failed";
                    progressItem.StatusColor = "#EF4444"; // red
                    AppendLog($"[Disk {target.Index}] Direct Mirror Clone finished with errors.");
                }
            }
            catch (OperationCanceledException)
            {
                progressItem.Status = "Cancelled";
                progressItem.StatusColor = "#71717A"; // gray
                AppendLog($"[Disk {target.Index}] Direct Mirror Clone cancelled by user.");
            }
            catch (Exception ex)
            {
                progressItem.Status = "Error";
                progressItem.StatusColor = "#EF4444"; // red
                AppendLog($"[Disk {target.Index}] ERROR: {ex.Message}");
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);

            bool anyFailed = _activeProgresses.Any(p => p.Status == "Failed" || p.Status == "Error");
            bool allCompleted = _activeProgresses.All(p => p.Status == "Completed");

            if (allCompleted)
            {
                AppendLog("All parallel cloning operations completed successfully!");
                ExecutePostBackupAction();
            }
            else if (anyFailed)
            {
                AppendLog("One or more cloning operations finished with errors.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR running parallel tasks: {ex.Message}");
        }
        finally
        {
            SetRunningState(false);
        }
    }

    private async Task RunBackupAsync(DiskInfo master, List<DiskInfo> targets)
    {
        SetRunningState(true);
        ClearLog();
        AppendLog($"Starting parallel backup of {master.NameLine} to {targets.Count} target(s)...");

        _cts = new CancellationTokenSource();
        _activeProgresses.Clear();

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
        if (TargetsListBox.SelectedItem is not DiskInfo target ||
            string.IsNullOrWhiteSpace(target.DriveLetters))
        {
            await ShowMessageAsync("Select a target disk", "Select a target disk in the list to view its backup history. It must have a mounted drive letter.");
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
        var selectedTargets = TargetsListBox.Items.Cast<DiskInfo>().Where(d => d.IsSelected).ToList();
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

        ProgressCard.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        RefreshButton.IsEnabled = !running;
        MasterComboBox.IsEnabled = !running;
        TargetsListBox.IsEnabled = !running;
        PostBackupActionComboBox.IsEnabled = !running;
        MainTabControl.IsEnabled = !running;
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

    private void AppendTerminalText(string text, string? brushKey = null)
    {
        CreateTerminalProgressRun(text, brushKey);
    }

    private Run CreateTerminalProgressRun(string initialText, string? brushKey = null)
    {
        var run = new Run(initialText);
        if (brushKey != null)
        {
            run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
        }
        TerminalOutputText.Inlines.InsertBefore(CursorContainer, run);
        TerminalScrollViewer.ScrollToEnd();
        return run;
    }

    private async void StartScanButton_Click(object sender, RoutedEventArgs e)
    {
        StartScanButton.IsEnabled = false;

        // 1. Fade out the centered button panel
        var fadeButton = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(0.25));
        StartButtonCenteringGrid.BeginAnimation(OpacityProperty, fadeButton);
        await Task.Delay(250);
        StartButtonCenteringGrid.Visibility = Visibility.Collapsed;

        // 2. Make ConsoleBorder visible and animate its expansion (Scale Transform)
        ConsoleBorder.Visibility = Visibility.Visible;
        var scaleXAnim = new DoubleAnimation(0.1, 1.0, TimeSpan.FromSeconds(0.4))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleYAnim = new DoubleAnimation(0.05, 1.0, TimeSpan.FromSeconds(0.4))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var opacityAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(0.3));

        ConsoleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        ConsoleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        ConsoleBorder.BeginAnimation(OpacityProperty, opacityAnim);
        await Task.Delay(400);

        // 3. Simulated Windows CLI Update & Scan sequence
        // Theme brush keys so the console reads correctly in light and dark mode
        const string darkBrush = TextPrimary;
        const string blueBrush = AccentText;
        const string greenBrush = SuccessText;
        const string yellowBrush = CautionText;

        // Windows CMD Header
        AppendTerminalText("Microsoft Windows [Version 10.0.22631.3527]\n", darkBrush);
        AppendTerminalText("(c) Microsoft Corporation. All rights reserved.\n\n", darkBrush);
        await Task.Delay(400);

        // Typing out Windows command
        AppendTerminalText("C:\\Users\\Administrator> ", darkBrush);
        string cmd = "replicore.exe --scan --verbose\n";
        foreach (char c in cmd)
        {
            AppendTerminalText(c.ToString(), darkBrush);
            await Task.Delay(25);
        }
        await Task.Delay(200);

        // Windows scanning logs
        AppendTerminalText("[INFO]  Loading Replicore Kernel Driver (replicore.sys)... ", darkBrush);
        await Task.Delay(300);
        AppendTerminalText("SUCCESS\n", greenBrush);

        AppendTerminalText("[INFO]  Checking Administrator elevation tokens... ", darkBrush);
        await Task.Delay(200);
        AppendTerminalText("ELEVATED\n", blueBrush);

        AppendTerminalText("[INFO]  Connecting to Windows Volume Shadow Copy Service (VSS)... ", darkBrush);
        await Task.Delay(350);
        AppendTerminalText("CONNECTED\n", greenBrush);

        AppendTerminalText("[INFO]  Registering VSS writers and checking providers... ", darkBrush);
        await Task.Delay(250);
        AppendTerminalText("Done\n", greenBrush);

        AppendTerminalText("[INFO]  Querying WMI (Windows Management Instrumentation) storage namespaces...\n", darkBrush);
        await Task.Delay(300);

        // Windows-style progress bar using blocks
        var progressRun = CreateTerminalProgressRun("Scanning Storage Topology: [                    ] 0%", blueBrush);
        for (int p = 0; p <= 100; p += 5)
        {
            int blocks = p / 5; // Max 20 blocks
            string bar = new string('█', blocks) + new string('░', 20 - blocks);
            progressRun.Text = $"Scanning Storage Topology: [{bar}] {p}%";
            await Task.Delay(35);
        }
        AppendTerminalText("\n", darkBrush);
        await Task.Delay(300);

        AppendTerminalText("[INFO]  Resolving physical disk partition maps & mount points...\n", darkBrush);
        await Task.Delay(200);

        // Run hardware enumeration asynchronously
        List<DiskInfo> disks = new();
        try
        {
            disks = await Task.Run(() => _diskService.GetPhysicalDisks());
        }
        catch (Exception ex)
        {
            AppendTerminalText($"[ERROR] WMI Storage Enumeration failed: {ex.Message}\n", CriticalText);
            AppendTerminalText("[FATAL] Initialization halted.\n", CriticalText);
            StartScanButton.IsEnabled = true;
            StartButtonCenteringGrid.Visibility = Visibility.Visible;
            StartButtonCenteringGrid.Opacity = 1.0;
            ConsoleBorder.Visibility = Visibility.Collapsed;
            return;
        }

        _allDisks = disks;

        // Print physical disk layouts in Windows style
        foreach (var disk in _allDisks)
        {
            AppendTerminalText($"[  OK  ] Found PhysicalDrive{disk.Index} — {disk.Model}\n", greenBrush);
            AppendTerminalText($"         Size: {disk.SizeFormatted} | Mounts: {(string.IsNullOrEmpty(disk.DriveLetters) ? "None" : disk.DriveLetters)} | S.M.A.R.T: ", darkBrush);
            if (disk.IsHealthy)
            {
                AppendTerminalText("HEALTHY [OK]\n", greenBrush);
            }
            else
            {
                AppendTerminalText($"ALERT [{disk.HealthStatus}]\n", yellowBrush);
            }
            await Task.Delay(300);
        }

        AppendTerminalText("\nAll systems operational. Starting Replicore Dashboard GUI...\n", blueBrush);
        await Task.Delay(800);

        // Bind data and load UI states
        MasterComboBox.ItemsSource = _allDisks;
        if (_allDisks.Count > 0)
        {
            MasterComboBox.SelectedItem = _allDisks.FirstOrDefault(d => d.IsSystemDisk) ?? _allDisks[0];
        }
        UpdateTargetList();

        AppendLog($"Found {_allDisks.Count} disk(s).");
        UpdateDetailText(MasterComboBox, MasterDetailText, MasterPartitionsControl, MasterNoPartitionsText, MasterHealthPill, MasterHealthText);
        UpdateDetailText(TargetsListBox, TargetDetailText, TargetPartitionsControl, TargetNoPartitionsText, TargetHealthPill, TargetHealthText);

        // Fade out transition
        DashboardGrid.Visibility = Visibility.Visible;
        if (FindResource("TransitionToDashboard") is System.Windows.Media.Animation.Storyboard transition)
        {
            transition.Completed += (s, ev) =>
            {
                TerminalStartGrid.Visibility = Visibility.Collapsed;
            };
            transition.Begin(this);
        }
        else
        {
            TerminalStartGrid.Visibility = Visibility.Collapsed;
        }
    }
}