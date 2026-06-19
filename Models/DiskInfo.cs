namespace Replicore.Models;

/// <summary>
/// Represents a physical disk on the system, as shown in the Master/Target pickers.
/// Pulled from WMI (Win32_DiskDrive) rather than raw device enumeration, since WMI
/// gives us friendly model names and sizes without needing a raw handle just to list disks.
/// </summary>
public class DiskInfo
{
    /// <summary>WMI DeviceID, e.g. \\.\PHYSICALDRIVE0 — used for raw access later.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Used in the UI to select multiple target drives for parallel cloning.</summary>
    public bool IsSelected { get; set; }

    /// <summary>Index, e.g. 0, 1, 2 — matches "Disk 0" in Windows Disk Management.</summary>
    public int Index { get; set; }

    /// <summary>Friendly model name, e.g. "Samsung SSD 970 EVO 1TB".</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Total size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>True if this disk contains the currently running Windows installation.</summary>
    public bool IsSystemDisk { get; set; }

    /// <summary>Drive letters mounted from this physical disk, e.g. "C:, D:".</summary>
    public string DriveLetters { get; set; } = string.Empty;

    /// <summary>Interface type, e.g. "USB", "SCSI", "IDE" — used to flag external/removable disks.</summary>
    public string InterfaceType { get; set; } = string.Empty;

    /// <summary>S.M.A.R.T. health status (e.g., "OK", "Pred Fail", "Degraded").</summary>
    public string HealthStatus { get; set; } = "OK";

    /// <summary>True if the drive health is OK.</summary>
    public bool IsHealthy => HealthStatus.Equals("OK", StringComparison.OrdinalIgnoreCase);

    /// <summary>Logical partitions mounted on this physical disk.</summary>
    public List<PartitionInfo> Partitions { get; set; } = new();

    public string SizeFormatted
    {
        get
        {
            double gb = SizeBytes / 1024.0 / 1024.0 / 1024.0;
            return gb >= 1000 ? $"{gb / 1024.0:0.##} TB" : $"{gb:0.#} GB";
        }
    }

    /// <summary>Top line of the two-line combo box row — "Disk 0 — Samsung SSD 970 EVO".</summary>
    public string NameLine => $"Disk {Index} — {Model}";

    /// <summary>Bottom muted line — "953.9 GB  •  C:  •  System Disk" or "953.9 GB  •  No drive letter".</summary>
    public string MetaLine
    {
        get
        {
            var parts = new List<string> { SizeFormatted };
            parts.Add(string.IsNullOrWhiteSpace(DriveLetters) ? "No drive letter" : DriveLetters);
            if (IsSystemDisk) parts.Add("System Disk");
            parts.Add(IsHealthy ? "Healthy" : $"Health Alert: {HealthStatus}");
            return string.Join("   •   ", parts);
        }
    }

    /// <summary>What the UI shows in the dropdown — "Disk 0 — Samsung SSD 970 EVO (476.9 GB) [C:]".</summary>
    public string DisplayName =>
        $"Disk {Index} — {Model} ({SizeFormatted})" +
        (string.IsNullOrWhiteSpace(DriveLetters) ? "" : $" [{DriveLetters}]") +
        (IsSystemDisk ? "  ⚠ System Disk" : "") +
        (!IsHealthy ? "  ⚠ HEALTH ALERT" : "");

    public override string ToString() => DisplayName;
}

public class PartitionInfo
{
    public string DriveLetter { get; set; } = string.Empty;
    public string VolumeName { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long FreeSpaceBytes { get; set; }

    public double UsedPercent => SizeBytes > 0 ? (1.0 - (double)FreeSpaceBytes / SizeBytes) * 100 : 0;

    public string SizeFormatted => FormatSize(SizeBytes);
    public string FreeSpaceFormatted => FormatSize(FreeSpaceBytes);
    public string UsedSpaceFormatted => FormatSize(SizeBytes - FreeSpaceBytes);

    public string DisplayLabel => string.IsNullOrWhiteSpace(VolumeName) ? "Local Disk" : VolumeName;

    private string FormatSize(long bytes)
    {
        double gb = bytes / 1024.0 / 1024.0 / 1024.0;
        return gb >= 1000 ? $"{gb / 1024.0:0.##} TB" : $"{gb:0.#} GB";
    }
}
