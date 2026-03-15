using System.Text.RegularExpressions;

namespace DotAgent.Services;

/// <summary>
/// Provides Context7 documentation tools by delegating to the ctx7 CLI.
/// The agent outputs XML-like tags that are parsed and executed via the CLI.
/// </summary>
public class Context7Tools
{
    private readonly ShellExecutor _shell;
    private readonly DocCache _docCache;

    public Context7Tools(DocCache docCache, ShellExecutor? shell = null)
    {
        _docCache = docCache;
        _shell = shell ?? new ShellExecutor();
    }

    // ── Result type ──────────────────────────────────────────────

    public record Context7Result(string ToolName, bool Success, string Output);

    // ── Parse and Execute ────────────────────────────────────────

    public static bool HasContext7Tool(string output) =>
        Regex.IsMatch(output, @"<context7_(resolve|query)\b", RegexOptions.IgnoreCase);

    public async Task<(Context7Result result, string remainingOutput)> ParseAndExecuteAsync(string output)
    {
        // Try XML tag format first: <context7_resolve library="..." query="..." />
        var tagMatch = Regex.Match(output,
            @"<(context7_(?:resolve|query))\b([^>]*)/?>" ,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        string toolName;
        string attrs;
        string remaining;

        if (tagMatch.Success)
        {
            toolName = tagMatch.Groups[1].Value.ToLowerInvariant();
            attrs = tagMatch.Groups[2].Value;
            remaining = output.Remove(tagMatch.Index, tagMatch.Length);
        }
        else
        {
            // Fallback: bare command in bash block (no angle brackets)
            // e.g. context7_resolve library="fastapi" query="auth"
            var bareMatch = Regex.Match(output,
                @"(context7_(?:resolve|query))\s+(.*?)(?:\n|$)",
                RegexOptions.IgnoreCase);

            if (!bareMatch.Success)
                return (new Context7Result("unknown", false, "No Context7 tool command found"), output);

            toolName = bareMatch.Groups[1].Value.ToLowerInvariant();
            attrs = bareMatch.Groups[2].Value;
            remaining = output.Remove(bareMatch.Index, bareMatch.Length);
        }

        var library = ExtractAttr(attrs, "library") ?? ExtractAttr(attrs, "name");
        var libraryId = ExtractAttr(attrs, "libraryId") ?? library;
        var query = ExtractAttr(attrs, "query") ?? "";

        if (toolName == "context7_resolve")
        {
            if (string.IsNullOrWhiteSpace(library))
                return (new Context7Result(toolName, false, "ERROR: Missing 'library' attribute."), output);
            var result = await ResolveLibraryAsync(library, query);
            return (result, remaining);
        }

        if (toolName == "context7_query")
        {
            if (string.IsNullOrWhiteSpace(libraryId))
                return (new Context7Result(toolName, false, "ERROR: Missing 'libraryId' attribute."), output);
            if (string.IsNullOrWhiteSpace(query))
                return (new Context7Result(toolName, false, "ERROR: Missing 'query' attribute."), output);
            var result = await QueryDocsAsync(libraryId, query);
            return (result, remaining);
        }

        return (new Context7Result("unknown", false, "Unknown Context7 tool."), output);
    }

    // ── CLI-based implementations ────────────────────────────────

    public async Task<Context7Result> ResolveLibraryAsync(string libraryName, string query)
    {
        var cacheKey = $"resolve:{libraryName}:{query}";
        var cached = _docCache.Get("context7", cacheKey);
        if (cached != null)
            return new Context7Result("context7_resolve", true, $"[From cache]\n{cached}");

        try
        {
            var cmd = string.IsNullOrWhiteSpace(query)
                ? $"npx -y ctx7 library \"{EscapeShell(libraryName)}\""
                : $"npx -y ctx7 library \"{EscapeShell(libraryName)}\" \"{EscapeShell(query)}\"";

            var result = await _shell.RunAsync(cmd);

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
                return new Context7Result("context7_resolve", false,
                    $"ctx7 library failed (exit {result.ExitCode}): {result.Output}");

            _docCache.Set("context7", cacheKey, result.Output);
            return new Context7Result("context7_resolve", true, result.Output);
        }
        catch (Exception ex)
        {
            return new Context7Result("context7_resolve", false, $"Error: {ex.Message}");
        }
    }

    public async Task<Context7Result> QueryDocsAsync(string libraryId, string query)
    {
        var cacheKey = $"docs:{libraryId}:{query}";
        var cached = _docCache.Get("context7", cacheKey);
        if (cached != null)
            return new Context7Result("context7_query", true, $"[From cache]\n{cached}");

        try
        {
            var cmd = $"npx -y ctx7 docs \"{EscapeShell(libraryId)}\" \"{EscapeShell(query)}\"";
            var result = await _shell.RunAsync(cmd);

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
                return new Context7Result("context7_query", false,
                    $"ctx7 docs failed (exit {result.ExitCode}): {result.Output}");

            _docCache.Set("context7", cacheKey, result.Output);
            return new Context7Result("context7_query", true, result.Output);
        }
        catch (Exception ex)
        {
            return new Context7Result("context7_query", false, $"Error: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static string? ExtractAttr(string attrs, string name)
    {
        var match = Regex.Match(attrs, $@"{name}\s*=\s*[""']([^""']*)[""']",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string EscapeShell(string input) =>
        input.Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");
}
