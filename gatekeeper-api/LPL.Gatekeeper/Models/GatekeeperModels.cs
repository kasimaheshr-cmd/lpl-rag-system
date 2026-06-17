namespace LPL.Gatekeeper.Models;

// What the advisor sends to /login
public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

// What your system knows about each advisor
// In production this comes from Active Directory / Cognito
public class AdvisorProfile
{
    public string UserId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Department { get; set; } = "";
    public string BranchCode { get; set; } = "";
}

public class QuestionRequest
{
    public string Question { get; set; } = "";
    public string SessionId { get; set; } = "";
}

public class AnswerResponse
{
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public List<string> Sources { get; set; } = new();
    public string Department { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string SearchType { get; set; } = "";
    public AuditInfo? Audit { get; set; }
}

public class AuditInfo
{
    public string RequestId { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

// Audit log entry returned from the compliance endpoint
// Compliance officers query this to review advisor activity
public class AuditLogEntry
{
    public string RequestId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Department { get; set; } = "";
    public string Question { get; set; } = "";
    public List<string> Sources { get; set; } = new();
    public long DurationMs { get; set; }
    public DateTime Timestamp { get; set; }
    public string Status { get; set; } = "success";
}

// Document deletion request — Admin only
public class DeleteDocumentResponse
{
    public string Status { get; set; } = "";
    public string Source { get; set; } = "";
    public int ChunksRemoved { get; set; }
    public string DeletedBy { get; set; } = "";
    public DateTime Timestamp { get; set; }
}