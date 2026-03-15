using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotAgent.Services;

/// <summary>
/// Provides Context7 documentation retrieval tools.
/// The agent outputs structured XML-like commands that are parsed and executed directly.
/// </summary>
public class Context7Tools
{
    private readonly HttpClient _httpClient;
    private readonly DocCache _docCache;
    private readonly string? _apiKey;

    private const string ResolveEndpoint = "https://api.context7.com/v1/resolve-library-id";
    private const string QueryEndpoint = "https://api.context7.com/v1/query-docs";

    public Context7Tools(DocCache docCache)
    {
        _docCache = docCache;
        _apiKey = Environment.GetEnvironmentVariable("CONTEXT7_API_KEY");
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)  // Prevent indefinite hang
        };
        
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }
    }

    // ── Tool Result Types ─────────────────────────────────────────

    public record Context7Result(
        string ToolName,
        bool Success,
        string Output);

    // ── Parse and Execute Tool Commands ────────────────────────────

    /// <summary>
    /// Check if the LLM output contains a Context7 tool command.
    /// </summary>
    public static bool HasContext7Tool(string output)
    {
        return Regex.IsMatch(output, @"<context7_(resolve|query)\b",
            RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Extract and execute the first Context7 tool command from LLM output.
    /// Returns the result and the remaining output text.
    /// </summary>
    public async Task<(Context7Result result, string remainingOutput)> ParseAndExecuteAsync(string output)
    {
        // Find any context7 tag
        var tagMatch = Regex.Match(output, 
            @"<(context7_(?:resolve|query))\b([^>]*)/>", 
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!tagMatch.Success)
        {
            return (new Context7Result("unknown", false, "No Context7 tool command found"), output);
        }

        var toolName = tagMatch.Groups[1].Value.ToLowerInvariant();
        var attributesString = tagMatch.Groups[2].Value;
        var remaining = output.Remove(tagMatch.Index, tagMatch.Length);

        // Extract attributes flexibly
        var library = ExtractAttribute(attributesString, "library") ?? ExtractAttribute(attributesString, "name");
        var libraryId = ExtractAttribute(attributesString, "libraryId") ?? library;
        var query = ExtractAttribute(attributesString, "query") ?? "";

        if (toolName == "context7_resolve")
        {
            if (string.IsNullOrWhiteSpace(library))
            {
                return (new Context7Result(toolName, false, "ERROR: Missing 'library' attribute in context7_resolve."), output);
            }
            var result = await ResolveLibraryId(library, query);
            return (result, remaining);
        }
        else if (toolName == "context7_query")
        {
            if (string.IsNullOrWhiteSpace(libraryId))
            {
                return (new Context7Result(toolName, false, "ERROR: Missing 'libraryId' attribute in context7_query."), output);
            }
            if (string.IsNullOrWhiteSpace(query))
            {
                return (new Context7Result(toolName, false, "ERROR: Missing 'query' attribute in context7_query."), output);
            }
            var result = await QueryDocs(libraryId, query);
            return (result, remaining);
        }

        return (new Context7Result("unknown", false, "Unknown Context7 tool command."), output);
    }

    private static string? ExtractAttribute(string attributesString, string attributeName)
    {
        var match = Regex.Match(attributesString,
            $@"{attributeName}\s*=\s*[""']([^""']*)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // ── Tool Implementations ──────────────────────────────────────

    /// <summary>
    /// Resolve a library name to a Context7-compatible library ID.
    /// </summary>
    public async Task<Context7Result> ResolveLibraryId(string libraryName, string query)
    {
        try
        {
            // Check cache first
            var cacheKey = $"resolve:{libraryName}:{query}";
            var cached = _docCache.Get("context7", cacheKey);
            if (cached != null)
            {
                return new Context7Result("context7_resolve", true, $"[From cache]\n{cached}");
            }

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                return new Context7Result("context7_resolve", false,
                    "ERROR: CONTEXT7_API_KEY environment variable not set. Please set it to use Context7 tools.");
            }

            var requestBody = new
            {
                libraryName = libraryName,
                query = query
            };

            var response = await _httpClient.PostAsJsonAsync(ResolveEndpoint, requestBody);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return new Context7Result("context7_resolve", false,
                    $"Context7 API error: {response.StatusCode}\n{errorContent}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            
            // Parse the response to extract library info
            var result = ParseResolveResponse(jsonResponse);
            
            // Cache the result
            _docCache.Set("context7", cacheKey, result);

            return new Context7Result("context7_resolve", true, result);
        }
        catch (Exception ex)
        {
            return new Context7Result("context7_resolve", false,
                $"Error resolving library: {ex.Message}");
        }
    }

    /// <summary>
    /// Query documentation for a specific library.
    /// </summary>
    public async Task<Context7Result> QueryDocs(string libraryId, string query)
    {
        try
        {
            // Check cache first
            var cacheKey = $"docs:{libraryId}:{query}";
            var cached = _docCache.Get("context7", cacheKey);
            if (cached != null)
            {
                return new Context7Result("context7_query", true, $"[From cache]\n{cached}");
            }

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                return new Context7Result("context7_query", false,
                    "ERROR: CONTEXT7_API_KEY environment variable not set. Please set it to use Context7 tools.");
            }

            var requestBody = new
            {
                libraryId = libraryId,
                query = query
            };

            var response = await _httpClient.PostAsJsonAsync(QueryEndpoint, requestBody);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return new Context7Result("context7_query", false,
                    $"Context7 API error: {response.StatusCode}\n{errorContent}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            
            // Parse the response to extract documentation
            var result = ParseQueryResponse(jsonResponse);
            
            // Cache the result
            _docCache.Set("context7", cacheKey, result);

            return new Context7Result("context7_query", true, result);
        }
        catch (Exception ex)
        {
            return new Context7Result("context7_query", false,
                $"Error querying docs: {ex.Message}");
        }
    }

    // ── Response Parsing ───────────────────────────────────────────

    private static string ParseResolveResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Context7 Library Resolution Results:");
            sb.AppendLine(new string('─', 40));

            // Handle array of libraries
            if (root.ValueKind == JsonValueKind.Array)
            {
                var idx = 1;
                foreach (var lib in root.EnumerateArray())
                {
                    var id = lib.TryGetProperty("id", out var idProp) ? idProp.GetString() : 
                             lib.TryGetProperty("libraryId", out var libIdProp) ? libIdProp.GetString() : "unknown";
                    var name = lib.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "unknown";
                    var description = lib.TryGetProperty("description", out var descProp) ? descProp.GetString() : "";
                    var snippets = lib.TryGetProperty("codeSnippets", out var snipProp) ? snipProp.GetInt32() : 0;
                    var score = lib.TryGetProperty("benchmarkScore", out var scoreProp) ? scoreProp.GetInt32() : 0;

                    sb.AppendLine($"[{idx}] {name}");
                    sb.AppendLine($"    ID: {id}");
                    if (!string.IsNullOrEmpty(description))
                        sb.AppendLine($"    Description: {description}");
                    sb.AppendLine($"    Code Snippets: {snippets} | Score: {score}");
                    sb.AppendLine();
                    idx++;
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // Single library object
                var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() :
                         root.TryGetProperty("libraryId", out var libIdProp) ? libIdProp.GetString() : "unknown";
                var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "unknown";
                var description = root.TryGetProperty("description", out var descProp) ? descProp.GetString() : "";

                sb.AppendLine($"Library: {name}");
                sb.AppendLine($"ID: {id}");
                if (!string.IsNullOrEmpty(description))
                    sb.AppendLine($"Description: {description}");
            }
            else
            {
                // Fallback: return raw JSON
                sb.AppendLine(json);
            }

            return sb.ToString();
        }
        catch
        {
            // If parsing fails, return raw JSON
            return json;
        }
    }

    private static string ParseQueryResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Context7 Documentation:");
            sb.AppendLine(new string('─', 40));

            // Handle various response formats
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    ParseDocItem(item, sb);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // Check for documentation array
                if (root.TryGetProperty("documentation", out var docs) && docs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in docs.EnumerateArray())
                    {
                        ParseDocItem(item, sb);
                    }
                }
                else if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        ParseDocItem(item, sb);
                    }
                }
                else
                {
                    // Single documentation object
                    ParseDocItem(root, sb);
                }
            }
            else
            {
                // Fallback: return raw JSON
                sb.AppendLine(json);
            }

            return sb.ToString();
        }
        catch
        {
            // If parsing fails, return raw JSON
            return json;
        }
    }

    private static void ParseDocItem(JsonElement item, System.Text.StringBuilder sb)
    {
        var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : "";
        var content = item.TryGetProperty("content", out var contentProp) ? contentProp.GetString() : "";
        var code = item.TryGetProperty("code", out var codeProp) ? codeProp.GetString() :
                   item.TryGetProperty("codeExample", out var codeExProp) ? codeExProp.GetString() : "";
        var source = item.TryGetProperty("source", out var sourceProp) ? sourceProp.GetString() :
                     item.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : "";

        if (!string.IsNullOrEmpty(title))
        {
            sb.AppendLine($"### {title}");
        }
        if (!string.IsNullOrEmpty(source))
        {
            sb.AppendLine($"Source: {source}");
        }
        if (!string.IsNullOrEmpty(content))
        {
            sb.AppendLine();
            sb.AppendLine(content);
        }
        if (!string.IsNullOrEmpty(code))
        {
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(code);
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine(new string('─', 40));
        sb.AppendLine();
    }
}
