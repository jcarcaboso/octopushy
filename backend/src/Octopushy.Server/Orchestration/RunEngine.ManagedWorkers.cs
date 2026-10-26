using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Orchestration;

public sealed partial class RunEngine
{
    private async Task BindManagedWorkerAsync(OctopushyDb db, Run run, CancellationToken ct)
    {
        var context = RunContext.Parse(run.Context);
        var binding = await db.ManagedWorkers.SingleOrDefaultAsync(w => w.ResourceId == run.ResourceId, ct);
        if (binding is not null)
        {
            if (!ManagedWorkerSafety.Matches(binding, run, context))
                throw new RunException("Managed workers cannot be retired, replaced, moved or lose their worker role.");
            if (binding.ServerUrl != context.ManagedWorker!.ServerUrl)
                throw new RunException("Changing a bound managed worker's server endpoint is not supported.");
            return;
        }
        if (context.ManagedWorker is not { } worker) return;
        if (run.Operation != Operations.Provision || run.RevisionId is null || context.Proxmox.Vmid is null || context.Proxmox.Node is null)
            throw new RunException("Managed worker requires a revision-bound guest provision run.");
        if (await db.ManagedWorkers.AnyAsync(w => w.Generation == worker.Generation, ct)
            || db.ManagedWorkers.Local.Any(w => w.Generation == worker.Generation))
            throw new RunException("This worker generation is already bound to another resource; replacement is not supported.");
        var resource = await db.Resources.SingleAsync(r => r.Id == run.ResourceId, ct);
        resource.ManagedWorker = new ManagedWorkerBinding
        {
            ResourceId = resource.Id,
            Generation = worker.Generation,
            Installation = resource.ParentAddress!,
            Host = context.Proxmox.Node,
            Vmid = context.Proxmox.Vmid.Value,
            RevisionId = run.RevisionId.Value,
            RunId = run.Id,
            ServerUrl = ManagedWorkerSafety.PublicUrl(worker.ServerUrl),
            CreatedAt = clock.GetUtcNow(),
        };
    }

    private async Task ApplyWorkerEffectsAsync(OctopushyDb db, Run run, Step step, Attempt attempt, JsonNode? outputs, CancellationToken ct)
    {
        if (step.Name is not (StepNames.PrepareWorker or StepNames.InstallWorker)) return;
        var context = RunContext.Parse(run.Context);
        var binding = await db.ManagedWorkers.SingleAsync(w => w.ResourceId == run.ResourceId, ct);
        var resource = await db.Resources.SingleAsync(r => r.Id == run.ResourceId, ct);
        if (!ManagedWorkerSafety.Matches(binding, run, context) || run.RevisionId is null
            || resource.DesiredRevisionId != run.RevisionId || resource.DesiredInputsHash != run.InputsHash
            || attempt.RunId != run.Id || attempt.StepId != step.Id || attempt.AgentId is null
            || attempt.Executor != "managed-worker" || attempt.Action != (step.Name == StepNames.PrepareWorker ? "prepare" : "install") || !attempt.Mutating)
            throw new RunException("The result does not belong to the current bound resource, generation and revision.");
        if (outputs is not JsonObject report || report.Count != 4 || report["protocolVersion"]?.GetValue<int>() != 1
            || report["generation"]?.GetValue<string>() != binding.Generation.ToString())
            throw new RunException("Invalid public bootstrap report.");
        if (step.Name == StepNames.InstallWorker)
        {
            if (report["installed"]?.GetValue<bool>() != true || report["agentId"]?.GetValue<string>() != binding.AgentId?.ToString())
                throw new RunException("Installed identity does not match the issued agent.");
            binding.InstalledAt ??= clock.GetUtcNow();
            return;
        }
        var encoded = report["csrDer"]?.GetValue<string>();
        var version = report["agentVersion"]?.GetValue<string>();
        if (encoded is null || encoded.Length > 16384 || string.IsNullOrWhiteSpace(version) || version.Length > 128)
            throw new RunException("Missing or oversized public CSR/version.");
        var csr = Convert.FromBase64String(encoded);
        var request = CertificateRequest.LoadSigningRequest(csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
        if (binding.CsrDer is { } previous)
        {
            var previousRequest = CertificateRequest.LoadSigningRequest(Convert.FromBase64String(previous), HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
            if (!request.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(previousRequest.PublicKey.ExportSubjectPublicKeyInfo()))
                throw new RunException("This generation already has a different public key; identity replacement is forbidden.");
            return; // Same key: reuse the durable certificate, never issue again.
        }
        // Issuing authentication authority is new controller work, not reconciliation of the guest command.
        if (await RepositoryTrust.BlockedReasonAsync(db, run, ct) is { } trustReason)
            throw new RunException(trustReason);
        using var scope = scopes.CreateScope();
        var ca = scope.ServiceProvider.GetRequiredService<AgentCertificateAuthority>();
        var id = Guid.CreateVersion7();
        var certificate = ca.IssueAgentCertificate(csr, id);
        db.Agents.Add(new AgentRecord
        {
            Id = id,
            Name = context.Proxmox.Name!,
            Labels = context.ManagedWorker!.Labels.ToArray(),
            MaxConcurrency = context.ManagedWorker.MaxConcurrency,
            EnrolledAt = clock.GetUtcNow(),
            CertificateSerial = certificate.Serial,
            CertificateExpiresAt = certificate.NotAfter,
            Version = version,
        });
        binding.AgentId = id;
        binding.CsrDer = encoded;
        binding.CertificatePem = PemEncoding.WriteString("CERTIFICATE", certificate.Der);
        binding.CaCertificatePem = ca.Certificate.ExportCertificatePem();
        binding.PreparedAt = clock.GetUtcNow();
        binding.PrepareAttemptId = attempt.Id;
        binding.RunId = run.Id;
        binding.RevisionId = run.RevisionId.Value;
    }

    private async Task<bool> WorkerReadyAsync(OctopushyDb db, Run run, Step step, CancellationToken ct)
    {
        var binding = await db.ManagedWorkers.SingleAsync(w => w.ResourceId == run.ResourceId, ct);
        step.StartedAt ??= clock.GetUtcNow();
        var agent = binding.AgentId is { } id ? await db.Agents.SingleOrDefaultAsync(a => a.Id == id, ct) : null;
        var session = agent is null ? null : connections.Get(agent.Id);
        if (binding.InstalledAt is not null && binding.AuthenticatedAt is not null && binding.AuthenticatedAt >= binding.PreparedAt
            && agent is { Disabled: false } && session is not null && session.Executors.Contains("opentofu") && session.Executors.Contains("proxmox"))
        {
            binding.ReadyAt ??= clock.GetUtcNow();
            step.Status = StepStatuses.Succeeded;
            step.FinishedAt = clock.GetUtcNow();
            step.Detail = "The correlated worker authenticated with mTLS and advertised workload capabilities.";
            var resource = await db.Resources.SingleAsync(r => r.Id == run.ResourceId, ct);
            resource.SyncedInputsHash = run.InputsHash;
            resource.VerifiedAt = clock.GetUtcNow();
            return true;
        }
        step.Status = StepStatuses.Running;
        step.Detail = "Waiting for the correlated worker's authenticated mTLS Hello and workload capabilities; VM existence is not readiness.";
        run.Status = RunStatuses.Running;
        run.Detail = step.Detail;
        return false;
    }
}
