using Spectre.Console;
using DotAgent.Data;
using DotAgent.Models;
using DotAgent.Services;
using System.Text;
using System.Text.RegularExpressions;

namespace DotAgent.Orchestrator;

public class AgentOrchestrator
{
    private readonly Database _db;
    private readonly LlmService _llm;
    private readonly ShellExecutor _shell;
    private readonly FileTools _fileTools;
    private readonly Context7Tools _context7Tools;
    private readonly DocCache _docCache;
    private readonly PromptEnhancer _promptEnhancer;

    private readonly string _workspaceRoot;
    private readonly string _systemPrompt;

    private readonly List<Message> _history = [];
    private Session? _session;

    // ── Color palette (matches banner gradient) ───────────────────
    private const string Gold    = "#F0AA00";
    private const string GoldDim = "white";
    private const string GoldLit = "#FFDC78";

    // ── Safety ────────────────────────────────────────────────────
    private static readonly string[] ReadOnlyPrefixes =
    [
        "ls", "find", "tree", "cat", "head", "tail", "wc", "file", "stat",
        "grep", "rg", "pwd", "echo", "which", "du", "df", "uname", "date",
        "realpath", "basename", "dirname", "diff", "ctx7", "npx", "uv"
    ];

    private const int MaxSteps = 25;
    private const int DoomLoopThreshold = 3;

    public AgentOrchestrator(Database db, LlmService llm, ShellExecutor shell)
    {
        _db    = db;
        _llm   = llm;
        _shell = shell;
        _fileTools = new FileTools();
        _docCache = new DocCache();
        _context7Tools = new Context7Tools(_docCache, shell);
        _promptEnhancer = new PromptEnhancer();

        _workspaceRoot = Path.GetFullPath(
            Environment.GetEnvironmentVariable("WORKSPACE") ?? Directory.GetCurrentDirectory());

        _systemPrompt = LoadPrompt("SystemPrompt.txt");
    }

    // ── Public entry points ───────────────────────────────────────

    public async Task RunNewSessionAsync(CancellationToken ct = default)
    {
        AnsiConsole.WriteLine();
        var title = AnsiConsole.Ask<string>($"  [{Gold}]Session title[/] [dim](or press Enter)[/]: ").Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = $"Session {DateTime.Now:MMM dd HH:mm}";

        _session = _db.CreateSession(title);
        _history.Clear();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [dim]Session started:[/] [{Gold}]{_session.Id[..8]}[/] · {_session.Title}");
        AnsiConsole.Write(new Rule().RuleStyle($"{GoldDim} dim"));
        AnsiConsole.WriteLine();

        await ChatLoopAsync(ct);
    }

    public async Task ResumeSessionAsync(string sessionId, CancellationToken ct = default)
    {
        _session = _db.ListSessions().FirstOrDefault(s => s.Id == sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        _history.Clear();
        _history.AddRange(_db.GetMessages(sessionId));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [dim]Resuming:[/] [{Gold}]{_session.Id[..8]}[/] · {_session.Title}");
        AnsiConsole.MarkupLine($"  [dim]{_history.Count} messages loaded[/]");
        AnsiConsole.Write(new Rule().RuleStyle($"{GoldDim} dim"));
        AnsiConsole.WriteLine();

        await ChatLoopAsync(ct);
    }

    // ── Chat loop ─────────────────────────────────────────────────

    private async Task ChatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.Markup($"  [bold {Gold}]You ›[/] ");
            var userInput = (Console.ReadLine() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(userInput)) continue;

            if (userInput.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                userInput.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                _db.UpdateSessionStatus(_session!.Id, "completed");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("  [dim]Session saved. Returning to menu.[/]");
                AnsiConsole.WriteLine();
                return;
            }

            // ── Prompt Enhancement (first message only) ──────────────
            var enhancedInput = userInput;
            var isFirstMessage = !_history.Any(m => m.Role == "user");

            if (isFirstMessage)
            {
                try
                {
                    var (enhanced, wasEnhanced) = await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots2)
                        .SpinnerStyle(Style.Parse(Gold))
                        .StartAsync($"[{GoldDim}]enhancing prompt...[/]", async _ =>
                            await _promptEnhancer.EnhanceAsync(userInput, ct));

                    if (wasEnhanced)
                    {
                        AnsiConsole.MarkupLine($"  [{Gold}]✦ Enhanced prompt:[/]");
                        AnsiConsole.WriteLine();
                        AnsiConsole.Write(
                            new Panel(new Markup($"[{GoldLit}]{Markup.Escape(enhanced)}[/]"))
                                .Header($"[{Gold} bold] enhanced [/]")
                                .Border(BoxBorder.Rounded)
                                .BorderColor(new Color(240, 170, 0))
                                .Padding(1, 0));
                        AnsiConsole.WriteLine();

                        if (AnsiConsole.Confirm($"  [{Gold}]Use enhanced prompt?[/]", defaultValue: true))
                            enhancedInput = enhanced;
                    }
                }
                catch { /* Enhancement failed — use original */ }
            }

            var msg = new Message
            {
                SessionId = _session!.Id,
                Role      = "user",
                Content   = enhancedInput
            };
            _history.Add(msg);
            _db.SaveMessage(msg);

            await AgentLoopAsync(ct);
        }
    }

    // ── Build system prompt with context ──────────────────────────

    private string BuildSystemPrompt()
    {
        var userTask = _history.FirstOrDefault(m => m.Role == "user")?.Content;
        var taskCtx = !string.IsNullOrWhiteSpace(userTask)
            ? $"\n\n═══ USER'S ORIGINAL TASK ═══\n{userTask}\n"
            : "";

        var envCtx = $"""

═══ ENVIRONMENT ═══
Working directory: {_shell.WorkingDirectory}
OS: {Environment.OSVersion}
Shell: /bin/bash
Current time: {DateTime.Now:yyyy-MM-dd HH:mm}
""";

        return _systemPrompt + taskCtx + envCtx;
    }

    // ── Agent loop (think → act → observe) ───────────────────────

    private async Task AgentLoopAsync(CancellationToken ct)
    {
        int step = 0;
        var recentCommands = new List<string>();

        while (!ct.IsCancellationRequested)
        {
            step++;
            if (step > MaxSteps)
            {
                AnsiConsole.MarkupLine($"  [{Gold}]⚠[/] [dim]Reached {MaxSteps} steps. Returning control.[/]");
                AnsiConsole.WriteLine();
                break;
            }

            AnsiConsole.WriteLine();

            // ── Think: call LLM ────────────────────────────────
            string llmResponse;
            TokenUsage usage = new(0, 0);
            LlmMetrics? metrics = null;

            try
            {
                var contextWindow = _history.Count > 20 ? _history[^20..] : _history;

                (llmResponse, usage, metrics) = await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots2)
                    .SpinnerStyle(Style.Parse(Gold))
                    .StartAsync($"[{GoldDim}]thinking...[/]", async _ =>
                        await _llm.CompleteAsync(contextWindow, BuildSystemPrompt(), _ => { }, ct));
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"  [red bold]✗ Timeout:[/] [red]LLM took too long.[/]");
                break;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red bold]✗ LLM error:[/] [red]{Markup.Escape(ex.Message)}[/]");
                break;
            }

            // ── Render response ──────────────────────────────────
            var textPart = ExtractTextPart(llmResponse);
            if (!string.IsNullOrWhiteSpace(textPart))
            {
                AnsiConsole.Write(new Rule($"[bold {Gold}] Agent [/]")
                    .RuleStyle($"{GoldDim} dim").LeftJustified());
                PrintTokenUsage(usage, metrics);
                AnsiConsole.WriteLine();
                RenderMarkdownToConsole(textPart.Trim());
                AnsiConsole.Write(new Rule().RuleStyle($"{GoldDim} dim"));
                AnsiConsole.WriteLine();
            }
            else
            {
                PrintTokenUsage(usage);
            }

            // Persist assistant message
            var assistantMsg = new Message
            {
                SessionId = _session!.Id,
                Role      = "assistant",
                Content   = llmResponse
            };
            _history.Add(assistantMsg);
            _db.SaveMessage(assistantMsg);
            _db.TouchSession(_session.Id);

            // ── Act: detect action type ──────────────────────────
            var actionType = ActionParser.GetActionType(llmResponse);

            if (actionType == ActionParser.ActionType.None)
                break; // No action — return control to user

            // ── File Tool ────────────────────────────────────────
            if (actionType == ActionParser.ActionType.FileTool)
            {
                var (result, _) = _fileTools.ParseAndExecute(llmResponse);

                AnsiConsole.Write(
                    new Panel(new Markup($"[{GoldLit}]{result.ToolName}[/]"))
                        .Header($"[{Gold} bold] file tool [/]")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(new Color(240, 170, 0))
                        .Padding(1, 0));
                AnsiConsole.WriteLine();

                var icon = result.Success ? "✓" : "✗";
                var color = result.Success ? "green" : "red";
                AnsiConsole.MarkupLine($"  [{color}]{icon}[/] [dim]{Markup.Escape(result.Output.Split('\n')[0])}[/]");
                AnsiConsole.WriteLine();

                _history.Add(new Message { SessionId = _session.Id, Role = "tool_result", Content = result.Output });
                _db.SaveMessage(_history[^1]);
                continue;
            }

            // ── Context7 Tool ────────────────────────────────────
            if (actionType == ActionParser.ActionType.Context7Tool)
            {
                var (result, _) = await _context7Tools.ParseAndExecuteAsync(llmResponse);

                AnsiConsole.Write(
                    new Panel(new Markup($"[{GoldLit}]{result.ToolName}[/]"))
                        .Header($"[{Gold} bold] context7 [/]")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(new Color(240, 170, 0))
                        .Padding(1, 0));
                AnsiConsole.WriteLine();

                var icon = result.Success ? "✓" : "✗";
                var color = result.Success ? "green" : "red";
                AnsiConsole.MarkupLine($"  [{color}]{icon}[/] [dim]{Markup.Escape(result.Output.Split('\n')[0])}[/]");
                AnsiConsole.WriteLine();

                _history.Add(new Message { SessionId = _session.Id, Role = "tool_result", Content = result.Output });
                _db.SaveMessage(_history[^1]);
                continue;
            }

            // ── Bash Command ─────────────────────────────────────
            var command = ActionParser.Extract(llmResponse)!;

            if (ActionParser.IsExit(command))
            {
                // ── Plan detection: if agent output a plan + exit, ask for approval
                if (!string.IsNullOrWhiteSpace(textPart) && textPart.Contains("## Plan"))
                {
                    AnsiConsole.MarkupLine($"  [{Gold}]✓ Plan ready![/]");
                    AnsiConsole.WriteLine();

                    var approve = AnsiConsole.Confirm($"  [{Gold}]Approve plan and start executing?[/]", defaultValue: true);
                    if (approve)
                    {
                        AnsiConsole.MarkupLine($"  [{Gold}]→ Executing plan...[/]");
                        AnsiConsole.Write(new Rule().RuleStyle($"{GoldDim} dim"));
                        AnsiConsole.WriteLine();

                        var kickoff = new Message
                        {
                            SessionId = _session.Id,
                            Role      = "user",
                            Content   = "Plan approved. Start executing step by step."
                        };
                        _history.Add(kickoff);
                        _db.SaveMessage(kickoff);
                        continue; // Continue the agent loop into execution
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("  [dim]Plan not approved. You can refine your request.[/]");
                        AnsiConsole.WriteLine();
                        break; // Return to chat loop
                    }
                }

                _db.UpdateSessionStatus(_session.Id, "completed");
                AnsiConsole.MarkupLine("  [dim]Agent exited session.[/]");
                AnsiConsole.WriteLine();
                return;
            }

            // Doom loop detection
            var cmdTrimmed = command.Trim();
            recentCommands.Add(cmdTrimmed);
            if (recentCommands.Count > DoomLoopThreshold) recentCommands.RemoveAt(0);
            if (recentCommands.Count == DoomLoopThreshold && recentCommands.All(c => c == cmdTrimmed))
            {
                AnsiConsole.MarkupLine($"  [{Gold}]⚠[/] [dim]Repeating same command {DoomLoopThreshold}x. Returning control.[/]");
                AnsiConsole.WriteLine();
                break;
            }

            // Show command panel
            AnsiConsole.Write(
                new Panel(new Markup($"[{GoldLit}]{Markup.Escape(command)}[/]"))
                    .Header($"[{Gold} bold] bash [/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(new Color(240, 170, 0))
                    .Padding(1, 0));
            AnsiConsole.WriteLine();

            // Sandbox check + auto-execute read-only
            bool confirmed;
            if (!IsReadOnlyCommand(command) && !IsCommandWorkspaceSafe(command, out var reason))
            {
                AnsiConsole.MarkupLine($"  [red bold]✗ Blocked:[/] [dim]{Markup.Escape(reason)}[/]");
                AnsiConsole.WriteLine();
                _history.Add(new Message { SessionId = _session.Id, Role = "tool_result",
                    Content = $"ERROR: Blocked by sandbox. {reason}" });
                _db.SaveMessage(_history[^1]);
                continue;
            }

            if (IsReadOnlyCommand(command))
            {
                AnsiConsole.MarkupLine($"  [{Gold}]●[/] [dim]Auto-executing read-only command...[/]");
                confirmed = true;
            }
            else
            {
                confirmed = AnsiConsole.Confirm($"  [{Gold}]Execute?[/]", defaultValue: true);
            }

            if (!confirmed)
            {
                _history.Add(new Message { SessionId = _session.Id, Role = "tool_result",
                    Content = "User skipped this command." });
                _db.SaveMessage(_history[^1]);
                break;
            }

            // ── Execute ──────────────────────────────────────────
            AnsiConsole.WriteLine();
            ShellExecutor.ExecutionResult result2 = default!;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse(Gold))
                .StartAsync("[dim]executing...[/]", async _ => { result2 = await _shell.RunAsync(command); });

            _db.SaveExecution(new Execution
            {
                SessionId  = _session.Id,
                Command    = command,
                Output     = result2.Output,
                ExitCode   = result2.ExitCode,
                DurationMs = result2.DurationMs
            });

            AnsiConsole.WriteLine();
            var exitIcon  = result2.ExitCode == 0 ? "✓" : "✗";
            var exitColor = result2.ExitCode == 0 ? "green" : "red";
            var timedOut  = result2.TimedOut ? " [red]· timed out[/]" : "";
            AnsiConsole.MarkupLine($"  [{exitColor}]{exitIcon}[/] [dim]exit {result2.ExitCode} · {result2.DurationMs}ms{timedOut}[/]");
            AnsiConsole.WriteLine();

            var toolContent = string.IsNullOrWhiteSpace(result2.Output)
                ? "Command executed successfully. No output."
                : TruncateOutput(result2.Output, result2.ExitCode);

            _history.Add(new Message { SessionId = _session.Id, Role = "tool_result", Content = toolContent });
            _db.SaveMessage(_history[^1]);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static string LoadPrompt(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", filename);
        if (!File.Exists(path))
            path = Path.Combine(Directory.GetCurrentDirectory(), "Prompts", filename);
        return File.ReadAllText(path);
    }

    private bool IsWithinWorkspace(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        var root = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.Ordinal);
    }

    private bool IsCommandWorkspaceSafe(string command, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(command)) { reason = "Empty command"; return false; }
        if (command.Contains("../") || command.Contains("..\\")) { reason = "Directory traversal not allowed"; return false; }

        foreach (Match m in Regex.Matches(command, @"(?<![\w./-])/(?:[^\s'\""`\\]|\\\s)+"))
        {
            var token = m.Value.TrimEnd(';', '&', '|', ')', ']', '}');
            if (token == "/") { reason = "Root '/' access not allowed"; return false; }
            if (!IsWithinWorkspace(Path.GetFullPath(token))) { reason = $"Path outside workspace: {token}"; return false; }
        }

        var cdMatch = Regex.Match(command, @"\bcd\s+([^\s;&|]+)");
        if (cdMatch.Success)
        {
            var target = cdMatch.Groups[1].Value.Trim('"', '\'', '`');
            if (Path.IsPathRooted(target) && !IsWithinWorkspace(Path.GetFullPath(target)))
            { reason = $"cd target outside workspace: {target}"; return false; }
        }
        return true;
    }

    private static bool IsReadOnlyCommand(string command)
    {
        var parts = Regex.Split(command, @"\s*(?:&&|;|\|\|)\s*");
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            var firstWord = Path.GetFileName(trimmed.Split([' ', '\t'], 2)[0]);

            if (firstWord.Equals("npx", StringComparison.OrdinalIgnoreCase))
            {
                var args = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (!args.Any(a => a.Contains("ctx7", StringComparison.OrdinalIgnoreCase) ||
                                   a.Contains("context7", StringComparison.OrdinalIgnoreCase)))
                    return false;
                continue;
            }

            if (!ReadOnlyPrefixes.Any(p => firstWord.Equals(p, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    private static string TruncateOutput(string output, int exitCode)
    {
        const int max = 2000;
        if (output.Length <= max) return output;
        if (exitCode != 0) return $"[Truncated — last 800 chars]\n...{output[^800..]}";
        return $"{output[..(max / 2)]}\n\n... [{output.Length - max} chars omitted] ...\n\n{output[^(max / 2)..]}";
    }

    private static string ExtractTextPart(string response)
    {
        // Strip bash code blocks
        var text = Regex.Replace(response, @"```bash\s*\n[\s\S]*?```", "", RegexOptions.Multiline);
        // Strip file tool XML blocks: <write_file ...>...</write_file>, <read_file>...</read_file>, etc.
        text = Regex.Replace(text, @"<(write_file|read_file|list_dir|create_dir|delete_file)\b[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        // Strip context7 self-closing tags
        text = Regex.Replace(text, @"<context7_\w+\b[^>]*/?>", "", RegexOptions.IgnoreCase);
        // Collapse 3+ consecutive blank lines into 1
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    // ── Markdown rendering ───────────────────────────────────────

    private static void RenderMarkdownToConsole(string text)
    {
        var lines = text.Split('\n');
        var inCodeBlock = false;
        var codeLang = "";
        var codeContent = new StringBuilder();

        foreach (var rawLine in lines)
        {
            if (rawLine.TrimStart().StartsWith("```"))
            {
                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    codeLang = rawLine.TrimStart().Length > 3 ? rawLine.TrimStart()[3..].Trim() : "code";
                    if (string.IsNullOrWhiteSpace(codeLang)) codeLang = "code";
                    codeContent.Clear();
                }
                else
                {
                    inCodeBlock = false;
                    AnsiConsole.Write(
                        new Panel(new Markup($"[{GoldLit}]{Markup.Escape(codeContent.ToString().TrimEnd())}[/]"))
                            .Header($"[{Gold} bold] {Markup.Escape(codeLang)} [/]")
                            .Border(BoxBorder.Rounded)
                            .BorderColor(new Color(100, 100, 100))
                            .Padding(1, 0));
                    AnsiConsole.WriteLine();
                }
                continue;
            }

            if (inCodeBlock) { codeContent.AppendLine(rawLine); continue; }

            var line = Markup.Escape(rawLine);
            if (Regex.IsMatch(line, @"^###\s+"))      line = Regex.Replace(line, @"^###\s+(.+)$", $"[bold {Gold}]$1[/]");
            else if (Regex.IsMatch(line, @"^##\s+"))   line = Regex.Replace(line, @"^##\s+(.+)$", $"[bold {Gold} underline]$1[/]");
            else if (Regex.IsMatch(line, @"^#\s+"))    line = Regex.Replace(line, @"^#\s+(.+)$", $"[bold {Gold} underline]$1[/]");
            else if (Regex.IsMatch(line, @"^\s*[-*]\s+")) line = Regex.Replace(line, @"^\s*[-*]\s+(.+)$", $"[{Gold}]•[/] $1");
            else if (Regex.IsMatch(line, @"^\s*\d+\.\s+")) line = Regex.Replace(line, @"^\s*(\d+)\.\s+(.+)$", $"[{Gold}]$1.[/] $2");

            line = Regex.Replace(line, @"([├└│─┬┤┐┘┌╰╭╮╯]+)", "[dim]$1[/]");
            line = Regex.Replace(line, @"\*\*(.+?)\*\*", "[bold]$1[/]");
            line = Regex.Replace(line, @"__(.+?)__", "[bold]$1[/]");
            line = Regex.Replace(line, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "[italic]$1[/]");
            line = Regex.Replace(line, @"`([^`]+)`", $"[{Gold}]$1[/]");

            try { AnsiConsole.MarkupLine($"  {line}"); }
            catch { AnsiConsole.WriteLine($"  {rawLine}"); }
        }
        AnsiConsole.WriteLine();
    }

    private static void PrintTokenUsage(TokenUsage usage, LlmMetrics? metrics = null)
    {
        if (usage.Total == 0) return;
        var text = $"tokens: ↑ {usage.PromptTokens:N0}  ↓ {usage.CompletionTokens:N0}  Σ {usage.Total:N0}";
        if (metrics != null) text += $"  │  TTFT: {metrics.TimeToFirstTokenMs:N0}ms  Total: {metrics.TotalDurationMs:N0}ms";
        var w = Console.WindowWidth > 0 ? Console.WindowWidth : 120;
        AnsiConsole.MarkupLine($"{new string(' ', Math.Max(0, w - text.Length - 2))}[dim]{Markup.Escape(text)}[/]");
    }
}