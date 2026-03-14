using System.Diagnostics;

namespace DotAgent.Services;

public class ShellExecutor
{
    private readonly int _timeoutMs;
    private string _workingDirectory;

    public string WorkingDirectory => _workingDirectory;

    public ShellExecutor(int timeoutSeconds = 120)
    {
        _timeoutMs = timeoutSeconds * 1000;
        _workingDirectory = Environment.GetEnvironmentVariable("WORKSPACE")
            ?? Directory.GetCurrentDirectory();

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

        return new ExecutionResult(
            Output: output.ToString().TrimEnd(),
            ExitCode: process.ExitCode,
            DurationMs: sw.ElapsedMilliseconds,
            TimedOut: false);
    }

    private static void TryDeleteScript(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }
}