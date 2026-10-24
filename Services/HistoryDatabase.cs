using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Replicore.Models;

namespace Replicore.Services;

/// <summary>
/// SQLite store for backup schedules and the history of every run, in C:\ProgramData\Replicore.
///
/// Both the app and the scheduled task (running as SYSTEM) use the same file, which is why it
/// lives in ProgramData rather than a user profile. The folder is limited to Administrators and
/// SYSTEM: scheduled runs execute whatever schedule is stored here as SYSTEM, so a standard user
/// who could edit the database could make SYSTEM copy any file anywhere.
/// </summary>
public class HistoryDatabase
{
    public static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Replicore");

    public static readonly string LogFolder = Path.Combine(DataFolder, "logs");

    private static readonly string DatabasePath = Path.Combine(DataFolder, "replicore.db");

    // Default Timeout: wait up to 30 s when the scheduled task and the app write at the same moment
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        DefaultTimeout = 30
    }.ToString();

    public HistoryDatabase()
    {
        EnsureDataFolder();
        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schedules (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                kind        TEXT NOT NULL,
                source      TEXT NOT NULL,
                volumes     TEXT NOT NULL,
                bootable    INTEGER NOT NULL,
                folders     TEXT NOT NULL,
                destination TEXT NOT NULL,
                day         INTEGER NOT NULL,
                time        TEXT NOT NULL,
                created_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runs (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                schedule_id INTEGER,
                kind        TEXT NOT NULL,
                source      TEXT NOT NULL,
                destination TEXT NOT NULL,
                started_at  TEXT NOT NULL,
                finished_at TEXT,
                status      TEXT NOT NULL,
                details     TEXT NOT NULL,
                log_path    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS runs_started ON runs (started_at DESC);
            """);
    }

    private static void EnsureDataFolder()
    {
        var dir = Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(LogFolder);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        dir.SetAccessControl(security);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    // Dates are stored as round-trip UTC strings so they sort correctly as text
    private static string ToText(DateTime value) => value.ToUniversalTime().ToString("O");
    private static DateTime FromText(string value) => DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    // ---------------- Schedules ----------------

    public long AddSchedule(BackupSchedule schedule)
    {
        using var connection = Open();
        using var command = Command(connection, """
            INSERT INTO schedules (kind, source, volumes, bootable, folders, destination, day, time, created_at)
            VALUES ($kind, $source, $volumes, $bootable, $folders, $destination, $day, $time, $created);
            SELECT last_insert_rowid();
            """,
            ("$kind", schedule.Kind.ToString()),
            ("$source", schedule.SourceLabel),
            ("$volumes", JsonSerializer.Serialize(schedule.Volumes)),
            ("$bootable", schedule.Bootable ? 1 : 0),
            ("$folders", JsonSerializer.Serialize(schedule.Folders)),
            ("$destination", schedule.Destination),
            ("$day", (int)schedule.Day),
            ("$time", schedule.Time),
            ("$created", ToText(DateTime.UtcNow)));
        schedule.Id = (long)command.ExecuteScalar()!;
        return schedule.Id;
    }

    public void DeleteSchedule(long id)
    {
        using var connection = Open();
        using var command = Command(connection, "DELETE FROM schedules WHERE id = $id", ("$id", id));
        command.ExecuteNonQuery();
    }

    public BackupSchedule? GetSchedule(long id) =>
        QuerySchedules("SELECT * FROM schedules WHERE id = $id", ("$id", id)).FirstOrDefault();

    public List<BackupSchedule> GetSchedules() => QuerySchedules("SELECT * FROM schedules ORDER BY id");

    private List<BackupSchedule> QuerySchedules(string sql, params (string, object?)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var list = new List<BackupSchedule>();
        while (reader.Read())
        {
            list.Add(new BackupSchedule
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Kind = Enum.Parse<ScheduleKind>(reader.GetString(reader.GetOrdinal("kind"))),
                SourceLabel = reader.GetString(reader.GetOrdinal("source")),
                Volumes = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("volumes"))) ?? new(),
                Bootable = reader.GetInt64(reader.GetOrdinal("bootable")) != 0,
                Folders = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("folders"))) ?? new(),
                Destination = reader.GetString(reader.GetOrdinal("destination")),
                Day = (DayOfWeek)reader.GetInt64(reader.GetOrdinal("day")),
                Time = reader.GetString(reader.GetOrdinal("time")),
                CreatedAt = FromText(reader.GetString(reader.GetOrdinal("created_at")))
            });
        }
        return list;
    }

    // ---------------- Runs ----------------

    /// <summary>Records a run as started and returns its id, to pass to <see cref="FinishRun"/>.</summary>
    public long StartRun(long? scheduleId, string kind, string source, string destination, string logPath = "")
    {
        using var connection = Open();
        using var command = Command(connection, """
            INSERT INTO runs (schedule_id, kind, source, destination, started_at, status, details, log_path)
            VALUES ($schedule, $kind, $source, $destination, $started, $status, '', $log);
            SELECT last_insert_rowid();
            """,
            ("$schedule", scheduleId),
            ("$kind", kind),
            ("$source", source),
            ("$destination", destination),
            ("$started", ToText(DateTime.UtcNow)),
            ("$status", RunStatus.Running.ToString()),
            ("$log", logPath));
        return (long)command.ExecuteScalar()!;
    }

    public void FinishRun(long runId, RunStatus status, string details)
    {
        using var connection = Open();
        using var command = Command(connection,
            "UPDATE runs SET finished_at = $finished, status = $status, details = $details WHERE id = $id",
            ("$finished", ToText(DateTime.UtcNow)),
            ("$status", status.ToString()),
            ("$details", details),
            ("$id", runId));
        command.ExecuteNonQuery();
    }

    public void SetRunLog(long runId, string logPath)
    {
        using var connection = Open();
        using var command = Command(connection, "UPDATE runs SET log_path = $log WHERE id = $id", ("$log", logPath), ("$id", runId));
        command.ExecuteNonQuery();
    }

    public List<BackupRun> GetRuns(int limit = 500) =>
        QueryRuns("SELECT * FROM runs ORDER BY started_at DESC LIMIT $limit", ("$limit", limit));

    /// <summary>The most recent run of each schedule, keyed by schedule id.</summary>
    public Dictionary<long, BackupRun> GetLastRunPerSchedule() =>
        QueryRuns("""
            SELECT * FROM runs r WHERE schedule_id IS NOT NULL AND started_at =
                (SELECT MAX(started_at) FROM runs WHERE schedule_id = r.schedule_id)
            """)
        .GroupBy(r => r.ScheduleId!.Value)
        .ToDictionary(g => g.Key, g => g.First());

    private List<BackupRun> QueryRuns(string sql, params (string, object?)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var list = new List<BackupRun>();
        while (reader.Read())
        {
            int finished = reader.GetOrdinal("finished_at");
            int schedule = reader.GetOrdinal("schedule_id");
            list.Add(new BackupRun
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                ScheduleId = reader.IsDBNull(schedule) ? null : reader.GetInt64(schedule),
                Kind = reader.GetString(reader.GetOrdinal("kind")),
                Source = reader.GetString(reader.GetOrdinal("source")),
                Destination = reader.GetString(reader.GetOrdinal("destination")),
                StartedAt = FromText(reader.GetString(reader.GetOrdinal("started_at"))),
                FinishedAt = reader.IsDBNull(finished) ? null : FromText(reader.GetString(finished)),
                Status = Enum.Parse<RunStatus>(reader.GetString(reader.GetOrdinal("status"))),
                Details = reader.GetString(reader.GetOrdinal("details")),
                LogPath = reader.GetString(reader.GetOrdinal("log_path"))
            });
        }
        return list;
    }
}
