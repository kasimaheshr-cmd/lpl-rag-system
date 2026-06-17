using LPL.Gatekeeper.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LPL.Gatekeeper.Controllers;

[ApiController]
[Route("audit")]
// Class-level [Authorize] — ALL endpoints in this controller
// require authentication. No anonymous access.
[Authorize]
public class AuditController : ControllerBase
{
    private readonly IAuditService _auditService;
    private readonly ITokenService _tokenService;
    private readonly ILogger<AuditController> _logger;
    private readonly IMongoAuditRepository _mongoRepository;
    
    public AuditController(
        IAuditService auditService,
        ITokenService tokenService,
        IMongoAuditRepository mongoRepository,
        ILogger<AuditController> logger)
    {
        _auditService = auditService;
        _tokenService = tokenService;
        _logger       = logger;
    }

    // ── GET /audit/logs ───────────────────────────────────────
    // Returns recent audit logs across ALL advisors.
    //
    // [Authorize(Policy = "ComplianceOnly")] means this endpoint
    // is blocked for Role = "Advisor". Only Compliance and Admin
    // roles can reach it, even with a valid JWT.
    //
    // FINRA use case: mike.compliance runs this to review
    // what questions advisors are asking the AI system.
    [HttpGet("logs")]
    [Authorize(Policy = "ComplianceOnly")]
    public IActionResult GetRecentLogs([FromQuery] int count = 50)
    {
    var profile = _tokenService.ExtractProfile(HttpContext);
    var logs    = await _mongoRepository.GetRecentAsync(count);

    return Ok(new
    {
        total       = logs.Count,
        accessed_by = profile?.UserId,
        accessed_at = DateTime.UtcNow,
        logs
    });
    }

    // ── GET /audit/logs/{userId} ──────────────────────────────
    // Returns audit logs for a specific advisor.
    // Compliance can view any advisor.
    // Advisors can ONLY view their own logs.
    [HttpGet("logs/{userId}")]
    [Authorize] // Any authenticated user — but logic checks ownership
    public IActionResult GetUserLogs(string userId)
    {
   var profile      = _tokenService.ExtractProfile(HttpContext);
    if (profile == null) return Unauthorized();

    var isPrivileged = profile.Role is "Compliance" or "Admin";
    if (!isPrivileged && profile.UserId != userId)
        return Forbid();

    var logs = await _mongoRepository.GetByUserAsync(userId);
    return Ok(new { user_id = userId, total = logs.Count, logs });
    }

    [HttpGet("request/{requestId}")]
[Authorize(Policy = "ComplianceOnly")]
public async Task<IActionResult> GetRequest(string requestId)
{
    // Returns BOTH the request event AND response event
    // Shows the full Q&A pair in one call
    var events = await _mongoRepository.GetByRequestIdAsync(requestId);
    return Ok(new { request_id = requestId, events });
}

[HttpGet("stats")]
[Authorize(Policy = "ComplianceOnly")]
public async Task<IActionResult> GetStats()
{
    var stats = await _mongoRepository.GetStatsAsync();
    return Ok(stats);
}

    // ── GET /audit/stats ──────────────────────────────────────
    // Summary statistics — Compliance dashboard data
    [HttpGet("stats")]
    [Authorize(Policy = "ComplianceOnly")]
    public IActionResult GetStats()
    {
        var logs = _auditService.GetRecentLogs(1000);

        var stats = new
        {
            total_queries  = logs.Count,
            unique_advisors = logs.Select(l => l.UserId).Distinct().Count(),
            avg_duration_ms = logs.Any()
                ? (long)logs.Average(l => l.DurationMs) : 0,
            rejected_count = logs.Count(l => l.Status.StartsWith("rejected")),
            top_sources    = logs
                .SelectMany(l => l.Sources)
                .GroupBy(s => s)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => new { source = g.Key, count = g.Count() }),
            by_department  = logs
                .GroupBy(l => l.Department)
                .Select(g => new { dept = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
        };

        return Ok(stats);
    }
}