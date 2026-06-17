using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LPL.Gatekeeper.Models;
using Microsoft.IdentityModel.Tokens;

namespace LPL.Gatekeeper.Services;

// ─── Interface ────────────────────────────────────────────────
// Always code to an interface in .NET microservices.
// This lets you swap TokenService with CognitoTokenService later
// without touching any controller code.
public interface ITokenService
{
    string GenerateToken(AdvisorProfile advisor);
    AdvisorProfile? ExtractProfile(HttpContext context);
}

// ─── Implementation ───────────────────────────────────────────
public class TokenService : ITokenService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TokenService> _logger;

    // IConfiguration is injected by ASP.NET DI container —
    // reads from appsettings.json automatically
    public TokenService(IConfiguration config, ILogger<TokenService> logger)
    {
        _config = config;
        _logger = logger;
    }

    // ── GenerateToken ─────────────────────────────────────────
    // Called at login. Returns a compact signed string the client
    // stores and sends with every subsequent request.
    //
    // Structure of a JWT: header.payload.signature
    // Header: algorithm used (HS256)
    // Payload: claims (userId, role, expiry)
    // Signature: HMAC-SHA256(header + payload, secret)
    public string GenerateToken(AdvisorProfile advisor)
    {
        var secret = _config["Jwt:Secret"]!;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));

        // SigningCredentials bundles the key with the algorithm
        // HmacSha256 = HMAC + SHA256 — industry standard for JWTs
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // Claims are key-value pairs embedded in the token payload.
        // They travel with every request — no database lookup needed.
        // Standard claims use ClaimTypes constants.
        // Custom claims (department, branchCode) use plain strings.
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, advisor.UserId),
            new Claim(ClaimTypes.Role,           advisor.Role),
            new Claim("department",              advisor.Department),
            new Claim("branchCode",              advisor.BranchCode),

            // JTI = JWT ID — unique ID for this specific token.
            // Used for token revocation (if we ever need to blacklist one).
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),

            // IAT = Issued At. Lets us calculate token age.
            new Claim(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64)
        };

        var expiryHours = double.Parse(_config["Jwt:ExpiryHours"] ?? "8");

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(expiryHours),
            signingCredentials: creds
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        _logger.LogInformation(
            "Token issued for User:{UserId} Role:{Role} Expiry:{Expiry}h",
            advisor.UserId, advisor.Role, expiryHours);

        return tokenString;
    }

    // ── ExtractProfile ────────────────────────────────────────
    // Called in every controller action to get the advisor's
    // identity from the validated JWT claims.
    //
    // ASP.NET's JwtBearer middleware already validated the token
    // before the controller runs. We just read the claims.
    public AdvisorProfile? ExtractProfile(HttpContext context)
    {
        var userId = context.User
            .FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userId))
            return null;

        return new AdvisorProfile
        {
            UserId = userId,
            Role = context.User.FindFirst(ClaimTypes.Role)?.Value ?? "Advisor",
            Department = context.User.FindFirst("department")?.Value ?? "General",
            BranchCode = context.User.FindFirst("branchCode")?.Value ?? "UNKNOWN"
        };
    }
}