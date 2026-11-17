using System.IO;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Replicore.Services;

/// <summary>
/// Finalizes a cloned target disk's partition table:
/// - Assigns a new unique Disk ID (GPT Disk GUID, GPT partition GUIDs and MBR Disk Signature) so
///   the target disk never collides with the source disk while both are connected, and updates matching
///   GUID references inside the target's EFI System Partition (BCD) so it stays bootable.
/// - On a larger target disk, moves the GPT backup header and partition array to the real end of the
///   target and updates both headers (and the protective MBR) so Windows and UEFI see the full disk.
/// </summary>
internal static class GptRepair
{
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("EFI PART");
    private static readonly Guid EfiSystemPartitionType = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    private static readonly byte[] RegfSignature = Encoding.ASCII.GetBytes("regf");

    /// <summary>True if the disk has a GPT primary header at LBA 1.</summary>
    public static bool IsGpt(SafeFileHandle disk, int sectorSize)
    {
        var sector = new AlignedBuffer(sectorSize);
        RandomAccess.Read(disk, sector.Span(sectorSize), sectorSize);
        return sector.Span(sectorSize)[..8].SequenceEqual(Signature);
    }

    /// <summary>
    /// Assigns a new unique Disk ID to <paramref name="disk"/> and, for GPT disks on a larger target,
    /// relocates the backup GPT table to the end of <paramref name="diskLength"/> bytes.
    /// </summary>
    public static (bool IdUpdated, bool BackupMoved) FinalizeDisk(SafeFileHandle disk, int sectorSize, long diskLength)
    {
        long lastLba = diskLength / sectorSize - 1;

        // --- Read and check the primary GPT header at LBA 1 ---
        var headerBuf = new AlignedBuffer(sectorSize);
        var header = headerBuf.Span(sectorSize);
        RandomAccess.Read(disk, header, sectorSize);
        if (!header[..8].SequenceEqual(Signature))
        {
            bool mbrUpdated = UpdateMbrDiskSignature(disk, sectorSize);
            return (mbrUpdated, false);
        }

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (headerSize < 92 || headerSize > sectorSize)
            throw new InvalidDataException($"GPT header size {headerSize} is invalid.");

        long oldAlternateLba = BinaryPrimitives.ReadInt64LittleEndian(header[32..]);
        long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(header[72..]);
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(header[80..]);
        int entrySize = BinaryPrimitives.ReadInt32LittleEndian(header[84..]);

        if (oldAlternateLba > lastLba)
            throw new InvalidDataException("The GPT describes a disk larger than the target.");

        bool relocate = oldAlternateLba < lastLba;
        long entriesBytes = (long)entryCount * entrySize;
        int entriesSectors = (int)((entriesBytes + sectorSize - 1) / sectorSize);
        long newBackupEntriesLba = lastLba - entriesSectors;
        long newLastUsableLba = newBackupEntriesLba - 1;

        // --- Read the partition entry array ---
        var entriesBuf = new AlignedBuffer(entriesSectors * sectorSize);
        var entries = entriesBuf.Span(entriesSectors * sectorSize);
        RandomAccess.Read(disk, entries, entriesLba * sectorSize);

        // --- Assign a new Disk GUID and Partition UniqueGUIDs to avoid signature collision with the source ---
        var guidMap = new List<(byte[] OldGuid, byte[] NewGuid)>();
        var efiPartitions = new List<(long Offset, long Length)>();
        var efiTypeBytes = EfiSystemPartitionType.ToByteArray();

        var oldDiskGuid = header.Slice(56, 16).ToArray();
        var newDiskGuid = Guid.NewGuid().ToByteArray();
        newDiskGuid.CopyTo(header.Slice(56, 16));
        if (oldDiskGuid.AsSpan().ContainsAnyExcept((byte)0))
            guidMap.Add((oldDiskGuid, newDiskGuid));

        if (entrySize >= 128)
        {
            for (int i = 0; i < entryCount; i++)
            {
                var entry = entries.Slice(i * entrySize, entrySize);
                var typeGuid = entry[..16];
                if (!typeGuid.ContainsAnyExcept((byte)0)) continue;

                var oldPartGuid = entry.Slice(16, 16).ToArray();
                var newPartGuid = Guid.NewGuid().ToByteArray();
                newPartGuid.CopyTo(entry.Slice(16, 16));
                if (oldPartGuid.AsSpan().ContainsAnyExcept((byte)0))
                    guidMap.Add((oldPartGuid, newPartGuid));

                if (typeGuid.SequenceEqual(efiTypeBytes))
                {
                    long startLba = BinaryPrimitives.ReadInt64LittleEndian(entry[32..]);
                    long endLba = BinaryPrimitives.ReadInt64LittleEndian(entry[40..]);
                    if (startLba > 1 && endLba >= startLba)
                        efiPartitions.Add((startLba * sectorSize, (endLba - startLba + 1) * sectorSize));
                }
            }
        }

        uint entriesCrc = Crc32.Compute(entries[..(int)entriesBytes]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[88..], entriesCrc);

        // --- Primary header: points at the backup location and usable area ---
        BinaryPrimitives.WriteInt64LittleEndian(header[32..], lastLba);             // AlternateLBA
        if (relocate)
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

        // Write updated partition entries and headers
        RandomAccess.Write(disk, entries, entriesLba * sectorSize);
        RandomAccess.Write(disk, entries, newBackupEntriesLba * sectorSize);
        RandomAccess.Write(disk, backup, lastLba * sectorSize);

        // Only erase the stale header if it doesn't overlap the backup array just written
        // (possible when the target is only a few sectors larger than the source).
        if (relocate && oldAlternateLba < newBackupEntriesLba)
        {
            var zeroBuf = new AlignedBuffer(sectorSize);
            zeroBuf.Span(sectorSize).Clear();
            RandomAccess.Write(disk, zeroBuf.Span(sectorSize), oldAlternateLba * sectorSize);
        }

        RandomAccess.Write(disk, header, sectorSize);

        UpdateProtectiveMbr(disk, sectorSize, lastLba, relocate);

        foreach (var (offset, length) in efiPartitions)
            UpdateEfiBootGuids(disk, sectorSize, diskLength, offset, length, guidMap);

        return (true, relocate);
    }

    /// <summary>Updates the protective MBR's disk signature and (when relocated) grows its 0xEE entry.</summary>
    private static void UpdateProtectiveMbr(SafeFileHandle disk, int sectorSize, long lastLba, bool relocate)
    {
        var mbrBuf = new AlignedBuffer(sectorSize);
        var mbr = mbrBuf.Span(sectorSize);
        RandomAccess.Read(disk, mbr, 0);

        if (mbr[510] != 0x55 || mbr[511] != 0xAA) return;

        WriteNewMbrSignature(mbr);

        const int firstEntry = 446;
        if (relocate && mbr[firstEntry + 4] == 0xEE)
        {
            uint sizeInLba = (uint)Math.Min(lastLba, uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(mbr[(firstEntry + 12)..], sizeInLba);
        }

        RandomAccess.Write(disk, mbr, 0);
    }

    /// <summary>Assigns a new 32-bit disk signature at offset 440 (0x1B8) on a legacy MBR disk.</summary>
    private static bool UpdateMbrDiskSignature(SafeFileHandle disk, int sectorSize)
    {
        var mbrBuf = new AlignedBuffer(sectorSize);
        var mbr = mbrBuf.Span(sectorSize);
        RandomAccess.Read(disk, mbr, 0);

        if (mbr[510] != 0x55 || mbr[511] != 0xAA) return false;

        WriteNewMbrSignature(mbr);
        RandomAccess.Write(disk, mbr, 0);
        return true;
    }

    private static void WriteNewMbrSignature(Span<byte> mbr)
    {
        uint oldSig = BinaryPrimitives.ReadUInt32LittleEndian(mbr[440..]);
        uint newSig;
        Span<byte> bytes = stackalloc byte[4];
        do
        {
            RandomNumberGenerator.Fill(bytes);
            newSig = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        } while (newSig == 0 || newSig == oldSig);

        BinaryPrimitives.WriteUInt32LittleEndian(mbr[440..], newSig);
    }

    /// <summary>
    /// Updates the disk and partition GUIDs stored inside the target's EFI System Partition (such as
    /// \EFI\Microsoft\Boot\BCD) so the cloned disk boots with its new unique GPT GUIDs.
    /// </summary>
    private static void UpdateEfiBootGuids(SafeFileHandle disk, int sectorSize, long diskLength,
        long partitionOffset, long partitionLength, List<(byte[] OldGuid, byte[] NewGuid)> guidMap)
    {
        if (guidMap.Count == 0 || partitionOffset < sectorSize * 2) return;
        if (partitionOffset + partitionLength > diskLength || partitionLength > 1024L * 1024 * 1024) return;

        const int scanChunkSize = 4 * 1024 * 1024;
        var buffer = new AlignedBuffer(scanChunkSize);

        // Consecutive windows overlap by one sector, so a GUID that straddles a window edge is
        // always wholly inside the next window. (FAT file data isn't 4 KB-aligned to the partition.)
        for (long done = 0; done < partitionLength; done += scanChunkSize - sectorSize)
        {
            int count = (int)Math.Min(scanChunkSize, partitionLength - done);
            count -= count % sectorSize;
            if (count < 16) break;

            var span = buffer.Span(count);
            SafeRead(disk, span, partitionOffset + done);

            if (ReplaceGuids(span, sectorSize, guidMap))
                RandomAccess.Write(disk, span, partitionOffset + done);

            if (done + count >= partitionLength) break;
        }
    }

    /// <summary>
    /// Replaces every old GUID with its new one, except inside the 512-byte base block of a registry
    /// hive ("regf" at a sector boundary), whose checksum would otherwise break. Returns true if changed.
    /// </summary>
    internal static bool ReplaceGuids(Span<byte> data, int sectorSize, List<(byte[] OldGuid, byte[] NewGuid)> guidMap)
    {
        bool modified = false;
        foreach (var (oldGuid, newGuid) in guidMap)
        {
            int from = 0;
            while (from <= data.Length - 16)
            {
                int match = data[from..].IndexOf(oldGuid);
                if (match < 0) break;
                int at = from + match;
                int blockStart = at - at % sectorSize;
                bool inHiveHeader = blockStart + 4 <= data.Length && data.Slice(blockStart, 4).SequenceEqual(RegfSignature)
                                    && at + 16 <= blockStart + 512;
                if (!inHiveHeader)
                {
                    newGuid.CopyTo(data.Slice(at, 16));
                    modified = true;
                }
                from = at + 16;
            }
        }
        return modified;
    }

    private static void SafeRead(SafeFileHandle disk, Span<byte> buffer, long offset)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(disk, buffer[total..], offset + total);
            if (read == 0) throw new EndOfStreamException($"Unexpected end of disk at {offset + total} bytes.");
            total += read;
        }
    }

    private static void WriteHeaderCrc(Span<byte> header, int headerSize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32.Compute(header[..headerSize]));
    }

    internal static void SelfCheck()
    {
        string tmpGpt = Path.Combine(Path.GetTempPath(), $"replicore_gpt_{Guid.NewGuid():N}.img");
        string tmpShadow = Path.Combine(Path.GetTempPath(), $"replicore_shadow_{Guid.NewGuid():N}.img");
        string tmpSource = Path.Combine(Path.GetTempPath(), $"replicore_source_{Guid.NewGuid():N}.img");
        try
        {
            // 1. Verify GPT Disk ID / Partition GUID regeneration, BCD sync, and backup relocation
            const int sectorSize = 512;
            const int sourceLen = 8 * 1024 * 1024;
            const int targetLen = 10 * 1024 * 1024;
            var raw = new byte[targetLen];

            uint oldMbrSig = 0x12345678;
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(440, 4), oldMbrSig);
            raw[446 + 4] = 0xEE;
            raw[510] = 0x55;
            raw[511] = 0xAA;

            var oldDiskGuid = Guid.NewGuid();
            var oldEfiGuid = Guid.NewGuid();
            var oldOsGuid = Guid.NewGuid();
            long oldLastLba = sourceLen / sectorSize - 1;

            Signature.CopyTo(raw.AsSpan(512));
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(512 + 12, 4), 92);
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(512 + 24, 8), 1);
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(512 + 32, 8), oldLastLba);
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(512 + 40, 8), 34);
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(512 + 48, 8), oldLastLba - 33);
            oldDiskGuid.ToByteArray().CopyTo(raw.AsSpan(512 + 56, 16));
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(512 + 72, 8), 2);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(512 + 80, 4), 128);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(512 + 84, 4), 128);

            EfiSystemPartitionType.ToByteArray().CopyTo(raw.AsSpan(1024, 16));
            oldEfiGuid.ToByteArray().CopyTo(raw.AsSpan(1024 + 16, 16));
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(1024 + 32, 8), 2048);
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(1024 + 40, 8), 4095);

            new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7").ToByteArray().CopyTo(raw.AsSpan(1024 + 128, 16));
            oldOsGuid.ToByteArray().CopyTo(raw.AsSpan(1024 + 128 + 16, 16));
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(1024 + 128 + 32, 8), 4096);
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(1024 + 128 + 40, 8), 8191);

            int efiByteOffset = 2048 * sectorSize;
            RegfSignature.CopyTo(raw.AsSpan(efiByteOffset));
            oldDiskGuid.ToByteArray().CopyTo(raw.AsSpan(efiByteOffset + 64, 16)); // inside 512B regf header
            oldDiskGuid.ToByteArray().CopyTo(raw.AsSpan(efiByteOffset + 4096 + 32, 16));
            oldEfiGuid.ToByteArray().CopyTo(raw.AsSpan(efiByteOffset + 4096 + 48, 16));
            oldOsGuid.ToByteArray().CopyTo(raw.AsSpan(efiByteOffset + 4096 + 64, 16));

            File.WriteAllBytes(tmpGpt, raw);
            using (var fs = File.Open(tmpGpt, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                var (idUpdated, backupMoved) = FinalizeDisk(fs.SafeFileHandle, sectorSize, targetLen);
                if (!idUpdated || !backupMoved) throw new InvalidOperationException("FinalizeDisk flags mismatch.");
            }

            var after = File.ReadAllBytes(tmpGpt);
            uint newMbrSig = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(440, 4));
            var newDiskGuid = new Guid(after.AsSpan(512 + 56, 16));
            var newEfiGuid = new Guid(after.AsSpan(1024 + 16, 16));
            var newOsGuid = new Guid(after.AsSpan(1024 + 128 + 16, 16));
            long newLastLba = targetLen / sectorSize - 1;
            var backupDiskGuid = new Guid(after.AsSpan((int)(newLastLba * sectorSize + 56), 16));

            if (newMbrSig == oldMbrSig || newMbrSig == 0) throw new InvalidOperationException("MBR signature not updated.");
            if (newDiskGuid == oldDiskGuid || backupDiskGuid != newDiskGuid) throw new InvalidOperationException("GPT DiskGUID not updated.");
            if (newEfiGuid == oldEfiGuid || newOsGuid == oldOsGuid) throw new InvalidOperationException("Partition GUIDs not updated.");
            if (new Guid(after.AsSpan(efiByteOffset + 64, 16)) != oldDiskGuid) throw new InvalidOperationException("regf base header should stay untouched.");
            if (new Guid(after.AsSpan(efiByteOffset + 4096 + 32, 16)) != newDiskGuid) throw new InvalidOperationException("BCD DiskGUID not synced.");
            if (new Guid(after.AsSpan(efiByteOffset + 4096 + 48, 16)) != newEfiGuid) throw new InvalidOperationException("BCD EFI GUID not synced.");
            if (new Guid(after.AsSpan(efiByteOffset + 4096 + 64, 16)) != newOsGuid) throw new InvalidOperationException("BCD OS GUID not synced.");

            // 1b. GUIDs straddling a 4 KB page edge and a 4 MB scan-window edge must still be replaced
            var big = new byte[6 * 1024 * 1024];
            var oldA = Guid.NewGuid().ToByteArray();
            var newA = Guid.NewGuid().ToByteArray();
            oldA.CopyTo(big, 1024 + 4096 - 7);
            oldA.CopyTo(big, 1024 + 4 * 1024 * 1024 - 9);
            string tmpBig = Path.Combine(Path.GetTempPath(), $"replicore_esp_{Guid.NewGuid():N}.img");
            try
            {
                File.WriteAllBytes(tmpBig, big);
                using (var fs = File.Open(tmpBig, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    UpdateEfiBootGuids(fs.SafeFileHandle, sectorSize, big.Length, 1024, big.Length - 1024, [(oldA, newA)]);
                var esp = File.ReadAllBytes(tmpBig);
                if (!esp.AsSpan(1024 + 4096 - 7, 16).SequenceEqual(newA)) throw new InvalidOperationException("Page-straddling GUID not replaced.");
                if (!esp.AsSpan(1024 + 4 * 1024 * 1024 - 9, 16).SequenceEqual(newA)) throw new InvalidOperationException("Window-straddling GUID not replaced.");
            }
            finally
            {
                if (File.Exists(tmpBig)) File.Delete(tmpBig);
            }

            // 2. Verify 99% shadow-copy cluster-boundary EOF fallback to raw source disk
            var shadowBytes = new byte[61440]; // 4 KB shorter than the 65536-byte partition extent
            var sourceBytes = new byte[65536];
            Array.Fill(shadowBytes, (byte)0xAA);
            Array.Fill(sourceBytes, (byte)0xBB);
            File.WriteAllBytes(tmpShadow, shadowBytes);
            File.WriteAllBytes(tmpSource, sourceBytes);

            using (var shadowFs = File.Open(tmpShadow, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sourceFs = File.Open(tmpSource, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var snap = new SourceSnapshot();
                snap.Regions.Add(new SourceSnapshot.Region("C:", 0, 65536, shadowFs.SafeFileHandle));
                var reader = new DiskCloneEngine.SourceReader(sourceFs.SafeFileHandle, snap);
                var dest = new AlignedBuffer(65536);
                reader.Read(0, dest, 0, 65536);
                var span = dest.Span(65536);
                if (span[..61440].ContainsAnyExcept((byte)0xAA)) throw new InvalidOperationException("Shadow data mismatch.");
                if (span[61440..].ContainsAnyExcept((byte)0xBB)) throw new InvalidOperationException("Tail fallback data mismatch.");
            }
        }
        finally
        {
            if (File.Exists(tmpGpt)) File.Delete(tmpGpt);
            if (File.Exists(tmpShadow)) File.Delete(tmpShadow);
            if (File.Exists(tmpSource)) File.Delete(tmpSource);
        }
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
