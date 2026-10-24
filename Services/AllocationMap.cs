using System.IO;
using System.Numerics;

namespace Replicore.Services;

/// <summary>
/// Which parts of the source disk hold data, from the NTFS allocation bitmap of each snapshotted
/// volume. Smart copy skips free clusters. Everything outside those volumes — partition tables,
/// EFI, Recovery and non-NTFS partitions — always counts as used, as does the tail of a volume
/// past its last cluster (where NTFS keeps its backup boot sector).
/// </summary>
internal sealed class AllocationMap
{
    private sealed record Volume(long DiskOffset, long ClusterSize, long ClusterCount, byte[] Bitmap)
    {
        public long DataLength => ClusterSize * ClusterCount;
    }

    private readonly List<Volume> _volumes = new();

    private AllocationMap() { }

    /// <summary>
    /// Reads the bitmaps from the shadow copies, so they match the snapshot that is copied.
    /// A volume whose bitmap can't be read is copied in full.
    /// </summary>
    public static AllocationMap Create(SourceSnapshot snapshot, Action<string> log)
    {
        var map = new AllocationMap();
        foreach (var region in snapshot.Regions)
        {
            try
            {
                var (clusterSize, clusterCount) = NativeDisk.GetNtfsClusterInfo(region.Shadow);
                if (clusterSize <= 0 || clusterSize * clusterCount > region.Length)
                    throw new InvalidDataException("the volume reports an unexpected size");

                byte[] bitmap = NativeDisk.GetVolumeBitmap(region.Shadow, clusterCount);
                map._volumes.Add(new Volume(region.DiskOffset, clusterSize, clusterCount, bitmap));
                long used = CountUsed(bitmap, clusterCount) * clusterSize;
                log($"{region.Volume} holds {DiskCloneEngine.FormatGB(used)} of data; its free space will be skipped.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                log($"{region.Volume} will be copied in full ({ex.Message}).");
            }
        }
        return map;
    }

    /// <summary>True only if the whole range lies in free clusters of one volume.</summary>
    public bool IsFree(long diskOffset, long length)
    {
        foreach (var volume in _volumes)
        {
            long start = diskOffset - volume.DiskOffset;
            long end = start + length;
            if (start < 0 || end > volume.DataLength) continue;
            return AllClear(volume.Bitmap, start / volume.ClusterSize, (end - 1) / volume.ClusterSize);
        }
        return false;
    }

    private static bool AllClear(byte[] bitmap, long firstBit, long lastBit)
    {
        long bit = firstBit;
        while (bit <= lastBit)
        {
            if ((bit & 7) == 0 && bit + 7 <= lastBit)
            {
                if (bitmap[bit >> 3] != 0) return false;
                bit += 8;
            }
            else
            {
                if ((bitmap[bit >> 3] & (1 << (int)(bit & 7))) != 0) return false;
                bit++;
            }
        }
        return true;
    }

    private static long CountUsed(byte[] bitmap, long clusterCount)
    {
        long fullBytes = clusterCount / 8;
        long used = 0;
        for (long i = 0; i < fullBytes; i++) used += BitOperations.PopCount(bitmap[i]);
        int tailBits = (int)(clusterCount % 8);
        if (tailBits > 0) used += BitOperations.PopCount((uint)(bitmap[fullBytes] & ((1 << tailBits) - 1)));
        return used;
    }
}
