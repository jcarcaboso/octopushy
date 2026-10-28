using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server;
using Octopushy.Server.Agents;
using Octopushy.Server.Api;
using Octopushy.Server.Auth;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

// The build runs this program to write the OpenAPI document; it needs no database or background work then.
var generatingDocument = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection(OctopushyOptions.Section).Get<OctopushyOptions>() ?? new OctopushyOptions();
settings.DiskReporter.Validate();
// A relative data directory is relative to the server's content root, not the working directory.
settings.DataDirectory = Path.GetFullPath(settings.DataDirectory, builder.Environment.ContentRootPath);
builder.Services.Configure<OctopushyOptions>(builder.Configuration.GetSection(OctopushyOptions.Section));
builder.Services.PostConfigure<OctopushyOptions>(options => options.DataDirectory = settings.DataDirectory);
if (!generatingDocument)
{
    Directory.CreateDirectory(settings.DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

builder.Services.AddDbContext<OctopushyDb>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Octopushy") ?? throw new InvalidOperationException("Set ConnectionStrings:Octopushy."))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<KeyEncryptionKey>();
builder.Services.AddSingleton<Envelope>();
builder.Services.AddScoped<VariableStore>();
builder.Services.AddSingleton<GitCli>();
builder.Services.AddSingleton<RepositoryMirror>();
builder.Services.AddSingleton<RevisionCache>();
builder.Services.AddSingleton<RepositorySync>();
builder.Services.AddSingleton<AgentCertificateAuthority>();
builder.Services.AddSingleton<AgentConnections>();
builder.Services.AddSingleton<AttemptLogStore>();
builder.Services.AddSingleton<ArtifactStore>();
builder.Services.AddSingleton<OrchestratorSignal>();
builder.Services.AddSingleton<RunFactory>();
builder.Services.AddSingleton<RunEngine>();
builder.Services.AddScoped<AssignmentBuilder>();
builder.Services.AddScoped<InventoryUpdater>();
builder.Services.AddSingleton<Planner>();
if (!generatingDocument && settings.RunBackgroundServices)
{
    builder.Services.AddHostedService(services => services.GetRequiredService<RepositorySync>());
    builder.Services.AddHostedService<Orchestrator>();
}

builder.Services.AddScoped<Views>();
builder.Services.AddSingleton<SetupCode>();

builder.Services.AddGrpc(options =>
{
    // Assignments carry the repository snapshot, and results carry saved plans.
    // Matches the agent's limits in both directions.
    options.MaxReceiveMessageSize = 64 * 1024 * 1024;
    options.MaxSendMessageSize = 64 * 1024 * 1024;
});

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("octopushy");
if (!generatingDocument)
{
    // Keeps sessions valid across restarts.
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataDirectory, "data-protection")));
}
builder.Services.AddAuthentication("operator-or-console")
    .AddPolicyScheme("operator-or-console", null, options =>
        options.ForwardDefaultSelector = http => http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? OperatorTokenAuthentication.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, OperatorTokenAuthentication>(OperatorTokenAuthentication.SchemeName, _ => { })
    .AddCookie(options =>
    {
        options.Cookie.Name = "octopushy_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AccountEndpoints.LoginRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy(GuestDiskUsageEndpoints.RateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (settings.TrustForwardedHeaders)
    {
        // The API port is reached only through the frontend's web server.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    kestrel.Listen(IPAddress.Parse(settings.ApiBindAddress), settings.ApiPort, listen => listen.Protocols = HttpProtocols.Http1);
    kestrel.Listen(IPAddress.Parse(settings.AgentChannel.BindAddress), settings.AgentChannel.Port, listen =>
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
        listen.UseHttps(https =>
        {
            var ca = kestrel.ApplicationServices.GetRequiredService<AgentCertificateAuthority>();
            https.ServerCertificateSelector = (_, _) => ca.ServerCertificate();
            // Enrollment and the CA download need no certificate; the agent channel checks for one.
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (certificate, _, _) => ca.ValidateClientCertificate(certificate);
        });
    });
});

var app = builder.Build();

if (args is ["migrate", ..])
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<OctopushyDb>().Database.MigrateAsync();
    app.Logger.LogInformation("The database schema is up to date.");
    return;
}

if (!generatingDocument)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
    if (settings.MigrateOnStart)
    {
        await db.Database.MigrateAsync();
    }
    else if ((await db.Database.GetPendingMigrationsAsync()).Any())
    {
        throw new InvalidOperationException("The database schema is out of date; run 'octopushy-server migrate' first.");
    }

    await KeyEncryptionKey.InitializeAsync(db, settings, app.Logger, CancellationToken.None);
    await app.Services.GetRequiredService<AgentCertificateAuthority>().InitializeAsync(CancellationToken.None);
    await app.Services.GetRequiredService<SetupCode>().EnsureAsync(db, CancellationToken.None);
}

app.UseForwardedHeaders();
// Each port serves only its own endpoints, by the port the connection arrived on rather than
// the Host header, so a proxy may keep the browser's host name.
app.Use(async (context, next) =>
{
    var onAgentPort = context.Connection.LocalPort == settings.AgentChannel.Port;
    var path = context.Request.Path;
    var agentPath = path.StartsWithSegments("/agent") || path.Value?.StartsWith("/octopushy.agent.v1.", StringComparison.Ordinal) == true;
    if (onAgentPort != agentPath)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi("/api/openapi/{documentName}.json");
app.MapGet("/api/health", () => TypedResults.Ok(new { status = "ok" })).ExcludeFromDescription();
app.MapAccount();
app.MapOperatorTokens();
app.MapApi();

app.MapGrpcService<EnrollmentService>();
app.MapGrpcService<AgentChannelService>();
app.MapGet("/agent/ca.pem", (AgentCertificateAuthority ca) => Results.Text(ca.CertificatePem, "application/x-pem-file"))
    .ExcludeFromDescription();

await app.RunAsync();

/// <summary>Exposed for integration tests.</summary>
public partial class Program;
