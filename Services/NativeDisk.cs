using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Replicore.Services;

/// <summary>
/// Thin Win32 wrappers for raw disk access: opening physical drives, volumes and shadow copies,
/// and the handful of DeviceIoControl queries the clone engine needs.
/// </summary>
internal static class NativeDisk
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    private const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    private const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    private const int ERROR_MORE_DATA = 234;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        [Out] byte[]? lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    private static extern bool DeviceIoControlWithInput(
        SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, uint nInBufferSize,
        [Out] byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

    /// <summary>
    /// Opens a disk, volume or shadow copy for unbuffered reads, so verification reads come from
    /// the device itself rather than the Windows cache. Buffers must be sector-aligned.
    /// </summary>
    public static SafeFileHandle OpenForRead(string path) =>
        Open(path, GENERIC_READ, FILE_FLAG_NO_BUFFERING);

    /// <summary>Opens a physical disk for unbuffered, write-through writing.</summary>
    public static SafeFileHandle OpenForWrite(string path) =>
        Open(path, GENERIC_READ | GENERIC_WRITE, FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH);

    /// <summary>Opens a volume with no data access — enough for metadata queries like disk extents.</summary>
    public static SafeFileHandle OpenForQuery(string path) => Open(path, 0, 0);

    private static SafeFileHandle Open(string path, uint access, uint flags)
    {
        var handle = CreateFile(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Couldn't open {path} (Win32 error {error}).", error);
        }
        return handle;
    }

    /// <summary>Exact size in bytes of a disk, volume or shadow copy (unlike WMI's geometry-rounded size).</summary>
    public static long GetLength(SafeFileHandle handle)
    {
        var output = Query(handle, IOCTL_DISK_GET_LENGTH_INFO, 8);
        return BitConverter.ToInt64(output, 0);
    }

    /// <summary>Logical sector size in bytes (512 or 4096).</summary>
    public static int GetSectorSize(SafeFileHandle handle)
    {
        // DISK_GEOMETRY_EX starts with DISK_GEOMETRY: Cylinders(8) MediaType(4) TracksPerCylinder(4)
        // SectorsPerTrack(4) BytesPerSector(4) -> BytesPerSector is at offset 20.
        var output = Query(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, 256);
        return BitConverter.ToInt32(output, 20);
    }

    /// <summary>Physical disk extents backing a volume, e.g. "\\.\C:".</summary>
    public static List<(int DiskNumber, long StartingOffset, long Length)> GetVolumeExtents(SafeFileHandle volume)
    {
        // VOLUME_DISK_EXTENTS: NumberOfDiskExtents(4) + padding(4), then DISK_EXTENT[]:
        // DiskNumber(4) + padding(4) + StartingOffset(8) + ExtentLength(8) = 24 bytes each.
        var output = Query(volume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, 8 + 24 * 32);
        int count = BitConverter.ToInt32(output, 0);
        var extents = new List<(int, long, long)>();
        for (int i = 0; i < count; i++)
        {
            int at = 8 + i * 24;
            extents.Add((BitConverter.ToInt32(output, at), BitConverter.ToInt64(output, at + 8), BitConverter.ToInt64(output, at + 16)));
        }
        return extents;
    }

    /// <summary>Cluster size and count of an NTFS volume. Throws <see cref="IOException"/> if it isn't NTFS.</summary>
    public static (long ClusterSize, long ClusterCount) GetNtfsClusterInfo(SafeFileHandle volume)
    {
        // NTFS_VOLUME_DATA_BUFFER: TotalClusters is at offset 16, BytesPerCluster at offset 44
        var output = Query(volume, FSCTL_GET_NTFS_VOLUME_DATA, 128);
        return (BitConverter.ToUInt32(output, 44), BitConverter.ToInt64(output, 16));
    }

    /// <summary>
    /// A volume's allocation bitmap: one bit per cluster, set when the cluster is in use.
    /// Anything that couldn't be read stays marked as in use, so it is never skipped.
    /// </summary>
    public static byte[] GetVolumeBitmap(SafeFileHandle volume, long clusterCount)
    {
        var bitmap = new byte[(clusterCount + 7) / 8];
        Array.Fill(bitmap, (byte)0xFF);

        const int header = 16; // VOLUME_BITMAP_BUFFER: StartingLcn(8) + BitmapSize(8), then the bits
        var output = new byte[header + 4 * 1024 * 1024];
        var input = new byte[8];
        long startingLcn = 0;
        while (startingLcn < clusterCount)
        {
            BitConverter.TryWriteBytes(input, startingLcn);
            bool done = DeviceIoControlWithInput(volume, FSCTL_GET_VOLUME_BITMAP, input, (uint)input.Length,
                output, (uint)output.Length, out uint returned, IntPtr.Zero);
            if (!done)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ERROR_MORE_DATA) throw new IOException($"Reading the volume bitmap failed (Win32 error {error}).", error);
            }

            // The returned StartingLcn is rounded down to a multiple of 8, so it maps to a whole byte
            long returnedStart = BitConverter.ToInt64(output, 0);
            long filled = returned >= header ? returned - header : output.Length - header;
            int bytes = (int)Math.Min(filled, bitmap.Length - returnedStart / 8);
            if (bytes <= 0) break;
            Array.Copy(output, header, bitmap, returnedStart / 8, bytes);
            startingLcn = returnedStart + bytes * 8L;
            if (done) break;
        }
        return bitmap;
    }

    /// <summary>Tells Windows to re-read a disk's partition table after it was written raw.</summary>
    public static void UpdateDiskProperties(SafeFileHandle disk)
    {
        DeviceIoControl(disk, IOCTL_DISK_UPDATE_PROPERTIES, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero);
    }

    public static void Flush(SafeFileHandle handle)
    {
        if (!FlushFileBuffers(handle))
            throw new IOException($"Flushing the disk failed (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    private static byte[] Query(SafeFileHandle handle, uint code, int outputSize)
    {
        var output = new byte[outputSize];
        if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, output, (uint)output.Length, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException($"Disk query 0x{code:X} failed (Win32 error {error}).", error);
        }
        return output;
    }
}

/// <summary>
/// A pinned byte buffer whose start is aligned to 4 KB, as required for unbuffered
/// (FILE_FLAG_NO_BUFFERING) disk I/O on both 512-byte and 4K-sector drives.
/// </summary>
internal sealed class AlignedBuffer
{
    private const int Alignment = 4096;
    private readonly byte[] _raw;
    private readonly int _offset;

    public int Length { get; }

    public AlignedBuffer(int length)
    {
        _raw = GC.AllocateUninitializedArray<byte>(length + Alignment, pinned: true);
        long address = Marshal.UnsafeAddrOfPinnedArrayElement(_raw, 0).ToInt64();
        _offset = (int)((Alignment - address % Alignment) % Alignment);
        Length = length;
    }

    public Span<byte> Span(int length) => _raw.AsSpan(_offset, length);
}
