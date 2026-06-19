using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Replicore.Models;
using Replicore.Services;

namespace Replicore.Views;

public partial class MainWindow : Window
{
    private readonly DiskEnumerationService _diskService = new();
    private readonly CloneService _cloneService = new();
    private CancellationTokenSource? _cts;
    private int _logCount = 0;
    private readonly System.Collections.ObjectModel.ObservableCollection<CloneTaskProgress> _activeProgresses = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadDisks();

        ActiveProgressesControl.ItemsSource = _activeProgresses;

        MasterComboBox.SelectionChanged += (_, _) => UpdateDetailText(MasterComboBox, MasterDetailText, MasterPartitionsControl, MasterNoPartitionsText, MasterHealthPill, MasterHealthText);
        TargetsListBox.SelectionChanged += (_, _) => UpdateDetailText(TargetsListBox, TargetDetailText, TargetPartitionsControl, TargetNoPartitionsText, TargetHealthPill, TargetHealthText);
    }

    private void LoadDisks()
    {
        try
        {
            var disks = _diskService.GetPhysicalDisks();

            MasterComboBox.ItemsSource = disks;
            TargetsListBox.ItemsSource = disks;

            if (disks.Count > 0)
            {
                // Default master to the system disk
                MasterComboBox.SelectedItem = disks.FirstOrDefault(d => d.IsSystemDisk) ?? disks[0];
            }

            AppendLog($"Found {disks.Count} disk(s).");
            UpdateDetailText(MasterComboBox, MasterDetailText, MasterPartitionsControl, MasterNoPartitionsText, MasterHealthPill, MasterHealthText);
            UpdateDetailText(TargetsListBox, TargetDetailText, TargetPartitionsControl, TargetNoPartitionsText, TargetHealthPill, TargetHealthText);
        }
        catch (System.Management.ManagementException ex)
        {
            AppendLog($"ERROR scanning disks: {ex.Message}");
            MessageBox.Show(
                "Access Denied: Failed to query physical drives.\n\n" +
                "This application requires Administrator privileges to enumerate physical drives, partition layouts, and S.M.A.R.T. health data.\n\n" +
                "Please run this terminal (or the built executable) as Administrator.",
                "Access Denied — Replicore",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR scanning disks: {ex.Message}");
            MessageBox.Show($"An unexpected error occurred while scanning disks:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            healthText.Foreground = disk.IsHealthy 
                ? (Brush)FindResource("SuccessBrush") 
                : (Brush)FindResource("DestructiveBrush");
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

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        LoadDisks();
    }

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DirectCloneWarningBanner == null || ProgressTitleText == null || StartBackupButton == null) return;

        bool isMirror = DirectMirrorRadio.IsChecked == true;
        DirectCloneWarningBanner.Visibility = isMirror ? Visibility.Visible : Visibility.Collapsed;

        if (isMirror)
        {
            ProgressTitleText.Text = "Cloning Progress:";
            StartBackupButton.Content = "Start Clone";
        }
        else
        {
            ProgressTitleText.Text = "Backup Progress:";
            StartBackupButton.Content = "Start Backup";
        }
    }

    private async void StartBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (MasterComboBox.SelectedItem is not DiskInfo master)
        {
            MessageBox.Show("Select a master (source) disk first.", "Replicore",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedTargets = TargetsListBox.Items.Cast<DiskInfo>().Where(d => d.IsSelected).ToList();
        if (selectedTargets.Count == 0)
        {
            MessageBox.Show("Please select at least one Target (Destination) disk using the checkboxes.", "Replicore",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // --- Safety guard 1: master and target can't be the same physical disk ---
        foreach (var target in selectedTargets)
        {
            if (master.Index == target.Index)
            {
                MessageBox.Show($"Master and target cannot be the same disk (Disk {target.Index}).", "Replicore",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // --- Safety guard 2: warn before writing to a disk that holds Windows itself ---
            if (target.IsSystemDisk)
            {
                var result = MessageBox.Show(
                    $"'{target.NameLine}' appears to be the disk Windows is currently running from. " +
                    "Using it as a target is highly unusual and may fail or corrupt your running system.\n\nContinue anyway?",
                    "Replicore — Warning",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;
            }

            // --- Safety guard 3: warning if target health is failing ---
            if (!target.IsHealthy)
            {
                var result = MessageBox.Show(
                    $"Target disk '{target.NameLine}' reports a failing S.M.A.R.T. health status ({target.HealthStatus}). " +
                    "Writing to a failing drive is risky and can lead to backup corruption or hardware failure.\n\nAre you sure you want to proceed?",
                    "Replicore — Critical Warning",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;
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
                    MessageBox.Show(
                        $"The Target disk '{target.NameLine}' ({target.SizeFormatted}) is smaller than the Master disk ({master.SizeFormatted}).\n\n" +
                        "Direct Sector Mirroring requires all Target disks to be equal in size or larger than the Master disk.",
                        "Replicore — Size Mismatch",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
            }

            // --- Mirror Safety Check: Permanent Wipe Confirmation ---
            string targetsListStr = string.Join("\n", selectedTargets.Select(t => $"   • {t.NameLine} ({t.SizeFormatted})"));
            
            var confirmWipe = MessageBox.Show(
                $"🚨 CRITICAL WARNING: DATA DESTRUCTION 🚨\n\n" +
                $"You have selected 'Direct Disk Mirror'. This will completely OVERWRITE and WIPE all data, file systems, and partitions on the following target disk(s):\n\n" +
                $"{targetsListStr}\n\n" +
                $"All existing files on these drives will be permanently lost.\n\n" +
                $"Once finished, these target drives will be exact duplicates of the master and bootable.\n\n" +
                $"Are you 100% sure you want to completely format and overwrite these drives?",
                "Permanent Wipe Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmWipe != MessageBoxResult.Yes) return;

            var doubleConfirm = MessageBox.Show(
                $"FINAL CHECK:\n\n" +
                $"You are about to wipe and overwrite the following drives:\n\n" +
                $"{targetsListStr}\n\n" +
                $"This action is permanent and CANNOT be undone.\n\n" +
                $"Click Yes to proceed with formatting and mirroring.",
                "Confirm Final Wipe",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (doubleConfirm != MessageBoxResult.Yes) return;

            await RunMirrorCloneAsync(master, selectedTargets);
        }
        else
        {
            // --- Backup Safety Check: Backup Confirmation ---
            string targetsListStr = string.Join("\n", selectedTargets.Select(t => $"   • {t.NameLine}"));
            var confirmBackup = MessageBox.Show(
                $"This will create a backup image of:\n\n   {master.NameLine}\n\nto the following target drive(s):\n\n{targetsListStr}\n\n" +
                "Existing data on the target locations is not erased. Continue?",
                "Confirm Backup",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirmBackup != MessageBoxResult.Yes) return;

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
            MessageBox.Show("Select a target disk in the list to preview history (must have a mounted drive letter).", "Replicore",
                MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void SaveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedTargets = TargetsListBox.Items.Cast<DiskInfo>().Where(d => d.IsSelected).ToList();
        var validTargets = selectedTargets.Where(t => !string.IsNullOrWhiteSpace(t.DriveLetters)).ToList();

        if (MasterComboBox.SelectedItem is not DiskInfo master || string.IsNullOrWhiteSpace(master.DriveLetters) ||
            validTargets.Count == 0)
        {
            MessageBox.Show("Please select a Master and at least one Target drive with a mounted volume (drive letter) first to schedule a backup.", "Replicore",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string day = (ScheduleDayComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Sunday";
        string time = ScheduleTimeTextBox.Text.Trim();

        // Validate 24h format (HH:mm)
        if (!System.Text.RegularExpressions.Regex.IsMatch(time, @"^(0[0-9]|1[0-9]|2[0-3]):[0-5][0-9]$"))
        {
            MessageBox.Show("Please enter a valid time in 24-hour format (e.g., 02:00 or 23:30).", "Replicore",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show($"Weekly backup scheduled successfully for {validTargets.Count} drive(s): {day}s at {time}.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            string errors = string.Join("\n", errorMessages);
            MessageBox.Show($"Some scheduled backup tasks failed to register:\n{errors}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteScheduleButton_Click(object sender, RoutedEventArgs e)
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
            MessageBox.Show("Scheduled backup task(s) deleted successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR deleting task(s): {ex.Message}");
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        var row = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4)
        };

        row.Inlines.Add(new System.Windows.Documents.Run($"{DateTime.Now:HH:mm:ss}  ")
        {
            Foreground = (Brush)FindResource("MutedForegroundBrush")
        });

        // Parse content for visual styling in log
        Brush msgBrush = (Brush)FindResource("ConsoleForegroundBrush");
        if (message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || 
            message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            msgBrush = (Brush)FindResource("DestructiveBrush");
        }
        else if (message.Contains("successfully", StringComparison.OrdinalIgnoreCase) || 
                 message.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            msgBrush = (Brush)FindResource("SuccessBrush");
        }
        else if (message.Contains("Starting", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("Scanning", StringComparison.OrdinalIgnoreCase))
        {
            // Electric purple/blue for starting highlights
            msgBrush = new SolidColorBrush(Color.FromRgb(129, 140, 248));
        }

        row.Inlines.Add(new System.Windows.Documents.Run(message)
        {
            Foreground = msgBrush
        });

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

    // Window Chrome control interactions
    private void MinButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    
    private void MaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
