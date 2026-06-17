using MongoDB.Driver;
using LPL.Gatekeeper.Models;

namespace LPL.Gatekeeper.Services;

// ─── Interface ────────────────────────────────────────────────
public interface IMongoAuditRepository
{
    Task InsertAsync(AuditEventDocument document);
    Task<List<AuditEventDocument>> GetRecentAsync(int count = 50);
    Task<List<AuditEventDocument>> GetByUserAsync(string userId);
    Task<List<AuditEventDocument>> GetByRequestIdAsync(string requestId);
    Task<MongoAuditStats> GetStatsAsync();
}

// ─── Repository ───────────────────────────────────────────────
// Repository pattern: hides MongoDB API from the rest of the app.
// If you ever switch from MongoDB to PostgreSQL, only this file
// changes — controllers and services stay identical.
public class MongoAuditRepository : IMongoAuditRepository
{
    private readonly IMongoCollection<AuditEventDocument> _collection;
    private readonly ILogger<MongoAuditRepository> _logger;

    public MongoAuditRepository(
        IMongoDbService mongoDb,
        ILogger<MongoAuditRepository> logger)
    {
        _collection = mongoDb.AuditEvents;
        _logger     = logger;
    }

    // ── Insert ────────────────────────────────────────────────
    // InsertOneAsync — single document insert.
    // MongoDB generates the _id automatically if not set.
    // In SQL: INSERT INTO audit_events VALUES (...)
    public async Task InsertAsync(AuditEventDocument document)
    {
        try
        {
            await _collection.InsertOneAsync(document);

            _logger.LogDebug(
                "[MONGO] Inserted | EventType:{EventType} | " +
                "User:{UserId} | Id:{Id}",
                document.EventType, document.UserId, document.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[MONGO] Insert failed | EventId:{EventId}",
                document.EventId);
            throw;
        }
    }

    // ── GetRecentAsync ────────────────────────────────────────
    // Returns most recent N documents across all users.
    // In SQL: SELECT TOP 50 * FROM audit_events ORDER BY occurred_at DESC
    //
    // MongoDB fluent builder:
    // Find(filter) → Sort() → Limit() → ToListAsync()
    public async Task<List<AuditEventDocument>> GetRecentAsync(
        int count = 50)
    {
        count = Math.Min(count, 500);

        return await _collection
            .Find(Builders<AuditEventDocument>.Filter.Empty)
            .Sort(Builders<AuditEventDocument>.Sort
                .Descending(e => e.OccurredAt))
            .Limit(count)
            .ToListAsync();
    }

    // ── GetByUserAsync ────────────────────────────────────────
    // Returns all events for a specific user.
    // In SQL: SELECT * FROM audit_events WHERE user_id = @userId
    // Uses the idx_userId_time index — fast even at millions of docs.
    public async Task<List<AuditEventDocument>> GetByUserAsync(
        string userId)
    {
        var filter = Builders<AuditEventDocument>.Filter
            .Eq(e => e.UserId, userId);

        return await _collection
            .Find(filter)
            .Sort(Builders<AuditEventDocument>.Sort
                .Descending(e => e.OccurredAt))
            .ToListAsync();
    }

    // ── GetByRequestIdAsync ───────────────────────────────────
    // Returns both request + response events for one /ask call.
    // Useful for showing the full picture: question + answer pair.
    // In SQL: SELECT * FROM audit_events WHERE request_id = @id
    public async Task<List<AuditEventDocument>> GetByRequestIdAsync(
        string requestId)
    {
        var filter = Builders<AuditEventDocument>.Filter
            .Eq(e => e.RequestId, requestId);

        return await _collection
            .Find(filter)
            .Sort(Builders<AuditEventDocument>.Sort
                .Ascending(e => e.OccurredAt))
            .ToListAsync();
    }

    // ── GetStatsAsync ─────────────────────────────────────────
    // MongoDB Aggregation Pipeline — the equivalent of SQL GROUP BY.
    // Pipelines are arrays of stages processed sequentially.
    // Much more powerful than SQL GROUP BY for nested data.
    public async Task<MongoAuditStats> GetStatsAsync()
    {
        // Stage 1: Group all documents and compute aggregates
        // $group: { _id: null } means group everything together
        var pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument
            {
                { "_id",           BsonNull.Value },
                { "totalCount",    new BsonDocument("$sum", 1) },
                { "uniqueUsers",   new BsonDocument("$addToSet", "$userId") },
                { "avgDuration",   new BsonDocument("$avg", "$durationMs") },
                { "rejectedCount", new BsonDocument("$sum",
                    new BsonDocument("$cond",
                        new BsonArray { "$isRejection", 1, 0 })) },
                { "successCount",  new BsonDocument("$sum",
                    new BsonDocument("$cond",
                        new BsonArray { "$isSuccess", 1, 0 })) }
            })
        };

        // $unwind + $group for top sources would be a longer pipeline
        // Simplified here — add MongoDB aggregation deep dive in Week 6

        var result = await _collection
            .Aggregate<MongoDB.Bson.BsonDocument>(pipeline)
            .FirstOrDefaultAsync();

        if (result == null) return new MongoAuditStats();

        return new MongoAuditStats
        {
            TotalEvents    = result["totalCount"].AsInt32,
            UniqueAdvisors = result["uniqueUsers"].AsBsonArray.Count,
            AvgDurationMs  = (long)result["avgDuration"].ToDouble(),
            RejectedCount  = result["rejectedCount"].AsInt32,
            SuccessCount   = result["successCount"].AsInt32
        };
    }
}

// Stats model returned by GetStatsAsync
public class MongoAuditStats
{
    public int TotalEvents    { get; set; }
    public int UniqueAdvisors { get; set; }
    public long AvgDurationMs { get; set; }
    public int RejectedCount  { get; set; }
    public int SuccessCount   { get; set; }
}