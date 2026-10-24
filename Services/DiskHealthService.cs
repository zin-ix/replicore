using System.Management;
using Replicore.Models;

namespace Replicore.Services;

/// <summary>
/// Reads drive health from the Windows Storage Management API: the drive's own health and
/// predictive-failure flags (MSFT_PhysicalDisk) and its reliability counters
/// (MSFT_StorageReliabilityCounter), which is how Windows exposes S.M.A.R.T. data.
/// SATA and NVMe drives report most counters; many USB enclosures report none.
/// </summary>
public static class DiskHealthService
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    private const int HotTemperatureC = 60;
    private const int WornOutPercent = 90;

    /// <summary>Health for each disk, keyed by disk number.</summary>
    /// <param name="win32Statuses">Win32_DiskDrive.Status per disk number, e.g. "OK" or "Pred Fail".</param>
    public static Dictionary<int, DiskHealth> GetHealth(IReadOnlyDictionary<int, string> win32Statuses)
    {
        var physicalDisks = new Dictionary<int, ManagementObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNamespace, "SELECT * FROM MSFT_PhysicalDisk");
            foreach (ManagementObject disk in searcher.Get())
            {
                // For disks outside a Storage Space, DeviceId is the disk number
                if (int.TryParse(disk["DeviceId"]?.ToString(), out int number)) physicalDisks[number] = disk;
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Storage API unavailable: fall back to the Win32 status alone
        }

        return win32Statuses.ToDictionary(s => s.Key, s => Evaluate(physicalDisks.GetValueOrDefault(s.Key), s.Value));
    }

    private static DiskHealth Evaluate(ManagementObject? disk, string win32Status)
    {
        var level = HealthLevel.Unknown;
        var issues = new List<string>();
        var details = new List<HealthDetail>();

        void Raise(HealthLevel to, string? issue = null)
        {
            if (to > level) level = to;
            if (issue != null && !issues.Contains(issue)) issues.Add(issue);
        }

        const string predictsFailure = "The drive predicts it will fail soon (S.M.A.R.T.).";
        if (win32Status.Equals("OK", StringComparison.OrdinalIgnoreCase)) Raise(HealthLevel.Healthy);
        else if (win32Status.Equals("Pred Fail", StringComparison.OrdinalIgnoreCase)) Raise(HealthLevel.Critical, predictsFailure);
        else if (!string.IsNullOrWhiteSpace(win32Status)) Raise(HealthLevel.Warning, $"Windows reports the drive status as \"{win32Status}\".");

        if (disk == null) return new DiskHealth { Level = level, Issues = issues, Details = details };

        switch (Number(disk, "HealthStatus"))
        {
            case 0: Raise(HealthLevel.Healthy); break;
            case 1: Raise(HealthLevel.Warning, "Windows reports a warning for this drive."); break;
            case 2: Raise(HealthLevel.Critical, "Windows reports this drive as unhealthy."); break;
        }

        // OperationalStatus: 3 Degraded, 5 Predictive Failure, 6 Error, 7 Non-Recoverable Error
        if (disk["OperationalStatus"] is ushort[] statuses)
        {
            if (statuses.Contains((ushort)5)) Raise(HealthLevel.Critical, predictsFailure);
            if (statuses.Contains((ushort)6) || statuses.Contains((ushort)7)) Raise(HealthLevel.Critical, "The drive reports a hardware error.");
            if (statuses.Contains((ushort)3)) Raise(HealthLevel.Warning, "The drive reports degraded operation.");
        }

        string mediaType = Number(disk, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
        if (mediaType.Length > 0) details.Add(new HealthDetail("Type", mediaType));

        try
        {
            foreach (ManagementObject counter in disk.GetRelated("MSFT_StorageReliabilityCounter"))
            {
                using (counter) AddCounters(counter, mediaType, details, Raise);
                break;
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Some drivers (often USB bridges) don't support reliability counters
        }

        return new DiskHealth { Level = level, Issues = issues, Details = details };
    }

    private static void AddCounters(ManagementObject counter, string mediaType, List<HealthDetail> details,
        Action<HealthLevel, string?> raise)
    {
        if (Number(counter, "Temperature") is > 0 and var temperature)
        {
            details.Add(new HealthDetail("Temperature", $"{temperature} °C"));
            if (temperature >= HotTemperatureC) raise(HealthLevel.Warning, $"Running hot ({temperature} °C).");
        }

        // Wear is the percentage of rated write endurance used; only meaningful for SSDs
        if (mediaType != "HDD" && Number(counter, "Wear") is { } wear)
        {
            details.Add(new HealthDetail("Life used", $"{wear}%"));
            if (wear >= WornOutPercent) raise(HealthLevel.Warning, $"The SSD has used {wear}% of its rated write endurance.");
        }

        if (Number(counter, "PowerOnHours") is { } hours)
            details.Add(new HealthDetail("Powered on", $"{hours:N0} hours ({hours / 24:N0} days)"));

        long? readErrors = Number(counter, "ReadErrorsUncorrected");
        long? writeErrors = Number(counter, "WriteErrorsUncorrected");
        if (readErrors.HasValue || writeErrors.HasValue)
        {
            long total = (readErrors ?? 0) + (writeErrors ?? 0);
            details.Add(new HealthDetail("Unrecoverable errors", $"{total:N0}"));
            if (total > 0) raise(HealthLevel.Warning, $"The drive has reported {total:N0} unrecoverable read/write error(s).");
        }
    }

    private static long? Number(ManagementBaseObject obj, string property)
    {
        try
        {
            return obj[property] is { } value ? Convert.ToInt64(value) : null;
        }
        catch (ManagementException)
        {
            return null; // property not present on this Windows version
        }
    }
}
