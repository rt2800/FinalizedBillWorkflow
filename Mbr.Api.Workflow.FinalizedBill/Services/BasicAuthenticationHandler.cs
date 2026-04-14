using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Mbr.Api.Workflow.FinalizedBill.Models;

namespace Mbr.Api.Workflow.FinalizedBill.Services;

public sealed class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly BasicAuthSettings _settings;

    public BasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<BasicAuthSettings> settings)
        : base(options, logger, encoder)
    {
        _settings = settings.Value;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("Authorization"))
        {
            return AuthenticateResult.NoResult();
        }

        try
        {
            var authHeader = AuthenticationHeaderValue.Parse(Request.Headers["Authorization"]!);
            if (!"Basic".Equals(authHeader.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return AuthenticateResult.NoResult();
            }

            var credentialBytes = Convert.FromBase64String(authHeader.Parameter ?? string.Empty);
            var credentials = Encoding.UTF8.GetString(credentialBytes).Split(':', 2);

            if (credentials.Length != 2)
            {
                return AuthenticateResult.Fail("Invalid Basic authentication header format.");
            }

            var clientId = credentials[0];
            var clientSecret = credentials[1];

            var client = _settings.Clients.FirstOrDefault(c =>
                c.ClientId.Equals(clientId, StringComparison.OrdinalIgnoreCase) &&
                c.ClientSecret.Equals(clientSecret, StringComparison.OrdinalIgnoreCase));

            if (client == null)
            {
                return AuthenticateResult.Fail("Invalid ClientId or ClientSecret.");
            }

            var claims = new[] {
                new Claim(ClaimTypes.NameIdentifier, client.ClientId),
                new Claim(ClaimTypes.Name, client.ClientId),
            };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
        catch (Exception ex)
        {
            return AuthenticateResult.Fail($"Authentication failed: {ex.Message}");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.Append("WWW-Authenticate", "Basic realm=\"Mbr.Api.Workflow.FinalizedBill\"");
        return base.HandleChallengeAsync(properties);
    }
}
