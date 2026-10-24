namespace Replicore.Models;

public enum RunStatus { Running, Succeeded, Failed, Cancelled }

/// <summary>One backup or clone Replicore ran, scheduled or started from the Clone page.</summary>
public class BackupRun
{
    public long Id { get; set; }

    /// <summary>The schedule that started this run; null for a run started by hand.</summary>
    public long? ScheduleId { get; set; }

    /// <summary>"Full system image", "Selected folders", "Image backup" or "Clone".</summary>
    public string Kind { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public RunStatus Status { get; set; }

    /// <summary>A short result line, e.g. the error that stopped the run.</summary>
    public string Details { get; set; } = string.Empty;

    /// <summary>Full output of a scheduled run; empty when there's no log file.</summary>
    public string LogPath { get; set; } = string.Empty;

    public string TriggerLabel => ScheduleId.HasValue ? $"Schedule {ScheduleId}" : "Manual";
    public string StartedText => StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string StatusText => Status switch
    {
        RunStatus.Running => "Running",
        RunStatus.Succeeded => "Succeeded",
        RunStatus.Failed => "Failed",
        _ => "Cancelled"
    };

    public string DurationText
    {
        get
        {
            if (FinishedAt is not DateTime end) return "";
            var span = end - StartedAt;
            return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
                 : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s"
                 : $"{Math.Max(0, span.Seconds)}s";
        }
    }

    public bool HasLog => LogPath.Length > 0 && System.IO.File.Exists(LogPath);
}
