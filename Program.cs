using DotNetEnv;
using DotAgent.Data;
using Spectre.Console;

// ── Bootstrap ─────────────────────────────────────────────────────
Env.Load();
var db = new Database();

var llm = new DotAgent.Services.LlmService();
AnsiConsole.MarkupLine("  [green]✓[/] LlmService initialised");

// ── Top padding ───────────────────────────────────────────────────
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
    "  [dim]AI Agent powered by [/][bold cyan]GLM-4.7-Flash[/][dim] · Z.ai · SQLite[/]"));
AnsiConsole.WriteLine();
AnsiConsole.WriteLine();

AnsiConsole.Write(new Rule().RuleStyle("cyan dim"));
AnsiConsole.WriteLine();

AnsiConsole.MarkupLine("  [dim]Model[/]    [cyan]glm-4.7-flash[/]");
AnsiConsole.MarkupLine("  [dim]Storage[/]  [cyan]SQLite · agent.db[/]");
AnsiConsole.MarkupLine("  [dim]Shell[/]    [cyan]bash[/]");
AnsiConsole.WriteLine();
AnsiConsole.Write(new Rule().RuleStyle("cyan dim"));
AnsiConsole.WriteLine();

// ── Main menu loop ────────────────────────────────────────────────
while (true)
{
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("  [cyan]What do you want to do?[/]")
            .HighlightStyle(Style.Parse("cyan bold"))
            .AddChoices("New Chat", "Load Session", "Exit")
    );

    AnsiConsole.WriteLine();

    switch (choice)
    {
        case "New Chat":
            AnsiConsole.MarkupLine("  [cyan]›[/] [dim]Starting new session... (wired in Step 5)[/]");
            AnsiConsole.WriteLine();
            break;

        case "Load Session":
            var sessions = db.ListSessions().ToList();
            if (sessions.Count == 0)
            {
                AnsiConsole.MarkupLine("  [dim]No sessions yet. Start a new chat first.[/]");
            }
            else
            {
                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Grey)
                    .AddColumn("[dim]ID[/]")
                    .AddColumn("[dim]Title[/]")
                    .AddColumn("[dim]Status[/]")
                    .AddColumn("[dim]Last active[/]");

                foreach (var s in sessions)
                {
                    var dt = DateTimeOffset.FromUnixTimeSeconds(s.UpdatedAt)
                        .LocalDateTime.ToString("MMM dd HH:mm");
                    table.AddRow(
                        $"[cyan]{s.Id[..8]}[/]",
                        s.Title,
                        s.Status == "active" ? "[green]active[/]" : "[dim]completed[/]",
                        $"[dim]{dt}[/]"
                    );
                }
                AnsiConsole.Write(table);
            }
            AnsiConsole.WriteLine();
            break;

        case "Exit":
            AnsiConsole.MarkupLine("  [dim]Goodbye.[/]");
            AnsiConsole.WriteLine();
            return;
    }
}