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
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    public List<DiskInfo> GetPhysicalDisks()
    {
        var disks = new List<DiskInfo>();
        var win32Statuses = new Dictionary<int, string>();
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
                InterfaceType = disk["InterfaceType"]?.ToString() ?? ""
            };
            win32Statuses[info.Index] = disk["Status"]?.ToString() ?? "";

            info.Partitions = GetPartitionsForDisk(info.Index);

            // Format drive letters for high-level display
            var letters = info.Partitions.Select(p => p.DriveLetter).Where(l => !string.IsNullOrWhiteSpace(l));
            info.DriveLetters = string.Join(", ", letters);

            info.IsSystemDisk = info.Partitions.Any(p => p.DriveLetter.Contains(systemDriveLetter.TrimEnd('\\')));

            disks.Add(info);
        }

        var health = DiskHealthService.GetHealth(win32Statuses);
        foreach (var info in disks) info.Health = health.GetValueOrDefault(info.Index, DiskHealth.Unknown);
        AddLayouts(disks);

        return disks.OrderBy(d => d.Index).ToList();
    }

    // Gaps smaller than this are partition alignment padding, not usable free space
    private const long MinimumGap = 16L * 1024 * 1024;

    private static readonly Dictionary<string, string> GptTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"] = "EFI",
        ["{e3c9e316-0b5c-4db8-817d-f92df00215ae}"] = "Reserved",
        ["{de94bba4-06d1-4d40-a16a-bfd50179d6ac}"] = "Recovery",
        ["{0fc63daf-8483-4772-8e79-3d69d8477de4}"] = "Linux",
        ["{0657fd6d-a4ab-43c4-84e5-0933c84b4f4f}"] = "Linux swap",
    };
    private const string GptBasicData = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";

    private static readonly Dictionary<int, string> MbrTypeNames = new()
    {
        [0x05] = "Extended", [0x0F] = "Extended", [0x27] = "Recovery", [0xEF] = "EFI", [0x82] = "Linux swap", [0x83] = "Linux",
    };
    // NTFS/exFAT, FAT32 and FAT16 data partitions
    private static readonly HashSet<int> MbrDataTypes = [0x07, 0x0B, 0x0C, 0x0E, 0x06];

    /// <summary>
    /// Fills in each disk's full partition layout from the Storage Management API, including the
    /// partitions without a drive letter (EFI, Microsoft Reserved, Recovery) that Win32_LogicalDisk skips.
    /// </summary>
    private static void AddLayouts(List<DiskInfo> disks)
    {
        var sizes = new Dictionary<int, long>();
        var partitions = new Dictionary<int, List<ManagementBaseObject>>();
        try
        {
            using (var diskSearcher = new ManagementObjectSearcher(StorageNamespace, "SELECT Number, Size FROM MSFT_Disk"))
                foreach (var disk in diskSearcher.Get())
                    sizes[Convert.ToInt32(disk["Number"])] = Convert.ToInt64(disk["Size"]);

            using var partitionSearcher = new ManagementObjectSearcher(StorageNamespace,
                "SELECT DiskNumber, PartitionNumber, Offset, Size, DriveLetter, GptType, MbrType FROM MSFT_Partition");
            foreach (var partition in partitionSearcher.Get())
            {
                int number = Convert.ToInt32(partition["DiskNumber"]);
                if (!partitions.TryGetValue(number, out var list)) partitions[number] = list = new();
                list.Add(partition);
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Storage API unavailable; the partition map shows only what WMI already found
        }

        foreach (var info in disks)
        {
            info.LayoutSizeBytes = sizes.GetValueOrDefault(info.Index, info.SizeBytes);
            info.Layout = BuildLayout(info, partitions.GetValueOrDefault(info.Index) ?? new());
        }
    }

    private static List<DiskSegment> BuildLayout(DiskInfo disk, List<ManagementBaseObject> partitions)
    {
        var segments = new List<DiskSegment>();
        long position = 0;

        void AddGap(long until)
        {
            if (until - position >= MinimumGap)
                segments.Add(new DiskSegment
                {
                    Offset = position, Size = until - position, Kind = SegmentKind.Unallocated, Title = "Free",
                    Description = $"Unallocated\n{FormatSize(until - position)}"
                });
        }

        foreach (var partition in partitions.OrderBy(p => Convert.ToInt64(p["Offset"])))
        {
            long offset = Convert.ToInt64(partition["Offset"]);
            long size = Convert.ToInt64(partition["Size"]);
            AddGap(offset);
            segments.Add(DescribePartition(disk, partition, offset, size));
            position = Math.Max(position, offset + size);
        }
        AddGap(disk.LayoutSizeBytes);
        return segments;
    }

    private static DiskSegment DescribePartition(DiskInfo disk, ManagementBaseObject partition, long offset, long size)
    {
        char letterChar = partition["DriveLetter"] is { } value ? Convert.ToChar(value) : '\0';
        string letter = char.IsLetter(letterChar) ? $"{letterChar}:" : "";
        var volume = disk.Partitions.FirstOrDefault(p => letter.Length > 0 && p.DriveLetter.Equals(letter, StringComparison.OrdinalIgnoreCase));

        string gptType = partition["GptType"]?.ToString() ?? "";
        int? mbrType = partition["MbrType"] is { } mbr ? Convert.ToInt32(mbr) : null;
        string? typeName = gptType.Length > 0
            ? GptTypeNames.GetValueOrDefault(gptType)
            : mbrType is { } type ? MbrTypeNames.GetValueOrDefault(type) : null;
        bool isData = letter.Length > 0 || gptType.Equals(GptBasicData, StringComparison.OrdinalIgnoreCase) ||
                      (mbrType is { } t && MbrDataTypes.Contains(t));

        if (volume != null)
        {
            string label = volume.DisplayLabel;
            string fileSystem = volume.FileSystem.Length > 0 ? $" ({volume.FileSystem})" : "";
            return new DiskSegment
            {
                Offset = offset, Size = size, Kind = SegmentKind.Volume, DriveLetter = letter, Title = $"{letter} {label}",
                Description = $"{letter} {label}{fileSystem}\n{FormatSize(size)}, {volume.UsedSpaceFormatted} used"
            };
        }

        string name = isData && typeName == null ? (letter.Length > 0 ? letter : "Data") : typeName ?? $"Partition {partition["PartitionNumber"]}";
        string note = isData && letter.Length == 0 ? "No drive letter" : "";
        return new DiskSegment
        {
            Offset = offset, Size = size, Kind = isData ? SegmentKind.Volume : SegmentKind.System, DriveLetter = letter, Title = name,
            Description = $"{name} partition\n{FormatSize(size)}{(note.Length > 0 ? $", {note}" : "")}"
        };
    }

    private static string FormatSize(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        if (mb < 1024) return $"{mb:0} MB";
        double gb = mb / 1024.0;
        return gb >= 1000 ? $"{gb / 1024.0:0.##} TB" : $"{gb:0.#} GB";
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
