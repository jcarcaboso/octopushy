using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Octopushy.Server.Data;

namespace Octopushy.Server.Agents;

/// <summary>
/// Makes one-time enrollment tokens, for the Agents page and for a deploy that generates one. The server keeps only the
/// token's hash; the value is returned once and never again.
/// </summary>
public static partial class EnrollmentTokens
{
    /// <summary>The longest a token may live: a week.</summary>
    public const int MaxMinutes = 7 * 24 * 60;

    public static bool IsLabel(string label) => LabelPattern().IsMatch(label);

    /// <summary>Adds a token to <paramref name="db"/>, unsaved, and returns it with the value an agent enrolls with.</summary>
    public static (EnrollmentToken Token, string Value) Create(OctopushyDb db, AgentCertificateAuthority ca, string? name, IEnumerable<string> labels,
        int maxConcurrency, int minutes, DateTimeOffset now)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = new EnrollmentToken
        {
            Id = Guid.CreateVersion7(),
            TokenHash = EnrollmentService.TokenHash(secret),
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Labels = labels.Distinct().Order().ToArray(),
            MaxConcurrency = Math.Clamp(maxConcurrency, 1, 64),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Math.Clamp(minutes, 5, MaxMinutes)),
        };
        db.EnrollmentTokens.Add(token);
        return (token, $"opy1.{secret}.{ca.Fingerprint}");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex LabelPattern();
}
