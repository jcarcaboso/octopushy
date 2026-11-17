using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
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

/// <summary>
/// Sign-in attempts per user name, from any address, next to the limit per address. A browser that signed in as the
/// user before counts apart from everyone else, so someone who sends wrong passwords for a real user name cannot keep
/// the operator's own browser out. A browser new to the user waits until they stop and a window passes.
/// </summary>
public sealed class AccountLoginLimiter : IDisposable
{
    public const int PermitLimit = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>User names share this many buckets, so names nobody has bound the memory the limiter keeps.</summary>
    public const int Buckets = 4096;

    public PartitionedRateLimiter<int> AttemptLimiter { get; } = PartitionedRateLimiter.Create<int, int>(bucket =>
        RateLimitPartition.GetFixedWindowLimiter(bucket, _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit, Window = Window }));

    /// <summary>
    /// The bucket of a user name as login looks it up: trimmed, and compared exactly. Browsers the user signed in from
    /// before share a second bucket.
    /// </summary>
    public static int Bucket(string username, bool knownBrowser) =>
        (int)(BitConverter.ToUInt32(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(username))) % Buckets) + (knownBrowser ? Buckets : 0);

    public void Dispose() => AttemptLimiter.Dispose();
}

public static class AccountEndpoints
{
    public const string LoginRateLimit = "login";

    /// <summary>Set at sign-in and kept through sign-out: the user name, sealed, so the server knows the browser again.</summary>
    public const string KnownBrowserCookie = "octopushy_known";

    private static readonly PasswordHasher<User> Hasher = new();

    private static readonly User DummyUser = new() { Username = "", PasswordHash = "" };

    // Hashed once with the current algorithm and work factor, so verifying against it costs what a real one does.
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(DummyUser, Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));

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

        group.MapPost("/login", async Task<Results<Ok<AuthState>, ProblemHttpResult>> (LoginRequest request, HttpContext http, OctopushyDb db, AccountLoginLimiter limiter, CancellationToken ct) =>
        {
            var username = request.Username.Trim();
            using var lease = limiter.AttemptLimiter.AttemptAcquire(AccountLoginLimiter.Bucket(username, SignedInBefore(http, username)));
            if (!lease.IsAcquired)
            {
                return TypedResults.Problem(title: "Too many sign-in attempts for this user name. Try again later.", statusCode: StatusCodes.Status429TooManyRequests);
            }

            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
            var result = PasswordVerificationResult.Failed;
            if (user is null)
            {
                // An unknown user name takes as long as a wrong password.
                Hasher.VerifyHashedPassword(DummyUser, DummyHash.Value, request.Password);
            }
            else
            {
                result = Hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
            }
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

    private static IDataProtector KnownBrowsers(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("octopushy.known-browser");

    private static bool SignedInBefore(HttpContext http, string username)
    {
        if (!http.Request.Cookies.TryGetValue(KnownBrowserCookie, out var value) || value.Length > 1024)
        {
            return false;
        }

        try
        {
            return KnownBrowsers(http).Unprotect(value) == username;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static Task SignInAsync(HttpContext http, User user)
    {
        http.Response.Cookies.Append(KnownBrowserCookie, KnownBrowsers(http).Protect(user.Username), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/api/auth",
            MaxAge = TimeSpan.FromDays(365),
        });
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }
}
