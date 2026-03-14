using Spectre.Console;
using DotAgent.Data;
using DotAgent.Models;
using DotAgent.Services;

namespace DotAgent.Orchestrator;

public class AgentOrchestrator
{
    private readonly Database _db;
    private readonly LlmService _llm;
    private readonly ShellExecutor _shell;

    private readonly List<Message> _history = [];
    private Session? _session;

    private readonly string _systemPrompt;

    public AgentOrchestrator(Database db, LlmService llm, ShellExecutor shell)
    {
        _db    = db;
        _llm   = llm;
        _shell = shell;

        var promptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.txt");
        if (!File.Exists(promptPath))
        {
            promptPath = Path.Combine(Directory.GetCurrentDirectory(), "Prompts", "SystemPrompt.txt");
        }
        _systemPrompt = File.ReadAllText(promptPath);
    }

    public async Task RunNewSessionAsync(CancellationToken ct = default)
    {
        AnsiConsole.WriteLine();
        var title = AnsiConsole.Ask<string>("  [cyan]Session title[/] [dim](or press Enter)[/]: ")
            .Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = $"Session {DateTime.Now:MMM dd HH:mm}";

        _session = _db.CreateSession(title);
        _history.Clear();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [dim]Session started:[/] [cyan]{_session.Id[..8]}[/] · {_session.Title}");
        AnsiConsole.Write(new Rule().RuleStyle("cyan dim"));
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
        AnsiConsole.MarkupLine($"  [dim]Resuming:[/] [cyan]{_session.Id[..8]}[/] · {_session.Title}");
        AnsiConsole.MarkupLine($"  [dim]{_history.Count} messages loaded from database[/]");
        AnsiConsole.Write(new Rule().RuleStyle("cyan dim"));
        AnsiConsole.WriteLine();

        await ChatLoopAsync(ct);
    }

    private async Task ChatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var userInput = AnsiConsole.Prompt(
                new TextPrompt<string>("  [cyan]You[/] [dim]›[/]")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(userInput))
                continue;

            if (userInput.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                userInput.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase))
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

    private async Task AgentLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Markup("  [cyan]Agent[/] [dim]›[/] ");

            string llmResponse;
            try
            {
                llmResponse = await _llm.CompleteAsync(
                    _history,
                    _systemPrompt,
                    onToken: token => AnsiConsole.Markup(
                        Markup.Escape(token)),
                    ct: ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"  [red]LLM error:[/] {Markup.Escape(ex.Message)}");
                break;
            }

            AnsiConsole.WriteLine();

            var assistantMsg = new Message
            {
                SessionId = _session!.Id,
                Role      = "assistant",
                Content   = llmResponse
            };
            _history.Add(assistantMsg);
            _db.SaveMessage(assistantMsg);
            _db.TouchSession(_session.Id);

            var command = ActionParser.Extract(llmResponse);

            if (command is null)
                break;

            if (ActionParser.IsExit(command))
            {
                _db.UpdateSessionStatus(_session.Id, "completed");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("  [dim]Agent exited session.[/]");
                AnsiConsole.WriteLine();
                return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[dim]command[/]").RuleStyle("yellow dim"));
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(command)}[/]");
            AnsiConsole.Write(new Rule().RuleStyle("yellow dim"));
            AnsiConsole.WriteLine();

            var confirmed = AnsiConsole.Confirm("  [dim]Execute?[/]", defaultValue: true);
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

            ExecutionResult result = default!;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("[dim]Running...[/]", async _ =>
                {
                    result = await _shell.RunAsync(command);
                });

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[dim]output[/]").RuleStyle("grey dim"));

            var outputText = string.IsNullOrWhiteSpace(result.Output)
                ? "[dim](no output)[/]"
                : Markup.Escape(result.Output);

            AnsiConsole.MarkupLine(outputText);

            var exitColor = result.ExitCode == 0 ? "green" : "red";
            AnsiConsole.MarkupLine(
                $"  [dim]exit[/] [{exitColor}]{result.ExitCode}[/]  " +
                $"[dim]{result.DurationMs}ms[/]" +
                (result.TimedOut ? "  [red]timed out[/]" : ""));

            AnsiConsole.Write(new Rule().RuleStyle("grey dim"));
            AnsiConsole.WriteLine();

            _db.SaveExecution(new Execution
            {
                SessionId  = _session.Id,
                Command    = command,
                Output     = result.Output,
                ExitCode   = result.ExitCode,
                DurationMs = result.DurationMs
            });

            var toolMsg = new Message
            {
                SessionId = _session.Id,
                Role      = "tool_result",
                Content   = string.IsNullOrWhiteSpace(result.Output)
                    ? "Command executed successfully. No output."
                    : result.Output
            };
            _history.Add(toolMsg);
            _db.SaveMessage(toolMsg);
        }
    }
}