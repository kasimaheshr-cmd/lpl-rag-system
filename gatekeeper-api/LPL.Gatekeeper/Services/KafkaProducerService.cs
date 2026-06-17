using System.Text.Json;
using Confluent.Kafka;
using LPL.Gatekeeper.Models;

namespace LPL.Gatekeeper.Services;

// ─── Interface ────────────────────────────────────────────────
public interface IKafkaProducerService
{
    Task PublishAuditEventAsync(AuditEvent auditEvent);
}

// ─── Kafka Producer ───────────────────────────────────────────
// Implements IDisposable because the Kafka producer holds
// network connections that must be flushed and closed cleanly.
// If you don't flush, buffered messages are lost on shutdown.
public class KafkaProducerService : IKafkaProducerService, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly IProducer<string, string> _dlqProducer;
    private readonly string _auditTopic;
    private readonly string _dlqTopic;
    private readonly ILogger<KafkaProducerService> _logger;
    private bool _disposed;

    public KafkaProducerService(
        IConfiguration config,
        ILogger<KafkaProducerService> logger)
    {
        _logger = logger;
        _auditTopic = config["Kafka:AuditTopic"] ?? "lpl.audit.events";
        _dlqTopic = config["Kafka:DeadLetterTopic"] ?? "lpl.audit.events.dlq";

        var bootstrapServers = config["Kafka:BootstrapServers"]
            ?? "localhost:9092";

        // ── Producer configuration ────────────────────────────
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,

            // Idempotence: exactly-once delivery guarantee.
            // Kafka assigns a producer ID and sequence numbers.
            // Broker deduplicates any retried messages.
            EnableIdempotence = true,

            // Acks.All: message confirmed only when ALL replicas
            // have written it. Safest for financial audit data.
            // Acks.Leader would be faster but risks data loss
            // if the leader crashes before replication.
            Acks = Acks.All,

            // Retry 5 times before giving up.
            // With idempotence enabled, retries are safe.
            MessageSendMaxRetries = 5,

            // Wait up to 1 second before giving up on a send.
            MessageTimeoutMs = 1000,

            // Compression reduces network bandwidth.
            // Snappy: fast + decent compression ratio.
            // Good for high-throughput audit streams.
            CompressionType = CompressionType.Snappy
        };

        _producer = new ProducerBuilder<string, string>(producerConfig).Build();
        _dlqProducer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = bootstrapServers }).Build();

        _logger.LogInformation(
            "Kafka producer connected to {Servers}", bootstrapServers);
    }

    // ── PublishAuditEventAsync ────────────────────────────────
    // Publishes one audit event to Kafka.
    // The Kafka message key = UserId.
    // Key determines which partition receives the message.
    // Same advisor's events always go to the same partition —
    // guaranteeing ORDER within one advisor's history.
    public async Task PublishAuditEventAsync(AuditEvent auditEvent)
    {
        var json = JsonSerializer.Serialize(auditEvent, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var message = new Message<string, string>
        {
            // Key = UserId: all events from same advisor go to same partition
            // This preserves chronological order per advisor
            Key = auditEvent.UserId,
            Value = json,

            // Headers: metadata visible without deserializing the value
            // Useful for routing and filtering without reading the payload
            Headers = new Headers
            {
                { "event-type",  System.Text.Encoding.UTF8.GetBytes(auditEvent.EventType) },
                { "gateway-version", System.Text.Encoding.UTF8.GetBytes(auditEvent.GatewayVersion) },
                { "department",  System.Text.Encoding.UTF8.GetBytes(auditEvent.Department) }
            }
        };

        try
        {
            var result = await _producer.ProduceAsync(_auditTopic, message);

            _logger.LogInformation(
                "Audit event published | Topic:{Topic} | Partition:{Partition} | " +
                "Offset:{Offset} | EventId:{EventId}",
                result.Topic,
                result.Partition.Value,
                result.Offset.Value,
                auditEvent.EventId);
        }
        catch (ProduceException<string, string> ex)
        {
            // ── Dead Letter Queue ─────────────────────────────
            // If the main topic publish fails, we don't throw.
            // We publish to the DLQ instead so the request still
            // succeeds for the advisor. The audit event is not
            // lost — it's in the DLQ waiting for manual review.
            //
            // This is the Dead Letter Queue (DLQ) pattern:
            // Never lose a message, never block the caller.
            _logger.LogError(
                ex,
                "Failed to publish to main topic. Sending to DLQ. EventId:{EventId}",
                auditEvent.EventId);

            try
            {
                // Add failure reason to the DLQ message
                auditEvent.RejectionReason = $"kafka_error:{ex.Error.Code}";
                var dlqJson = JsonSerializer.Serialize(auditEvent);

                await _dlqProducer.ProduceAsync(_dlqTopic,
                    new Message<string, string>
                    {
                        Key = auditEvent.UserId,
                        Value = dlqJson
                    });

                _logger.LogWarning(
                    "Event sent to DLQ. EventId:{EventId}", auditEvent.EventId);
            }
            catch (Exception dlqEx)
            {
                // DLQ also failed — last resort: at least log it
                // In production: send PagerDuty alert here
                _logger.LogCritical(
                    dlqEx,
                    "DLQ publish also failed! EventId:{EventId} — " +
                    "audit event may be lost. MANUAL REVIEW REQUIRED.",
                    auditEvent.EventId);
            }
        }
    }

    // Flush ensures all buffered messages are sent before shutdown
    public void Dispose()
    {
        if (!_disposed)
        {
            _logger.LogInformation("Flushing Kafka producer...");
            _producer.Flush(TimeSpan.FromSeconds(10));
            _producer.Dispose();
            _dlqProducer.Dispose();
            _disposed = true;
        }
    }
}