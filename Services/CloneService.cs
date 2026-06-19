using System.Diagnostics;
using System.IO;
using System.Text;

namespace Replicore.Services;

public class CloneProgressEventArgs : EventArgs
{
    public string Message { get; set; } = string.Empty;
    public int? PercentComplete { get; set; }
    public double? SpeedMB { get; set; }
    public double? EtaSeconds { get; set; }
}

public class BackupVersion
{
    public string Timestamp { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string VersionId { get; set; } = string.Empty;
    public string RecoverableItems { get; set; } = string.Empty;
}

/// <summary>
/// Wraps Windows' built-in wbadmin engine for full-disk backup and restore.
///
/// Why wbadmin instead of a custom byte-copy loop: wbadmin uses VSS under the hood,
/// meaning it can safely snapshot a disk that's actively in use (including the running
/// system disk) without corrupting open files. A hand-written sector copier either has to
/// reimplement that snapshot logic from scratch or refuse to clone disks that are in use —
/// neither is a good trade-off for a tool whose whole purpose is "save me from a reinstall."
/// </summary>
public class CloneService
{
    public event EventHandler<CloneProgressEventArgs>? ProgressChanged;

    // Win32 API imports for direct disk sector access
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        [System.Runtime.InteropServices.Out] byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle hFile);

    /// <summary>
    /// Performs a raw sector-by-sector mirror clone directly between two physical drives.
    /// WARNING: This completely overwrites all partition tables and data on the target drive!
    /// </summary>
    public async Task<bool> DirectSectorCloneAsync(string sourceDeviceId, string targetDeviceId, int targetDiskIndex, long totalBytes, IProgress<CloneProgressEventArgs> progress, CancellationToken token)
    {
        // First run diskpart clean on target disk to release volume locks and erase partition tables
        bool cleanSuccess = await CleanDiskAsync(targetDiskIndex, progress, token);
        if (!cleanSuccess)
        {
            progress.Report(new CloneProgressEventArgs { Message = "WARNING: diskpart clean failed or was interrupted. Attempting direct sector clone anyway..." });
        }
        else
        {
            progress.Report(new CloneProgressEventArgs { Message = "Target disk partitions and tables cleaned successfully. Pausing 3 seconds for Windows to update physical disk state..." });
            await Task.Delay(3000, token);
        }

        return await Task.Run(() =>
        {
            progress.Report(new CloneProgressEventArgs { Message = "Opening handles to source and target physical drives..." });

            using var sourceHandle = CreateFile(
                sourceDeviceId,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (sourceHandle.IsInvalid)
            {
                int errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                progress.Report(new CloneProgressEventArgs { Message = $"ERROR: Failed to open source drive {sourceDeviceId} (Win32 Error: {errorCode}). Ensure the app runs as Administrator." });
                return false;
            }

            using var targetHandle = CreateFile(
                targetDeviceId,
                GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_WRITE_THROUGH,
                IntPtr.Zero);

            if (targetHandle.IsInvalid)
            {
                int errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                progress.Report(new CloneProgressEventArgs { Message = $"ERROR: Failed to open target drive {targetDeviceId} (Win32 Error: {errorCode}). Verify that the target has no active locks." });
                return false;
            }

            const int bufferSize = 1024 * 1024; // 1 MB copy chunks
            byte[] buffer = new byte[bufferSize];

            long bytesCopied = 0;
            var startTime = DateTime.Now;
            int lastPercent = -1;

            progress.Report(new CloneProgressEventArgs { Message = "Starting direct sector-by-sector mirror clone...", PercentComplete = 0 });

            while (bytesCopied < totalBytes)
            {
                token.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(bufferSize, totalBytes - bytesCopied);

                uint bytesRead;
                bool readSuccess = ReadFile(sourceHandle, buffer, (uint)toRead, out bytesRead, IntPtr.Zero);
                if (!readSuccess)
                {
                    int errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    progress.Report(new CloneProgressEventArgs { Message = $"ERROR: Failed to read from source drive (Win32 Error: {errorCode})." });
                    return false;
                }
                if (bytesRead == 0) break;

                uint bytesWritten;
                bool writeSuccess = WriteFile(targetHandle, buffer, bytesRead, out bytesWritten, IntPtr.Zero);
                if (!writeSuccess)
                {
                    int errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    progress.Report(new CloneProgressEventArgs { Message = $"ERROR: Failed to write to target drive (Win32 Error: {errorCode})." });
                    return false;
                }
                if (bytesWritten != bytesRead)
                {
                    progress.Report(new CloneProgressEventArgs { Message = "ERROR: Mismatch in written bytes." });
                    return false;
                }

                bytesCopied += bytesRead;

                int percent = (int)(((double)bytesCopied / totalBytes) * 100);
                double elapsedSeconds = (DateTime.Now - startTime).TotalSeconds;

                // Throttled UI Progress Updates: only report when percentage changes
                if (percent != lastPercent || bytesCopied == totalBytes)
                {
                    lastPercent = percent;
                    double speedMB = (bytesCopied / 1024.0 / 1024.0) / (elapsedSeconds > 0 ? elapsedSeconds : 1);
                    double remainingBytes = totalBytes - bytesCopied;
                    double speedBytes = (double)bytesCopied / (elapsedSeconds > 0 ? elapsedSeconds : 1);
                    double etaSeconds = speedBytes > 0 ? remainingBytes / speedBytes : 0;

                    progress.Report(new CloneProgressEventArgs
                    {
                        Message = $"Copied {bytesCopied / (1024.0 * 1024.0 * 1024.0):F2} GB / {totalBytes / (1024.0 * 1024.0 * 1024.0):F2} GB ({speedMB:F1} MB/s)",
                        PercentComplete = percent,
                        SpeedMB = speedMB,
                        EtaSeconds = etaSeconds
                    });
                }
            }

            progress.Report(new CloneProgressEventArgs { Message = "Direct sector copy completed. Flushing buffers to physical disk...", PercentComplete = 100 });
            FlushFileBuffers(targetHandle);

            return true;
        }, token);
    }

    /// <summary>
    /// Executes diskpart clean on the specified disk index to wipe out all partitions and partition structures (GPT/MBR).
    /// </summary>
    public async Task<bool> CleanDiskAsync(int diskIndex, IProgress<CloneProgressEventArgs> progress, CancellationToken token)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), $"diskpart_clean_{diskIndex}.txt");
        try
        {
            await File.WriteAllTextAsync(scriptPath, $"select disk {diskIndex}\r\nclean\r\n", Encoding.ASCII, token);
            progress.Report(new CloneProgressEventArgs { Message = $"Wiping partition tables and data structures on Disk {diskIndex} via diskpart clean..." });

            bool success = true;
            await RunCommandAsync("diskpart.exe", $"/s \"{scriptPath}\"", line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    progress.Report(new CloneProgressEventArgs { Message = $"[diskpart] {line.Trim()}" });
                    if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
                    {
                        success = false;
                    }
                }
            }, token);

            return success;
        }
        catch (Exception ex)
        {
            progress.Report(new CloneProgressEventArgs { Message = $"ERROR: Failed to run diskpart clean: {ex.Message}" });
            return false;
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Runs a full backup of the given source volume(s) to the target location.
    /// targetPath should be a drive letter or UNC path with enough free space.
    /// </summary>
    public async Task<bool> BackupAsync(string sourceVolumeLetter, string targetPath, IProgress<CloneProgressEventArgs> progress, CancellationToken token)
    {
        // -backupTarget: where the image is written
        // -include: which volume to capture (the "master")
        // -allCritical: also grabs the System Reserved/EFI partition so the backup is bootable
        // -quiet: avoids wbadmin's interactive confirmation prompt
        string args = $"start backup -backupTarget:{targetPath} -include:{sourceVolumeLetter} -allCritical -quiet";

        return await RunWbAdminAsync(args, progress, token);
    }

    /// <summary>
    /// Lists available recovery points on a given backup target so the user can pick
    /// which snapshot to restore from in the UI.
    /// </summary>
    public async Task<string> GetBackupVersionsAsync(string targetPath)
    {
        var output = new StringBuilder();
        await RunCommandAsync("wbadmin", $"get versions -backupTarget:{targetPath}", line =>
        {
            output.AppendLine(line);
        }, CancellationToken.None);

        return output.ToString();
    }

    /// <summary>
    /// Parses the raw string output of `wbadmin get versions` into typed BackupVersion records.
    /// </summary>
    public List<BackupVersion> ParseBackupVersions(string rawText)
    {
        var list = new List<BackupVersion>();
        if (string.IsNullOrWhiteSpace(rawText)) return list;

        var lines = rawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        BackupVersion? current = null;

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Backup time:", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null) list.Add(current);
                current = new BackupVersion { Timestamp = trimmed.Substring("Backup time:".Length).Trim() };
            }
            else if (current != null)
            {
                if (trimmed.StartsWith("Backup location:", StringComparison.OrdinalIgnoreCase))
                {
                    current.Location = trimmed.Substring("Backup location:".Length).Trim();
                }
                else if (trimmed.StartsWith("Version identifier:", StringComparison.OrdinalIgnoreCase))
                {
                    current.VersionId = trimmed.Substring("Version identifier:".Length).Trim();
                }
                else if (trimmed.StartsWith("Can be recovered:", StringComparison.OrdinalIgnoreCase))
                {
                    current.RecoverableItems = trimmed.Substring("Can be recovered:".Length).Trim();
                }
            }
        }
        if (current != null) list.Add(current);
        return list;
    }

    /// <summary>
    /// Restores a full system image to the target disk. This is the "instead of reinstalling
    /// Windows" recovery path — it's typically run from Windows Recovery Environment (WinRE),
    /// not from inside a normal desktop session, since you can't overwrite the disk Windows
    /// is currently booted from while it's running.
    /// </summary>
    public async Task<bool> RestoreAsync(string versionId, string targetDiskId, IProgress<CloneProgressEventArgs> progress, CancellationToken token)
    {
        string args = $"start sysrecovery -version:{versionId} -backupTarget:{targetDiskId} -quiet";
        return await RunWbAdminAsync(args, progress, token);
    }

    private async Task<bool> RunWbAdminAsync(string arguments, IProgress<CloneProgressEventArgs> progress, CancellationToken token)
    {
        bool success = true;

        await RunCommandAsync("wbadmin", arguments, line =>
        {
            // wbadmin prints lines like "Created a shadow copy of volumes... 45%"
            // We parse out percentage hints where present to drive the UI progress bar.
            int? percent = TryParsePercent(line);

            progress.Report(new CloneProgressEventArgs
            {
                Message = line,
                PercentComplete = percent
            });

            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                success = false;
        }, token);

        return success;
    }

    private static int? TryParsePercent(string line)
    {
        int idx = line.IndexOf('%');
        if (idx < 1) return null;

        int start = idx - 1;
        while (start > 0 && char.IsDigit(line[start - 1])) start--;

        if (int.TryParse(line.Substring(start, idx - start), out int value))
            return value;

        return null;
    }

    private async Task RunCommandAsync(string fileName, string arguments, Action<string> onLine, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine($"ERROR: {e.Data}"); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
    }

    private void OnProgress(string message, int? percent)
    {
        ProgressChanged?.Invoke(this, new CloneProgressEventArgs
        {
            Message = message,
            PercentComplete = percent
        });
    }
}
