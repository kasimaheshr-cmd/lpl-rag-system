using System.Text;
using LPL.Gatekeeper.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

// ─── Serilog setup ────────────────────────────────────────────
// Configure before builder so startup errors are captured.
// Rolling file: new file every day. Keeps 30 days of logs.
// In production: replace File sink with CloudWatch sink.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(
        path: "logs/gatekeeper-.txt",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// ─── Core services ────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ─── Swagger with Bearer auth support ─────────────────────────
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "LPL Compliance AI Gateway",
        Version = "v1",
        Description = "Enterprise gateway for the LPL RAG system"
    });

    // This adds the padlock icon in Swagger UI
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Enter: Bearer {your JWT token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id   = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ─── JWT Authentication ───────────────────────────────────────
// Read secret from config (appsettings.json or environment variable)
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("JWT Secret not configured");

builder.Services.AddAuthentication(options =>
{
    // Set JWT as the default scheme for both authentication
    // and challenge (what happens when auth fails)
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        // Validate the signature — prevents token forgery
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(jwtSecret)),

        // Validate issuer — ensures this token came from our system
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],

        // Validate audience — ensures token is meant for this service
        ValidateAudience = true,
        ValidAudience = builder.Configuration["Jwt:Audience"],

        // Validate expiry — tokens older than ExpiryHours are rejected
        ValidateLifetime = true,

        // ClockSkew: allow 0 seconds tolerance on expiry.
        // Default is 5 minutes which creates a security gap.
        ClockSkew = TimeSpan.Zero
    };

    // Log authentication events for debugging
    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILogger<Program>>();
            logger.LogWarning("Auth failed: {Error}", context.Exception.Message);
            return Task.CompletedTask;
        },
        OnTokenValidated = context =>
        {
            var userId = context.Principal?
                .FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILogger<Program>>();
            logger.LogDebug("Token validated for User:{UserId}", userId);
            return Task.CompletedTask;
        }
    };
});

// ─── Authorization policies ───────────────────────────────────
// Define named policies. Used in Day 3 with [Authorize(Policy="ComplianceOnly")]
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdvisorOnly", policy =>
        policy.RequireRole("Advisor", "Compliance", "Admin"));

    options.AddPolicy("ComplianceOnly", policy =>
        policy.RequireRole("Compliance", "Admin"));

    options.AddPolicy("AdminOnly", policy =>
        policy.RequireRole("Admin"));
});

// ─── Application services ─────────────────────────────────────
// Singleton: one instance for the lifetime of the app.
// Good for services that hold no per-request state.
builder.Services.AddSingleton<IAuditService, AuditService>();
builder.Services.AddSingleton<IPIIDetectionService, PIIDetectionService>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IMongoDbService, MongoDbService>();
builder.Services.AddSingleton<IMongoAuditRepository, MongoAuditRepository>();
builder.Services.AddSingleton<IRedisCacheService, RedisCacheService>();
// ── Kafka services ────────────────────────────────────────────
// Singleton producer — one Kafka connection shared across all requests.
// Creating a producer per request would exhaust TCP connections.
builder.Services.AddSingleton<IKafkaProducerService, KafkaProducerService>();

// Background consumer — runs as a hosted service.
// Starts with the app, reads Kafka events, persists to file/DB.
builder.Services.AddHostedService<KafkaAuditConsumer>();
// ─── HttpClient for AI Engine ─────────────────────────────────
// Named HttpClient with base address and timeout.
// IHttpClientFactory manages connection pooling — do not use
// `new HttpClient()` directly, it exhausts sockets.
builder.Services.AddHttpClient("AIEngine", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["AIEngine:BaseUrl"] ?? "http://localhost:8001");
    client.Timeout = TimeSpan.FromSeconds(120);
});

// ─── CORS ─────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

// ─── Build the app ────────────────────────────────────────────
var app = builder.Build();

var mongoDb = app.Services.GetRequiredService<IMongoDbService>();
app.Logger.LogInformation("MongoDB startup check complete");

var redis = app.Services.GetRequiredService<IRedisCacheService>(); // ← ADD
app.Logger.LogInformation("Redis startup check complete"); 
// ─── Middleware pipeline ORDER MATTERS ────────────────────────
// Request flows top to bottom through this pipeline.
// Response flows bottom to top.
// Authentication must come before Authorization.
app.UseCors();              // 1. Allow cross-origin requests
app.UseSwagger();           // 2. Serve swagger.json
app.UseSwaggerUI();         // 3. Serve Swagger HTML UI
app.UseAuthentication();    // 4. Parse + validate JWT token
app.UseAuthorization();     // 5. Check policies + roles
app.MapControllers();       // 6. Route to controller actions

app.Run();
