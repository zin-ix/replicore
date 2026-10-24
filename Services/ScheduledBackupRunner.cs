using System.Diagnostics;
using System.IO;
using System.Text;
using Replicore.Models;

namespace Replicore.Services;

/// <summary>
/// Runs one saved schedule without any window: "Replicore.exe --run-schedule {id}", started by
/// Task Scheduler as SYSTEM. It records the run in the history database and writes the full
/// output of wbadmin or robocopy to a log file under C:\ProgramData\Replicore\logs.
/// </summary>
public static class ScheduledBackupRunner
{
    // wbadmin runs one backup at a time, so schedules that start together wait for each other
    private const string RunMutexName = @"Global\Replicore.ScheduledBackup";

    /// <summary>Exit code for Task Scheduler's "Last Run Result": 0 when the backup succeeded.</summary>
    public static int Run(long scheduleId)
    {
        HistoryDatabase db;
        BackupSchedule? schedule;
        try
        {
            db = new HistoryDatabase();
            schedule = db.GetSchedule(scheduleId);
        }
        catch
        {
            return 2;
        }
        if (schedule == null) return 3; // Deleted in the app while its task still existed

        string logPath = Path.Combine(HistoryDatabase.LogFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-schedule{scheduleId}.log");
        string source = schedule.Kind == ScheduleKind.Folders ? string.Join("; ", schedule.Folders) : schedule.SourceSummary;
        long runId = db.StartRun(schedule.Id, schedule.KindLabel, source, schedule.Destination, logPath);

        using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string line) => log.WriteLine($"{DateTime.Now:HH:mm:ss}  {line}");

        using var mutex = new Mutex(false, RunMutexName);
        try
        {
            Log($"{schedule.KindLabel}: {schedule.SourceSummary}. {schedule.WhenSummary}.");
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromHours(12)))
                {
                    db.FinishRun(runId, RunStatus.Failed, "Another scheduled backup was still running after 12 hours.");
                    return 4;
                }
            }
            catch (AbandonedMutexException) { /* The previous run was killed; this one can go ahead */ }

            var (status, details) = schedule.Kind == ScheduleKind.Folders
                ? RunFolderBackup(schedule, Log)
                : RunImageBackup(schedule, Log);

            Log(details);
            db.FinishRun(runId, status, details);
            return status == RunStatus.Succeeded ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex}");
            db.FinishRun(runId, RunStatus.Failed, ex.Message);
            return 1;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* Never acquired */ }
        }
    }

    private static (RunStatus, string) RunImageBackup(BackupSchedule schedule, Action<string> log)
    {
        string target = schedule.Destination.Contains(' ') ? $"\"{schedule.Destination}\"" : schedule.Destination;
        string args = $"start backup -backupTarget:{target} -include:{string.Join(",", schedule.Volumes)}" +
                      $"{(schedule.Bootable ? " -allCritical" : "")} -quiet";

        string? lastError = null;
        int exit = RunProcess("wbadmin", args, line =>
        {
            log(line);
            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) lastError = line.Trim();
        });

        return exit == 0
            ? (RunStatus.Succeeded, $"Image of {string.Join(", ", schedule.Volumes)} saved to {schedule.Destination}.")
            : (RunStatus.Failed, lastError ?? $"wbadmin failed with exit code {exit}.");
    }

    private static (RunStatus, string) RunFolderBackup(BackupSchedule schedule, Action<string> log)
    {
        var failed = new List<string>();
        var destinations = FolderBackupPaths(schedule.Destination, schedule.Folders);
        foreach (var (folder, destination) in schedule.Folders.Zip(destinations))
        {
            if (!Directory.Exists(folder))
            {
                log($"ERROR: {folder} doesn't exist.");
                failed.Add($"{folder} (not found)");
                continue;
            }

            log($"Copying {folder} to {destination}");

            // /E subfolders, /B backup mode so SYSTEM can read protected files, /XJ skips junctions that
            // loop (e.g. inside user profiles), /R:1 /W:1 so a locked file doesn't stall the whole run
            string args = $"{Quote(folder)} {Quote(destination)} /E /B /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /NP /NDL /BYTES";
            int exit = RunProcess("robocopy", args, log);

            // robocopy: 0-7 mean success (with or without changes), 8 and above mean some files failed
            if (exit >= 8)
            {
                log($"ERROR: robocopy exit code {exit} for {folder}.");
                failed.Add($"{folder} (exit code {exit})");
            }
        }

        if (failed.Count == 0)
            return (RunStatus.Succeeded, $"{schedule.Folders.Count} folder(s) copied to {schedule.Destination}.");
        return (RunStatus.Failed, $"Some files weren't copied: {string.Join("; ", failed)}. See the log.");
    }

    /// <summary>
    /// Where each folder lands: C:\Users\you\Documents → E:\Backups\back_up_Documents. A second folder
    /// with the same name in the schedule gets back_up_Documents_2, so the two never mix.
    /// </summary>
    public static List<string> FolderBackupPaths(string destination, List<string> folders)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return folders.Select(folder =>
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            if (name.Length == 0) name = folder[..1]; // A drive root such as C:\ is named after its letter

            string candidate = $"back_up_{name}";
            for (int n = 2; !used.Add(candidate); n++) candidate = $"back_up_{name}_{n}";
            return Path.Combine(destination, candidate);
        }).ToList();
    }

    /// <summary>A trailing backslash would escape robocopy's closing quote, so "C:\" becomes "C:\.".</summary>
    private static string Quote(string path) => "\"" + (path.EndsWith('\\') ? path + "." : path) + "\"";

    private static int RunProcess(string fileName, string arguments, Action<string> onLine)
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
        var gate = new object();
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (gate) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (gate) onLine($"ERROR: {e.Data}"); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return process.ExitCode;
    }
}
