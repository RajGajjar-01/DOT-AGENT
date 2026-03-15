using System.Text;
using System.Text.RegularExpressions;
using DotAgent.Data;
using DotAgent.Models;

namespace DotAgent.Services;

/// <summary>
/// Tracks planned files, reconciles with filesystem, and builds manifests for the agent.
/// This solves the "agent forgets what files it planned to create" problem.
/// </summary>
public class PlanStateTracker
{
    private readonly Database _db;
    private readonly string _workspaceRoot;

    // Regex patterns to extract steps from plan markdown
    private static readonly Regex StepPattern = new(
        @"###\s+Step\s+(\d+):\s*(.+?)(?=\n|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex FilePattern = new(
        @"-\s*\*\*File:\*\*\s*`?([^`\n]+)`?",
        RegexOptions.Compiled);

    private static readonly Regex ActionPattern = new(
        @"-\s*\*\*Action:\*\*\s*(create|modify|delete)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DescriptionPattern = new(
        @"-\s*\*\*What:\*\*\s*(.+?)(?=\n|$)",
        RegexOptions.Compiled);

    public PlanStateTracker(Database db)
    {
        _db = db;
        _workspaceRoot = Environment.GetEnvironmentVariable("WORKSPACE")
            ?? Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Parse a plan markdown document into structured steps and save to DB.
    /// </summary>
    public void ParseAndSavePlan(string sessionId, string planMarkdown)
    {
        // Clear existing steps for this session
        _db.ClearPlannedSteps(sessionId);

        var steps = ParsePlanSteps(planMarkdown);
        foreach (var step in steps)
        {
            step.SessionId = sessionId;
            _db.SavePlannedStep(step);
        }
    }

    /// <summary>
    /// Parse plan markdown into a list of PlannedStep objects.
    /// </summary>
    public List<PlannedStep> ParsePlanSteps(string planMarkdown)
    {
        var steps = new List<PlannedStep>();

        // Split by step headers
        var stepMatches = StepPattern.Matches(planMarkdown);

        for (int i = 0; i < stepMatches.Count; i++)
        {
            var match = stepMatches[i];
            var stepNumber = int.Parse(match.Groups[1].Value);
            var stepTitle = match.Groups[2].Value.Trim();

            // Get the content for this step (until next step or end)
            var startIndex = match.Index + match.Length;
            var endIndex = i + 1 < stepMatches.Count
                ? stepMatches[i + 1].Index
                : planMarkdown.Length;

            var stepContent = planMarkdown.Substring(startIndex, endIndex - startIndex);

            // Extract file path
            var fileMatch = FilePattern.Match(stepContent);
            var filePath = fileMatch.Success
                ? fileMatch.Groups[1].Value.Trim().Trim('`')
                : "";

            // Extract action
            var actionMatch = ActionPattern.Match(stepContent);
            var action = actionMatch.Success
                ? actionMatch.Groups[1].Value.ToLower()
                : "create";

            // Extract description
            var descMatch = DescriptionPattern.Match(stepContent);
            var description = descMatch.Success
                ? descMatch.Groups[1].Value.Trim()
                : stepTitle;

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                steps.Add(new PlannedStep
                {
                    StepNumber = stepNumber,
                    FilePath = filePath,
                    Action = action,
                    Description = description,
                    Status = "pending"
                });
            }
        }

        return steps;
    }

    /// <summary>
    /// Reconcile planned steps with actual filesystem state.
    /// Updates status: pending -> done if file exists, etc.
    /// </summary>
    public void ReconcileWithFilesystem(string sessionId)
    {
        var steps = _db.GetPlannedSteps(sessionId).ToList();
        var fileChanges = _db.GetFileChanges(sessionId).ToList();

        foreach (var step in steps)
        {
            var fullPath = GetFullPath(step.FilePath);
            var existsOnDisk = File.Exists(fullPath) || Directory.Exists(fullPath);

            // Check if we already tracked this file as created/modified
            var wasTracked = fileChanges.Any(fc =>
                fc.FilePath.Equals(step.FilePath, StringComparison.OrdinalIgnoreCase) ||
                fc.FilePath.EndsWith(step.FilePath, StringComparison.OrdinalIgnoreCase));

            if (step.Status == "pending")
            {
                if (existsOnDisk || wasTracked)
                {
                    _db.UpdatePlannedStepStatus(step.Id, "done");
                }
            }
            else if (step.Status == "done" && !existsOnDisk && !wasTracked)
            {
                // File was deleted externally, mark as pending again
                _db.UpdatePlannedStepStatus(step.Id, "pending");
            }
        }
    }

    /// <summary>
    /// Mark a step as done when a file is created/modified.
    /// </summary>
    public void MarkStepDone(string sessionId, string filePath)
    {
        var step = _db.GetPlannedStepByPath(sessionId, filePath);
        if (step != null && step.Status == "pending")
        {
            _db.UpdatePlannedStepStatus(step.Id, "done");
        }
    }

    /// <summary>
    /// Build a manifest showing planned files with their status.
    /// This gets injected into the system prompt so the agent knows what's pending.
    /// </summary>
    public string BuildPlannedManifest(string sessionId)
    {
        var steps = _db.GetPlannedSteps(sessionId).ToList();
        if (steps.Count == 0) return "";

        var sb = new StringBuilder();

        var pending = steps.Where(s => s.Status == "pending").ToList();
        var done = steps.Where(s => s.Status == "done").ToList();
        var skipped = steps.Where(s => s.Status == "skipped").ToList();

        if (pending.Count > 0)
        {
            sb.AppendLine("### Pending Files (NOT YET CREATED)");
            sb.AppendLine();
            foreach (var step in pending)
            {
                sb.AppendLine($"  ○ [{step.Action}] `{step.FilePath}` — {step.Description}");
            }
            sb.AppendLine();
        }

        if (done.Count > 0)
        {
            sb.AppendLine("### Completed Files");
            sb.AppendLine();
            foreach (var step in done)
            {
                sb.AppendLine($"  ✓ [{step.Action}] `{step.FilePath}`");
            }
            sb.AppendLine();
        }

        if (skipped.Count > 0)
        {
            sb.AppendLine("### Skipped Files");
            sb.AppendLine();
            foreach (var step in skipped)
            {
                sb.AppendLine($"  ⊘ [{step.Action}] `{step.FilePath}` — skipped");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Total: {pending.Count} pending, {done.Count} done, {skipped.Count} skipped");

        return sb.ToString();
    }

    /// <summary>
    /// Get the next pending step to execute.
    /// </summary>
    public PlannedStep? GetNextPendingStep(string sessionId)
    {
        var steps = _db.GetPlannedSteps(sessionId);
        return steps.FirstOrDefault(s => s.Status == "pending");
    }

    /// <summary>
    /// Check if all planned steps are complete.
    /// </summary>
    public bool IsPlanComplete(string sessionId)
    {
        var steps = _db.GetPlannedSteps(sessionId);
        return steps.All(s => s.Status != "pending");
    }

    private string GetFullPath(string relativePath)
    {
        // Handle various path formats
        relativePath = relativePath.TrimStart('/', '\\');
        return Path.Combine(_workspaceRoot, relativePath);
    }
}
