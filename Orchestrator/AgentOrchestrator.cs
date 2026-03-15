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
    private readonly FileTracker _fileTracker;
    private readonly PlanStateTracker _planStateTracker;
    private readonly FileTools _fileTools;
    private readonly Context7Tools _context7Tools;
    private readonly DocCache _docCache;  // Shared instance

    private readonly string _workspaceRoot;

    private readonly List<Message> _history = [];
    private Session? _session;
    private bool _awaitingClarification = false;

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
        "realpath", "basename", "dirname", "diff", "ctx7", "npx", "uv"
    ];

    public AgentOrchestrator(Database db, LlmService llm, ShellExecutor shell)
    {
        _db    = db;
        _llm   = llm;
        _shell = shell;
        _fileTracker = new FileTracker(db);
        _planStateTracker = new PlanStateTracker(db);
        _fileTools = new FileTools();
        _docCache = new DocCache();  // Single shared instance
        _context7Tools = new Context7Tools(_docCache);  // Reuse shared cache

        var root = Environment.GetEnvironmentVariable("WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        _workspaceRoot = Path.GetFullPath(root);

        _planPrompt = LoadPrompt("PlanPrompt.txt");
        _executePromptTemplate = LoadPrompt("ExecutePrompt.txt");
    }

    private bool IsWithinWorkspace(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        var root = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.Ordinal);
    }

    private bool IsCommandWorkspaceSafe(string command, out string reason)
    {
        reason = "";

        if (string.IsNullOrWhiteSpace(command))
        {
            reason = "Empty command";
            return false;
        }

        // Block explicit traversal patterns. This is a conservative sandbox.
        if (command.Contains("../") || command.Contains("..\\"))
        {
            reason = "Command contains directory traversal '..' which is not allowed";
            return false;
        }

        // Extract absolute paths like /etc/passwd or /root/...
        // We allow /workspace/... only.
        foreach (Match m in Regex.Matches(command, @"(?<![\w./-])/(?:[^\s'""`\\]|\\\s)+"))
        {
            var token = m.Value.TrimEnd(';', '&', '|', ')', ']', '}');
            if (token == "/")
            {
                reason = "Access to filesystem root '/' is not allowed";
                return false;
            }

            var full = Path.GetFullPath(token);
            if (!IsWithinWorkspace(full))
            {
                reason = $"Command references path outside workspace: {token}";
                return false;
            }
        }

        // Block "cd /" or "cd /something" outside workspace.
        var cdMatch = Regex.Match(command, @"\bcd\s+([^\s;&|]+)");
        if (cdMatch.Success)
        {
            var target = cdMatch.Groups[1].Value.Trim('"', '\'', '`');
            if (Path.IsPathRooted(target))
            {
                var full = Path.GetFullPath(target);
                if (!IsWithinWorkspace(full))
                {
                    reason = $"cd target is outside workspace: {target}";
                    return false;
                }
            }
        }

        return true;
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
        {
            AnsiConsole.MarkupLine($"  [{Gold}]●[/] [dim]Plan loaded[/]");

            // Reconcile planned steps with filesystem on resume
            _planStateTracker.ReconcileWithFilesystem(sessionId);

            var steps = _db.GetPlannedSteps(sessionId).ToList();
            var pending = steps.Count(s => s.Status == "pending");
            var done = steps.Count(s => s.Status == "done");
            AnsiConsole.MarkupLine($"  [{Gold}]●[/] [dim]{done} done, {pending} pending files[/]");
        }

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

                // /plan continue — re-enter plan mode with existing history
                if (task.Equals("continue", StringComparison.OrdinalIgnoreCase) && _awaitingClarification)
                {
                    _awaitingClarification = false;
                    AnsiConsole.MarkupLine($"  [{Gold}]◐[/] [dim]Continuing plan with your answers...[/]");
                    await AgentLoopAsync(AgentMode.Plan, ct);
                    continue;
                }

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

                _awaitingClarification = false;
                AnsiConsole.MarkupLine($"  [{Gold}]◐[/] [dim]Entering plan mode...[/]");
                await AgentLoopAsync(AgentMode.Plan, ct);
                continue;
            }

            // If awaiting clarification, treat user input as answers and re-enter plan mode
            if (_awaitingClarification)
            {
                var answerMsg = new Message
                {
                    SessionId = _session!.Id,
                    Role      = "user",
                    Content   = userInput
                };
                _history.Add(answerMsg);
                _db.SaveMessage(answerMsg);

                _awaitingClarification = false;
                AnsiConsole.MarkupLine($"  [{Gold}]◐[/] [dim]Continuing plan with your answers...[/]");
                await AgentLoopAsync(AgentMode.Plan, ct);
                continue;
            }

            // Check if we have an approved plan before allowing execute mode
            var existingPlan = _db.GetPlan(_session!.Id);
            if (string.IsNullOrWhiteSpace(existingPlan))
            {
                // No plan exists - MUST enter plan mode first
                AnsiConsole.MarkupLine($"  [{Gold}]◐[/] [dim]No plan found. Entering plan mode first...[/]");
                
                var planMsg = new Message
                {
                    SessionId = _session.Id,
                    Role      = "user",
                    Content   = userInput
                };
                _history.Add(planMsg);
                _db.SaveMessage(planMsg);

                await AgentLoopAsync(AgentMode.Plan, ct);
                continue;
            }

            // Plan exists - proceed to execute mode
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
            if (!string.IsNullOrWhiteSpace(plan))
            {
                // Truncate plan to only pending steps for faster context
                var truncatedPlan = TruncatePlanToPending(plan);
                basePrompt = _executePromptTemplate.Replace("{PLAN}", truncatedPlan);
            }
            else
            {
                basePrompt = LoadPrompt("SystemPrompt.txt");
            }
        }

        // Build context sections in cache-friendly order:
        // 1. Static environment info (no timestamps)
        // 2. Planned files manifest
        // 3. File manifest
        // 4. Dynamic timestamp (at the very end to not break cache)

        var staticEnv = $"""

═══════════════════════════════════════════════════
ENVIRONMENT
═══════════════════════════════════════════════════

- Working directory: {_shell.WorkingDirectory}
- OS: {Environment.OSVersion}
- Shell: /bin/bash
""";

        // Inject planned files manifest (what the agent plans to create)
        var plannedManifest = _planStateTracker.BuildPlannedManifest(_session!.Id);
        var plannedContext = "";
        if (!string.IsNullOrWhiteSpace(plannedManifest))
        {
            plannedContext = $"""

═══════════════════════════════════════════════════
PLANNED FILES STATUS
═══════════════════════════════════════════════════

{plannedManifest}

IMPORTANT: Check the "Pending Files" section above.
- Do NOT recreate files marked as "done".
- Start from the first "pending" file.
- After creating a file, it will be marked as done automatically.
""";
        }

        // Inject file manifest (what the agent has created/modified so far)
        var manifest = _fileTracker.BuildFileManifest(_session!.Id);
        var fileContext = "";
        if (!string.IsNullOrWhiteSpace(manifest))
        {
            fileContext = $"""

═══════════════════════════════════════════════════
FILES CREATED/MODIFIED IN THIS SESSION
═══════════════════════════════════════════════════

{manifest}
Use this to understand what has already been done.
Continue from where you left off. Do NOT recreate existing files.
""";
        }

        // Dynamic timestamp at the very end (won't break cache for the prefix)
        var timestamp = $"""

Current time: {DateTime.Now:yyyy-MM-dd HH:mm}
""";

        return basePrompt + staticEnv + plannedContext + fileContext + timestamp;
    }



    // ── Agent loop (inner: think → act → observe) ─────────────────

    private const int MaxSteps = 20;
    private const int DoomLoopThreshold = 3;

    private async Task AgentLoopAsync(AgentMode mode, CancellationToken ct)
    {
        int step = 0;
        var recentCommands = new List<string>();

        while (!ct.IsCancellationRequested)
        {
            step++;

            // Max steps guard
            if (step > MaxSteps)
            {
                AnsiConsole.MarkupLine($"  [{Gold}]⚠[/] [dim]Reached {MaxSteps} steps. Returning control to you.[/]");
                AnsiConsole.WriteLine();
                break;
            }

            AnsiConsole.WriteLine();

            string llmResponse;
            TokenUsage usage = new TokenUsage(0, 0);
            LlmMetrics? metrics = null;
            var systemPrompt = GetSystemPrompt(mode);

            try
            {
                var contextWindow = _history.Count > 5
                    ? _history[^5..]  // Reduced from 10 to 5 for faster responses
                    : _history;

                (llmResponse, usage, metrics) = await AnsiConsole.Status()
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
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine(
                    $"  [red bold]✗ Timeout:[/] [red]LLM response took too long. Try a faster model.[/]");
                break;
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
                PrintTokenUsage(usage, metrics);
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

                        // Parse plan into structured steps for state tracking
                        _planStateTracker.ParseAndSavePlan(_session.Id, planText);

                        AnsiConsole.MarkupLine($"  [{Gold}]✓ Plan saved![/]");

                        // Show planned files summary
                        var pendingCount = _db.GetPlannedSteps(_session.Id).Count(s => s.Status == "pending");
                        if (pendingCount > 0)
                            AnsiConsole.MarkupLine($"  [{Gold}]●[/] [dim]{pendingCount} files planned[/]");

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
                            AnsiConsole.MarkupLine("  [dim]Plan saved but not executing yet. You can refine it or answer the questions above.[/]");
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

            // ── Act: check for bash block or file tool ──────────────
            var actionType = ActionParser.GetActionType(llmResponse);
            
            if (actionType == ActionParser.ActionType.None)
            {
                // In plan mode, if no command and no plan detected, the agent
                // may be asking clarifying questions
                if (mode == AgentMode.Plan && !string.IsNullOrWhiteSpace(textPart) && textPart.Contains('?'))
                {
                    _awaitingClarification = true;
                    AnsiConsole.MarkupLine($"  [{Gold}]↑[/] [dim]Answer the questions above, then type your response.[/]");
                    AnsiConsole.MarkupLine($"  [dim]  (or type [/][{Gold}]/plan continue[/][dim] to proceed)[/]");
                    AnsiConsole.WriteLine();
                }
                break;
            }

            // ── Handle File Tool commands ───────────────────────────
            if (actionType == ActionParser.ActionType.FileTool)
            {
                var (fileResult, _) = _fileTools.ParseAndExecute(llmResponse);
                
                // Show file tool panel
                AnsiConsole.Write(
                    new Panel(new Markup($"[{GoldLit}]{fileResult.ToolName}[/]"))
                        .Header($"[{Gold} bold] file tool [/]")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(new Color(240, 170, 0))
                        .Padding(1, 0));
                AnsiConsole.WriteLine();

                // In plan mode, only allow read operations
                if (mode == AgentMode.Plan && fileResult.ToolName is "write_file" or "delete_file" or "create_dir")
                {
                    AnsiConsole.MarkupLine($"  [red bold]✗ Blocked:[/] [dim]Write operations are not allowed in plan mode[/]");
                    var blockMsg = new Message
                    {
                        SessionId = _session.Id,
                        Role      = "tool_result",
                        Content   = "ERROR: Write operations are blocked in plan mode. You can only use read_file and list_dir."
                    };
                    _history.Add(blockMsg);
                    _db.SaveMessage(blockMsg);
                    continue;
                }

                // Show result
                var icon = fileResult.Success ? "✓" : "✗";
                var color = fileResult.Success ? "green" : "red";
                AnsiConsole.MarkupLine($"  [{color}]{icon}[/] [dim]{fileResult.Output.Split('\n')[0]}[/]");
                
                if (fileResult.Output.Contains('\n'))
                {
                    var lines = fileResult.Output.Split('\n')[1..];
                    foreach (var line in lines)
                        AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(line)}[/]");
                }
                AnsiConsole.WriteLine();

                // Track file changes
                if (fileResult.Success && fileResult.FilePath != null)
                {
                    _fileTracker.TrackCommand(_session.Id, 
                        $"{fileResult.ToolName}: {fileResult.FilePath}", 
                        fileResult.Output, 
                        fileResult.Success ? 0 : 1);
                    
                    if (mode == AgentMode.Execute)
                        _planStateTracker.ReconcileWithFilesystem(_session.Id);
                }

                var fileToolMsg = new Message
                {
                    SessionId = _session.Id,
                    Role      = "tool_result",
                    Content   = fileResult.Output
                };
                _history.Add(fileToolMsg);
                _db.SaveMessage(fileToolMsg);
                continue;
            }

            // ── Handle Context7 Tool commands ───────────────────────
            if (actionType == ActionParser.ActionType.Context7Tool)
            {
                var (ctx7Result, _) = await _context7Tools.ParseAndExecuteAsync(llmResponse);
                
                // Show context7 tool panel
                AnsiConsole.Write(
                    new Panel(new Markup($"[{GoldLit}]{ctx7Result.ToolName}[/]"))
                        .Header($"[{Gold} bold] context7 tool [/]")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(new Color(240, 170, 0))
                        .Padding(1, 0));
                AnsiConsole.WriteLine();

                // Show result
                var icon = ctx7Result.Success ? "✓" : "✗";
                var color = ctx7Result.Success ? "green" : "red";
                AnsiConsole.MarkupLine($"  [{color}]{icon}[/] [dim]{ctx7Result.Output.Split('\n')[0]}[/]");
                
                if (ctx7Result.Output.Contains('\n'))
                {
                    var lines = ctx7Result.Output.Split('\n')[1..];
                    foreach (var line in lines)
                        AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(line)}[/]");
                }
                AnsiConsole.WriteLine();

                var ctx7ToolMsg = new Message
                {
                    SessionId = _session.Id,
                    Role      = "tool_result",
                    Content   = ctx7Result.Output
                };
                _history.Add(ctx7ToolMsg);
                _db.SaveMessage(ctx7ToolMsg);
                continue;
            }

            // ── Handle Bash commands ─────────────────────────────────
            if (ActionParser.IsExit(command!))
            {
                if (mode == AgentMode.Plan)
                {
                    // Plan mode exit — check if agent was asking questions
                    if (!string.IsNullOrWhiteSpace(textPart) && textPart.Contains('?') && !textPart.Contains("## Plan"))
                    {
                        _awaitingClarification = true;
                        AnsiConsole.MarkupLine($"  [{Gold}]↑[/] [dim]Answer the questions above, then type your response.[/]");
                        AnsiConsole.MarkupLine($"  [dim]  (or type [/][{Gold}]/plan continue[/][dim] to proceed)[/]");
                        AnsiConsole.WriteLine();
                    }
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
                    Content   = "ERROR: Write commands are blocked in plan mode. You can only use read-only commands (ls, cat, grep, find, tree, ctx7, etc). Do NOT try to create, edit, or delete files."
                };
                _history.Add(blockMsg);
                _db.SaveMessage(blockMsg);
                continue;
            }

            // ── Doom loop detection ───────────────────────────────
            var cmdTrimmed = command.Trim();
            recentCommands.Add(cmdTrimmed);
            if (recentCommands.Count > DoomLoopThreshold)
                recentCommands.RemoveAt(0);

            if (recentCommands.Count == DoomLoopThreshold && recentCommands.All(c => c == cmdTrimmed))
            {
                AnsiConsole.MarkupLine($"  [{Gold}]⚠[/] [dim]Agent is repeating the exact same command {DoomLoopThreshold} times. Returning control to you.[/]");
                AnsiConsole.WriteLine();
                break;
            }

            // ── Show command panel ────────────────────────────────
            AnsiConsole.Write(
                new Panel(new Markup($"[{GoldLit}]{Markup.Escape(command)}[/]"))
                    .Header($"[{Gold} bold] bash [/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(new Color(240, 170, 0))
                    .Padding(1, 0));
            AnsiConsole.WriteLine();

            bool confirmed;

            // ── Sandbox: restrict bash commands to workspace only ──
            if (!IsCommandWorkspaceSafe(command, out var sandboxReason))
            {
                AnsiConsole.MarkupLine($"  [red bold]✗ Blocked:[/] [dim]Command is outside workspace sandbox[/]");
                AnsiConsole.MarkupLine($"  [dim]Reason: {Markup.Escape(sandboxReason)}[/]");
                AnsiConsole.WriteLine();

                var blockMsg = new Message
                {
                    SessionId = _session.Id,
                    Role      = "tool_result",
                    Content   = $"ERROR: Command blocked by workspace sandbox. Reason: {sandboxReason}. Allowed root: {_workspaceRoot}"
                };
                _history.Add(blockMsg);
                _db.SaveMessage(blockMsg);
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

            // Track file changes from this command
            _fileTracker.TrackCommand(_session.Id, command, result.Output, result.ExitCode);

            // Mark planned steps as done when files are created
            if (result.ExitCode == 0 && mode == AgentMode.Execute)
            {
                _planStateTracker.ReconcileWithFilesystem(_session.Id);
            }

            AnsiConsole.WriteLine();
            var exitIcon  = result.ExitCode == 0 ? "✓" : "✗";
            var exitColor = result.ExitCode == 0 ? "green" : "red";
            var timedOut  = result.TimedOut ? " [red]· timed out[/]" : "";

            AnsiConsole.MarkupLine(
                $"  [{exitColor}]{exitIcon}[/] [dim]exit {result.ExitCode} · {result.DurationMs}ms{timedOut}[/]");
            AnsiConsole.WriteLine();

            var toolContent = string.IsNullOrWhiteSpace(result.Output)
                ? "Command executed successfully. No output."
                : TruncateToolOutput(result.Output, result.ExitCode);

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

    /// <summary>
    /// Truncate plan to only show pending steps (based on planned_steps status).
    /// This reduces context size for faster LLM responses.
    /// </summary>
    private string TruncatePlanToPending(string fullPlan)
    {
        var steps = _db.GetPlannedSteps(_session!.Id).ToList();
        var pendingSteps = steps.Where(s => s.Status == "pending").ToList();
        
        if (pendingSteps.Count == 0)
        {
            // All done - just show goal and verification
            var goalMatch = Regex.Match(fullPlan, @"\*\*Goal:\*\*\s*(.+?)(?=\n|$)");
            var goal = goalMatch.Success ? goalMatch.Value : "";
            return $"{goal}\n\n**All steps completed.** Run verification commands if any.";
        }

        // Build minimal plan with only pending steps
        var sb = new StringBuilder();
        
        // Extract goal
        var goalMatch2 = Regex.Match(fullPlan, @"\*\*Goal:\*\*\s*(.+?)(?=\n|$)");
        if (goalMatch2.Success)
            sb.AppendLine(goalMatch2.Value);
        
        // Extract tech stack
        var techMatch = Regex.Match(fullPlan, @"\*\*Tech Stack:\*\*\s*(.+?)(?=\n|$)");
        if (techMatch.Success)
            sb.AppendLine(techMatch.Value);
        
        sb.AppendLine();
        sb.AppendLine($"**Pending Steps:** {pendingSteps.Count} remaining");
        sb.AppendLine();
        
        foreach (var step in pendingSteps.Take(3)) // Show max 3 pending steps
        {
            sb.AppendLine($"### Step {step.StepNumber}: {step.Description}");
            sb.AppendLine($"- **File:** `{step.FilePath}`");
            sb.AppendLine($"- **Action:** {step.Action}");
            sb.AppendLine();
        }
        
        if (pendingSteps.Count > 3)
            sb.AppendLine($"... and {pendingSteps.Count - 3} more pending steps.");
        
        // Extract verification section
        var verifyMatch = Regex.Match(fullPlan, @"### Verification[\s\S]*?(?=### Risks|$)");
        if (verifyMatch.Success)
        {
            sb.AppendLine();
            sb.AppendLine(verifyMatch.Value.Trim());
        }

        return sb.ToString();
    }

    // ── Tool output truncation ────────────────────────────────────

    /// <summary>
    /// Truncate tool output to reduce tokens while preserving key information.
    /// For errors, keep the tail (where the actual error usually is).
    /// For success, keep head + tail with a middle truncation indicator.
    /// </summary>
    private static string TruncateToolOutput(string output, int exitCode)
    {
        const int maxChars = 2000; // Reduced from 4000 for faster responses
        const int tailChars = 800; // Keep this much from the end for errors

        if (output.Length <= maxChars)
            return output;

        // For errors, prioritize the tail (error messages are usually at the end)
        if (exitCode != 0)
        {
            var errorTail = output[^tailChars..];
            return $"[Output truncated - showing last {tailChars} chars]\n...{errorTail}";
        }

        // For success, show head + tail
        var head = output[..(maxChars / 2)];
        var tail = output[^(maxChars / 2)..];
        return $"{head}\n\n... [output truncated, {output.Length - maxChars} chars omitted] ...\n\n{tail}";
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

            // Safety: only allow npx for ctx7 commands
            if (firstWord.Equals("npx", StringComparison.OrdinalIgnoreCase))
            {
                var args = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                // Allow: npx ctx7 ..., npx -y @upstash/context7-mcp ...
                var hasCtx7 = args.Any(a =>
                    a.Contains("ctx7", StringComparison.OrdinalIgnoreCase) ||
                    a.Contains("context7", StringComparison.OrdinalIgnoreCase));
                if (!hasCtx7) return false;
                continue;
            }

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

    private static void PrintTokenUsage(TokenUsage usage, LlmMetrics? metrics = null)
    {
        if (usage.Total == 0) return;

        var text = $"tokens: ↑ {usage.PromptTokens:N0}  ↓ {usage.CompletionTokens:N0}  Σ {usage.Total:N0}";
        
        if (metrics != null)
        {
            text += $"  │  TTFT: {metrics.TimeToFirstTokenMs:N0}ms  Total: {metrics.TotalDurationMs:N0}ms";
        }
        
        var termWidth = Console.WindowWidth > 0 ? Console.WindowWidth : 120;
        var padding = Math.Max(0, termWidth - text.Length - 2);

        AnsiConsole.MarkupLine($"{new string(' ', padding)}[dim]{Markup.Escape(text)}[/]");
    }
}