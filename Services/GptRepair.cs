using System.IO;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Replicore.Services;

/// <summary>
/// After a raw clone onto a larger disk, a GPT disk's backup header still sits where the source
/// disk ended, and the primary header still describes the smaller disk. This moves the backup
/// partition table to the real end of the target and updates both headers (and the protective
/// MBR) so Windows and UEFI firmware see a valid GPT spanning the whole disk.
/// The extra space is left unallocated; partitions are not changed.
/// </summary>
internal static class GptRepair
{
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("EFI PART");

    /// <summary>True if the disk has a GPT primary header at LBA 1.</summary>
    public static bool IsGpt(SafeFileHandle disk, int sectorSize)
    {
        var sector = new AlignedBuffer(sectorSize);
        RandomAccess.Read(disk, sector.Span(sectorSize), sectorSize);
        return sector.Span(sectorSize)[..8].SequenceEqual(Signature);
    }

    /// <summary>
    /// Rewrites the GPT so it describes a disk of <paramref name="diskLength"/> bytes.
    /// Returns false (and changes nothing) if the disk isn't GPT or already matches.
    /// </summary>
    public static bool RelocateBackupToEnd(SafeFileHandle disk, int sectorSize, long diskLength)
    {
        long lastLba = diskLength / sectorSize - 1;

        // --- Read and validate the primary header ---
        var headerBuf = new AlignedBuffer(sectorSize);
        var header = headerBuf.Span(sectorSize);
        RandomAccess.Read(disk, header, sectorSize);
        if (!header[..8].SequenceEqual(Signature)) return false;

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (headerSize < 92 || headerSize > sectorSize)
            throw new InvalidDataException($"GPT header size {headerSize} is invalid.");

        long oldAlternateLba = BinaryPrimitives.ReadInt64LittleEndian(header[32..]);
        long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(header[72..]);
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(header[80..]);
        int entrySize = BinaryPrimitives.ReadInt32LittleEndian(header[84..]);

        if (oldAlternateLba == lastLba) return false; // already spans the disk
        if (oldAlternateLba > lastLba)
            throw new InvalidDataException("The GPT describes a disk larger than the target.");

        long entriesBytes = (long)entryCount * entrySize;
        int entriesSectors = (int)((entriesBytes + sectorSize - 1) / sectorSize);
        long newBackupEntriesLba = lastLba - entriesSectors;
        long newLastUsableLba = newBackupEntriesLba - 1;

        // --- Copy the partition entry array (unchanged, so its CRC stays valid) ---
        var entriesBuf = new AlignedBuffer(entriesSectors * sectorSize);
        var entries = entriesBuf.Span(entriesSectors * sectorSize);
        RandomAccess.Read(disk, entries, entriesLba * sectorSize);

        // --- Primary header: points at the new backup location and larger usable area ---
        BinaryPrimitives.WriteInt64LittleEndian(header[32..], lastLba);          // AlternateLBA
        BinaryPrimitives.WriteInt64LittleEndian(header[48..], newLastUsableLba); // LastUsableLBA
        WriteHeaderCrc(header, headerSize);

        // --- Backup header: mirror of the primary with its own location fields ---
        var backupBuf = new AlignedBuffer(sectorSize);
        var backup = backupBuf.Span(sectorSize);
        header.CopyTo(backup);
        BinaryPrimitives.WriteInt64LittleEndian(backup[24..], lastLba);             // MyLBA
        BinaryPrimitives.WriteInt64LittleEndian(backup[32..], 1);                   // AlternateLBA
        BinaryPrimitives.WriteInt64LittleEndian(backup[72..], newBackupEntriesLba); // PartitionEntryLBA
        WriteHeaderCrc(backup, headerSize);

        // Write the backup structures first, then erase the stale backup header, then the primary.
        RandomAccess.Write(disk, entries, newBackupEntriesLba * sectorSize);
        RandomAccess.Write(disk, backup, lastLba * sectorSize);

        // Only erase the stale header if it doesn't overlap the backup array just written
        // (possible when the target is only a few sectors larger than the source).
        if (oldAlternateLba < newBackupEntriesLba)
        {
            var zeroBuf = new AlignedBuffer(sectorSize);
            zeroBuf.Span(sectorSize).Clear();
            RandomAccess.Write(disk, zeroBuf.Span(sectorSize), oldAlternateLba * sectorSize);
        }

        RandomAccess.Write(disk, header, sectorSize);

        UpdateProtectiveMbr(disk, sectorSize, lastLba);
        return true;
    }

    /// <summary>Grows the protective MBR's 0xEE entry to cover the whole disk (capped at 32 bits).</summary>
    private static void UpdateProtectiveMbr(SafeFileHandle disk, int sectorSize, long lastLba)
    {
        var mbrBuf = new AlignedBuffer(sectorSize);
        var mbr = mbrBuf.Span(sectorSize);
        RandomAccess.Read(disk, mbr, 0);

        const int firstEntry = 446;
        if (mbr[510] != 0x55 || mbr[511] != 0xAA || mbr[firstEntry + 4] != 0xEE) return;

        uint sizeInLba = (uint)Math.Min(lastLba, uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr[(firstEntry + 12)..], sizeInLba);
        RandomAccess.Write(disk, mbr, 0);
    }

    private static void WriteHeaderCrc(Span<byte> header, int headerSize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32.Compute(header[..headerSize]));
    }
}

/// <summary>Standard CRC-32 (IEEE 802.3), as used by GPT headers and partition arrays.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
