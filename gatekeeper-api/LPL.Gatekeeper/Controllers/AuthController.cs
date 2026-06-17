using LPL.Gatekeeper.Models;
using LPL.Gatekeeper.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LPL.Gatekeeper.Controllers;

[ApiController]
public class AuthController : ControllerBase
{
    private readonly ITokenService _tokenService;
    private readonly ILogger<AuthController> _logger;

    // ── Simulated User Store ──────────────────────────────────
    // In production this is replaced by:
    // - Active Directory / LDAP for on-premise firms
    // - Amazon Cognito for cloud-native
    // - Azure AD B2C for Microsoft shops
    //
    // The interface (username → AdvisorProfile) stays identical.
    // Only the data source changes. That's the Dependency Inversion
    // principle in action — your controller never changes.
    private static readonly Dictionary<string, (string Password, AdvisorProfile Profile)>
        Users = new()
        {
            ["john.smith"] = ("Pass1!", new AdvisorProfile
            {
                UserId = "john.smith",
                Role = "Advisor",
                Department = "Wealth Management",
                BranchCode = "NYC-001"
            }),
            ["sarah.jones"] = ("Pass1!", new AdvisorProfile
            {
                UserId = "sarah.jones",
                Role = "Advisor",
                Department = "Retirement Planning",
                BranchCode = "CHI-003"
            }),
            ["mike.compliance"] = ("Pass1!", new AdvisorProfile
            {
                UserId = "mike.compliance",
                Role = "Compliance",
                Department = "Compliance",
                BranchCode = "HQ-000"
            }),
            ["admin.tech"] = ("Pass1!", new AdvisorProfile
            {
                UserId = "admin.tech",
                Role = "Admin",
                Department = "Technology",
                BranchCode = "HQ-000"
            })
        };

    public AuthController(ITokenService tokenService,
        ILogger<AuthController> logger)
    {
        _tokenService = tokenService;
        _logger = logger;
    }

    // ── POST /login ───────────────────────────────────────────
    // Public endpoint — no [Authorize] attribute.
    // This is the ONLY unauthenticated entry point.
    // All other endpoints require a valid JWT.
    [HttpPost("/login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        // Never log passwords — compliance requirement
        _logger.LogInformation("Login attempt for User:{UserId}", request.Username);

        if (!Users.TryGetValue(request.Username, out var user))
        {
            // Return same message for unknown user AND wrong password
            // This prevents username enumeration attacks
            return Unauthorized(new { error = "Invalid credentials" });
        }

        if (user.Password != request.Password)
        {
            _logger.LogWarning("Failed login for User:{UserId}", request.Username);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        var token = _tokenService.GenerateToken(user.Profile);

        _logger.LogInformation(
            "Successful login for User:{UserId} Role:{Role}",
            user.Profile.UserId, user.Profile.Role);

        return Ok(new
        {
            token,
            user_id = user.Profile.UserId,
            role = user.Profile.Role,
            department = user.Profile.Department,
            branch = user.Profile.BranchCode,
            expires_in = "8 hours",
            token_type = "Bearer"
        });
    }

    // ── GET /me ───────────────────────────────────────────────
    // Protected endpoint — validates token, returns identity.
    // Useful for the UI to know who is logged in.
    [HttpGet("/me")]
    [Authorize]
    public IActionResult Me()
    {
        var profile = _tokenService.ExtractProfile(HttpContext);

        if (profile == null)
            return Unauthorized();

        return Ok(new
        {
            user_id = profile.UserId,
            role = profile.Role,
            department = profile.Department,
            branch = profile.BranchCode,
            token_valid = true
        });
    }

    // ── POST /refresh ─────────────────────────────────────────
    // Takes a valid (but expiring) token and issues a new one.
    // Prevents advisors from being kicked out mid-session.
    // Only allows refresh if token is within last 2 hours of expiry.
    [HttpPost("/refresh")]
    [Authorize]
    public IActionResult Refresh()
    {
        var profile = _tokenService.ExtractProfile(HttpContext);

        if (profile == null)
            return Unauthorized();

        var newToken = _tokenService.GenerateToken(profile);

        _logger.LogInformation("Token refreshed for User:{UserId}", profile.UserId);

        return Ok(new
        {
            token = newToken,
            expires_in = "8 hours"
        });
    }
}