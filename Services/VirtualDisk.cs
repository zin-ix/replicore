using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Replicore.Services;

/// <summary>
/// A new dynamic .vhdx file, attached as a disk without drive letters so the clone engine can
/// write to it like any physical target. A dynamic VHDX only stores the blocks that were written,
/// so with smart copy the file ends up about the size of the source's data.
/// Disposing detaches it; the file can then be opened in File Explorer, Hyper-V or Disk Management.
/// </summary>
internal sealed partial class VirtualDisk : IDisposable
{
    private const int VIRTUAL_STORAGE_TYPE_DEVICE_VHDX = 3;
    private static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47E9-901F-71415A66345B");
    private const int ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER = 0x2;
    // Smallest VHDX block size, so skipped free space doesn't allocate whole 32 MB blocks
    private const uint BlockSize = 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public int DeviceId;
        public Guid VendorId;
    }

    // CREATE_VIRTUAL_DISK_PARAMETERS, version 2. The union after Version is 8-byte aligned;
    // the size is padded so newer, larger versions of the struct are covered too.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct CreateParameters
    {
        [FieldOffset(0)] public int Version;
        [FieldOffset(8)] public Guid UniqueId;
        [FieldOffset(24)] public ulong MaximumSize;
        [FieldOffset(32)] public uint BlockSizeInBytes;
        [FieldOffset(36)] public uint SectorSizeInBytes;
        [FieldOffset(40)] public uint PhysicalSectorSizeInBytes;
    }

    // ATTACH_VIRTUAL_DISK_PARAMETERS, version 1 (Version + a reserved field in an 8-byte-aligned union)
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct AttachParameters
    {
        [FieldOffset(0)] public int Version;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateVirtualDisk(ref VirtualStorageType virtualStorageType, string path, int accessMask,
        IntPtr securityDescriptor, int flags, int providerSpecificFlags, ref CreateParameters parameters,
        IntPtr overlapped, out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern int AttachVirtualDisk(SafeFileHandle handle, IntPtr securityDescriptor, int flags,
        int providerSpecificFlags, ref AttachParameters parameters, IntPtr overlapped);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern int GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref int diskPathSizeInBytes, StringBuilder diskPath);

    [DllImport("virtdisk.dll")]
    private static extern int DetachVirtualDisk(SafeFileHandle handle, int flags, int providerSpecificFlags);

    [GeneratedRegex(@"(\d+)$")]
    private static partial Regex TrailingNumber();

    private readonly SafeFileHandle _handle;

    /// <summary>The attached disk's device path, e.g. \\.\PhysicalDrive5.</summary>
    public string PhysicalPath { get; }
    public int DiskIndex { get; }

    private VirtualDisk(SafeFileHandle handle, string physicalPath, int diskIndex)
    {
        _handle = handle;
        PhysicalPath = physicalPath;
        DiskIndex = diskIndex;
    }

    /// <summary>Creates an empty image exactly the size of the source disk, with the same sector size, and attaches it.</summary>
    public static VirtualDisk CreateFor(string sourceDeviceId, string path)
    {
        long size;
        int sectorSize;
        using (var source = NativeDisk.OpenForRead(sourceDeviceId))
        {
            size = NativeDisk.GetLength(source);
            sectorSize = NativeDisk.GetSectorSize(source);
        }

        var storageType = new VirtualStorageType { DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_VHDX, VendorId = VendorMicrosoft };
        var parameters = new CreateParameters
        {
            Version = 2,
            UniqueId = Guid.NewGuid(),
            MaximumSize = (ulong)size,
            BlockSizeInBytes = BlockSize,
            SectorSizeInBytes = (uint)sectorSize,
            PhysicalSectorSizeInBytes = 4096
        };
        // Version 2 requires VIRTUAL_DISK_ACCESS_NONE (0); flags 0 makes a dynamically expanding disk
        int error = CreateVirtualDisk(ref storageType, path, 0, IntPtr.Zero, 0, 0, ref parameters, IntPtr.Zero, out var handle);
        if (error != 0) throw new IOException($"Couldn't create {path}: {new Win32Exception(error).Message}", error);

        try
        {
            var attach = new AttachParameters { Version = 1 };
            error = AttachVirtualDisk(handle, IntPtr.Zero, ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER, 0, ref attach, IntPtr.Zero);
            if (error != 0) throw new IOException($"Couldn't attach {path}: {new Win32Exception(error).Message}", error);

            var builder = new StringBuilder(260);
            int bytes = builder.Capacity * sizeof(char);
            error = GetVirtualDiskPhysicalPath(handle, ref bytes, builder);
            if (error != 0) throw new IOException($"Couldn't find the attached disk for {path}: {new Win32Exception(error).Message}", error);

            string physicalPath = builder.ToString();
            var match = TrailingNumber().Match(physicalPath);
            if (!match.Success) throw new IOException($"Unexpected disk path {physicalPath} for {path}.");

            WaitUntilOpenable(physicalPath);
            return new VirtualDisk(handle, physicalPath, int.Parse(match.Value));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>The disk device can take a moment to appear after attaching.</summary>
    private static void WaitUntilOpenable(string physicalPath)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                NativeDisk.OpenForQuery(physicalPath).Dispose();
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    public void Dispose()
    {
        DetachVirtualDisk(_handle, 0, 0);
        _handle.Dispose();
    }
}
