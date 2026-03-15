using System.Net.Http.Json;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace DotAgent.Services;

/// <summary>
/// Pre-processes vague user prompts into detailed, actionable specifications
/// using Tavily web search + dedicated Groq LLM call.
///
/// Flow: User prompt → Tavily search for context → Groq refines with search results.
/// Always uses Groq for fast, cheap prompt enhancement.
/// </summary>
public class PromptEnhancer
{
    private readonly ChatClient? _groqClient;
    private readonly HttpClient _httpClient;
    private readonly string? _tavilyApiKey;
    private readonly string _workspaceRoot;

    private const string GroqModel = "llama-3.3-70b-versatile";
    private const string GroqEndpoint = "https://api.groq.com/openai/v1/";
    private const string TavilyEndpoint = "https://api.tavily.com/search";

    private const string EnhancerSystemPrompt =
        "You are a concise prompt enhancer. You refine vague coding requests into clear, actionable specifications using the provided web search context. Be brief.";

    private const string EnhancerTemplate = """
        You are a prompt enhancer for an autonomous coding agent.
        Your job: transform vague user requests into detailed, actionable specifications.

        Given the user's request, workspace context, and web search results, produce an enhanced version that includes:
        1. **Tech stack** — infer from search results + workspace context, use current best practices
        2. **Feature list** — explicit MVP features (only what was asked, nothing extra)
        3. **File structure** — proposed directory layout
        4. **Key dependencies** — specific package names and versions from search results
        5. **Success criteria** — how to verify it works

        Rules:
        - If the request is ALREADY detailed (has specific tech, file paths, or code), return EXACTLY: PASS
        - Keep enhancements concise — MAX 200 words
        - Do NOT add features the user didn't ask for
        - Prefer current, well-maintained libraries (use search results to verify)
        - Output ONLY the enhanced specification, nothing else
        - Do NOT include any preamble like "Here's the enhanced prompt:"

        WORKSPACE CONTEXT:
        {0}

        WEB SEARCH RESULTS:
        {1}

        USER'S REQUEST:
        {2}
        """;

    public PromptEnhancer()
    {
        _workspaceRoot = Environment.GetEnvironmentVariable("WORKSPACE")
            ?? Directory.GetCurrentDirectory();

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Tavily API key
        _tavilyApiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY");

        // Build a dedicated Groq client for prompt enhancement
        var groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (!string.IsNullOrWhiteSpace(groqKey))
        {
            var model = Environment.GetEnvironmentVariable("GROQ_MODEL") ?? GroqModel;
            var options = new OpenAIClientOptions { Endpoint = new Uri(GroqEndpoint) };
            var credential = new System.ClientModel.ApiKeyCredential(groqKey);
            _groqClient = new OpenAIClient(credential, options).GetChatClient(model);
        }
    }

    /// <summary>
    /// Enhance a vague user prompt into a detailed specification.
    /// Uses Tavily search for real-time context, then Groq for refinement.
    /// </summary>
    public async Task<(string Enhanced, bool WasEnhanced)> EnhanceAsync(
        string userInput, CancellationToken ct = default)
    {
        if (_groqClient == null)
            return (userInput, false);

        if (IsAlreadyDetailed(userInput))
            return (userInput, false);

        if (IsMetaCommand(userInput))
            return (userInput, false);

        var workspaceInfo = GetWorkspaceContext();

        // Search the web for current context about the user's request
        var searchResults = await TavilySearchAsync(userInput, ct);

        var prompt = string.Format(EnhancerTemplate, workspaceInfo, searchResults, userInput);

        try
        {
            var chatMessages = new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage(EnhancerSystemPrompt),
                ChatMessage.CreateUserMessage(prompt)
            };

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var response = new System.Text.StringBuilder();
            await foreach (var update in _groqClient.CompleteChatStreamingAsync(
                chatMessages, cancellationToken: linkedCts.Token))
            {
                foreach (var part in update.ContentUpdate)
                    if (!string.IsNullOrEmpty(part.Text))
                        response.Append(part.Text);
            }

            var trimmed = response.ToString().Trim();

            if (trimmed.Equals("PASS", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
                return (userInput, false);

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed == userInput)
                return (userInput, false);

            return (trimmed, true);
        }
        catch
        {
            return (userInput, false);
        }
    }

    // ── Tavily Search ────────────────────────────────────────────

    private async Task<string> TavilySearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_tavilyApiKey))
            return "(No web search — TAVILY_API_KEY not set)";

        try
        {
            var requestBody = new
            {
                api_key = _tavilyApiKey,
                query = $"best practices current libraries for: {query}",
                search_depth = "basic",
                max_results = 3,
                include_answer = true
            };

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var response = await _httpClient.PostAsJsonAsync(TavilyEndpoint, requestBody, linkedCts.Token);

            if (!response.IsSuccessStatusCode)
                return "(Web search failed)";

            var json = await response.Content.ReadAsStringAsync(linkedCts.Token);
            return ParseTavilyResponse(json);
        }
        catch
        {
            return "(Web search timed out)";
        }
    }

    private static string ParseTavilyResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sb = new System.Text.StringBuilder();

            // Include the AI-generated answer if available
            if (root.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.String)
            {
                var answerText = answer.GetString();
                if (!string.IsNullOrWhiteSpace(answerText))
                    sb.AppendLine($"Summary: {answerText}");
            }

            // Include top search results
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var result in results.EnumerateArray())
                {
                    var title = result.TryGetProperty("title", out var t) ? t.GetString() : "";
                    var content = result.TryGetProperty("content", out var c) ? c.GetString() : "";

                    if (!string.IsNullOrWhiteSpace(title))
                        sb.AppendLine($"- {title}: {content?[..Math.Min(content.Length, 200)]}");
                }
            }

            return sb.Length > 0 ? sb.ToString() : "(No relevant results)";
        }
        catch
        {
            return "(Could not parse search results)";
        }
    }

    // ── Skip logic ───────────────────────────────────────────────

    private static bool IsAlreadyDetailed(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return true;
        if (input.Length > 300) return true;
        if (input.Contains("```")) return true;

        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 5) return true;

        if (input.Contains('/') && (input.Contains(".py") || input.Contains(".cs")
            || input.Contains(".js") || input.Contains(".ts")
            || input.Contains(".html") || input.Contains(".json")))
            return true;

        return false;
    }

    private static bool IsMetaCommand(string input)
    {
        var trimmed = input.Trim().ToLowerInvariant();

        if (trimmed.StartsWith("/") || trimmed.StartsWith("exit")) return true;

        if (trimmed is "yes" or "no" or "ok" or "y" or "n" or "continue"
            or "approve" or "reject" or "skip")
            return true;

        if (input.Length < 15 && !input.Contains(' ')) return true;

        return false;
    }

    // ── Workspace context ────────────────────────────────────────

    private string GetWorkspaceContext()
    {
        var parts = new List<string> { $"Working directory: {_workspaceRoot}" };

        try
        {
            if (File.Exists(Path.Combine(_workspaceRoot, "package.json")))
                parts.Add("Project type: Node.js");
            if (File.Exists(Path.Combine(_workspaceRoot, "pyproject.toml")))
                parts.Add("Project type: Python");
            if (File.Exists(Path.Combine(_workspaceRoot, "requirements.txt")))
                parts.Add("Project type: Python");
            if (Directory.GetFiles(_workspaceRoot, "*.csproj").Length > 0)
                parts.Add("Project type: .NET C#");
            if (File.Exists(Path.Combine(_workspaceRoot, "Cargo.toml")))
                parts.Add("Project type: Rust");
            if (File.Exists(Path.Combine(_workspaceRoot, "go.mod")))
                parts.Add("Project type: Go");

            var topItems = Directory.GetFileSystemEntries(_workspaceRoot)
                .Select(Path.GetFileName)
                .Where(n => n != null && !n.StartsWith('.') && n != "node_modules"
                    && n != "bin" && n != "obj" && n != "venv" && n != "__pycache__")
                .Take(15)
                .ToArray();

            if (topItems.Length > 0)
                parts.Add($"Root contents: {string.Join(", ", topItems)}");
            else
                parts.Add("Workspace is empty — new project");
        }
        catch
        {
            parts.Add("(Could not read workspace)");
        }

        return string.Join("\n", parts);
    }
}
