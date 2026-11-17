using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server.Data;
using Octopushy.Server.Options;

namespace Octopushy.Server.Auth;

/// <summary>
/// A session cookie names its user, the user's session stamp when it was issued, and when the user signed in. It is
/// refused once the stamp has changed, which sign-out does, or once it is older than the absolute lifetime, however
/// often it was renewed. A cookie from before stamps has neither, so it is refused too.
/// </summary>
public static class Sessions
{
    public const string StampClaim = "octopushy:session-stamp";
    public const string SignedInClaim = "octopushy:signed-in";

    public static ClaimsPrincipal Principal(User user, DateTimeOffset now) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(StampClaim, user.SessionStamp.ToString()),
            new Claim(SignedInClaim, now.ToUnixTimeSeconds().ToString()),
        ],
        CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>Runs on every request with a session cookie: one read of the user's row by its key.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var lifetime = TimeSpan.FromDays(services.GetRequiredService<IOptions<OctopushyOptions>>().Value.Sessions.AbsoluteLifetimeDays);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var principal = context.Principal;
        if (int.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && Guid.TryParse(principal.FindFirstValue(StampClaim), out var stamp)
            && long.TryParse(principal.FindFirstValue(SignedInClaim), out var signedIn)
            && now < DateTimeOffset.FromUnixTimeSeconds(signedIn) + lifetime
            && await services.GetRequiredService<OctopushyDb>().Users
                .AnyAsync(u => u.Id == userId && u.SessionStamp == stamp, context.HttpContext.RequestAborted))
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }
}
