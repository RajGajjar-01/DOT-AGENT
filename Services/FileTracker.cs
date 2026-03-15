using System.Text;
using System.Text.RegularExpressions;
using DotAgent.Data;
using DotAgent.Models;

namespace DotAgent.Services;

public class FileTracker
{
    private readonly Database _db;

    // Patterns that indicate file creation
    private static readonly Regex[] CreatePatterns =
    [
        new(@"cat\s+<<\s*'?EOF'?\s*>\s*(.+)", RegexOptions.Compiled),           // cat << 'EOF' > file
        new(@"cat\s+<<\s*""?EOF""?\s*>\s*(.+)", RegexOptions.Compiled),          // cat << "EOF" > file
        new(@"cat\s+<<-?\s*'?\w+'?\s*>\s*(.+)", RegexOptions.Compiled),          // cat <<HEREDOC > file
        new(@"(?:echo|printf)\s+.+?>\s*(.+)", RegexOptions.Compiled),            // echo "..." > file
        new(@"tee\s+(.+?)(?:\s|$)", RegexOptions.Compiled),                      // tee file
        new(@"touch\s+(.+?)(?:\s|$)", RegexOptions.Compiled),                    // touch file
        new(@"cp\s+\S+\s+(.+?)(?:\s|$)", RegexOptions.Compiled),                // cp src dest
    ];

    // Patterns that indicate file modification
    private static readonly Regex[] ModifyPatterns =
    [
        new(@"sed\s+-i\s+.+?\s+(.+?)(?:\s|$)", RegexOptions.Compiled),          // sed -i 's/...' file
        new(@">>\s*(.+)", RegexOptions.Compiled),                                // >> file (append)
    ];

    // Patterns that indicate file deletion
    private static readonly Regex[] DeletePatterns =
    [
        new(@"rm\s+(?:-[rf]+\s+)?(.+?)(?:\s|$)", RegexOptions.Compiled),        // rm [-rf] file
    ];

    // Patterns that indicate directory creation
    private static readonly Regex[] MkdirPatterns =
    [
        new(@"mkdir\s+(?:-p\s+)?(.+?)(?:\s|$)", RegexOptions.Compiled),          // mkdir [-p] dir
    ];

    public FileTracker(Database db)
    {
        _db = db;
    }

    /// <summary>
    /// Analyze a command to detect file operations and record them.
    /// </summary>
    public void TrackCommand(string sessionId, string command, string output, int exitCode)
    {
        // Only track successful commands
        if (exitCode != 0) return;

        var changes = new List<FileChange>();

        // Check for file creation patterns
        foreach (var pattern in CreatePatterns)
        {
            var match = pattern.Match(command);
            if (match.Success)
            {
                var filePath = CleanPath(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    changes.Add(new FileChange
                    {
                        SessionId = sessionId,
                        FilePath  = filePath,
                        Action    = "created",
                        Summary   = $"Created via: {TruncateCommand(command)}"
                    });
                }
            }
        }

        // Check for file modification patterns
        foreach (var pattern in ModifyPatterns)
        {
            var match = pattern.Match(command);
            if (match.Success)
            {
                var filePath = CleanPath(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    changes.Add(new FileChange
                    {
                        SessionId = sessionId,
                        FilePath  = filePath,
                        Action    = "modified",
                        Summary   = $"Modified via: {TruncateCommand(command)}"
                    });
                }
            }
        }

        // Check for file deletion patterns
        foreach (var pattern in DeletePatterns)
        {
            var match = pattern.Match(command);
            if (match.Success)
            {
                var filePath = CleanPath(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    changes.Add(new FileChange
                    {
                        SessionId = sessionId,
                        FilePath  = filePath,
                        Action    = "deleted",
                        Summary   = $"Deleted via: {TruncateCommand(command)}"
                    });
                }
            }
        }

        // Check for directory creation
        foreach (var pattern in MkdirPatterns)
        {
            var match = pattern.Match(command);
            if (match.Success)
            {
                var dirPath = CleanPath(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(dirPath))
                {
                    changes.Add(new FileChange
                    {
                        SessionId = sessionId,
                        FilePath  = dirPath,
                        Action    = "created (dir)",
                        Summary   = $"Directory created via: {TruncateCommand(command)}"
                    });
                }
            }
        }

        // Save all detected changes
        foreach (var change in changes)
        {
            _db.SaveFileChange(change);
        }
    }

    /// <summary>
    /// Build a formatted manifest of all files changed in a session.
    /// </summary>
    public string BuildFileManifest(string sessionId)
    {
        var changes = _db.GetFileChanges(sessionId).ToList();
        if (changes.Count == 0) return "";

        var sb = new StringBuilder();

        // Group by file path, show latest action per file
        var byFile = changes
            .GroupBy(c => c.FilePath)
            .Select(g => g.Last()) // Take most recent action for each file
            .ToList();

        foreach (var fc in byFile)
        {
            var icon = fc.Action switch
            {
                "created"      => "✚",
                "modified"     => "✎",
                "deleted"      => "✗",
                "created (dir)" => "📁",
                _              => "•"
            };
            sb.AppendLine($"{icon} [{fc.Action}] {fc.FilePath}");
        }

        sb.AppendLine();
        sb.AppendLine($"Total: {byFile.Count} file(s) tracked in this session.");

        return sb.ToString();
    }

    private static string CleanPath(string path)
    {
        // Remove quotes, trailing whitespace, and common artifacts
        return path
            .Trim()
            .Trim('\'', '"')
            .TrimEnd(';', '&', '|', ' ')
            .Trim();
    }

    private static string TruncateCommand(string command)
    {
        var firstLine = command.Split('\n')[0].Trim();
        return firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine;
    }
}
