using System.Diagnostics;

namespace DotAgent.Services;

public class ShellExecutor
{
    private readonly int _timeoutMs;

    public ShellExecutor(int timeoutSeconds = 30)
    {
        _timeoutMs = timeoutSeconds * 1000;
    }

    public record ExecutionResult(
        string Output,
        int ExitCode,
        long DurationMs,
        bool TimedOut);

    public async Task<ExecutionResult> RunAsync(string command)
    {
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName               = "/bin/bash",
            Arguments              = $"-c \"{EscapeForShell(command)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

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
            return new ExecutionResult(
                Output: "Error: command timed out.",
                ExitCode: -1,
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: true);
        }

        sw.Stop();
        return new ExecutionResult(
            Output: output.ToString().TrimEnd(),
            ExitCode: process.ExitCode,
            DurationMs: sw.ElapsedMilliseconds,
            TimedOut: false);
    }

    private static string EscapeForShell(string command) =>
        command.Replace("\\", "\\\\").Replace("\"", "\\\"");
}