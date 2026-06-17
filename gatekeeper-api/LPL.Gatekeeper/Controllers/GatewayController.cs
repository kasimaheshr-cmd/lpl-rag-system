using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LPL.Gatekeeper.Models;
using LPL.Gatekeeper.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LPL.Gatekeeper.Controllers;

[ApiController]
public class GatewayController : ControllerBase
{
    private static readonly JsonSerializerOptions AiJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly ITokenService _tokenService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPIIDetectionService _piiService;
    private readonly IAuditService _auditService;
    private readonly IKafkaProducerService _kafka;
    private readonly IHostEnvironment _env;
    private readonly IRedisCacheService _cache;
    private readonly ILogger<GatewayController> _logger;

    public GatewayController(
        ITokenService tokenService,
        IHttpClientFactory httpClientFactory,
        IPIIDetectionService piiService,
        IAuditService auditService,
        IKafkaProducerService kafka,
        IHostEnvironment env,
        IRedisCacheService cache,
        ILogger<GatewayController> logger)
    {
        _tokenService = tokenService;
        _httpClientFactory = httpClientFactory;
        _piiService = piiService;
        _auditService = auditService;
        _kafka = kafka;
        _cache = cache;
        _env = env;
        _logger = logger;
    }

    [HttpPost("/ask")]
    [Authorize]
    public async Task<IActionResult> Ask([FromBody] QuestionRequest request)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8];

        var profile = _tokenService.ExtractProfile(HttpContext);
        if (profile == null)
            return Unauthorized();

        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question cannot be empty" });

        if (request.Question.Length > 1000)
            return BadRequest(new { error = "Question exceeds maximum length" });

        var piiResult = _piiService.Detect(request.Question);
        if (piiResult.ContainsPII)
        {
            _auditService.LogRejection(requestId, profile.UserId,
                $"PII detected: {string.Join(", ", piiResult.PIITypes)}", "[REDACTED]");

            _ = _kafka.PublishAuditEventAsync(new AuditEvent
            {
                RequestId = requestId,
                EventType = "rejection",
                UserId = profile.UserId,
                Department = profile.Department,
                BranchCode = profile.BranchCode,
                Question = "[REDACTED]",
                RejectionReason = $"pii_detected:{string.Join(",", piiResult.PIITypes)}",
                GatewayVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
                Environment = _env.EnvironmentName
            }).ContinueWith(t =>
            {
                if (t.Exception != null)
                    _logger.LogWarning(t.Exception, "Failed to publish rejection audit event to Kafka");
            }, TaskScheduler.Default);

            return BadRequest(new
            {
                error = "Question contains sensitive personal information",
                pii_types = piiResult.PIITypes,
                suggestion = "Rephrase without personal identifiers"
            });
        }

        _auditService.LogRequest(requestId, profile.UserId,
            request.Question, profile.Department);

        _ = _kafka.PublishAuditEventAsync(new AuditEvent
        {
            RequestId = requestId,
            EventType = "request",
            UserId = profile.UserId,
            Department = profile.Department,
            BranchCode = profile.BranchCode,
            Question = request.Question,
            GatewayVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
            Environment = _env.EnvironmentName
        }).ContinueWith(t =>
        {
            if (t.Exception != null)
                _logger.LogWarning(t.Exception, "Failed to publish request audit event to Kafka");
        }, TaskScheduler.Default);

        try
        {
            var cachedAnswer = await _cache.GetExactAsync(request.Question);

            if (cachedAnswer != null)
            {
                _auditService.LogResponse(
                    requestId, cachedAnswer.Answer,
                    cachedAnswer.Sources, 0);  // 0ms — came from cache

                return Ok(new AnswerResponse
                {
                    Question = request.Question,
                    Answer = cachedAnswer.Answer,
                    Sources = cachedAnswer.Sources,
                    Department = profile.Department,
                    SessionId = request.SessionId,
                    SearchType = "cache-hit-exact",
                    Audit = new AuditInfo
                    {
                        RequestId = requestId,
                        UserId = profile.UserId,
                        Timestamp = DateTime.UtcNow
                    }
                });
            }
            var client = _httpClientFactory.CreateClient("AIEngine");
            var payload = JsonSerializer.Serialize(new
            {
                question = request.Question,
                department = profile.Department,
                session_id = request.SessionId
            });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var aiResponse = await client.PostAsync("/ask", content);
            var responseBody = await aiResponse.Content.ReadAsStringAsync();

            if (!aiResponse.IsSuccessStatusCode)
                return StatusCode(502, new { error = "AI engine error" });

            var answer = JsonSerializer.Deserialize<AnswerResponse>(responseBody, AiJsonOptions);

            stopwatch.Stop();
            _auditService.LogResponse(requestId, answer?.Answer ?? "",
                answer?.Sources ?? new List<string>(), stopwatch.ElapsedMilliseconds);

            _ = _kafka.PublishAuditEventAsync(new AuditEvent
            {
                RequestId = requestId,
                EventType = "response",
                UserId = profile.UserId,
                Department = profile.Department,
                BranchCode = profile.BranchCode,
                Question = request.Question,
                Sources = answer?.Sources ?? new List<string>(),
                DurationMs = stopwatch.ElapsedMilliseconds,
                AnswerLength = (answer?.Answer ?? "").Length,
                GatewayVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
                Environment = _env.EnvironmentName
            }).ContinueWith(t =>
            {
                if (t.Exception != null)
                    _logger.LogWarning(t.Exception, "Failed to publish response audit event to Kafka");
            }, TaskScheduler.Default);

            if (answer != null)
            {
                answer.Audit = new AuditInfo
                {
                    RequestId = requestId,
                    UserId = profile.UserId,
                    Timestamp = DateTime.UtcNow
                };
            }

            await _cache.SetExactAsync(request.Question, new CachedAnswer
            {
                Question = request.Question,
                Answer = answer!.Answer,
                Sources = answer.Sources,
                Department = profile.Department,
                CachedAt = DateTime.UtcNow
            });

            return Ok(answer);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "AI engine unreachable");
            return StatusCode(503, new { error = "AI engine unreachable" });
        }
    }

    [HttpGet("cache/stats")]
[Authorize(Policy = "ComplianceOnly")]
public async Task<IActionResult> GetCacheStats()
{
    var stats = await _cache.GetStatsAsync();
    return Ok(stats);
}
    // ── DELETE /document/{source} ─────────────────────────────────
    // Removes all chunks for a document source from the AI engine.
    // Admin only — irreversible operation.
    //
    // Why Admin-only and not Compliance?
    // Compliance reads audit logs but shouldn't be able to delete
    // evidence. Separation of duties — a compliance principle.
    [HttpDelete("/document/{source}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> DeleteDocument(string source)
    {
        var profile = _tokenService.ExtractProfile(HttpContext);

        _logger.LogWarning(
            "Document deletion initiated: Source:{Source} by User:{UserId}",
            source, profile?.UserId);

        // Validate source name — only alphanumeric + hyphens
        if (!System.Text.RegularExpressions.Regex.IsMatch(
            source, @"^[a-zA-Z0-9\-_]+$"))
        {
            return BadRequest(new { error = "Invalid source name format" });
        }

        try
        {
            var client = _httpClientFactory.CreateClient("AIEngine");
            var response = await client.DeleteAsync($"/document/{source}");

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return NotFound(new { error = $"Source '{source}' not found" });

            if (!response.IsSuccessStatusCode)
                return StatusCode(502, new { error = "AI engine error during deletion" });

            var result = await response.Content.ReadAsStringAsync();

            _logger.LogWarning(
                "[AUDIT] DOCUMENT_DELETED | Source:{Source} | DeletedBy:{UserId}",
                source, profile?.UserId);

            return Ok(new DeleteDocumentResponse
            {
                Status = "deleted",
                Source = source,
                DeletedBy = profile?.UserId ?? "unknown",
                Timestamp = DateTime.UtcNow
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "AI engine unreachable during document deletion");
            return StatusCode(503, new { error = "AI engine unreachable" });
        }
    }

}
