using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Replicore.Services;

/// <summary>Thrown when a clone can't safely start. The message is shown to the user as-is.</summary>
public class CloneBlockedException(string message) : Exception(message);

/// <summary>
/// Freezes the mounted volumes of a source disk with Volume Shadow Copy (VSS), so a disk that is
/// in use — including the one Windows is running from — can be copied consistently. The clone
/// engine reads each snapshotted volume's byte range from its shadow copy and everything else
/// (partition tables, EFI and recovery partitions) from the raw disk.
/// </summary>
internal sealed class SourceSnapshot : IDisposable
{
    /// <summary>A byte range of the source disk that must be read from a shadow copy instead.</summary>
    public sealed record Region(string Volume, long DiskOffset, long Length, SafeFileHandle Shadow);

    private static readonly byte[] BitLockerSignature = Encoding.ASCII.GetBytes("-FVE-FS-");

    private readonly List<ManagementObject> _shadowCopies = new();
    public List<Region> Regions { get; } = new();

    private SourceSnapshot() { }

    /// <param name="driveLetters">Mounted volumes on the source disk, e.g. "C:".</param>
    public static SourceSnapshot Create(int sourceDiskIndex, SafeFileHandle sourceDisk, int sectorSize,
        IEnumerable<string> driveLetters, Action<string> log)
    {
        string windowsVolume = (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\").TrimEnd('\\');
        var snapshot = new SourceSnapshot();
        try
        {
            foreach (string letter in driveLetters.Select(l => l.Trim().TrimEnd('\\')).Where(l => l.Length > 0).Distinct())
            {
                bool isWindowsVolume = letter.Equals(windowsVolume, StringComparison.OrdinalIgnoreCase);

                List<(int DiskNumber, long StartingOffset, long Length)> extents;
                using (var volume = NativeDisk.OpenForQuery($@"\\.\{letter}"))
                    extents = NativeDisk.GetVolumeExtents(volume);

                var onSource = extents.Where(e => e.DiskNumber == sourceDiskIndex).ToList();
                if (onSource.Count == 0) continue;
                if (extents.Count != 1)
                    throw new CloneBlockedException($"Volume {letter} spans more than one disk, so it can't be cloned as part of a single disk.");

                var extent = onSource[0];
                if (IsBitLockerVolume(sourceDisk, sectorSize, extent.StartingOffset))
                {
                    throw new CloneBlockedException(
                        $"Volume {letter} is encrypted with BitLocker (Device Encryption). Replicore can't make a consistent " +
                        "copy of an encrypted volume while it's in use.\n\n" +
                        "Turn off BitLocker or Device Encryption for this drive, wait for decryption to finish, then clone again.");
                }

                try
                {
                    var (shadow, device) = CreateShadowCopy(letter);
                    snapshot._shadowCopies.Add(shadow);
                    var handle = NativeDisk.OpenForRead(device);
                    long length = Math.Min(extent.Length, NativeDisk.GetLength(handle));
                    snapshot.Regions.Add(new Region(letter, extent.StartingOffset, length, handle));
                    log($"Created a shadow copy of {letter} so it can be copied while in use.");
                }
                catch (Exception ex) when (ex is not CloneBlockedException)
                {
                    if (isWindowsVolume)
                        throw new CloneBlockedException($"Couldn't create a shadow copy of the Windows volume {letter}: {ex.Message}\n\n" +
                                                        "Without it the copy wouldn't be consistent, so the clone was not started.");

                    // FAT/exFAT volumes don't support VSS; they're copied directly instead.
                    log($"WARNING: Couldn't create a shadow copy of {letter} ({ex.Message}). It will be copied directly - don't change files on it during the clone.");
                }
            }
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    /// <summary>BitLocker replaces the volume boot sector's OEM ID with "-FVE-FS-" on disk.</summary>
    private static bool IsBitLockerVolume(SafeFileHandle sourceDisk, int sectorSize, long volumeOffset)
    {
        var sector = new AlignedBuffer(sectorSize);
        RandomAccess.Read(sourceDisk, sector.Span(sectorSize), volumeOffset);
        return sector.Span(sectorSize).Slice(3, 8).SequenceEqual(BitLockerSignature);
    }

    private static (ManagementObject Shadow, string DevicePath) CreateShadowCopy(string letter)
    {
        using var shadowClass = new ManagementClass(@"root\cimv2", "Win32_ShadowCopy", null);
        using var input = shadowClass.GetMethodParameters("Create");
        input["Volume"] = letter + "\\";
        input["Context"] = "ClientAccessible";

        using var output = shadowClass.InvokeMethod("Create", input, null);
        uint result = Convert.ToUInt32(output["ReturnValue"]);
        if (result != 0) throw new IOException($"VSS returned error {result}");

        string id = (string)output["ShadowID"];
        using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_ShadowCopy WHERE ID='{id}'");
        foreach (ManagementObject shadow in searcher.Get())
            return (shadow, (string)shadow["DeviceObject"]);

        throw new IOException("The new shadow copy could not be found.");
    }

    public void Dispose()
    {
        foreach (var region in Regions) region.Shadow.Dispose();
        foreach (var shadow in _shadowCopies)
        {
            try { shadow.Delete(); } catch { /* best effort; Windows also cleans up old shadow copies */ }
            shadow.Dispose();
        }
    }
}
