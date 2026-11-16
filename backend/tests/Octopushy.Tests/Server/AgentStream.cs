using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;

namespace Octopushy.Tests.Server;

internal sealed class AgentStream : IAsyncStreamReader<AgentMessage>, IServerStreamWriter<ServerMessage>
{
    private readonly Channel<AgentMessage> _incoming = Channel.CreateUnbounded<AgentMessage>();
    public Channel<ServerMessage> Sent { get; } = Channel.CreateUnbounded<ServerMessage>();
    public Func<ServerMessage, Task>? OnWrite { get; init; }
    public TaskCompletionSource ReadingMessages { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public AgentMessage Current { get; private set; } = null!;
    public WriteOptions? WriteOptions { get; set; }
    private int _reads;

    public void Send(AgentMessage message) => Assert.True(_incoming.Writer.TryWrite(message));
    public void Complete() => _incoming.Writer.Complete();
    public void Dispose() { }

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (_reads++ == 1) ReadingMessages.TrySetResult();
        if (!await _incoming.Reader.WaitToReadAsync(cancellationToken)) return false;
        Current = await _incoming.Reader.ReadAsync(cancellationToken);
        return true;
    }

    public async Task WriteAsync(ServerMessage message)
    {
        if (OnWrite is { } callback) await callback(message);
        await Sent.Writer.WriteAsync(message);
    }

    public Task WriteAsync(ServerMessage message, CancellationToken cancellationToken) => WriteAsync(message);

    public static async Task<ServerCallContext> CallAsAsync(ServerHarness server, FakeAgent agent)
    {
        var ca = server.Services.GetRequiredService<AgentCertificateAuthority>();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=agent", key, HashAlgorithmName.SHA256);
        var (der, serial, _) = ca.IssueAgentCertificate(request.CreateSigningRequest(), agent.Id);
        await server.WithDbAsync(db => db.Agents.Where(a => a.Id == agent.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.CertificateSerial, serial)));
        return new CallContext(new DefaultHttpContext { Connection = { ClientCertificate = X509CertificateLoader.LoadCertificate(der) } });
    }

    private sealed class CallContext : ServerCallContext
    {
        public CallContext(HttpContext http) => UserState["__HttpContext"] = http;
        protected override string MethodCore => "test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, []);
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
