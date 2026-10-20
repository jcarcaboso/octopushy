using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server.Data;
using Octopushy.Server.Options;

namespace Octopushy.Server.Auth;

/// <summary>
/// The one-time code needed to create the account on a fresh install, written to the data
/// directory, so that whoever reaches an exposed new install first cannot claim it.
/// </summary>
public sealed class SetupCode(IOptions<OctopushyOptions> options, ILogger<SetupCode> logger)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private string FilePath => Path.Combine(options.Value.DataDirectory, "setup-code");

    public async Task EnsureAsync(OctopushyDb db, CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(cancellationToken))
        {
            Remove();
            return;
        }

        if (!File.Exists(FilePath))
        {
            Directory.CreateDirectory(options.Value.DataDirectory);
            var code = string.Concat(Enumerable.Range(0, 12).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]));
            await File.WriteAllTextAsync(FilePath, code + "\n", cancellationToken);
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        logger.LogWarning("No account exists yet. Create it in the web interface with the setup code in {Path}.", FilePath);
    }

    public bool Matches(string code) =>
        File.Exists(FilePath)
        && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(FilePath).Trim()),
            System.Text.Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant()));

    public void Remove()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}

public sealed record AuthState(bool SetupRequired, bool Authenticated, string? Username);

public sealed record SetupRequest(string SetupCode, string Username, string Password);

public sealed record LoginRequest(string Username, string Password);

public static class AccountEndpoints
{
    public const string LoginRateLimit = "login";

    private static readonly PasswordHasher<User> Hasher = new();

    public static void MapAccount(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth")
            .WithTags("Account")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/state", async (HttpContext http, OctopushyDb db, CancellationToken ct) =>
            TypedResults.Ok(new AuthState(
                !await db.Users.AnyAsync(ct),
                http.User.Identity?.IsAuthenticated == true,
                http.User.Identity?.Name)))
            .WithName("getAuthState");

        group.MapPost("/setup", async Task<Results<Ok<AuthState>, ProblemHttpResult>> (SetupRequest request, HttpContext http, OctopushyDb db, SetupCode setup, TimeProvider clock, CancellationToken ct) =>
        {
            if (await db.Users.AnyAsync(ct))
            {
                return TypedResults.Problem(title: "The account already exists.", statusCode: StatusCodes.Status409Conflict);
            }

            if (!setup.Matches(request.SetupCode))
            {
                return TypedResults.Problem(title: "The setup code is wrong.", statusCode: StatusCodes.Status403Forbidden);
            }

            if (PasswordProblem(request.Username, request.Password) is { } problem)
            {
                return TypedResults.Problem(title: problem, statusCode: StatusCodes.Status400BadRequest);
            }

            var user = new User { Username = request.Username.Trim(), PasswordHash = "", CreatedAt = clock.GetUtcNow() };
            user.PasswordHash = Hasher.HashPassword(user, request.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            setup.Remove();
            await SignInAsync(http, user);
            return TypedResults.Ok(new AuthState(false, true, user.Username));
        })
        .WithName("setupAccount")
        .RequireRateLimiting(LoginRateLimit);

        group.MapPost("/login", async Task<Results<Ok<AuthState>, ProblemHttpResult>> (LoginRequest request, HttpContext http, OctopushyDb db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == request.Username.Trim(), ct);
            var result = user is null ? PasswordVerificationResult.Failed : Hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
            if (result == PasswordVerificationResult.Failed)
            {
                return TypedResults.Problem(title: "The user name or password is wrong.", statusCode: StatusCodes.Status401Unauthorized);
            }

            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user!.PasswordHash = Hasher.HashPassword(user, request.Password);
                await db.SaveChangesAsync(ct);
            }

            await SignInAsync(http, user!);
            return TypedResults.Ok(new AuthState(false, true, user!.Username));
        })
        .WithName("login")
        .RequireRateLimiting(LoginRateLimit);

        group.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.NoContent();
        })
        .WithName("logout");
    }

    private static string? PasswordProblem(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length > 64)
        {
            return "Choose a user name of up to 64 characters.";
        }

        return password.Length < 12 ? "Use a password of at least 12 characters." : null;
    }

    private static Task SignInAsync(HttpContext http, User user)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }
}
