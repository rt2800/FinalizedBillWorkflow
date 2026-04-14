using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Mbr.Api.Workflow.FinalizedBill.Models;

namespace Mbr.Api.Workflow.FinalizedBill.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public AuthController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Generates a JWT token for a demo user.
    /// In a real application, this would validate credentials against a database.
    /// </summary>
    [Authorize(AuthenticationSchemes = "Basic")]
    [HttpPost("token")]
    [ProducesResponseType(typeof(ApiResponse<TokenResponse>), StatusCodes.Status200OK)]
    public IActionResult GenerateToken()
    {
        var jwtKey = _configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is missing.");
        var jwtIssuer = _configuration["Jwt:Issuer"] ?? "Mbr.Api.Workflow.FinalizedBill";
        var jwtAudience = _configuration["Jwt:Audience"] ?? "Mbr.Api.Workflow.FinalizedBillUsers";
        var expireMinutes = int.TryParse(_configuration["Jwt:ExpireMinutes"], out var min) ? min : 60;

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var userName = User.Identity?.Name ?? "DemoUser";

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, userName),
            new Claim(ClaimTypes.Role, "User"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expireMinutes),
            signingCredentials: credentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return Ok(ApiResponse<TokenResponse>.Ok(new TokenResponse(tokenString), "Token generated successfully."));
    }
}

public record TokenResponse(string Token);
