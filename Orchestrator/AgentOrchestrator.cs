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
    private readonly string _systemPrompt;

    // ── Color palette (matches banner gradient) ───────────────────
    private const string Gold     = "#F0AA00";
    private const string GoldDim  = "white";
    private const string GoldLit  = "#FFDC78";

    public AgentOrchestrator(Database db, LlmService llm, ShellExecutor shell)
    {
        _db    = db;
        _llm   = llm;
        _shell = shell;

        var promptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.txt");
        if (!File.Exists(promptPath))
            promptPath = Path.Combine(Directory.GetCurrentDirectory(), "Prompts", "SystemPrompt.txt");

        _systemPrompt = File.ReadAllText(promptPath);
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

            var userMsg = new Message
            {
                SessionId = _session!.Id,
                Role      = "user",
                Content   = userInput
            };
            _history.Add(userMsg);
            _db.SaveMessage(userMsg);

            await AgentLoopAsync(ct);
        }
    }

    // ── Agent loop (inner: think → act → observe) ─────────────────

    private async Task AgentLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.WriteLine();

            // ── Think: collect full response behind a spinner ─────
            string llmResponse;
            TokenUsage usage = new TokenUsage(0, 0);

            try
            {
                var contextWindow = _history.Count > 10
                    ? _history[^10..]
                    : _history;

                (llmResponse, usage) = await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots2)
                    .SpinnerStyle(Style.Parse(Gold))
                    .StartAsync($"[{GoldDim}]thinking...[/]", async _ =>
                    {
                        return await _llm.CompleteAsync(
                            contextWindow,
                            _systemPrompt,
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

            // Show agent response only if there's text outside the bash block
            if (!string.IsNullOrWhiteSpace(textPart))
            {
                AnsiConsole.Write(new Rule($"[bold {Gold}] Agent [/]")
                    .RuleStyle($"{GoldDim} dim")
                    .LeftJustified());
                PrintTokenUsage(usage);
                AnsiConsole.WriteLine();

                RenderMarkdownToConsole(textPart.Trim());

                AnsiConsole.Write(new Rule().RuleStyle($"{GoldDim} dim"));
                AnsiConsole.WriteLine();
            }
            else
            {
                // No text, but still show usage
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
                _db.UpdateSessionStatus(_session.Id, "completed");
                AnsiConsole.MarkupLine("  [dim]Agent exited session.[/]");
                AnsiConsole.WriteLine();
                return;
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

            // Persist execution record
            _db.SaveExecution(new Execution
            {
                SessionId  = _session.Id,
                Command    = command,
                Output     = result.Output,
                ExitCode   = result.ExitCode,
                DurationMs = result.DurationMs
            });

            // Show exit status
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

    // ── Extract text outside bash fences ────────────────────────────

    private static string ExtractTextPart(string response)
    {
        // Only remove ```bash blocks (the action commands), keep all others
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
            // Detect code fence start/end
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

            // Normal line processing
            var line = Markup.Escape(rawLine);

            // Headers
            if (Regex.IsMatch(line, @"^###\s+"))
                line = Regex.Replace(line, @"^###\s+(.+)$", $"[bold {Gold}]$1[/]");
            else if (Regex.IsMatch(line, @"^##\s+"))
                line = Regex.Replace(line, @"^##\s+(.+)$", $"[bold {Gold} underline]$1[/]");
            else if (Regex.IsMatch(line, @"^#\s+"))
                line = Regex.Replace(line, @"^#\s+(.+)$", $"[bold {Gold} underline]$1[/]");
            // Bullets
            else if (Regex.IsMatch(line, @"^\s*[-*]\s+"))
                line = Regex.Replace(line, @"^\s*[-*]\s+(.+)$", $"[{Gold}]•[/] $1");
            // Numbered lists
            else if (Regex.IsMatch(line, @"^\s*\d+\.\s+"))
                line = Regex.Replace(line, @"^\s*(\d+)\.\s+(.+)$", $"[{Gold}]$1.[/] $2");

            // Tree characters
            line = Regex.Replace(line, @"([├└│─┬┤┐┘┌╰╭╮╯]+)", "[dim]$1[/]");

            // Inline: bold → italic → code
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
                // Fallback: write unformatted if markup is malformed
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