using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server.Data;

namespace Octopushy.Server.Auth;

public sealed class OperatorTokenAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    OctopushyDb db, TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "operator-token";
    public const string TokenIdClaim = "octopushy:operator-token";
    public const string Prefix = "opy_op_";
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var raw = header[7..];
        if (!Request.IsHttps || raw.Length != Prefix.Length + 32 + 1 + 64
            || !raw.StartsWith(Prefix, StringComparison.Ordinal) || raw[Prefix.Length + 32] != '_'
            || !Guid.TryParseExact(raw.Substring(Prefix.Length, 32), "N", out var id))
            return AuthenticateResult.Fail("Invalid operator credential.");
        var token = await db.OperatorTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, Context.RequestAborted);
        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= clock.GetUtcNow()
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token.TokenHash), Encoding.ASCII.GetBytes(Hash(raw))))
            return AuthenticateResult.Fail("Invalid operator credential.");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == token.UserId, Context.RequestAborted);
        if (user is null) return AuthenticateResult.Fail("Invalid operator credential.");
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, $"{user.Username} [CLI: {token.Name}]"),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(TokenIdClaim, token.Id.ToString()),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperatorTokenRequest(string Name, int ExpiresInDays = 30);
public sealed record OperatorTokenView(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
public sealed record CreatedOperatorToken(Guid Id, string Token, DateTimeOffset ExpiresAt);

public static class OperatorTokenEndpoints
{
    public static void MapOperatorTokens(this IEndpointRouteBuilder app)
    {
        // Automation may use a grant but only the console session can grant or revoke others.
        var console = new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build();
        var group = app.MapGroup("/api/auth/operator-tokens").WithTags("Account");
        group.MapGet("", async (HttpContext http, OctopushyDb db, CancellationToken ct) =>
        {
            var userId = int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return await db.OperatorTokens.AsNoTracking().Where(t => t.UserId == userId).OrderByDescending(t => t.CreatedAt)
                .Select(t => new OperatorTokenView(t.Id, t.Name, t.CreatedAt, t.ExpiresAt, t.RevokedAt)).ToListAsync(ct);
        }).RequireAuthorization(console).WithName("listOperatorTokens");
        group.MapPost("", async Task<Results<Ok<CreatedOperatorToken>, ProblemHttpResult>> (
            OperatorTokenRequest request, HttpContext http, OctopushyDb db, TimeProvider clock, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps)
                return TypedResults.Problem(statusCode: 400, title: "Operator credentials require HTTPS.");
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 80 || name.Any(char.IsControl) || request.ExpiresInDays is < 1 or > 365)
                return TypedResults.Problem(statusCode: 400, title: "Choose a name of up to 80 characters and an expiry between 1 and 365 days.");
            var now = clock.GetUtcNow();
            var id = Guid.CreateVersion7();
            var raw = $"{OperatorTokenAuthentication.Prefix}{id:N}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}";
            var token = new OperatorToken
            {
                Id = id,
                UserId = int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!),
                Name = name,
                TokenHash = OperatorTokenAuthentication.Hash(raw),
                CreatedAt = now,
                ExpiresAt = now.AddDays(request.ExpiresInDays),
            };
            db.OperatorTokens.Add(token);
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new CreatedOperatorToken(token.Id, raw, token.ExpiresAt));
        }).RequireAuthorization(console).WithName("createOperatorToken");
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, OctopushyDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await db.OperatorTokens.Where(t => t.Id == id && t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.GetUtcNow()), ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(console).WithName("revokeOperatorToken");
        group.MapDelete("/self", async (HttpContext http, OctopushyDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (Guid.TryParse(http.User.FindFirstValue(OperatorTokenAuthentication.TokenIdClaim), out var id))
                await db.OperatorTokens.Where(t => t.Id == id && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.GetUtcNow()), ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(new AuthorizationPolicyBuilder(OperatorTokenAuthentication.SchemeName).RequireAuthenticatedUser().Build())
            .WithName("revokeCurrentOperatorToken");
    }
}
