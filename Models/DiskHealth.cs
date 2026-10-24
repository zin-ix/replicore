namespace Replicore.Models;

/// <summary>Ordered from least to most severe, so the worst finding wins.</summary>
public enum HealthLevel { Unknown, Healthy, Warning, Critical }

/// <summary>One row of the health details list, e.g. "Temperature" / "38 °C".</summary>
public sealed record HealthDetail(string Label, string Value);

/// <summary>
/// Disk health from the Windows Storage Management API (MSFT_PhysicalDisk) and the drive's
/// reliability counters — the S.M.A.R.T. data Windows exposes, such as temperature, SSD wear
/// and uncorrectable errors.
/// </summary>
public sealed class DiskHealth
{
    public static DiskHealth Unknown { get; } = new();

    public HealthLevel Level { get; init; } = HealthLevel.Unknown;

    /// <summary>Problems found, worded for the user, e.g. "Running hot (64 °C)".</summary>
    public List<string> Issues { get; init; } = new();

    /// <summary>Readings to show, e.g. temperature and power-on time. Only what the drive reports.</summary>
    public List<HealthDetail> Details { get; init; } = new();

    public string Label => Level switch
    {
        HealthLevel.Healthy => "Healthy",
        HealthLevel.Warning => "Warning",
        HealthLevel.Critical => "Failing",
        _ => "Health unknown"
    };

    /// <summary>Hover text for the health badge.</summary>
    public string ToolTipText
    {
        get
        {
            var lines = Issues.Concat(Details.Select(d => $"{d.Label}: {d.Value}")).ToList();
            return lines.Count == 0 ? "This drive doesn't report health data." : string.Join("\n", lines);
        }
    }
}
