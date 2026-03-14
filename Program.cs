using DotNetEnv;
using DotAgent.Data;
using DotAgent.Services;
using DotAgent.Orchestrator;
using Spectre.Console;

// ── Bootstrap ─────────────────────────────────────────────────────
Env.Load();
var db     = new Database();
var llm    = new LlmService();
var shell  = new ShellExecutor();
var agent  = new AgentOrchestrator(db, llm, shell);

// ── Banner ────────────────────────────────────────────────────────
AnsiConsole.WriteLine();
AnsiConsole.WriteLine();

var lines = new[]
{
    "  ██████╗  ██████╗ ████████╗ █████╗  ██████╗ ███████╗███╗   ██╗████████╗",
    "  ██╔══██╗██╔═══██╗╚══██╔══╝██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝",
    "  ██║  ██║██║   ██║   ██║   ███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   ",
    "  ██║  ██║██║   ██║   ██║   ██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   ",
    "  ██████╔╝╚██████╔╝   ██║   ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   ",
    "  ╚═════╝  ╚═════╝    ╚═╝   ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝  ",
};

Color[] gradient =
[
    new Color(255, 255, 255),
    new Color(255, 240, 180),
    new Color(255, 220, 120),
    new Color(255, 200,  60),
    new Color(240, 170,   0),
    new Color(210, 140,   0),
];

for (int i = 0; i < lines.Length; i++)
{
    AnsiConsole.Write(new Markup(
        $"[#{gradient[i].R:X2}{gradient[i].G:X2}{gradient[i].B:X2}]{lines[i]}[/]"));
    AnsiConsole.WriteLine();
}

AnsiConsole.WriteLine();
AnsiConsole.Write(new Markup(
    $"  [dim]AI Agent powered by [/][bold #F0AA00]{Markup.Escape(llm.ModelName)}[/][dim] · {Markup.Escape(llm.ProviderName)} · SQLite[/]"));
AnsiConsole.WriteLine();
AnsiConsole.WriteLine();

// ── Main menu loop ────────────────────────────────────────────────
while (true)
{
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title($"  [bold #F0AA00]What do you want to do?[/]  [dim]({Markup.Escape(llm.ModelName)})[/]")
            .HighlightStyle(Style.Parse("bold #F0AA00"))
            .AddChoices("⊕  New Chat", "↻  Load Session", "⇄  Switch Model", "✕  Exit")
    );

    AnsiConsole.WriteLine();

    switch (choice)
    {
        case "⊕  New Chat":
            try
            {
                await agent.RunNewSessionAsync();
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("  [dim]Session interrupted.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]Error:[/] {Markup.Escape(ex.Message)}");
            }
            AnsiConsole.WriteLine();
            break;

        case "↻  Load Session":
            var sessions = db.ListSessions().ToList();

            if (sessions.Count == 0)
            {
                AnsiConsole.MarkupLine("  [dim]No sessions yet. Start a new chat first.[/]");
                AnsiConsole.WriteLine();
                break;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn(new TableColumn("[dim]#[/]").Centered())
                .AddColumn("[dim]ID[/]")
                .AddColumn("[dim]Title[/]")
                .AddColumn(new TableColumn("[dim]Status[/]").Centered())
                .AddColumn("[dim]Last active[/]");

            for (int i = 0; i < sessions.Count; i++)
            {
                var s  = sessions[i];
                var dt = DateTimeOffset.FromUnixTimeSeconds(s.UpdatedAt)
                             .LocalDateTime.ToString("MMM dd HH:mm");
                var statusMarkup = s.Status == "active"
                    ? "[green]● active[/]"
                    : "[dim]○ done[/]";

                table.AddRow(
                    $"[#F0AA00]{i + 1}[/]",
                    $"[dim]{s.Id[..8]}[/]",
                    Markup.Escape(s.Title),
                    statusMarkup,
                    $"[dim]{dt}[/]"
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            var pick = AnsiConsole.Prompt(
                new TextPrompt<int>("  [#F0AA00]Pick a session #[/] [dim](0 to cancel)[/]:")
                    .Validate(n => n >= 0 && n <= sessions.Count
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"Enter 1–{sessions.Count} or 0 to cancel")));

            if (pick == 0) break;

            try
            {
                await agent.ResumeSessionAsync(sessions[pick - 1].Id);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("  [dim]Session interrupted.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]Error:[/] {Markup.Escape(ex.Message)}");
            }
            AnsiConsole.WriteLine();
            break;

        case "✕  Exit":
            AnsiConsole.MarkupLine("  [dim]Goodbye.[/]");
            AnsiConsole.WriteLine();
            return;

        case "⇄  Switch Model":
            if (llm.Providers.Count <= 1)
            {
                AnsiConsole.MarkupLine("  [dim]Only one provider configured. Add more in your .env file.[/]");
            }
            else
            {
                var modelChoice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title($"  [bold #F0AA00]Pick a model[/]")
                        .HighlightStyle(Style.Parse("bold #F0AA00"))
                        .AddChoices(llm.Providers.Select(p =>
                        {
                            var active = p.Name == llm.ProviderName ? " ●" : "";
                            return $"{p.Name} · {p.Model}{active}";
                        }))
                );

                var selected = llm.Providers.First(p => modelChoice.StartsWith(p.Name));
                llm.SwitchProvider(selected);
                AnsiConsole.MarkupLine($"  [#F0AA00]Switched to[/] [bold]{Markup.Escape(selected.Model)}[/] [dim]({selected.Name})[/]");
            }
            AnsiConsole.WriteLine();
            break;
    }
}