using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using LPL.Gatekeeper.Models;

namespace LPL.Gatekeeper.Services;

// ─── Interface ────────────────────────────────────────────────
public interface IMongoDbService
{
    IMongoCollection<AuditEventDocument> AuditEvents { get; }
    IMongoCollection<SessionDocument> Sessions { get; }
}

// ─── MongoDB Service ──────────────────────────────────────────
// Singleton — one connection pool shared across all requests.
// MongoClient is thread-safe and manages connection pooling.
// Never create MongoClient per-request — it's expensive.
public class MongoDbService : IMongoDbService
{
    private readonly IMongoDatabase _database;
    private readonly ILogger<MongoDbService> _logger;

    public IMongoCollection<AuditEventDocument> AuditEvents =>
        _database.GetCollection<AuditEventDocument>(
            _auditCollectionName);

    public IMongoCollection<SessionDocument> Sessions =>
        _database.GetCollection<SessionDocument>(
            _sessionCollectionName);

    private readonly string _auditCollectionName;
    private readonly string _sessionCollectionName;

    public MongoDbService(
        IConfiguration config,
        ILogger<MongoDbService> logger)
    {
        _logger = logger;

        var connectionString = config["MongoDB:ConnectionString"]
            ?? "mongodb://admin:LPLMongo2024!@localhost:27017";
        var databaseName     = config["MongoDB:DatabaseName"]
            ?? "lpl_audit";

        _auditCollectionName   = config["MongoDB:AuditCollection"]
            ?? "audit_events";
        _sessionCollectionName = config["MongoDB:SessionCollection"]
            ?? "sessions";

        // MongoClient settings — configure timeouts and pool size
        var settings = MongoClientSettings.FromConnectionString(
            connectionString);

        // Connection timeout — fail fast if MongoDB is unreachable
        settings.ConnectTimeout    = TimeSpan.FromSeconds(5);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);

        // Connection pool — handles concurrent requests efficiently
        settings.MaxConnectionPoolSize = 50;

        var client = new MongoClient(settings);
        _database  = client.GetDatabase(databaseName);

        // Create indexes on startup
        // Indexes make queries fast — like indexes in SQL
        EnsureIndexesAsync().GetAwaiter().GetResult();

        _logger.LogInformation(
            "MongoDB connected | DB:{Database} | " +
            "AuditCollection:{Audit} | SessionCollection:{Session}",
            databaseName, _auditCollectionName, _sessionCollectionName);
    }

    private async Task EnsureIndexesAsync()
    {
        // ── Audit Events indexes ──────────────────────────────

        // Index on userId — makes GetLogsForUser() fast
        // Without this: full collection scan = slow at scale
        await AuditEvents.Indexes.CreateOneAsync(
            new CreateIndexModel<AuditEventDocument>(
                Builders<AuditEventDocument>.IndexKeys
                    .Ascending(e => e.UserId)
                    .Descending(e => e.OccurredAt),
                new CreateIndexOptions { Name = "idx_userId_time" }));

        // Index on requestId — links request + response events
        await AuditEvents.Indexes.CreateOneAsync(
            new CreateIndexModel<AuditEventDocument>(
                Builders<AuditEventDocument>.IndexKeys
                    .Ascending(e => e.RequestId),
                new CreateIndexOptions { Name = "idx_requestId" }));

        // Index on eventType + department — for stats queries
        await AuditEvents.Indexes.CreateOneAsync(
            new CreateIndexModel<AuditEventDocument>(
                Builders<AuditEventDocument>.IndexKeys
                    .Ascending(e => e.EventType)
                    .Ascending(e => e.Department),
                new CreateIndexOptions { Name = "idx_eventType_dept" }));

        // TTL index — automatically deletes documents after 7 years
        // FINRA requires 7 year retention for financial records
        // MongoDB's TTL mechanism checks this every 60 seconds
        await AuditEvents.Indexes.CreateOneAsync(
            new CreateIndexModel<AuditEventDocument>(
                Builders<AuditEventDocument>.IndexKeys
                    .Ascending(e => e.OccurredAt),
                new CreateIndexOptions
                {
                    Name = "idx_ttl_7years",
                    ExpireAfter = TimeSpan.FromDays(365 * 7)
                }));

        // ── Session indexes ───────────────────────────────────

        // Unique index on sessionId — fast lookups, prevents duplicates
        await Sessions.Indexes.CreateOneAsync(
            new CreateIndexModel<SessionDocument>(
                Builders<SessionDocument>.IndexKeys
                    .Ascending(s => s.SessionId),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name   = "idx_sessionId_unique"
                }));

        // TTL index — auto-delete sessions after ExpiresAt
        // Sessions expire after 8 hours (matches JWT expiry)
        await Sessions.Indexes.CreateOneAsync(
            new CreateIndexModel<SessionDocument>(
                Builders<SessionDocument>.IndexKeys
                    .Ascending(s => s.ExpiresAt),
                new CreateIndexOptions
                {
                    ExpireAfter = TimeSpan.Zero, // delete AT ExpiresAt
                    Name        = "idx_session_ttl"
                }));

        _logger.LogInformation("MongoDB indexes ensured");
    }
}