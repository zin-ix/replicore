using System.Xml.Linq;
using Replicore.Models;

namespace Replicore.Services;

/// <summary>Next run and state of a schedule's task, as Task Scheduler reports it.</summary>
public record ScheduledTaskStatus(bool Exists, DateTime? NextRun, bool IsRunning, bool IsDisabled);

/// <summary>
/// Registers Replicore's weekly tasks through the Task Scheduler COM API.
///
/// Why COM instead of schtasks.exe: schtasks /create can't turn on "run as soon as possible after a
/// missed start", "wake the computer" or "run on battery", and its /query output is localized, so
/// it can't be parsed reliably on a non-English Windows. The task XML sets all of those settings.
/// </summary>
public class TaskSchedulerService
{
    private const string FolderPath = @"\Replicore";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonServiceAccount = 5;
    private const int TaskStateDisabled = 1;
    private const int TaskStateRunning = 4;

    // Tasks from earlier versions, which used schtasks with fixed names in the root folder
    private static readonly string[] LegacyTaskNames =
        new[] { "Replicore_Backup" }.Concat(Enumerable.Range(0, 25).Select(i => $"Replicore_Backup_Disk{i}")).ToArray();

    /// <summary>
    /// True for "task or folder doesn't exist". .NET maps those HRESULTs to FileNotFoundException and
    /// DirectoryNotFoundException rather than COMException, so they're matched by code.
    /// </summary>
    private static bool IsNotFound(Exception ex) =>
        ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);

    private static dynamic Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        return service;
    }

    private static dynamic GetOrCreateFolder(dynamic service)
    {
        try { return service.GetFolder(FolderPath); }
        catch (Exception ex) when (IsNotFound(ex)) { return service.GetFolder(@"\").CreateFolder("Replicore"); }
    }

    /// <summary>Creates or replaces the weekly task that runs "Replicore.exe --run-schedule {id}" as SYSTEM.</summary>
    public void Register(BackupSchedule schedule)
    {
        dynamic service = Connect();
        dynamic folder = GetOrCreateFolder(service);
        string name = schedule.TaskPath[(FolderPath.Length + 1)..];
        folder.RegisterTask(name, BuildTaskXml(schedule), TaskCreateOrUpdate, "SYSTEM", null, TaskLogonServiceAccount, null);
    }

    public void Delete(BackupSchedule schedule)
    {
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder(FolderPath);
            folder.DeleteTask(schedule.TaskPath[(FolderPath.Length + 1)..], 0);
        }
        catch (Exception ex) when (IsNotFound(ex)) { /* Already gone */ }
    }

    /// <summary>Starts the task now, exactly as the schedule would (as SYSTEM, in the background).</summary>
    public void RunNow(BackupSchedule schedule)
    {
        dynamic service = Connect();
        dynamic task = service.GetFolder(FolderPath).GetTask(schedule.TaskPath[(FolderPath.Length + 1)..]);
        task.Run(null);
    }

    public ScheduledTaskStatus GetStatus(BackupSchedule schedule)
    {
        try
        {
            dynamic service = Connect();
            dynamic task = service.GetFolder(FolderPath).GetTask(schedule.TaskPath[(FolderPath.Length + 1)..]);
            DateTime next = task.NextRunTime;
            int state = task.State;
            // Task Scheduler reports "no next run" as the OLE zero date (1899-12-30)
            return new ScheduledTaskStatus(true, next.Year < 2000 ? null : next, state == TaskStateRunning, state == TaskStateDisabled);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return new ScheduledTaskStatus(false, null, false, false);
        }
    }

    /// <summary>Deletes the fixed-name tasks earlier versions of Replicore created with schtasks.</summary>
    public void DeleteLegacyTasks()
    {
        dynamic service = Connect();
        dynamic root = service.GetFolder(@"\");
        foreach (string name in LegacyTaskNames)
        {
            try { root.DeleteTask(name, 0); }
            catch (Exception ex) when (IsNotFound(ex)) { /* Never created */ }
        }
    }

    private static string BuildTaskXml(BackupSchedule schedule)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var time = TimeSpan.Parse(schedule.Time);

        // The trigger needs a start date; the most recent matching weekday keeps the first run this week
        var today = DateTime.Today;
        var start = today.AddDays(-(((int)today.DayOfWeek - (int)schedule.Day + 7) % 7)).Add(time);

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Couldn't find Replicore.exe's path.");

        var xml = new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo",
                new XElement(ns + "Author", "Replicore"),
                new XElement(ns + "Description", $"{schedule.KindLabel}: {schedule.SourceSummary}. {schedule.WhenSummary}.")),
            new XElement(ns + "Triggers",
                new XElement(ns + "CalendarTrigger",
                    new XElement(ns + "StartBoundary", start.ToString("yyyy-MM-dd'T'HH:mm:ss")),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "ScheduleByWeek",
                        new XElement(ns + "DaysOfWeek", new XElement(ns + schedule.Day.ToString())),
                        new XElement(ns + "WeeksInterval", "1")))),
            new XElement(ns + "Principals",
                new XElement(ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(ns + "UserId", "S-1-5-18"), // SYSTEM
                    new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings",
                // Replicore serializes runs itself, so a second start while one is running is skipped
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(ns + "StopIfGoingOnBatteries", "false"),
                // Run after a missed start (PC was off) and wake the PC from sleep for the run
                new XElement(ns + "StartWhenAvailable", "true"),
                new XElement(ns + "WakeToRun", "true"),
                new XElement(ns + "ExecutionTimeLimit", "PT24H"),
                new XElement(ns + "IdleSettings",
                    new XElement(ns + "StopOnIdleEnd", "false"),
                    new XElement(ns + "RestartOnIdle", "false")),
                new XElement(ns + "Enabled", "true")),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(ns + "Exec",
                    new XElement(ns + "Command", exe),
                    new XElement(ns + "Arguments", $"--run-schedule {schedule.Id}"))));

        return xml.ToString();
    }
}
