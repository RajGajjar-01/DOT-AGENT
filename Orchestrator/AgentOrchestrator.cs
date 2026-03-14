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

    private readonly List<Message> _history = [];
    private Session? _session;

    private readonly string _planPrompt;
    private readonly string _executePromptTemplate;

    private enum AgentMode { Plan, Execute }

    // ── Color palette (matches banner gradient) ───────────────────
    private const string Gold     = "#F0AA00";
    private const string GoldDim  = "white";
    private const string GoldLit  = "#FFDC78";

    // ── Read-only commands allowed in Plan mode ───────────────────
    private static readonly string[] ReadOnlyPrefixes =
    [
        "ls", "find", "tree", "cat", "head", "tail", "wc", "file", "stat",
        "grep", "rg", "pwd", "echo", "which", "du", "df", "uname", "date",
        "realpath", "basename", "dirname", "diff"
    ];

    public AgentOrchestrator(Database db, LlmService llm, ShellExecutor shell)
    {
        _db    = db;
        _llm   = llm;
        _shell = shell;

        _planPrompt = LoadPrompt("PlanPrompt.txt");
        _executePromptTemplate = LoadPrompt("ExecutePrompt.txt");
    }

    private static string LoadPrompt(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", filename);
        if (!File.Exists(path))
            path = Path.Combine(Directory.GetCurrentDirectory(), "Prompts", filename);
        return File.ReadAllText(path);
    }

    // ── Public entry points ───────────────────────────────────────

    public async Task RunNewSessionAsync(CancellationToken ct = default)
    {
        AnsiConsole.WriteLine();
        var title = AnsiConsole.Ask<string>($"  [{Gold}]Session title[/] [dim](or press Enter)[/]: ")
            .Trim();
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
        AnsiConsole.MarkupLine($"  [dim]{_history.Count} messages loaded from database[/]");

        var plan = _db.GetPlan(sessionId);
        if (!string.IsNullOrWhiteSpace(plan))
            AnsiConsole.MarkupLine($"  [{Gold}]●[/] [dim]Plan loaded[/]");

        AnsiConsole.Write(new Rule().RuleStyle($"{GoldDim} dim"));
        AnsiConsole.WriteLine();

        await ChatLoopAsync(ct);
    }

    // ── Chat loop (outer) ─────────────────────────────────────────

    private async Task ChatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.Markup($"  [bold {Gold}]You ›[/] ");
            var userInput = (Console.ReadLine() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(userInput))
                continue;

            if (userInput.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                userInput.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                _db.UpdateSessionStatus(_session!.Id, "completed");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("  [dim]Session saved. Returning to menu.[/]");
                AnsiConsole.WriteLine();
                return;
            }

            // /plan command — enter plan mode for a task
            if (userInput.StartsWith("/plan", StringComparison.OrdinalIgnoreCase))
            {
                var task = userInput.Length > 5 ? userInput[5..].Trim() : "";
                if (string.IsNullOrWhiteSpace(task))
                {
                    AnsiConsole.Markup($"  [{Gold}]What should I plan?[/] ");
                    task = (Console.ReadLine() ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(task)) continue;
                }

                var userMsg = new Message
                {
                    SessionId = _session!.Id,
                    Role      = "user",
                    Content   = task
                };
                _history.Add(userMsg);
                _db.SaveMessage(userMsg);

                AnsiConsole.MarkupLine($"  [{Gold}]◐[/] [dim]Entering plan mode...[/]");
                await AgentLoopAsync(AgentMode.Plan, ct);
                continue;
            }

            var msg = new Message
            {
                SessionId = _session!.Id,
                Role      = "user",
                Content   = userInput
            };
            _history.Add(msg);
            _db.SaveMessage(msg);

            await AgentLoopAsync(AgentMode.Execute, ct);
        }
    }

    private string GetSystemPrompt(AgentMode mode)
    {
        string basePrompt;

        if (mode == AgentMode.Plan)
            basePrompt = _planPrompt;
        else
        {
            var plan = _db.GetPlan(_session!.Id);
            basePrompt = !string.IsNullOrWhiteSpace(plan)
                ? _executePromptTemplate.Replace("{PLAN}", plan)
                : LoadPrompt("SystemPrompt.txt");
        }

        // Inject environment context
        var env = $"""

═══════════════════════════════════════════════════
ENVIRONMENT
═══════════════════════════════════════════════════

- Working directory: {_shell.WorkingDirectory}
- OS: {Environment.OSVersion}
- Date: {DateTime.Now:yyyy-MM-dd HH:mm}
- Shell: /bin/bash

All commands run in the working directory above.
""";

        return basePrompt + env;
    }



    // ── Agent loop (inner: think → act → observe) ─────────────────

    private async Task AgentLoopAsync(AgentMode mode, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.WriteLine();

            string llmResponse;
            TokenUsage usage = new TokenUsage(0, 0);
            var systemPrompt = GetSystemPrompt(mode);

            try
            {
                var contextWindow = _history.Count > 10
                    ? _history[^10..]
                    : _history;

                (llmResponse, usage) = await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots2)
                    .SpinnerStyle(Style.Parse(Gold))
                    .StartAsync($"[{GoldDim}]{(mode == AgentMode.Plan ? "planning" : "executing")}...[/]", async _ =>
                    {
                        return await _llm.CompleteAsync(
                            contextWindow,
                            systemPrompt,
                            onToken: _ => { },
                            ct: ct);
                    });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"  [red bold]✗ LLM error:[/] [red]{Markup.Escape(ex.Message)}[/]");
                break;
            }

            // ── Render the response cleanly ───────────────────────
            var command  = ActionParser.Extract(llmResponse);
            var textPart = ExtractTextPart(llmResponse);

            // Show agent response
            if (!string.IsNullOrWhiteSpace(textPart))
            {
                var modeLabel = mode == AgentMode.Plan ? "Planner" : "Agent";
                AnsiConsole.Write(new Rule($"[bold {Gold}] {modeLabel} [/]")
                    .RuleStyle($"{GoldDim} dim")
                    .LeftJustified());
                PrintTokenUsage(usage);
                AnsiConsole.WriteLine();

                RenderMarkdownToConsole(textPart.Trim());

                AnsiConsole.Write(new Rule().RuleStyle($"{GoldDim} dim"));
                AnsiConsole.WriteLine();

                // In plan mode, check if the response contains a plan
                if (mode == AgentMode.Plan && textPart.Contains("## Plan"))
                {
                    var planText = ExtractPlan(textPart);
                    if (!string.IsNullOrWhiteSpace(planText))
                    {
                        _db.SavePlan(_session!.Id, planText);

                        AnsiConsole.MarkupLine($"  [{Gold}]✓ Plan saved![/]");
                        AnsiConsole.WriteLine();

                        var approve = AnsiConsole.Confirm($"  [{Gold}]Approve plan and start executing?[/]", defaultValue: true);
                        if (approve)
                        {
                            AnsiConsole.MarkupLine($"  [{Gold}]→ Switching to execute mode[/]");
                            AnsiConsole.Write(new Rule().RuleStyle($"{GoldDim} dim"));
                            AnsiConsole.WriteLine();

                            // Persist assistant message first
                            var planMsg = new Message
                            {
                                SessionId = _session.Id,
                                Role      = "assistant",
                                Content   = llmResponse
                            };
                            _history.Add(planMsg);
                            _db.SaveMessage(planMsg);
                            _db.TouchSession(_session.Id);

                            // Inject a user message to kick off execution
                            var kickoff = new Message
                            {
                                SessionId = _session.Id,
                                Role      = "user",
                                Content   = "Plan approved. Start executing step by step."
                            };
                            _history.Add(kickoff);
                            _db.SaveMessage(kickoff);

                            // Recurse into execute mode
                            await AgentLoopAsync(AgentMode.Execute, ct);
                            return;
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("  [dim]Plan saved but not executing yet. You can refine it.[/]");
                            _db.SavePlan(_session.Id, ""); // Clear plan so we stay in plan mode
                        }
                    }
                }
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

            // ── Act: check for bash block ─────────────────────────
            if (command is null)
                break;

            if (ActionParser.IsExit(command))
            {
                if (mode == AgentMode.Plan)
                {
                    // Plan mode exit — agent is done planning
                    break;
                }
                _db.UpdateSessionStatus(_session.Id, "completed");
                AnsiConsole.MarkupLine("  [dim]Agent exited session.[/]");
                AnsiConsole.WriteLine();
                return;
            }

            // ── Plan mode: block write commands ───────────────────
            if (mode == AgentMode.Plan && !IsReadOnlyCommand(command))
            {
                AnsiConsole.MarkupLine($"  [red bold]✗ Blocked:[/] [dim]Write commands are not allowed in plan mode[/]");
                AnsiConsole.MarkupLine($"  [dim]Command: {Markup.Escape(command.Length > 80 ? command[..80] + "..." : command)}[/]");
                AnsiConsole.WriteLine();

                var blockMsg = new Message
                {
                    SessionId = _session.Id,
                    Role      = "tool_result",
                    Content   = "ERROR: Write commands are blocked in plan mode. You can only use read-only commands (ls, cat, grep, find, tree, etc). Do NOT try to create, edit, or delete files."
                };
                _history.Add(blockMsg);
                _db.SaveMessage(blockMsg);
                continue;
            }

            // ── Show command panel ────────────────────────────────
            AnsiConsole.Write(
                new Panel(new Markup($"[{GoldLit}]{Markup.Escape(command)}[/]"))
                    .Header($"[{Gold} bold] bash [/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(new Color(240, 170, 0))
                    .Padding(1, 0));
            AnsiConsole.WriteLine();

            var confirmed = AnsiConsole.Confirm($"  [{Gold}]Execute?[/]", defaultValue: true);

            if (!confirmed)
            {
                var skipMsg = new Message
                {
                    SessionId = _session.Id,
                    Role      = "tool_result",
                    Content   = "User skipped execution of this command."
                };
                _history.Add(skipMsg);
                _db.SaveMessage(skipMsg);
                break;
            }

            // ── Observe: execute command ──────────────────────────
            AnsiConsole.WriteLine();
            ShellExecutor.ExecutionResult result = default!;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse(Gold))
                .StartAsync("[dim]executing...[/]", async _ =>
                {
                    result = await _shell.RunAsync(command);
                });

            _db.SaveExecution(new Execution
            {
                SessionId  = _session.Id,
                Command    = command,
                Output     = result.Output,
                ExitCode   = result.ExitCode,
                DurationMs = result.DurationMs
            });

            AnsiConsole.WriteLine();
            var exitIcon  = result.ExitCode == 0 ? "✓" : "✗";
            var exitColor = result.ExitCode == 0 ? "green" : "red";
            var timedOut  = result.TimedOut ? " [red]· timed out[/]" : "";

            AnsiConsole.MarkupLine(
                $"  [{exitColor}]{exitIcon}[/] [dim]exit {result.ExitCode} · {result.DurationMs}ms{timedOut}[/]");
            AnsiConsole.WriteLine();

            var toolContent = string.IsNullOrWhiteSpace(result.Output)
                ? "Command executed successfully. No output."
                : result.Output;

            var toolMsg = new Message
            {
                SessionId = _session.Id,
                Role      = "tool_result",
                Content   = toolContent
            };
            _history.Add(toolMsg);
            _db.SaveMessage(toolMsg);
        }
    }

    // ── Plan extraction ───────────────────────────────────────────

    private static string ExtractPlan(string text)
    {
        var match = Regex.Match(text, @"##\s*Plan\b([\s\S]*)", RegexOptions.Multiline);
        return match.Success ? match.Value.Trim() : "";
    }

    // ── Read-only command check ───────────────────────────────────

    private static bool IsReadOnlyCommand(string command)
    {
        // Handle chained commands (&&, ;, ||)
        var parts = Regex.Split(command, @"\s*(?:&&|;|\|\|)\s*");
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Get the first word (the actual command)
            var firstWord = trimmed.Split([' ', '\t'], 2)[0];

            // Strip any path prefix (e.g., /usr/bin/cat → cat)
            firstWord = Path.GetFileName(firstWord);

            if (!ReadOnlyPrefixes.Any(p => firstWord.Equals(p, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    // ── Extract text outside bash fences ────────────────────────────

    private static string ExtractTextPart(string response)
    {
        var cleaned = Regex.Replace(response, @"```bash\s*\n[\s\S]*?```", "", RegexOptions.Multiline);
        return cleaned.Trim();
    }

    // ── Markdown → Spectre markup ─────────────────────────────────

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
                    codeLang = rawLine.TrimStart().Length > 3
                        ? rawLine.TrimStart()[3..].Trim()
                        : "code";
                    if (string.IsNullOrWhiteSpace(codeLang)) codeLang = "code";
                    codeContent.Clear();
                }
                else
                {
                    inCodeBlock = false;
                    var codeText = codeContent.ToString().TrimEnd();
                    AnsiConsole.Write(
                        new Panel(new Markup($"[{GoldLit}]{Markup.Escape(codeText)}[/]"))
                            .Header($"[{Gold} bold] {Markup.Escape(codeLang)} [/]")
                            .Border(BoxBorder.Rounded)
                            .BorderColor(new Color(100, 100, 100))
                            .Padding(1, 0));
                    AnsiConsole.WriteLine();
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeContent.AppendLine(rawLine);
                continue;
            }

            var line = Markup.Escape(rawLine);

            if (Regex.IsMatch(line, @"^###\s+"))
                line = Regex.Replace(line, @"^###\s+(.+)$", $"[bold {Gold}]$1[/]");
            else if (Regex.IsMatch(line, @"^##\s+"))
                line = Regex.Replace(line, @"^##\s+(.+)$", $"[bold {Gold} underline]$1[/]");
            else if (Regex.IsMatch(line, @"^#\s+"))
                line = Regex.Replace(line, @"^#\s+(.+)$", $"[bold {Gold} underline]$1[/]");
            else if (Regex.IsMatch(line, @"^\s*[-*]\s+"))
                line = Regex.Replace(line, @"^\s*[-*]\s+(.+)$", $"[{Gold}]•[/] $1");
            else if (Regex.IsMatch(line, @"^\s*\d+\.\s+"))
                line = Regex.Replace(line, @"^\s*(\d+)\.\s+(.+)$", $"[{Gold}]$1.[/] $2");

            line = Regex.Replace(line, @"([├└│─┬┤┐┘┌╰╭╮╯]+)", "[dim]$1[/]");

            line = Regex.Replace(line, @"\*\*(.+?)\*\*", "[bold]$1[/]");
            line = Regex.Replace(line, @"__(.+?)__",     "[bold]$1[/]");
            line = Regex.Replace(line, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "[italic]$1[/]");
            line = Regex.Replace(line, @"`([^`]+)`", $"[{Gold}]$1[/]");

            try
            {
                AnsiConsole.MarkupLine($"  {line}");
            }
            catch
            {
                AnsiConsole.WriteLine($"  {rawLine}");
            }
        }

        AnsiConsole.WriteLine();
    }

    private static void PrintTokenUsage(TokenUsage usage)
    {
        if (usage.Total == 0) return;

        var text = $"tokens: ↑ {usage.PromptTokens:N0}  ↓ {usage.CompletionTokens:N0}  Σ {usage.Total:N0}";
        var termWidth = Console.WindowWidth > 0 ? Console.WindowWidth : 120;
        var padding = Math.Max(0, termWidth - text.Length - 2);

        AnsiConsole.MarkupLine($"{new string(' ', padding)}[dim]{Markup.Escape(text)}[/]");
    }
}