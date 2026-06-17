using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace LPL.Gatekeeper.Models;

public sealed class AuditEventDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("event_id")]
    public string EventId { get; set; } = "";

    [BsonElement("request_id")]
    public string RequestId { get; set; } = "";

    [BsonElement("event_type")]
    public string EventType { get; set; } = "";

    [BsonElement("occurred_at")]
    public DateTime OccurredAt { get; set; }

    [BsonElement("user_id")]
    public string UserId { get; set; } = "";

    [BsonElement("department")]
    public string Department { get; set; } = "";

    [BsonElement("branch_code")]
    public string BranchCode { get; set; } = "";

    [BsonElement("question")]
    public string Question { get; set; } = "";

    [BsonElement("sources")]
    public List<string> Sources { get; set; } = new();

    [BsonElement("duration_ms")]
    public long DurationMs { get; set; }

    [BsonElement("answer_length")]
    public int AnswerLength { get; set; }

    [BsonElement("rejection_reason")]
    public string RejectionReason { get; set; } = "";

    [BsonElement("gateway_version")]
    public string GatewayVersion { get; set; } = "";

    [BsonElement("environment")]
    public string Environment { get; set; } = "";

    [BsonElement("is_rejection")]
    public bool IsRejection { get; set; }

    [BsonElement("is_success")]
    public bool IsSuccess { get; set; }
}

