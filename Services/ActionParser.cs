using System.Text.RegularExpressions;

namespace DotAgent.Services;

public static class ActionParser
{
    private static readonly Regex CommandBlock = new(
        @"```(?:bash-action|bash|sh)\s*\n?(.*?)\n?```",
        RegexOptions.Singleline | RegexOptions.Compiled);

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
}