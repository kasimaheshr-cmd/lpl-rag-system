using LPL.Gatekeeper.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace LPL.Gatekeeper.Services;

public interface IMongoDbService
{
    Task StartupCheckAsync(CancellationToken ct = default);
    IMongoDatabase Database { get; }
}

public sealed class MongoDbService : IMongoDbService
{
    private readonly ILogger<MongoDbService> _logger;
    public IMongoDatabase Database { get; }

    public MongoDbService(IConfiguration config, ILogger<MongoDbService> logger)
    {
        _logger = logger;

        var connectionString = config["MongoDB:ConnectionString"];
        var dbName = config["MongoDB:DatabaseName"] ?? "lpl_audit";

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("MongoDB:ConnectionString is not configured");

        var client = new MongoClient(connectionString);
        Database = client.GetDatabase(dbName);
    }

    public async Task StartupCheckAsync(CancellationToken ct = default)
    {
        var command = new BsonDocument("ping", 1);
        await Database.RunCommandAsync<BsonDocument>(command, cancellationToken: ct);
        _logger.LogInformation("MongoDB startup check complete");
    }
}

public interface IMongoAuditRepository
{
    Task InsertAsync(AuditEventDocument document, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEventDocument>> GetRecentAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEventDocument>> GetByUserAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEventDocument>> GetByRequestIdAsync(string requestId, CancellationToken ct = default);
    Task<object> GetStatsAsync(CancellationToken ct = default);
}

public sealed class MongoAuditRepository : IMongoAuditRepository
{
    private readonly IMongoCollection<AuditEventDocument> _collection;
    private readonly ILogger<MongoAuditRepository> _logger;

    public MongoAuditRepository(IMongoDbService mongo, IConfiguration config, ILogger<MongoAuditRepository> logger)
    {
        _logger = logger;
        var collectionName = config["MongoDB:AuditCollection"] ?? "audit_events";
        _collection = mongo.Database.GetCollection<AuditEventDocument>(collectionName);

        var indexKeys = Builders<AuditEventDocument>.IndexKeys
            .Ascending(x => x.EventId);
        var indexOptions = new CreateIndexOptions { Unique = true, Name = "ux_event_id" };
        _collection.Indexes.CreateOne(new CreateIndexModel<AuditEventDocument>(indexKeys, indexOptions));
    }

    public async Task InsertAsync(AuditEventDocument document, CancellationToken ct = default)
    {
        try
        {
            await _collection.InsertOneAsync(document, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            _logger.LogDebug("Duplicate event ignored. EventId:{EventId}", document.EventId);
        }
    }

    public async Task<IReadOnlyList<AuditEventDocument>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        if (count <= 0) return Array.Empty<AuditEventDocument>();

        return await _collection.Find(FilterDefinition<AuditEventDocument>.Empty)
            .SortByDescending(x => x.OccurredAt)
            .Limit(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEventDocument>> GetByUserAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return Array.Empty<AuditEventDocument>();

        return await _collection.Find(x => x.UserId == userId)
            .SortByDescending(x => x.OccurredAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEventDocument>> GetByRequestIdAsync(string requestId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return Array.Empty<AuditEventDocument>();

        return await _collection.Find(x => x.RequestId == requestId)
            .SortBy(x => x.OccurredAt)
            .ToListAsync(ct);
    }

    public async Task<object> GetStatsAsync(CancellationToken ct = default)
    {
        var total = await _collection.CountDocumentsAsync(FilterDefinition<AuditEventDocument>.Empty, cancellationToken: ct);
        var uniqueUsers = await _collection.DistinctAsync<string>("user_id", FilterDefinition<AuditEventDocument>.Empty, cancellationToken: ct);
        var rejected = await _collection.CountDocumentsAsync(x => x.EventType == "rejection", cancellationToken: ct);

        return new
        {
            total_events = total,
            unique_advisors = (await uniqueUsers.ToListAsync(ct)).Count,
            rejected_events = rejected
        };
    }
}

