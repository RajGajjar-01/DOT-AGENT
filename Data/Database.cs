using Dapper;
using Microsoft.Data.Sqlite;
using DotAgent.Models;

namespace DotAgent.Data;

public class Database
{
    private readonly string _connectionString;

    public Database()
    {
        var workspace = Environment.GetEnvironmentVariable("WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        var defaultDb = Path.Combine(workspace, "agent.db");
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? defaultDb;

        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var db = Open();
        db.Execute("""
            CREATE TABLE IF NOT EXISTS sessions (
                id         TEXT    PRIMARY KEY,
                title      TEXT    NOT NULL DEFAULT 'New session',
                status     TEXT    NOT NULL DEFAULT 'active',
                plan       TEXT    NOT NULL DEFAULT '',
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT    NOT NULL REFERENCES sessions(id),
                role       TEXT    NOT NULL,
                content    TEXT    NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS executions (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id  TEXT    NOT NULL REFERENCES sessions(id),
                command     TEXT    NOT NULL,
                output      TEXT    NOT NULL DEFAULT '',
                exit_code   INTEGER NOT NULL DEFAULT 0,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                created_at  INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS file_changes (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id  TEXT    NOT NULL REFERENCES sessions(id),
                file_path   TEXT    NOT NULL,
                action      TEXT    NOT NULL DEFAULT 'created',
                summary     TEXT    NOT NULL DEFAULT '',
                created_at  INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS planned_steps (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id  TEXT    NOT NULL REFERENCES sessions(id),
                step_number INTEGER NOT NULL,
                file_path   TEXT    NOT NULL,
                action      TEXT    NOT NULL DEFAULT 'create',
                description TEXT    NOT NULL DEFAULT '',
                status      TEXT    NOT NULL DEFAULT 'pending',
                updated_at  INTEGER NOT NULL,
                created_at  INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_messages_session
                ON messages(session_id);
            CREATE INDEX IF NOT EXISTS idx_executions_session
                ON executions(session_id);
            CREATE INDEX IF NOT EXISTS idx_file_changes_session
                ON file_changes(session_id);
            CREATE INDEX IF NOT EXISTS idx_planned_steps_session
                ON planned_steps(session_id);
        """);

        // Migration: add plan column if it doesn't exist
        try { db.Execute("ALTER TABLE sessions ADD COLUMN plan TEXT NOT NULL DEFAULT ''"); }
        catch { /* column already exists */ }
    }

    // ── Sessions ──────────────────────────────────────────────────

    public Session CreateSession(string title)
    {
        var s = new Session { Title = title };
        using var db = Open();
        db.Execute("""
            INSERT INTO sessions (id, title, status, created_at, updated_at)
            VALUES (@Id, @Title, @Status, @CreatedAt, @UpdatedAt)
        """, s);
        return s;
    }

    public IEnumerable<Session> ListSessions(int limit = 20)
    {
        using var db = Open();
        return db.Query<Session>(
            "SELECT * FROM sessions ORDER BY updated_at DESC LIMIT @limit",
            new { limit }).ToList();
    }

    public void TouchSession(string id)
    {
        using var db = Open();
        db.Execute(
            "UPDATE sessions SET updated_at = @now WHERE id = @id",
            new { id, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    public void UpdateSessionStatus(string id, string status)
    {
        using var db = Open();
        db.Execute(
            "UPDATE sessions SET status = @status, updated_at = @now WHERE id = @id",
            new { id, status, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    public void SavePlan(string id, string plan)
    {
        using var db = Open();
        db.Execute(
            "UPDATE sessions SET plan = @plan, updated_at = @now WHERE id = @id",
            new { id, plan, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    public string GetPlan(string id)
    {
        using var db = Open();
        return db.QueryFirstOrDefault<string>(
            "SELECT plan FROM sessions WHERE id = @id", new { id }) ?? "";
    }

    // ── Messages ──────────────────────────────────────────────────

    public void SaveMessage(Message m)
    {
        using var db = Open();
        db.Execute("""
            INSERT INTO messages (session_id, role, content, created_at)
            VALUES (@SessionId, @Role, @Content, @CreatedAt)
        """, m);
    }

    public IEnumerable<Message> GetMessages(string sessionId)
    {
        using var db = Open();
        return db.Query<Message>(
            "SELECT * FROM messages WHERE session_id = @sessionId ORDER BY id",
            new { sessionId }).ToList();
    }

    // ── Executions ────────────────────────────────────────────────

    public void SaveExecution(Execution e)
    {
        using var db = Open();
        db.Execute("""
            INSERT INTO executions (session_id, command, output, exit_code, duration_ms, created_at)
            VALUES (@SessionId, @Command, @Output, @ExitCode, @DurationMs, @CreatedAt)
        """, e);
    }

    // ── File Changes ──────────────────────────────────────────────

    public void SaveFileChange(FileChange fc)
    {
        using var db = Open();
        db.Execute("""
            INSERT INTO file_changes (session_id, file_path, action, summary, created_at)
            VALUES (@SessionId, @FilePath, @Action, @Summary, @CreatedAt)
        """, fc);
    }

    public IEnumerable<FileChange> GetFileChanges(string sessionId)
    {
        using var db = Open();
        return db.Query<FileChange>(
            "SELECT * FROM file_changes WHERE session_id = @sessionId ORDER BY id",
            new { sessionId }).ToList();
    }

    // ── Planned Steps ──────────────────────────────────────────────

    public void ClearPlannedSteps(string sessionId)
    {
        using var db = Open();
        db.Execute("DELETE FROM planned_steps WHERE session_id = @sessionId", new { sessionId });
    }

    public void SavePlannedStep(PlannedStep step)
    {
        using var db = Open();
        db.Execute("""
            INSERT INTO planned_steps (session_id, step_number, file_path, action, description, status, updated_at, created_at)
            VALUES (@SessionId, @StepNumber, @FilePath, @Action, @Description, @Status, @UpdatedAt, @CreatedAt)
        """, step);
    }

    public IEnumerable<PlannedStep> GetPlannedSteps(string sessionId)
    {
        using var db = Open();
        return db.Query<PlannedStep>(
            "SELECT * FROM planned_steps WHERE session_id = @sessionId ORDER BY step_number",
            new { sessionId }).ToList();
    }

    public void UpdatePlannedStepStatus(int stepId, string status)
    {
        using var db = Open();
        db.Execute(
            "UPDATE planned_steps SET status = @status, updated_at = @now WHERE id = @stepId",
            new { stepId, status, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    public PlannedStep? GetPlannedStepByPath(string sessionId, string filePath)
    {
        using var db = Open();
        return db.QueryFirstOrDefault<PlannedStep>(
            "SELECT * FROM planned_steps WHERE session_id = @sessionId AND file_path = @filePath",
            new { sessionId, filePath });
    }
}