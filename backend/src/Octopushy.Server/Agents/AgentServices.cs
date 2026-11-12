using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Images;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Agents;

/// <summary>Exchanges a one-time enrollment token and a certificate request for an agent certificate.</summary>
public sealed class EnrollmentService(
    IServiceScopeFactory scopes,
    AgentCertificateAuthority ca,
    TimeProvider clock,
    ILogger<EnrollmentService> logger) : Enrollment.EnrollmentBase
{
    public override async Task<EnrollResponse> Enroll(EnrollRequest request, ServerCallContext context)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var now = clock.GetUtcNow();
        var hash = TokenHash(request.Token);
        var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, context.CancellationToken);
        if (token is null || token.UsedAt is not null || token.ExpiresAt < now)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The enrollment token is unknown, already used, or expired."));
        }

        var agentId = Guid.CreateVersion7();
        (byte[] Der, string Serial, DateTimeOffset NotAfter) certificate;
        try
        {
            certificate = ca.IssueAgentCertificate(request.CsrDer.ToByteArray(), agentId);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }

        // Claim the token atomically, so two agents cannot enroll with it at once.
        var claimed = await db.EnrollmentTokens
            .Where(t => t.Id == token.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.UsedAt, now).SetProperty(t => t.AgentId, agentId), context.CancellationToken);
        if (claimed == 0)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The enrollment token was used meanwhile."));
        }

        db.Agents.Add(new AgentRecord
        {
            Id = agentId,
            Name = DisplayName(token.Name ?? request.Name, agentId),
            Labels = token.Labels,
            MaxConcurrency = token.MaxConcurrency,
            EnrolledAt = now,
            Version = request.AgentVersion,
            CertificateSerial = certificate.Serial,
            CertificateExpiresAt = certificate.NotAfter,
        });
        await db.SaveChangesAsync(context.CancellationToken);
        logger.LogInformation("Agent {Agent} enrolled with labels {Labels}.", agentId, string.Join(",", token.Labels));
        return new EnrollResponse
        {
            AgentId = agentId.ToString(),
            CertificateDer = ByteString.CopyFrom(certificate.Der),
            CaCertificateDer = ByteString.CopyFrom(ca.Certificate.RawData),
        };
    }

    public static string TokenHash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string DisplayName(string? name, Guid id)
    {
        var cleaned = new string((name ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(63).ToArray());
        return cleaned.Length > 0 ? cleaned : $"agent-{id.ToString()[..8]}";
    }
}

/// <summary>The stream each agent keeps open: assignments go out, logs and results come in.</summary>
public sealed class AgentChannelService(
    IServiceScopeFactory scopes,
    AgentCertificateAuthority ca,
    AgentConnections connections,
    WorkerReachability reachability,
    Uptime.UptimeMonitor uptime,
    RunEngine engine,
    AttemptLogStore logs,
    AttemptMasks masks,
    ImageStore images,
    Microsoft.Extensions.Options.IOptions<Options.OctopushyOptions> options,
    TimeProvider clock,
    ILogger<AgentChannelService> logger) : AgentChannel.AgentChannelBase
{
    public override async Task Connect(IAsyncStreamReader<AgentMessage> requestStream, IServerStreamWriter<ServerMessage> responseStream, ServerCallContext context)
    {
        var (agent, serial) = await AuthenticateAsync(context);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        if (!await requestStream.MoveNext(call.Token) || requestStream.Current.KindCase != AgentMessage.KindOneofCase.Hello)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first message must be Hello."));
        }

        var hello = requestStream.Current.Hello;
        await UpdateAgentAsync(agent.Id, a =>
        {
            a.Version = hello.AgentVersion;
            a.Executors = hello.Executors.ToArray();
            a.ToolRuntime = hello.ToolRuntime;
            a.ConnectedAt = a.LastSeenAt = clock.GetUtcNow();
            if (a.CertificateSerial == serial)
            {
                a.PreviousCertificateSerial = null;
            }
        }, call.Token);

        var session = new AgentSession(agent.Id, agent.Name, hello.Executors.ToList(), hello.Capabilities.ToList(), hello.ToolRuntime)
        {
            RemoteAddress = context.GetHttpContext().Connection.RemoteIpAddress,
        };
        connections.Add(session);
        reachability.OnConnected(session);
        logger.LogInformation("Agent {Name} ({Agent}) connected, version {Version}.", agent.Name, agent.Id, hello.AgentVersion);
        var writer = Task.CompletedTask;
        try
        {
            await responseStream.WriteAsync(new ServerMessage
            {
                Welcome = new Welcome
                {
                    AgentId = agent.Id.ToString(),
                    MaxConcurrency = (uint)agent.MaxConcurrency,
                    HeartbeatSeconds = (uint)options.Value.AgentChannel.HeartbeatSeconds,
                },
            }, call.Token);
            await engine.OnHelloAsync(agent.Id, hello, call.Token);

            writer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var message in session.Outbound.ReadAllAsync(call.Token))
                    {
                        await responseStream.WriteAsync(message, call.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    // The session was closed, for example because the agent was disabled.
                    await call.CancelAsync();
                }
            }, CancellationToken.None);

            var ownAttempts = new HashSet<Guid>();
            while (await requestStream.MoveNext(call.Token))
            {
                var message = requestStream.Current;
                switch (message.KindCase)
                {
                    case AgentMessage.KindOneofCase.Heartbeat:
                        await engine.OnHeartbeatAsync(agent.Id, message.Heartbeat.RunningAttemptIds, call.Token);
                        break;
                    case AgentMessage.KindOneofCase.Started when Guid.TryParse(message.Started.AttemptId, out var started):
                        await engine.OnStartedAsync(agent.Id, started, call.Token);
                        break;
                    case AgentMessage.KindOneofCase.Log when Guid.TryParse(message.Log.AttemptId, out var attemptId):
                        if (ownAttempts.Contains(attemptId) || await IsAssignedAsync(agent.Id, attemptId, call.Token))
                        {
                            ownAttempts.Add(attemptId);
                            var lines = message.Log.Lines.Select(l => (l.UnixMs, StreamName(l.Stream), l.Text)).ToList();
                            // A restarted controller reads the attempt's secrets back before it keeps a line.
                            using (await masks.EnsureAsync(attemptId, call.Token))
                                await logs.AppendAsync(attemptId, (long)message.Log.FirstSequence, lines, call.Token);
                        }

                        break;
                    case AgentMessage.KindOneofCase.StateSnapshot when message.StateSnapshot.State is not null && Guid.TryParse(message.StateSnapshot.AttemptId, out var snapshot):
                        await engine.OnStateSnapshotAsync(agent.Id, snapshot, message.StateSnapshot.State, call.Token);
                        break;
                    case AgentMessage.KindOneofCase.Reachability:
                        // Ephemeral: an answer this session is not waiting for is dropped, and nothing is acknowledged.
                        // An uptime probe's answer is never reachability evidence.
                        if (!await uptime.AcceptAsync(session, message.Reachability, call.Token))
                            await reachability.AcceptAsync(session, message.Reachability, call.Token);
                        break;
                    case AgentMessage.KindOneofCase.Result:
                        if (await engine.OnResultAsync(agent.Id, message.Result, call.Token))
                        {
                            await session.SendAsync(new ServerMessage { ResultAck = new ResultAck { AttemptId = message.Result.AttemptId } }, call.Token);
                        }

                        break;
                }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested || call.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // The connection dropped; the agent reconnects.
        }
        finally
        {
            connections.Remove(session);
            await call.CancelAsync();
            await writer;
            await UpdateAgentAsync(agent.Id, a => a.LastSeenAt = clock.GetUtcNow(), CancellationToken.None);
            logger.LogInformation("Agent {Name} ({Agent}) disconnected.", agent.Name, agent.Id);
        }
    }

    /// <summary>
    /// Receives the image of a <c>nix</c> <c>build</c> attempt assigned to the calling agent, in chunks of at most 4 MiB: the first
    /// names the attempt, the file name and the size, the last carries the SHA-256. Kept only when the bytes, their count and their
    /// digest all match; refused with "image store full" when it would not fit once unreferenced images are gone.
    /// </summary>
    public override async Task<ImageUploaded> UploadImage(IAsyncStreamReader<ImageChunk> requestStream, ServerCallContext context)
    {
        var (agent, _) = await AuthenticateAsync(context);
        var cancellationToken = context.CancellationToken;
        if (!await requestStream.MoveNext(cancellationToken))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "An upload starts with a chunk naming its attempt."));
        var first = requestStream.Current;
        if (!Guid.TryParse(first.AttemptId, out var attemptId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first chunk names the attempt the image is for."));
        if (first.Size > long.MaxValue)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The image is too large."));
        var size = (long)first.Size;
        // Only an upload that was let in holds room and a partial file; a refused one must not touch another's.
        var authorized = false;
        void Release()
        {
            if (authorized)
                images.Release(attemptId);
        }

        try
        {
            await engine.AuthorizeImageUploadAsync(agent.Id, attemptId, first.FileName, size, cancellationToken);
            authorized = true;
            var sha256 = await images.ReceiveAsync(attemptId, size, first,
                async ct => await requestStream.MoveNext(ct) ? requestStream.Current : null, cancellationToken);
            await engine.CompleteImageUploadAsync(agent.Id, attemptId, sha256, size, first.FileName, cancellationToken);
            return new ImageUploaded { Sha256 = sha256, Size = (ulong)size };
        }
        catch (ImageAccessException e)
        {
            Release();
            logger.LogWarning("Agent {Agent} was refused an upload for attempt {Attempt}: {Reason}", agent.Id, attemptId, e.Message);
            throw new RpcException(new Status(StatusCode.PermissionDenied, e.Message));
        }
        catch (ImageUploadException e)
        {
            Release();
            logger.LogWarning("The image upload of attempt {Attempt} was refused: {Reason}", attemptId, e.Message);
            throw new RpcException(new Status(e.Full ? StatusCode.ResourceExhausted : StatusCode.InvalidArgument, e.Message));
        }
        catch
        {
            Release();
            throw;
        }
    }

    /// <summary>
    /// Sends an image to the calling agent for an attempt assigned to it whose input names that image, in chunks of 4 MiB: the
    /// first carries the file name and size, the last the SHA-256.
    /// </summary>
    public override async Task DownloadImage(DownloadImageRequest request, IServerStreamWriter<ImageChunk> responseStream, ServerCallContext context)
    {
        var (agent, _) = await AuthenticateAsync(context);
        var cancellationToken = context.CancellationToken;
        if (!Guid.TryParse(request.AttemptId, out var attemptId) || !ImageStore.IsSha256(request.Sha256))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Name an attempt and an image's lowercase hex SHA-256."));
        TemplateImage image;
        try
        {
            image = await engine.AuthorizeImageDownloadAsync(agent.Id, attemptId, request.Sha256, cancellationToken);
        }
        catch (ImageAccessException e)
        {
            logger.LogWarning("Agent {Agent} was refused the image {Sha256} for attempt {Attempt}: {Reason}", agent.Id, request.Sha256, attemptId, e.Message);
            throw new RpcException(new Status(StatusCode.PermissionDenied, e.Message));
        }

        if (!images.Has(image.Sha256))
            throw new RpcException(new Status(StatusCode.NotFound, "The image is no longer on the server. Plan again once it is built again."));
        await using var file = new FileStream(images.PathOf(image.Sha256), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var length = file.Length;
        var buffer = new byte[ImageStore.ChunkBytes];
        long sent = 0;
        var firstChunk = true;
        do
        {
            var read = 0;
            while (read < buffer.Length && await file.ReadAsync(buffer.AsMemory(read), cancellationToken) is var n && n > 0)
                read += n;
            sent += read;
            var chunk = new ImageChunk { AttemptId = request.AttemptId, Data = ByteString.CopyFrom(buffer, 0, read) };
            if (firstChunk)
            {
                chunk.FileName = image.FileName;
                chunk.Size = (ulong)length;
                firstChunk = false;
            }

            if (sent >= length)
                chunk.Sha256 = image.Sha256;
            await responseStream.WriteAsync(chunk, cancellationToken);
        }
        while (sent < length);
    }

    public override async Task<RenewCertificateResponse> RenewCertificate(RenewCertificateRequest request, ServerCallContext context)
    {
        var (agent, serial) = await AuthenticateAsync(context);
        (byte[] Der, string Serial, DateTimeOffset NotAfter) certificate;
        try
        {
            certificate = ca.IssueAgentCertificate(request.CsrDer.ToByteArray(), agent.Id);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }

        await UpdateAgentAsync(agent.Id, a =>
        {
            // The certificate in use stays valid until the agent connects with the new one.
            a.PreviousCertificateSerial = serial;
            a.CertificateSerial = certificate.Serial;
            a.CertificateExpiresAt = certificate.NotAfter;
        }, context.CancellationToken);
        logger.LogInformation("Renewed the certificate of agent {Agent}.", agent.Id);
        return new RenewCertificateResponse { CertificateDer = ByteString.CopyFrom(certificate.Der) };
    }

    private async Task<(AgentRecord Agent, string Serial)> AuthenticateAsync(ServerCallContext context)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null || !ca.ValidateClientCertificate(certificate))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "A client certificate issued at enrollment is required."));
        }

        var agentId = AgentCertificateAuthority.AgentId(certificate)!.Value;
        var serial = AgentCertificateAuthority.Serial(certificate);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, context.CancellationToken);
        if (agent is null || agent.Disabled)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "This agent is not enrolled or is disabled."));
        }

        if (serial != agent.CertificateSerial && serial != agent.PreviousCertificateSerial)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "This certificate has been replaced."));
        }

        return (agent, serial);
    }

    private async Task<bool> IsAssignedAsync(Guid agentId, Guid attemptId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OctopushyDb>().Attempts
            .AnyAsync(a => a.Id == attemptId && a.AgentId == agentId, cancellationToken);
    }

    private async Task UpdateAgentAsync(Guid agentId, Action<AgentRecord> update, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is not null)
        {
            update(agent);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static string StreamName(LogStream stream) => stream switch
    {
        LogStream.Stdout => "stdout",
        LogStream.Stderr => "stderr",
        _ => "agent",
    };
}
