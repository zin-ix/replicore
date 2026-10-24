namespace Replicore.Models;

public enum SegmentKind
{
    /// <summary>A data partition, with or without a drive letter.</summary>
    Volume,
    /// <summary>EFI, Microsoft Reserved, Recovery and other non-data partitions.</summary>
    System,
    Unallocated
}

/// <summary>One partition or unallocated gap on a disk, as drawn in the partition map.</summary>
public sealed class DiskSegment
{
    public long Offset { get; init; }
    public long Size { get; init; }
    public SegmentKind Kind { get; init; }

    /// <summary>"C:" for a partition with a drive letter, otherwise empty.</summary>
    public string DriveLetter { get; init; } = string.Empty;

    /// <summary>Short label drawn inside the bar, e.g. "C: Windows", "EFI", "Free".</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Hover text with the details, e.g. "C: Windows (NTFS)\n476.9 GB, 120 GB used".</summary>
    public string Description { get; init; } = string.Empty;
}
