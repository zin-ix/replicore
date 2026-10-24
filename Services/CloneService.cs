using System.Diagnostics;
using System.IO;
using System.Text;

namespace Replicore.Services;

public class CloneProgressEventArgs : EventArgs
{
    /// <summary>Clone stage for the target card: Erasing, Cloning, Verifying, Finalizing or Failed. Null for log-only messages.</summary>
    public string? Phase { get; set; }
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
