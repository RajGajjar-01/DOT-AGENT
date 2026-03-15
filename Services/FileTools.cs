using System.Text;
using System.Text.RegularExpressions;

namespace DotAgent.Services;

/// <summary>
/// Provides direct file access tools that bypass bash.
/// The agent outputs structured XML-like commands that are parsed and executed directly.
/// </summary>
public class FileTools
{
    private readonly string _workspaceRoot;

    public FileTools()
    {
        var root = Environment.GetEnvironmentVariable("WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        _workspaceRoot = Path.GetFullPath(root);
    }

    // ── Tool Result Types ─────────────────────────────────────────

    public record FileToolResult(
        string ToolName,
        bool Success,
        string Output,
        string? FilePath = null);

    // ── Parse and Execute Tool Commands ────────────────────────────

    /// <summary>
    /// Check if the LLM output contains a file tool command.
    /// </summary>
    public static bool HasFileTool(string output)
    {
        return Regex.IsMatch(output, @"<(read_file|write_file|list_dir|delete_file|create_dir)\b",
            RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Extract and execute the first file tool command from LLM output.
    /// Returns the result and the remaining output text.
    /// </summary>
    public (FileToolResult result, string remainingOutput) ParseAndExecute(string output)
    {
        // Try to match each tool type

        // <read_file>path</read_file>
        var readMatch = Regex.Match(output, @"<read_file>\s*(.+?)\s*</read_file>", 
            RegexOptions.Singleline);
        if (readMatch.Success)
        {
            var path = readMatch.Groups[1].Value.Trim();
            var result = ReadFile(path);
            var remaining = output.Remove(readMatch.Index, readMatch.Length);
            return (result, remaining);
        }

        // <write_file path="...">content</write_file>
        var writeMatch = Regex.Match(output, 
            @"<write_file\s+path=""([^""]+)\""\s*>(.*?)</write_file>", 
            RegexOptions.Singleline);
        if (writeMatch.Success)
        {
            var path = writeMatch.Groups[1].Value.Trim();
            var content = writeMatch.Groups[2].Value;
            var result = WriteFile(path, content);
            var remaining = output.Remove(writeMatch.Index, writeMatch.Length);
            return (result, remaining);
        }

        // <write_file path='...'>content</write_file> (single quotes)
        var writeMatch2 = Regex.Match(output, 
            @"<write_file\s+path='([^']+)'\s*>(.*?)</write_file>", 
            RegexOptions.Singleline);
        if (writeMatch2.Success)
        {
            var path = writeMatch2.Groups[1].Value.Trim();
            var content = writeMatch2.Groups[2].Value;
            var result = WriteFile(path, content);
            var remaining = output.Remove(writeMatch2.Index, writeMatch2.Length);
            return (result, remaining);
        }

        // <list_dir>path</list_dir>
        var listMatch = Regex.Match(output, @"<list_dir>\s*(.+?)\s*</list_dir>", 
            RegexOptions.Singleline);
        if (listMatch.Success)
        {
            var path = listMatch.Groups[1].Value.Trim();
            var result = ListDir(path);
            var remaining = output.Remove(listMatch.Index, listMatch.Length);
            return (result, remaining);
        }

        // <create_dir>path</create_dir>
        var createDirMatch = Regex.Match(output, @"<create_dir>\s*(.+?)\s*</create_dir>", 
            RegexOptions.Singleline);
        if (createDirMatch.Success)
        {
            var path = createDirMatch.Groups[1].Value.Trim();
            var result = CreateDir(path);
            var remaining = output.Remove(createDirMatch.Index, createDirMatch.Length);
            return (result, remaining);
        }

        // <delete_file>path</delete_file>
        var deleteMatch = Regex.Match(output, @"<delete_file>\s*(.+?)\s*</delete_file>", 
            RegexOptions.Singleline);
        if (deleteMatch.Success)
        {
            var path = deleteMatch.Groups[1].Value.Trim();
            var result = DeleteFile(path);
            var remaining = output.Remove(deleteMatch.Index, deleteMatch.Length);
            return (result, remaining);
        }

        return (new FileToolResult("unknown", false, "No file tool command found"), output);
    }

    // ── Tool Implementations ──────────────────────────────────────

    public FileToolResult ReadFile(string relativePath)
    {
        try
        {
            var fullPath = ResolvePath(relativePath);
            
            if (!File.Exists(fullPath))
                return new FileToolResult("read_file", false, 
                    $"File not found: {relativePath}\nResolved path: {fullPath}");

            var content = File.ReadAllText(fullPath);
            var size = content.Length;
            
            return new FileToolResult("read_file", true, 
                $"File: {relativePath} ({size} chars)\n{new string('─', 40)}\n{content}",
                fullPath);
        }
        catch (Exception ex)
        {
            return new FileToolResult("read_file", false, 
                $"Error reading file: {ex.Message}");
        }
    }

    public FileToolResult WriteFile(string relativePath, string content)
    {
        try
        {
            var fullPath = ResolvePath(relativePath);
            var directory = Path.GetDirectoryName(fullPath);

            // Create directory if needed
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Check if file exists (for tracking)
            var exists = File.Exists(fullPath);

            File.WriteAllText(fullPath, content);
            var size = content.Length;
            var action = exists ? "modified" : "created";

            return new FileToolResult("write_file", true, 
                $"✓ File {action}: {relativePath} ({size} chars written)",
                fullPath);
        }
        catch (Exception ex)
        {
            return new FileToolResult("write_file", false, 
                $"Error writing file: {ex.Message}");
        }
    }

    public FileToolResult ListDir(string relativePath)
    {
        try
        {
            var fullPath = ResolvePath(relativePath);

            if (!Directory.Exists(fullPath))
                return new FileToolResult("list_dir", false, 
                    $"Directory not found: {relativePath}");

            var sb = new StringBuilder();
            sb.AppendLine($"Directory: {relativePath}");
            sb.AppendLine(new string('─', 40));

            var dirs = Directory.GetDirectories(fullPath)
                .OrderBy(d => d)
                .Select(d => $"📁 {Path.GetFileName(d)}/");
            
            var files = Directory.GetFiles(fullPath)
                .OrderBy(f => f)
                .Select(f => 
                {
                    var info = new FileInfo(f);
                    return $"📄 {Path.GetFileName(f)} ({FormatSize(info.Length)})";
                });

            foreach (var d in dirs) sb.AppendLine(d);
            foreach (var f in files) sb.AppendLine(f);

            var count = dirs.Count() + files.Count();
            sb.AppendLine($"\n{count} items total");

            return new FileToolResult("list_dir", true, sb.ToString());
        }
        catch (Exception ex)
        {
            return new FileToolResult("list_dir", false, 
                $"Error listing directory: {ex.Message}");
        }
    }

    public FileToolResult CreateDir(string relativePath)
    {
        try
        {
            var fullPath = ResolvePath(relativePath);
            
            if (Directory.Exists(fullPath))
                return new FileToolResult("create_dir", true, 
                    $"Directory already exists: {relativePath}");

            Directory.CreateDirectory(fullPath);
            return new FileToolResult("create_dir", true, 
                $"✓ Directory created: {relativePath}",
                fullPath);
        }
        catch (Exception ex)
        {
            return new FileToolResult("create_dir", false, 
                $"Error creating directory: {ex.Message}");
        }
    }

    public FileToolResult DeleteFile(string relativePath)
    {
        try
        {
            var fullPath = ResolvePath(relativePath);

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return new FileToolResult("delete_file", true, 
                    $"✓ File deleted: {relativePath}",
                    fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
                return new FileToolResult("delete_file", true, 
                    $"✓ Directory deleted: {relativePath}",
                    fullPath);
            }
            else
            {
                return new FileToolResult("delete_file", false, 
                    $"File or directory not found: {relativePath}");
            }
        }
        catch (Exception ex)
        {
            return new FileToolResult("delete_file", false, 
                $"Error deleting: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("Path cannot be empty");

        // Normalize separators and trim
        relativePath = relativePath.Trim();

        // If user provided an absolute path, only allow it when it is inside workspace.
        if (Path.IsPathRooted(relativePath))
        {
            var full = Path.GetFullPath(relativePath);
            if (IsWithinWorkspace(full))
                return full;

            throw new UnauthorizedAccessException(
                $"Access denied. Path is outside workspace. Workspace: {_workspaceRoot}, Path: {full}");
        }

        // Resolve relative paths against workspace, then validate it doesn't escape.
        var combined = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));
        if (!IsWithinWorkspace(combined))
        {
            throw new UnauthorizedAccessException(
                $"Access denied. Path traversal outside workspace. Workspace: {_workspaceRoot}, Path: {combined}");
        }

        return combined;
    }

    private bool IsWithinWorkspace(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        var root = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.Ordinal);
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:0.#}{suffixes[i]}";
    }
}
