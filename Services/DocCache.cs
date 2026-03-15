using Microsoft.Data.Sqlite;
using Dapper;

namespace DotAgent.Services;

/// <summary>
/// Caches Context7/Tavily documentation results to reduce external API latency.
/// Key: (provider, query/libraryId) → cached response with TTL.
/// </summary>
public class DocCache
{
    private readonly string _connectionString;
    private const int DefaultTtlHours = 24; // Cache docs for 24 hours

    public DocCache()
    {
        var workspace = Environment.GetEnvironmentVariable("WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        var defaultDb = Path.Combine(workspace, "agent.db");
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? defaultDb;

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
            CREATE TABLE IF NOT EXISTS doc_cache (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                provider    TEXT    NOT NULL,
                query_key   TEXT    NOT NULL,
                response    TEXT    NOT NULL,
                created_at  INTEGER NOT NULL,
                expires_at  INTEGER NOT NULL,
                UNIQUE(provider, query_key)
            );
            CREATE INDEX IF NOT EXISTS idx_doc_cache_lookup ON doc_cache(provider, query_key);
        """);
    }

    /// <summary>
    /// Try to get a cached response. Returns null if not found or expired.
    /// </summary>
    public string? Get(string provider, string queryKey)
    {
        using var db = Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        var cached = db.QuerySingleOrDefault<(string response, long expires_at)?>(
            "SELECT response, expires_at FROM doc_cache WHERE provider = @Provider AND query_key = @QueryKey",
            new { Provider = provider, QueryKey = NormalizeKey(queryKey) });
        
        if (cached is null) return null;
        
        var (response, expiresAt) = cached.Value;
        
        // Check if expired
        if (expiresAt < now)
        {
            // Delete expired entry
            db.Execute("DELETE FROM doc_cache WHERE provider = @Provider AND query_key = @QueryKey",
                new { Provider = provider, QueryKey = NormalizeKey(queryKey) });
            return null;
        }
        
        return response;
    }

    /// <summary>
    /// Store a response in cache with optional TTL override.
    /// </summary>
    public void Set(string provider, string queryKey, string response, int? ttlHours = null)
    {
        using var db = Open();
        
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ttl = ttlHours ?? DefaultTtlHours;
        var expiresAt = now + (ttl * 3600);
        
        db.Execute("""
            INSERT INTO doc_cache (provider, query_key, response, created_at, expires_at)
            VALUES (@Provider, @QueryKey, @Response, @CreatedAt, @ExpiresAt)
            ON CONFLICT(provider, query_key) DO UPDATE SET
                response = excluded.response,
                created_at = excluded.created_at,
                expires_at = excluded.expires_at
        """, new
        {
            Provider = provider,
            QueryKey = NormalizeKey(queryKey),
            Response = response,
            CreatedAt = now,
            ExpiresAt = expiresAt
        });
    }

    /// <summary>
    /// Get or fetch with caching. Uses the provided fetch function if cache miss.
    /// </summary>
    public async Task<string> GetOrFetchAsync(
        string provider,
        string queryKey,
        Func<Task<string>> fetch,
        int? ttlHours = null)
    {
        var cached = Get(provider, queryKey);
        if (cached != null)
            return cached;

        var response = await fetch();
        Set(provider, queryKey, response, ttlHours);
        return response;
    }

    /// <summary>
    /// Clear expired entries from cache.
    /// </summary>
    public int ClearExpired()
    {
        using var db = Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return db.Execute("DELETE FROM doc_cache WHERE expires_at < @Now", new { Now = now });
    }

    /// <summary>
    /// Clear all cached entries for a provider.
    /// </summary>
    public int ClearProvider(string provider)
    {
        using var db = Open();
        return db.Execute("DELETE FROM doc_cache WHERE provider = @Provider", new { Provider = provider });
    }

    private static string NormalizeKey(string key)
    {
        // Normalize: lowercase, trim, collapse whitespace
        return key.ToLowerInvariant().Trim();
    }
}
