using System.Text.RegularExpressions;

namespace DotAgent.Services;

public static class ActionParser
{
    private static readonly Regex CommandBlock = new(
        @"```(?:bash-action|bash|sh)\s*\n?(.*?)\n?```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // File tool patterns
    private static readonly Regex FileToolPattern = new(
        @"<(read_file|write_file|list_dir|create_dir|delete_file)\b[^>]*>.*?</\1>",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Context7 tool patterns (self-closing XML tags)
    private static readonly Regex Context7ToolPattern = new(
        @"<context7_(resolve|query)\b[^>]*/>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? Extract(string llmOutput)
    {
        var match = CommandBlock.Match(llmOutput);
        if (!match.Success) return null;

        var cmd = match.Groups[1].Value
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim();

        return string.IsNullOrWhiteSpace(cmd) ? null : cmd;
    }

    public static bool IsExit(string command) =>
        command.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if the output contains a file tool command.
    /// </summary>
    public static bool HasFileTool(string llmOutput) =>
        FileToolPattern.IsMatch(llmOutput);

    /// <summary>
    /// Check if the output contains a Context7 tool command.
    /// </summary>
    public static bool HasContext7Tool(string llmOutput) =>
        Context7ToolPattern.IsMatch(llmOutput);

    /// <summary>
    /// Determine the action type from LLM output.
    /// </summary>
    public enum ActionType { None, BashCommand, FileTool, Context7Tool }

    public static ActionType GetActionType(string llmOutput)
    {
        if (HasFileTool(llmOutput)) return ActionType.FileTool;
        if (HasContext7Tool(llmOutput)) return ActionType.Context7Tool;
        if (Extract(llmOutput) != null) return ActionType.BashCommand;
        return ActionType.None;
    }
}