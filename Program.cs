using Spectre.Console;

// ── Top padding ───────────────────────────────────────────────────
AnsiConsole.WriteLine();
AnsiConsole.WriteLine();

var lines = new[]
{
    "  ██████╗  ██████╗ ████████╗   █████╗  ██████╗ ███████╗███╗   ██╗████████╗",
    "  ██╔══██╗██╔═══██╗╚══██╔══╝  ██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝",
    "  ██║  ██║██║   ██║   ██║     ███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   ",
    "  ██║  ██║██║   ██║   ██║     ██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   ",
    "  ██████╔╝╚██████╔╝   ██║     ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   ",
    "  ╚═════╝  ╚═════╝    ╚═╝     ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝  ",
};

// Gradient colors top→bottom (cyan to blue, like Claude Code)
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
    AnsiConsole.Write(new Markup($"[#{gradient[i].R:X2}{gradient[i].G:X2}{gradient[i].B:X2}]{lines[i]}[/]"));
    AnsiConsole.WriteLine();
}

// ── Subtitle line (like Claude Code's version line) ───────────────
AnsiConsole.WriteLine();
AnsiConsole.Write(new Markup("  [dim]AI Agent powered by [/][bold cyan]GLM-4.7-Flash[/][dim] · Z.ai · SQLite[/]"));
AnsiConsole.WriteLine();
AnsiConsole.WriteLine();

// ── Thin divider ──────────────────────────────────────────────────
AnsiConsole.Write(new Rule().RuleStyle("cyan dim"));
AnsiConsole.WriteLine();

// ── Quick status row ─────────────────────────────────────────────
AnsiConsole.MarkupLine("  [dim]Model[/]    [cyan]glm-4.7-flash[/]");
AnsiConsole.MarkupLine("  [dim]Storage[/]  [cyan]SQLite · agent.db[/]");
AnsiConsole.MarkupLine("  [dim]Shell[/]    [cyan]bash[/]");
AnsiConsole.WriteLine();
AnsiConsole.Write(new Rule().RuleStyle("cyan dim"));
AnsiConsole.WriteLine();

// ── Interactive menu (Claude Code style) ─────────────────────────
var choice = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("  [cyan]What do you want to do?[/]")
        .HighlightStyle(Style.Parse("cyan bold"))
        .AddChoices("New Chat", "Load Session", "Exit")
);

AnsiConsole.WriteLine();
AnsiConsole.MarkupLine($"  [cyan]›[/] Starting [bold]{choice}[/]...");