using LPL.Gatekeeper.Models;

namespace LPL.Gatekeeper.Services;

// ─── Interface ────────────────────────────────────────────────
public interface IAuditService
{
    void LogRequest(string requestId, string userId,
        string department, string question);

    void LogResponse(string requestId, string answer,
        List<string> sources, long durationMs);

    void LogRejection(string requestId, string userId,
        string reason, string question);

    // New — used by the compliance endpoint
    List<AuditLogEntry> GetRecentLogs(int count = 50);
    List<AuditLogEntry> GetLogsForUser(string userId);
}

// ─── In-memory audit store ────────────────────────────────────
// Week 6 replaces this with MongoDB for persistence.
// The interface stays identical — controllers never change.
// This is the Repository pattern applied to audit logs.
public class AuditService : IAuditService
{
    private readonly ILogger<AuditService> _logger;

    // Thread-safe list — multiple requests hit this simultaneously
    // In production: MongoDB collection or AWS CloudWatch
    private readonly System.Collections.Concurrent.ConcurrentQueue<AuditLogEntry>
        _entries = new();

    // Keep last 1000 entries in memory
    // Production: no limit (MongoDB handles retention)
    private const int MaxEntries = 1000;

    public AuditService(ILogger<AuditService> logger)
    {
        _logger = logger;
    }

    public void LogRequest(string requestId, string userId,
        string department, string question)
    {
        // Structured logging — each {Property} is searchable
        // in CloudWatch, Datadog, Seq, etc.
        _logger.LogInformation(
            "[AUDIT] REQUEST | Id:{RequestId} | User:{UserId} | " +
            "Dept:{Department} | Question:{Question}",
            requestId, userId, department, question);

        // Store for compliance queries
        var entry = new AuditLogEntry
        {
            RequestId  = requestId,
            UserId     = userId,
            Department = department,
            Question   = question,
            Timestamp  = DateTime.UtcNow,
            Status     = "in-progress"
        };

        _entries.Enqueue(entry);
        TrimIfNeeded();
    }

    public void LogResponse(string requestId, string answer,
        List<string> sources, long durationMs)
    {
        _logger.LogInformation(
            "[AUDIT] RESPONSE | Id:{RequestId} | Sources:{Sources} | " +
            "Duration:{DurationMs}ms | AnswerLength:{Length}",
            requestId,
            string.Join(",", sources),
            durationMs,
            answer.Length);

        // Update the matching entry with response data
        // ConcurrentQueue doesn't support update-in-place —
        // we rebuild the list. In MongoDB this is a simple $set.
        var updated = _entries
            .Select(e =>
            {
                if (e.RequestId == requestId)
                {
                    e.Sources    = sources;
                    e.DurationMs = durationMs;
                    e.Status     = "success";
                }
                return e;
            }).ToList();
    }

    public void LogRejection(string requestId, string userId,
        string reason, string question)
    {
        _logger.LogWarning(
            "[AUDIT] REJECTED | Id:{RequestId} | User:{UserId} | " +
            "Reason:{Reason} | Question:{Question}",
            requestId, userId, reason, question);

        _entries.Enqueue(new AuditLogEntry
        {
            RequestId = requestId,
            UserId    = userId,
            Question  = question,
            Timestamp = DateTime.UtcNow,
            Status    = $"rejected:{reason}"
        });
    }

    // Returns most recent N entries across all users
    // Used by: mike.compliance to see what's happening
    public List<AuditLogEntry> GetRecentLogs(int count = 50)
        => _entries
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();

    // Returns all entries for a specific advisor
    // Used by: branch manager reviewing specific advisor
    public List<AuditLogEntry> GetLogsForUser(string userId)
        => _entries
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.Timestamp)
            .ToList();

    private void TrimIfNeeded()
    {
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
    }
}
