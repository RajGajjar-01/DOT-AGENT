namespace DotAgent.Models;

public class Session
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "New session";
    public string Status { get; set; } = "active";
    public string Plan { get; set; } = "";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

public class Message
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

public class Execution
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public long DurationMs { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

public class FileChange
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // "created", "modified", "deleted"
    public string Summary { get; set; } = string.Empty;
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

public class PlannedStep
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public int StepNumber { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // "create", "modify", "delete"
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "pending"; // "pending", "done", "skipped", "failed"
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}