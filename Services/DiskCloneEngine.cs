using System.IO;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Replicore.Services;

/// <summary>One disk being written by <see cref="DiskCloneEngine"/>, plus its outcome.</summary>
public sealed class CloneTarget(int diskIndex, string deviceId, IProgress<CloneProgressEventArgs> progress)
{
    public int DiskIndex { get; } = diskIndex;
    public string DeviceId { get; } = deviceId;
    public IProgress<CloneProgressEventArgs> Progress { get; } = progress;

    /// <summary>True once the copy (and verification, if requested) finished and the disk was finalized.</summary>
    public bool Succeeded { get; internal set; }
    public bool Verified { get; internal set; }
    /// <summary>Why this target failed; null while it is still healthy.</summary>
    public string? Error { get; internal set; }

    internal SafeFileHandle? Handle;
    internal long Length;
    internal bool IsActive => Error == null;
}

/// <summary>
/// Clones one physical disk to several target disks at once.
///
/// - The source is read once and each chunk is written to every target in parallel, so adding
///   targets doesn't multiply the load on the source disk.
/// - Mounted source volumes are read from Volume Shadow Copy snapshots, so a disk in use
///   (including the Windows disk) is copied consistently.
/// - The first and last chunks (which hold the partition tables) are written last, so a cancelled
///   or failed target is left looking empty rather than half-valid.
/// - Optionally re-reads every target from the device and compares it with the source.
/// - On a larger target, the GPT backup table is moved to the real end of the disk.
/// </summary>
public sealed class DiskCloneEngine(CloneService cloneService)
{
    private const int ChunkSize = 8 * 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    // diskpart relies on the Virtual Disk Service, which fails when several instances run at once
    private readonly SemaphoreSlim _diskpartLock = new(1, 1);

    public async Task CloneAsync(int sourceIndex, string sourceDeviceId, IReadOnlyList<string> sourceDriveLetters,
        IReadOnlyList<CloneTarget> targets, bool verify, IProgress<string> log, CancellationToken token)
    {
        using var source = NativeDisk.OpenForRead(sourceDeviceId);
        long sourceLength = NativeDisk.GetLength(source);
        int sectorSize = NativeDisk.GetSectorSize(source);
        log.Report($"Source Disk {sourceIndex} is {FormatGB(sourceLength)} with {sectorSize}-byte sectors.");

        // Snapshot the source before touching any target: if this is blocked, nothing gets erased.
        using var snapshot = await Task.Run(() =>
            SourceSnapshot.Create(sourceIndex, source, sectorSize, sourceDriveLetters, log.Report), token);

        try
        {
            await Task.WhenAll(targets.Select(t => PrepareTargetAsync(t, sourceLength, sectorSize, token)));
            var active = targets.Where(t => t.IsActive).ToList();
            if (active.Count == 0) return;

            var reader = new SourceReader(source, snapshot);
            await Task.Run(() => CopyPass(reader, sourceLength, active, token), token);

            if (verify && active.Any(t => t.IsActive))
                await Task.Run(() => VerifyPass(reader, sourceLength, active, token), token);

            foreach (var target in active.Where(t => t.IsActive))
                FinalizeTarget(target, sourceLength, sectorSize, verify);
        }
        finally
        {
            foreach (var target in targets) target.Handle?.Dispose();
        }
    }

    /// <summary>Erases the target with diskpart, opens it for writing and checks it can hold the source.</summary>
    private async Task PrepareTargetAsync(CloneTarget target, long sourceLength, int sectorSize, CancellationToken token)
    {
        try
        {
            Report(target, "Erasing", null, "Erasing the partition table with diskpart...");
            bool erased;
            await _diskpartLock.WaitAsync(token);
            try
            {
                erased = await cloneService.CleanDiskAsync(target.DiskIndex, target.Progress, token);
            }
            finally
            {
                _diskpartLock.Release();
            }
            if (!erased)
            {
                Fail(target, "diskpart couldn't erase the disk, so it wasn't written to.");
                return;
            }
            // Give Windows a moment to drop the old volumes before opening the raw disk
            await Task.Delay(3000, token);

            target.Handle = NativeDisk.OpenForWrite(target.DeviceId);
            target.Length = NativeDisk.GetLength(target.Handle);
            int targetSectorSize = NativeDisk.GetSectorSize(target.Handle);

            if (targetSectorSize != sectorSize)
                Fail(target, $"Its sector size ({targetSectorSize} bytes) differs from the source ({sectorSize} bytes), so a sector-by-sector copy wouldn't be usable.");
            else if (target.Length < sourceLength)
                Fail(target, $"It's {FormatGB(sourceLength - target.Length)} smaller than the source.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Fail(target, ex.Message);
        }
    }

    /// <summary>Order chunks so the first (primary partition table) and last (GPT backup) are written at the end.</summary>
    private static IEnumerable<long> WriteOrder(long chunkCount)
    {
        for (long i = 1; i < chunkCount - 1; i++) yield return i;
        if (chunkCount > 1) yield return chunkCount - 1;
        yield return 0;
    }

    private static void CopyPass(SourceReader reader, long sourceLength, List<CloneTarget> targets, CancellationToken token)
    {
        long chunkCount = (sourceLength + ChunkSize - 1) / ChunkSize;
        var order = WriteOrder(chunkCount).ToList();
        var buffers = new[] { new AlignedBuffer(ChunkSize), new AlignedBuffer(ChunkSize) };

        int LengthOf(long chunk) => (int)Math.Min(ChunkSize, sourceLength - chunk * ChunkSize);

        foreach (var target in targets) Report(target, "Cloning", 0, "Copying sectors...");

        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long bytesDone = 0;

        reader.Read(order[0] * ChunkSize, buffers[0], LengthOf(order[0]));

        for (int i = 0; i < order.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            long chunk = order[i];
            long offset = chunk * ChunkSize;
            int length = LengthOf(chunk);
            var current = buffers[i % 2];

            // Read the next chunk while this one is being written
            Task? nextRead = null;
            if (i + 1 < order.Count)
            {
                long nextChunk = order[i + 1];
                var nextBuffer = buffers[(i + 1) % 2];
                nextRead = Task.Run(() => reader.Read(nextChunk * ChunkSize, nextBuffer, LengthOf(nextChunk)), token);
            }

            Parallel.ForEach(targets.Where(t => t.IsActive), target =>
            {
                try
                {
                    RandomAccess.Write(target.Handle!, current.Span(length), offset);
                }
                catch (Exception ex)
                {
                    Fail(target, $"Write failed at {FormatGB(offset)}: {ex.Message}");
                }
            });

            nextRead?.GetAwaiter().GetResult();

            bytesDone += length;
            if (!targets.Any(t => t.IsActive)) return;

            if (stopwatch.Elapsed - lastReport >= ProgressInterval || i == order.Count - 1)
            {
                lastReport = stopwatch.Elapsed;
                ReportThroughput(targets, "Cloning", bytesDone, sourceLength, stopwatch.Elapsed);
            }
        }

        foreach (var target in targets.Where(t => t.IsActive))
        {
            try { NativeDisk.Flush(target.Handle!); }
            catch (Exception ex) { Fail(target, ex.Message); }
        }
    }

    /// <summary>Re-reads every target straight from the device and compares it with the source.</summary>
    private static void VerifyPass(SourceReader reader, long sourceLength, List<CloneTarget> targets, CancellationToken token)
    {
        var verifying = targets.Where(t => t.IsActive).ToList();
        var readers = verifying.ToDictionary(t => t, t => (Handle: NativeDisk.OpenForRead(t.DeviceId), Buffer: new AlignedBuffer(ChunkSize)));
        try
        {
            foreach (var target in verifying) Report(target, "Verifying", 0, "Verifying the copy against the source...");

            var sourceBuffer = new AlignedBuffer(ChunkSize);
            var stopwatch = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            for (long offset = 0; offset < sourceLength; offset += ChunkSize)
            {
                token.ThrowIfCancellationRequested();
                int length = (int)Math.Min(ChunkSize, sourceLength - offset);
                reader.Read(offset, sourceBuffer, length);

                Parallel.ForEach(verifying.Where(t => t.IsActive), target =>
                {
                    try
                    {
                        var (handle, buffer) = readers[target];
                        SourceReader.ReadExact(handle, buffer.Span(length), offset);
                        if (!buffer.Span(length).SequenceEqual(sourceBuffer.Span(length)))
                            Fail(target, $"Verification failed: data at {FormatGB(offset)} doesn't match the source.");
                    }
                    catch (Exception ex)
                    {
                        Fail(target, $"Verification read failed at {FormatGB(offset)}: {ex.Message}");
                    }
                });

                if (!verifying.Any(t => t.IsActive)) return;

                if (stopwatch.Elapsed - lastReport >= ProgressInterval || offset + length >= sourceLength)
                {
                    lastReport = stopwatch.Elapsed;
                    ReportThroughput(verifying, "Verifying", offset + length, sourceLength, stopwatch.Elapsed);
                }
            }
        }
        finally
        {
            foreach (var (handle, _) in readers.Values) handle.Dispose();
        }
    }

    /// <summary>Fixes the GPT on larger targets and asks Windows to re-read the new partition table.</summary>
    private static void FinalizeTarget(CloneTarget target, long sourceLength, int sectorSize, bool verified)
    {
        try
        {
            Report(target, "Finalizing", 100, "Finalizing the partition table...");
            long extra = target.Length - sourceLength;
            if (extra > 0)
            {
                bool moved = GptRepair.RelocateBackupToEnd(target.Handle!, sectorSize, target.Length);
                if (moved) Report(target, "Finalizing", 100, "Moved the GPT backup table to the end of the larger disk.");
                if (extra >= 1024L * 1024 * 1024)
                    Report(target, "Finalizing", 100, $"{FormatGB(extra)} at the end of the disk is unallocated. Extend a partition in Disk Management to use it.");
            }
            NativeDisk.Flush(target.Handle!);
            NativeDisk.UpdateDiskProperties(target.Handle!);

            target.Verified = verified;
            target.Succeeded = true;
        }
        catch (Exception ex)
        {
            Fail(target, $"Finalizing failed: {ex.Message}");
        }
    }

    private static void ReportThroughput(IEnumerable<CloneTarget> targets, string phase, long done, long total, TimeSpan elapsed)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        double bytesPerSecond = done / seconds;
        var args = new CloneProgressEventArgs
        {
            Phase = phase,
            PercentComplete = (int)(done * 100 / total),
            SpeedMB = bytesPerSecond / (1024 * 1024),
            EtaSeconds = bytesPerSecond > 0 ? (total - done) / bytesPerSecond : 0
        };
        foreach (var target in targets.Where(t => t.IsActive)) target.Progress.Report(args);
    }

    private static void Report(CloneTarget target, string phase, int? percent, string message) =>
        target.Progress.Report(new CloneProgressEventArgs { Phase = phase, PercentComplete = percent, Message = message });

    private static void Fail(CloneTarget target, string error)
    {
        lock (target)
        {
            if (target.Error != null) return;
            target.Error = error;
        }
        target.Progress.Report(new CloneProgressEventArgs { Phase = "Failed", Message = $"ERROR: {error}" });
    }

    private static string FormatGB(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
        : $"{bytes / (1024.0 * 1024):0.#} MB";

    /// <summary>Reads source bytes, taking snapshotted volume ranges from their shadow copies.</summary>
    private sealed class SourceReader(SafeFileHandle source, SourceSnapshot snapshot)
    {
        public void Read(long offset, AlignedBuffer destination, int length)
        {
            int done = 0;
            while (done < length)
            {
                long position = offset + done;
                int remaining = length - done;

                var region = snapshot.Regions.FirstOrDefault(r => position >= r.DiskOffset && position < r.DiskOffset + r.Length);
                if (region != null)
                {
                    int count = (int)Math.Min(remaining, region.DiskOffset + region.Length - position);
                    ReadInto(region.Shadow, destination, done, count, position - region.DiskOffset);
                    done += count;
                }
                else
                {
                    long nextRegion = snapshot.Regions.Where(r => r.DiskOffset > position)
                        .Select(r => r.DiskOffset).DefaultIfEmpty(long.MaxValue).Min();
                    int count = (int)Math.Min(remaining, nextRegion - position);
                    ReadInto(source, destination, done, count, position);
                    done += count;
                }
            }
        }

        /// <summary>
        /// Unbuffered reads need a 4 KB-aligned memory address. A read that starts mid-buffer at an
        /// unaligned spot (where a volume boundary isn't 4 KB-aligned) goes through a bounce buffer.
        /// </summary>
        private AlignedBuffer? _bounce;

        private void ReadInto(SafeFileHandle handle, AlignedBuffer destination, int destinationOffset, int count, long deviceOffset)
        {
            if (destinationOffset % 4096 == 0)
            {
                ReadExact(handle, destination.Span(destinationOffset + count)[destinationOffset..], deviceOffset);
                return;
            }
            _bounce ??= new AlignedBuffer(ChunkSize);
            ReadExact(handle, _bounce.Span(count), deviceOffset);
            _bounce.Span(count).CopyTo(destination.Span(destinationOffset + count)[destinationOffset..]);
        }

        public static void ReadExact(SafeFileHandle handle, Span<byte> buffer, long offset)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = RandomAccess.Read(handle, buffer[total..], offset + total);
                if (read == 0) throw new EndOfStreamException($"Unexpected end of device at {offset + total} bytes.");
                total += read;
            }
        }
    }
}
