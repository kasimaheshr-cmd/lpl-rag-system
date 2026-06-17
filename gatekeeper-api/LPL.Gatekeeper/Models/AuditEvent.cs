namespace LPL.Gatekeeper.Models;

// ─── Kafka Message Schema ─────────────────────────────────────
// This is the canonical audit event published to Kafka.
// Every consumer (audit store, alert service, analytics) reads this.
//
// Schema evolution rules:
// - ADD new nullable fields freely — consumers ignore unknown fields
// - NEVER remove or rename existing fields — breaks existing consumers
// - NEVER change a field's type — breaks deserialization
//
// In production: use Apache Avro with Schema Registry to enforce this.
public class AuditEvent
{
    // ── Identity ──────────────────────────────────────────────
    // EventId: unique per event — used for idempotency checking.
    // If the same EventId arrives twice, consumers ignore the duplicate.
    public string EventId { get; set; } = Guid.NewGuid().ToString();

    // RequestId: ties together the request + response events.
    // One advisor question produces TWO events:
    //   type=request  (when /ask is called)
    //   type=response (when AI engine returns)
    public string RequestId { get; set; } = "";

    // EventType: what happened
    // Values: "request" | "response" | "rejection" | "document_deleted"
    public string EventType { get; set; } = "";

    // ── Timing ────────────────────────────────────────────────
    // OccurredAt: when the event happened (UTC).
    // Use ISO 8601 string for Kafka — avoids timezone serialization issues.
    public string OccurredAt { get; set; } = DateTime.UtcNow.ToString("O");

    // ── Advisor identity ──────────────────────────────────────
    public string UserId { get; set; } = "";
    public string Department { get; set; } = "";
    public string BranchCode { get; set; } = "";

    // ── Request details ───────────────────────────────────────
    // Question: the actual text asked.
    // In production: encrypt this field — it may contain sensitive info
    // even after PII detection (financial strategy, client intent).
    public string Question { get; set; } = "";

    // ── Response details (populated on response event) ────────
    public List<string> Sources { get; set; } = new();
    public long DurationMs { get; set; }
    public int AnswerLength { get; set; }

    // ── Rejection details (populated on rejection event) ──────
    public string RejectionReason { get; set; } = "";

    // ── System metadata ───────────────────────────────────────
    // GatewayVersion: which version of the Gatekeeper published this.
    // Lets you correlate events with deployments.
    public string GatewayVersion { get; set; } = "1.0.0";

    // Environment: dev | staging | prod
    // Prevents staging events from polluting production dashboards.
    public string Environment { get; set; } = "dev";
}