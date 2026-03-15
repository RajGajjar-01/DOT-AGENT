using Dapper;
using Microsoft.Data.Sqlite;
using DotAgent.Models;

namespace DotAgent.Data;

public class Database
{
    private readonly string _connectionString;

    public Database()
    {
        var appDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(appDir, "data");
        var defaultDb = Path.Combine(dataDir, "agent.db");
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? defaultDb;

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
            CREATE INDEX IF NOT EXISTS idx_messages_session
                ON messages(session_id);
            CREATE INDEX IF NOT EXISTS idx_executions_session
                ON executions(session_id);
        """);
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
}