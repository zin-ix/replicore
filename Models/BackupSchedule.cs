namespace Replicore.Models;

/// <summary>What a scheduled backup captures.</summary>
public enum ScheduleKind
{
    /// <summary>wbadmin image of every volume on the source disk plus the boot files.</summary>
    FullImage,
    /// <summary>wbadmin image of the chosen volumes only.</summary>
    Volumes,
    /// <summary>robocopy of the chosen folders; wbadmin on Windows client editions can't back up folders.</summary>
    Folders
}

/// <summary>
/// A weekly backup saved in the database. Task Scheduler only knows to run
/// "Replicore.exe --run-schedule {Id}"; everything about what to back up lives here.
/// </summary>
public class BackupSchedule
{
    public long Id { get; set; }
    public ScheduleKind Kind { get; set; }

    /// <summary>"Disk 0 — Samsung SSD 970 EVO" for image schedules; empty for folder schedules.</summary>
    public string SourceLabel { get; set; } = string.Empty;

    /// <summary>Drive letters to image, e.g. "C:" and "D:". Resolved when the schedule is saved.</summary>
    public List<string> Volumes { get; set; } = new();

    /// <summary>Adds -allCritical so the image can restore Windows.</summary>
    public bool Bootable { get; set; }

    public List<string> Folders { get; set; } = new();

    /// <summary>"E:" or "\\server\share" for images; any folder for folder backups.</summary>
    public string Destination { get; set; } = string.Empty;

    public DayOfWeek Day { get; set; }

    /// <summary>24-hour start time, e.g. "02:00".</summary>
    public string Time { get; set; } = "02:00";

    public DateTime CreatedAt { get; set; }

    /// <summary>Where the task lives in Task Scheduler.</summary>
    public string TaskPath => $@"\Replicore\Backup {Id}";

    public string KindLabel => KindLabelFor(Kind);

    public static string KindLabelFor(ScheduleKind kind) => kind switch
    {
        ScheduleKind.FullImage => "Full system image",
        ScheduleKind.Volumes => "Selected volumes",
        _ => "Selected folders"
    };

    /// <summary>"Full system image of Disk 0 — Samsung SSD" or "3 folders".</summary>
    public string SourceSummary => Kind switch
    {
        ScheduleKind.FullImage => SourceLabel,
        ScheduleKind.Volumes => $"{string.Join(", ", Volumes)} on {SourceLabel}",
        _ => Folders.Count == 1 ? Folders[0] : $"{Folders.Count} folders"
    };

    /// <summary>"Every Sunday at 02:00 to E:".</summary>
    public string WhenSummary => $"Every {Day} at {Time} to {Destination}";
}
