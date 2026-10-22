using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Store;

namespace Octopushy.Server.Agents;

/// <summary>
/// The CA that agents trust and that issues their client certificates. Its key is kept in the
/// variable store's system scope; the agent endpoint's TLS certificate is issued from it at start
/// and renewed while running.
/// </summary>
public sealed class AgentCertificateAuthority(
    IServiceScopeFactory scopes,
    IOptions<OctopushyOptions> options,
    TimeProvider clock,
    ILogger<AgentCertificateAuthority> logger)
{
    private const string KeyEntry = "system/agent-ca/key";
    private const string CertificateEntry = "system/agent-ca/certificate";
    private const string AgentUriPrefix = "urn:octopushy:agent:";

    private X509Certificate2? _ca;
    private ECDsa? _caKey;
    private X509Certificate2? _server;

    public X509Certificate2 Certificate => _ca ?? throw new InvalidOperationException("The agent CA is not initialized.");

    /// <summary>Lowercase hex SHA-256 of the CA certificate, embedded in enrollment tokens.</summary>
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Certificate.RawData));

    public string CertificatePem => Certificate.ExportCertificatePem();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<VariableStore>();
        var key = await store.GetAsync(KeyEntry, cancellationToken);
        var certificate = await store.GetAsync(CertificateEntry, cancellationToken);
        if (key is null || certificate is null || !PairMatches(key.Value.Value, certificate.Value.Value))
        {
            // A new CA is only safe while nothing pins the old one: every enrolled agent and every issued
            // enrollment token does, so replacing it silently orphans them all. A half-written first
            // start (the key and certificate are separate writes) has nothing to orphan and is redone.
            var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
            if (await db.Agents.AnyAsync(cancellationToken) || await db.EnrollmentTokens.AnyAsync(cancellationToken))
            {
                var problem = key is null && certificate is null ? $"{KeyEntry} and {CertificateEntry} are missing or unavailable"
                    : key is null ? $"{KeyEntry} is missing or unavailable"
                    : certificate is null ? $"{CertificateEntry} is missing or unavailable"
                    : $"{KeyEntry} does not belong to {CertificateEntry}";
                throw new InvalidOperationException($"The agent CA is unusable on an existing installation: {problem}. Restore them from a backup; a new CA would make every enrolled agent and enrollment token untrusted.");
            }

            using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Octopushy agent CA", newKey, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var now = clock.GetUtcNow();
            using var created = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
            await store.SetInternalAsync(KeyEntry, newKey.ExportPkcs8PrivateKeyPem(), Visibilities.Secret, "Private key of the agent CA.", cancellationToken);
            await store.SetInternalAsync(CertificateEntry, created.ExportCertificatePem(), Visibilities.Plain, "Certificate of the agent CA.", cancellationToken);
            key = await store.GetAsync(KeyEntry, cancellationToken);
            certificate = await store.GetAsync(CertificateEntry, cancellationToken);
            logger.LogInformation("Created the agent CA.");
        }

        _caKey = ECDsa.Create();
        _caKey.ImportFromPem(key!.Value.Value);
        _ca = X509Certificate2.CreateFromPem(certificate!.Value.Value);
        _server = IssueServerCertificate();
        logger.LogInformation("Agent CA fingerprint: {Fingerprint}", Fingerprint);
    }

    /// <summary>Whether the private key is the one the certificate certifies; false for anything unparseable.</summary>
    private static bool PairMatches(string keyPem, string certificatePem)
    {
        try
        {
            using var privateKey = ECDsa.Create();
            privateKey.ImportFromPem(keyPem);
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            using var publicKey = certificate.GetECDsaPublicKey();
            return publicKey is not null && privateKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo());
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The agent endpoint's TLS certificate, reissued when less than 30 days remain.</summary>
    public X509Certificate2 ServerCertificate()
    {
        var current = _server ?? throw new InvalidOperationException("The agent CA is not initialized.");
        if (current.NotAfter.ToUniversalTime() - clock.GetUtcNow().UtcDateTime < TimeSpan.FromDays(30))
        {
            _server = current = IssueServerCertificate();
        }

        return current;
    }

    /// <summary>Signs a certificate request for an agent; the subject is set by the server.</summary>
    public (byte[] Der, string Serial, DateTimeOffset NotAfter) IssueAgentCertificate(byte[] csrDer, Guid agentId)
    {
        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequest(csrDer, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
        }
        catch (CryptographicException e)
        {
            throw new ArgumentException($"The certificate request is invalid: {e.Message}", nameof(csrDer), e);
        }

        var request = new CertificateRequest(new X500DistinguishedName($"CN={agentId}"), csr.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(AgentUriPrefix + agentId));
        request.CertificateExtensions.Add(san.Build());

        var now = clock.GetUtcNow();
        var notAfter = now.AddDays(options.Value.AgentChannel.CertificateDays);
        var serial = NewSerial();
        using var certificate = request.Create(Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(_caKey!), now.AddMinutes(-5), notAfter, serial);
        return (certificate.RawData, Convert.ToHexStringLower(serial), notAfter);
    }

    /// <summary>Checks that a client certificate was issued by this CA for an agent.</summary>
    public bool ValidateClientCertificate(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = clock.GetUtcNow().UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        return chain.Build(certificate) && AgentId(certificate) is not null;
    }

    /// <summary>The agent id in a certificate's subject, or null.</summary>
    public static Guid? AgentId(X509Certificate2 certificate) =>
        Guid.TryParse(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false), out var id) ? id : null;

    /// <summary>The serial number as lowercase big-endian hex, as recorded when it was issued.</summary>
    public static string Serial(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(certificate.SerialNumberBytes.Span);

    private X509Certificate2 IssueServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Octopushy agent endpoint", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in ServerNames())
        {
            if (IPAddress.TryParse(name, out var address))
            {
                san.AddIpAddress(address);
            }
            else
            {
                san.AddDnsName(name);
            }
        }

        request.CertificateExtensions.Add(san.Build());
        var now = clock.GetUtcNow();
        using var issued = request.Create(Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(_caKey!), now.AddMinutes(-5), now.AddDays(90), NewSerial());
        using var withKey = issued.CopyWithPrivateKey(key);
        // Re-importing gives a certificate whose key SslStream can use on every platform.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    private IEnumerable<string> ServerNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1", Dns.GetHostName() };
        foreach (var name in options.Value.AgentChannel.Hostnames)
        {
            names.Add(name);
        }

        if (options.Value.AgentChannel.PublicUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            names.Add(uri.Host.Trim('[', ']'));
        }

        return names;
    }

    private static byte[] NewSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        serial[0] |= 0x01;
        return serial;
    }
}
