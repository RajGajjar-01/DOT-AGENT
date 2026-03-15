using DotAgent.Models;

namespace DotAgent.Services;

/// <summary>
/// Pre-processes vague user prompts into detailed, actionable specifications
/// using a fast LLM call before the main agent loop runs.
///
/// Inspired by prompt enhancement patterns from v0.dev, Line0, and
/// Princeton's mini-swe-agent philosophy of "trust the LLM more, scaffold less."
/// </summary>
public class PromptEnhancer
{
    private readonly LlmService _llm;
    private readonly string _workspaceRoot;

    private const string EnhancerSystemPrompt =
        "You are a concise prompt enhancer. You refine vague coding requests into clear, actionable specifications. Be brief.";

    private const string EnhancerTemplate = """
        You are a prompt enhancer for an autonomous coding agent.
        Your job: transform vague user requests into detailed, actionable specifications.

        Given the user's request and workspace context, produce an enhanced version that includes:
        1. **Tech stack** — infer from workspace context or use sensible defaults
        2. **Feature list** — explicit MVP features (only what was asked, nothing extra)
        3. **File structure** — proposed directory layout
        4. **Success criteria** — how to verify it works

        Rules:
        - If the request is ALREADY detailed (has specific tech, file paths, or code), return EXACTLY: PASS
        - Keep enhancements concise — MAX 150 words
        - Do NOT add features the user didn't ask for
        - Prefer simple, proven tech stacks
        - Output ONLY the enhanced specification, nothing else
        - Do NOT include any preamble like "Here's the enhanced prompt:"

        WORKSPACE CONTEXT:
        {0}

        USER'S REQUEST:
        {1}
        """;

    public PromptEnhancer(LlmService llm)
    {
        _llm = llm;
        _workspaceRoot = Environment.GetEnvironmentVariable("WORKSPACE")
            ?? Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Enhance a vague user prompt into a detailed specification.
    /// Returns the enhanced prompt and whether enhancement was applied.
    /// </summary>
    public async Task<(string Enhanced, bool WasEnhanced)> EnhanceAsync(
        string userInput, CancellationToken ct = default)
    {
        // Skip enhancement for already-detailed prompts
        if (IsAlreadyDetailed(userInput))
            return (userInput, false);

        // Skip for very short commands or meta-commands
        if (IsMetaCommand(userInput))
            return (userInput, false);

        var workspaceInfo = GetWorkspaceContext();
        var prompt = string.Format(EnhancerTemplate, workspaceInfo, userInput);

        try
        {
            var messages = new List<Message>
            {
                new() { Role = "user", Content = prompt }
            };

            var (response, _, _) = await _llm.CompleteAsync(
                messages,
                EnhancerSystemPrompt,
                onToken: _ => { },
                ct: ct);

            var trimmed = response.Trim();

            // If LLM says PASS, the prompt was already good enough
            if (trimmed.Equals("PASS", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
                return (userInput, false);

            // Sanity check: enhanced prompt should be non-empty and different
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed == userInput)
                return (userInput, false);

            return (trimmed, true);
        }
        catch
        {
            // If enhancement fails for any reason, just use the original prompt
            return (userInput, false);
        }
    }

    /// <summary>
    /// Detect if a prompt is already detailed enough to skip enhancement.
    /// </summary>
    private static bool IsAlreadyDetailed(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return true; // nothing to enhance

        // Long prompts are probably already detailed
        if (input.Length > 300)
            return true;

        // Contains code blocks
        if (input.Contains("```"))
            return true;

        // Has multiple lines with structure (bullet points, numbered lists)
        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 5)
            return true;

        // Contains file paths
        if (input.Contains('/') && (input.Contains(".py") || input.Contains(".cs")
            || input.Contains(".js") || input.Contains(".ts")
            || input.Contains(".html") || input.Contains(".json")))
            return true;

        return false;
    }

    /// <summary>
    /// Skip enhancement for meta-commands and system interactions.
    /// </summary>
    private static bool IsMetaCommand(string input)
    {
        var trimmed = input.Trim().ToLowerInvariant();

        // Skip for agent control commands
        if (trimmed.StartsWith("/") || trimmed.StartsWith("exit"))
            return true;

        // Skip for yes/no/ok confirmations
        if (trimmed is "yes" or "no" or "ok" or "y" or "n" or "continue"
            or "approve" or "reject" or "skip")
            return true;

        // Skip if it looks like an answer to a clarifying question (very short)
        if (input.Length < 15 && !input.Contains(' '))
            return true;

        return false;
    }

    /// <summary>
    /// Gather workspace context to help the enhancer make smarter decisions.
    /// </summary>
    private string GetWorkspaceContext()
    {
        var parts = new List<string>
        {
            $"Working directory: {_workspaceRoot}"
        };

        try
        {
            // Detect project type
            if (File.Exists(Path.Combine(_workspaceRoot, "package.json")))
                parts.Add("Project type: Node.js (package.json detected)");
            if (File.Exists(Path.Combine(_workspaceRoot, "pyproject.toml")))
                parts.Add("Project type: Python (pyproject.toml detected)");
            if (File.Exists(Path.Combine(_workspaceRoot, "requirements.txt")))
                parts.Add("Project type: Python (requirements.txt detected)");
            if (Directory.GetFiles(_workspaceRoot, "*.csproj").Length > 0)
                parts.Add("Project type: .NET C# (.csproj detected)");
            if (File.Exists(Path.Combine(_workspaceRoot, "Cargo.toml")))
                parts.Add("Project type: Rust (Cargo.toml detected)");
            if (File.Exists(Path.Combine(_workspaceRoot, "go.mod")))
                parts.Add("Project type: Go (go.mod detected)");

            // List top-level items (limited)
            var topItems = Directory.GetFileSystemEntries(_workspaceRoot)
                .Select(Path.GetFileName)
                .Where(n => n != null && !n.StartsWith('.') && n != "node_modules"
                    && n != "bin" && n != "obj" && n != "venv" && n != "__pycache__")
                .Take(15)
                .ToArray();

            if (topItems.Length > 0)
                parts.Add($"Root contents: {string.Join(", ", topItems)}");

            // Check if workspace is empty (new project scenario)
            if (topItems.Length == 0)
                parts.Add("Workspace is empty — this will be a new project");
        }
        catch
        {
            parts.Add("(Could not read workspace contents)");
        }

        return string.Join("\n", parts);
    }
}
