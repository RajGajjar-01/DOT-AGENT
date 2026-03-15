using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DotAgent.Services;

public class ShellExecutor
{
    private readonly int _timeoutMs;
    private string _workingDirectory;
    private readonly DocCache _docCache;

    public string WorkingDirectory => _workingDirectory;

    public ShellExecutor(int timeoutSeconds = 120, DocCache? docCache = null)
    {
        _timeoutMs = timeoutSeconds * 1000;
        _workingDirectory = Environment.GetEnvironmentVariable("WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        _docCache = docCache ?? new DocCache();

        // Ensure workspace exists
        Directory.CreateDirectory(_workingDirectory);
    }

    public void SetWorkingDirectory(string path)
    {
        if (Directory.Exists(path))
            _workingDirectory = Path.GetFullPath(path);
    }

    public record ExecutionResult(
        string Output,
        int ExitCode,
        long DurationMs,
        bool TimedOut);

    public async Task<ExecutionResult> RunAsync(string command)
    {
        // Check if this is a cacheable doc command (Context7/Tavily)
        var cacheKey = ExtractDocCacheKey(command);
        if (cacheKey is { } ck)
        {
            var cached = _docCache.Get(ck.Provider, ck.Query);
            if (cached != null)
            {
                // Return cached result (simulate successful execution)
                return new ExecutionResult(
                    Output: $"[From cache] {cached}",
                    ExitCode: 0,
                    DurationMs: 0,
                    TimedOut: false);
            }
        }

        var sw = Stopwatch.StartNew();

        // Write the command to a temp script file to avoid escaping issues
        var scriptPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(scriptPath, command);

        var psi = new ProcessStartInfo
        {
            FileName               = "/bin/bash",
            Arguments              = scriptPath,
            WorkingDirectory       = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        // Pass through common env vars
        psi.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/root";
        psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
        psi.Environment["LANG"] = "en_US.UTF-8";

        // Pass Context7 API key if available
        var ctxKey = Environment.GetEnvironmentVariable("CONTEXT7_API_KEY");
        if (!string.IsNullOrWhiteSpace(ctxKey))
            psi.Environment["CONTEXT7_API_KEY"] = ctxKey;

        using var process = new Process { StartInfo = psi };

        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) output.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(_timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            sw.Stop();
            TryDeleteScript(scriptPath);
            return new ExecutionResult(
                Output: "Error: command timed out.",
                ExitCode: -1,
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: true);
        }

        sw.Stop();
        TryDeleteScript(scriptPath);

        var resultOutput = output.ToString().TrimEnd();
        var resultExitCode = process.ExitCode;

        // Cache successful doc commands
        if (cacheKey is { } ck2 && resultExitCode == 0 && !string.IsNullOrWhiteSpace(resultOutput))
        {
            _docCache.Set(ck2.Provider, ck2.Query, resultOutput);
        }

        return new ExecutionResult(
            Output: resultOutput,
            ExitCode: resultExitCode,
            DurationMs: sw.ElapsedMilliseconds,
            TimedOut: false);
    }

    private static void TryDeleteScript(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    // ── Doc command caching helpers ────────────────────────────────

    /// <summary>
    /// Extract cache key from doc commands (Context7/Tavily).
    /// Returns null if not a cacheable command.
    /// </summary>
    private static (string Provider, string Query)? ExtractDocCacheKey(string command)
    {
        // Context7 library search: npx -y @upstash/context7-mcp library <name> <query>
        var ctx7LibMatch = Regex.Match(command,
            @"context7-mcp\s+library\s+(\S+)\s+(.+?)(?:\s*$|\s*;|\s*&&|\s*\|\|)",
            RegexOptions.IgnoreCase);
        if (ctx7LibMatch.Success)
        {
            var libName = ctx7LibMatch.Groups[1].Value;
            var query = ctx7LibMatch.Groups[2].Value.Trim();
            return ("context7", $"library:{libName}:{query}");
        }

        // Context7 docs fetch: npx -y @upstash/context7-mcp docs <libraryId> <query>
        var ctx7DocsMatch = Regex.Match(command,
            @"context7-mcp\s+docs\s+(\S+)\s+(.+?)(?:\s*$|\s*;|\s*&&|\s*\|\|)",
            RegexOptions.IgnoreCase);
        if (ctx7DocsMatch.Success)
        {
            var libId = ctx7DocsMatch.Groups[1].Value;
            var query = ctx7DocsMatch.Groups[2].Value.Trim();
            return ("context7", $"docs:{libId}:{query}");
        }

        // Tavily search (if used): tavily search <query>
        if (command.Contains("tavily", StringComparison.OrdinalIgnoreCase))
        {
            var tavilyMatch = Regex.Match(command,
                @"tavily.*?search\s+""(.+?)""",
                RegexOptions.IgnoreCase);
            if (tavilyMatch.Success)
            {
                return ("tavily", tavilyMatch.Groups[1].Value);
            }
        }

        return null;
    }
}