using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LPL.Gatekeeper.Models;

namespace LPL.Gatekeeper.Services;

public sealed record PIIDetectionResult(bool ContainsPII, IReadOnlyList<string> PIITypes);

public interface IAuditService
{
    void LogRejection(string requestId, string userId, string reason, string redacted);
    void LogRequest(string requestId, string userId, string question, string department);
    void LogResponse(string requestId, string answer, IReadOnlyList<string> sources, long elapsedMs);

    IReadOnlyList<AuditLogEntry> GetRecentLogs(int count);
    IReadOnlyList<AuditLogEntry> GetLogsForUser(string userId);
}

public sealed class AuditService : IAuditService
{
    private readonly ILogger<AuditService> _logger;
    private readonly ConcurrentDictionary<string, AuditLogEntry> _byRequestId = new();
    private readonly ConcurrentQueue<string> _requestOrder = new();
    private const int MaxEntries = 10_000;

    public AuditService(ILogger<AuditService> logger) => _logger = logger;

    public void LogRejection(string requestId, string userId, string reason, string redacted)
    {
        _logger.LogWarning(
            "Audit reject {RequestId} User:{UserId} {Reason} Body:{Redacted}",
            requestId, userId, reason, redacted);

        Upsert(requestId, entry =>
        {
            entry.UserId = userId;
            entry.Status = $"rejected:{reason}";
            entry.Question = redacted;
            entry.Timestamp = DateTime.UtcNow;
        });
    }

    public void LogRequest(string requestId, string userId, string question, string department)
    {
        _logger.LogInformation(
            "Audit request {RequestId} User:{UserId} Dept:{Dept} Qlen:{Len}",
            requestId, userId, department, question.Length);

        Upsert(requestId, entry =>
        {
            entry.RequestId = requestId;
            entry.UserId = userId;
            entry.Department = department;
            entry.Question = question;
            entry.Timestamp = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(entry.Status)) entry.Status = "pending";
        });
    }

    public void LogResponse(string requestId, string answer, IReadOnlyList<string> sources, long elapsedMs)
    {
        _logger.LogInformation(
            "Audit response {RequestId} Sources:{Count} Ms:{Ms} Alen:{Alen}",
            requestId, sources.Count, elapsedMs, answer.Length);

        Upsert(requestId, entry =>
        {
            entry.RequestId = requestId;
            entry.Sources = sources.ToList();
            entry.DurationMs = elapsedMs;
            entry.Status = "success";
            entry.Timestamp = DateTime.UtcNow;
        });
    }

    public IReadOnlyList<AuditLogEntry> GetRecentLogs(int count)
    {
        if (count <= 0) return Array.Empty<AuditLogEntry>();

        return _requestOrder
            .Reverse()
            .Select(id => _byRequestId.TryGetValue(id, out var entry) ? entry : null)
            .Where(e => e != null)
            .Take(count)
            .Cast<AuditLogEntry>()
            .ToList();
    }

    public IReadOnlyList<AuditLogEntry> GetLogsForUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return Array.Empty<AuditLogEntry>();

        return _byRequestId.Values
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    private void Upsert(string requestId, Action<AuditLogEntry> mutate)
    {
        var isNew = false;
        var entry = _byRequestId.GetOrAdd(requestId, id =>
        {
            isNew = true;
            return new AuditLogEntry
            {
                RequestId = id,
                Timestamp = DateTime.UtcNow,
                Status = "pending"
            };
        });

        mutate(entry);

        if (isNew)
        {
            _requestOrder.Enqueue(requestId);
            TrimIfNeeded();
        }
    }

    private void TrimIfNeeded()
    {
        while (_byRequestId.Count > MaxEntries && _requestOrder.TryDequeue(out var oldest))
            _byRequestId.TryRemove(oldest, out _);
    }
}

public interface IPIIDetectionService
{
    PIIDetectionResult Detect(string text);
}

public sealed class PIIDetectionService : IPIIDetectionService
{
    private static readonly Regex Email = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex Ssn = new(
        @"\b\d{3}-\d{2}-\d{4}\b",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    public PIIDetectionResult Detect(string text)
    {
        var types = new List<string>();
        if (Email.IsMatch(text)) types.Add("email");
        if (Ssn.IsMatch(text)) types.Add("ssn");
        return new PIIDetectionResult(types.Count > 0, types);
    }
}
