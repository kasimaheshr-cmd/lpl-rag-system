using System.Text.Json;
using Confluent.Kafka;
using LPL.Gatekeeper.Models;

namespace LPL.Gatekeeper.Services;

// ── BackgroundService ─────────────────────────────────────────
// IHostedService / BackgroundService runs as a long-lived
// background thread inside your ASP.NET process.
// Starts when the app starts. Stops when the app stops.
// In production: this would be its own microservice.
// Today: runs in the same process for simplicity.
public class KafkaAuditConsumer : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<KafkaAuditConsumer> _logger;
    private readonly string _auditLogPath = "logs/audit-events.jsonl";
    private readonly IMongoAuditRepository _mongoRepository;
 

    public KafkaAuditConsumer(
        IConfiguration config,
        ILogger<KafkaAuditConsumer> logger,
         IMongoAuditRepository mongoRepository)
    {
        _config = config;
        _logger = logger;
        _mongoRepository = mongoRepository;
        // Create logs directory if it doesn't exist
        Directory.CreateDirectory("logs");
    }

    // ── ExecuteAsync ──────────────────────────────────────────
    // This method runs continuously until the app shuts down.
    // CancellationToken is triggered on app shutdown —
    // always pass it to blocking calls so they stop cleanly.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _logger.LogInformation("Kafka audit consumer starting...");

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _config["Kafka:BootstrapServers"] ?? "localhost:9092",
            GroupId          = _config["Kafka:ConsumerGroup"] ?? "lpl-audit-consumer",

            // AutoOffsetReset.Earliest: if this consumer has never run before,
            // start reading from the beginning of the topic.
            // This means replaying all historical events — important for
            // a new audit database catching up from Kafka history.
            AutoOffsetReset = AutoOffsetReset.Earliest,

            // EnableAutoCommit false: we manually commit offsets AFTER
            // successfully persisting the event. This guarantees
            // at-least-once processing — no event is skipped even if
            // the consumer crashes mid-write.
            EnableAutoCommit = false,

            // Session timeout: if the consumer doesn't heartbeat within
            // 30 seconds, Kafka considers it dead and reassigns partitions.
            SessionTimeoutMs = 30000
        };

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig)
            .SetErrorHandler((_, e) =>
                _logger.LogError("Kafka consumer error: {Error}", e.Reason))
            .SetPartitionsAssignedHandler((c, partitions) =>
                _logger.LogInformation("Partitions assigned: {Partitions}",
                    string.Join(",", partitions.Select(p => p.Partition.Value))))
            .Build();

        var topic = _config["Kafka:AuditTopic"] ?? "lpl.audit.events";
        consumer.Subscribe(topic);

        _logger.LogInformation(
            "Kafka consumer subscribed to topic: {Topic}", topic);

        // ── Consume loop ──────────────────────────────────────
        // Runs forever until stoppingToken is cancelled (app shutdown).
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Consume waits up to 1 second for a message.
                // Returns null on timeout — we loop and try again.
                // This allows the stoppingToken check to run regularly.
                var consumeResult = consumer.Consume(
                    TimeSpan.FromSeconds(1));

                if (consumeResult == null) continue;

                _logger.LogDebug(
                    "Received message | Partition:{Partition} | Offset:{Offset}",
                    consumeResult.Partition.Value,
                    consumeResult.Offset.Value);

                // ── Process the event ─────────────────────────
                await ProcessAuditEvent(consumeResult.Message.Value);

                // ── Commit offset ─────────────────────────────
                // Only commit AFTER successful processing.
                // If ProcessAuditEvent throws, offset is NOT committed —
                // the message will be redelivered on next restart.
                consumer.Commit(consumeResult);
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error: {Reason}", ex.Error.Reason);
                // Brief pause before retry — avoid tight error loop
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — stoppingToken was cancelled
                break;
            }
        }

        // Clean shutdown — commits any pending offsets
        consumer.Close();
        _logger.LogInformation("Kafka audit consumer stopped");
    }

    // ── ProcessAuditEvent ─────────────────────────────────────
    // Deserializes the Kafka message and persists it.
    // Today: writes to a .jsonl file (JSON Lines format).
    // Day 6: replaces file write with MongoDB insert.
    private async Task ProcessAuditEvent(string json)
    {
        try
        {
        var auditEvent = JsonSerializer.Deserialize<AuditEvent>(json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        if (auditEvent == null) return;

        // Convert AuditEvent (Kafka message) → AuditEventDocument (MongoDB)
        var document = new AuditEventDocument
        {
            EventId         = auditEvent.EventId,
            RequestId       = auditEvent.RequestId,
            EventType       = auditEvent.EventType,
            OccurredAt      = DateTime.Parse(auditEvent.OccurredAt).ToUniversalTime(),
            UserId          = auditEvent.UserId,
            Department      = auditEvent.Department,
            BranchCode      = auditEvent.BranchCode,
            Question        = auditEvent.Question,
            Sources         = auditEvent.Sources,
            DurationMs      = auditEvent.DurationMs,
            AnswerLength    = auditEvent.AnswerLength,
            RejectionReason = auditEvent.RejectionReason,
            GatewayVersion  = auditEvent.GatewayVersion,
            Environment     = auditEvent.Environment,
            IsRejection     = auditEvent.EventType == "rejection",
            IsSuccess       = auditEvent.EventType == "response"
        };

        // Insert into MongoDB
        await _mongoRepository.InsertAsync(document);

        _logger.LogInformation(
            "[KAFKA→MONGO] Persisted | EventType:{EventType} | " +
            "User:{UserId} | RequestId:{RequestId}",
            document.EventType,
            document.UserId,
            document.RequestId);
    }
        catch (JsonException ex)
        {
            // Malformed message — log it but don't crash the consumer
            // In production: send to DLQ with parse error annotation
            _logger.LogError(ex, "Failed to deserialize audit event: {Json}",
                json[..Math.Min(200, json.Length)]);
        }
    }
}