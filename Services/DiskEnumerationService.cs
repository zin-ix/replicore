using System.IO;
using System.Management;
using Replicore.Models;

namespace Replicore.Services;

/// <summary>
/// Enumerates physical disks via WMI so the UI can populate Master/Target dropdowns.
/// Read-only — this class never opens a raw handle or writes anything; it only reads
/// metadata, which is why it's safe to call without confirmation dialogs.
/// </summary>
public class DiskEnumerationService
{
    public List<DiskInfo> GetPhysicalDisks()
    {
        var disks = new List<DiskInfo>();
        string systemDriveLetter = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            ?? "C:\\";

        using var diskSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
        foreach (ManagementObject disk in diskSearcher.Get())
        {
            var info = new DiskInfo
            {
                DeviceId = disk["DeviceID"]?.ToString() ?? "",
                Index = Convert.ToInt32(disk["Index"]),
                Model = disk["Model"]?.ToString()?.Trim() ?? "Unknown Disk",
                SizeBytes = disk["Size"] is null ? 0 : Convert.ToInt64(disk["Size"]),
                InterfaceType = disk["InterfaceType"]?.ToString() ?? "",
                HealthStatus = disk["Status"]?.ToString() ?? "OK"
            };

            info.Partitions = GetPartitionsForDisk(info.Index);
            
            // Format drive letters for high-level display
            var letters = info.Partitions.Select(p => p.DriveLetter).Where(l => !string.IsNullOrWhiteSpace(l));
            info.DriveLetters = string.Join(", ", letters);
            
            info.IsSystemDisk = info.Partitions.Any(p => p.DriveLetter.Contains(systemDriveLetter.TrimEnd('\\')));

            disks.Add(info);
        }

        return disks.OrderBy(d => d.Index).ToList();
    }

    /// <summary>
    /// Walks the WMI association chain: DiskDrive -> Partition -> LogicalDisk,
    /// which is how Windows maps a physical disk index to full partition details.
    /// </summary>
    private List<PartitionInfo> GetPartitionsForDisk(int diskIndex)
    {
        var partitions = new List<PartitionInfo>();

        string partitionQuery =
            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\.\\PHYSICALDRIVE{diskIndex}'}} " +
            "WHERE AssocClass = Win32_DiskDriveToDiskPartition";

        using var partitionSearcher = new ManagementObjectSearcher(partitionQuery);
        foreach (ManagementObject partition in partitionSearcher.Get())
        {
            string partitionId = partition["DeviceID"]?.ToString() ?? "";

            string logicalDiskQuery =
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionId}'}} " +
                "WHERE AssocClass = Win32_LogicalDiskToPartition";

            using var logicalSearcher = new ManagementObjectSearcher(logicalDiskQuery);
            foreach (ManagementObject logicalDisk in logicalSearcher.Get())
            {
                var partInfo = new PartitionInfo
                {
                    DriveLetter = logicalDisk["DeviceID"]?.ToString() ?? "",
                    VolumeName = logicalDisk["VolumeName"]?.ToString() ?? "",
                    FileSystem = logicalDisk["FileSystem"]?.ToString() ?? "",
                    SizeBytes = logicalDisk["Size"] is null ? 0 : Convert.ToInt64(logicalDisk["Size"]),
                    FreeSpaceBytes = logicalDisk["FreeSpace"] is null ? 0 : Convert.ToInt64(logicalDisk["FreeSpace"])
                };
                partitions.Add(partInfo);
            }
        }

        return partitions;
    }
}
