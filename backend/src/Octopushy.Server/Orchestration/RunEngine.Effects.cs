using System.Text.Json.Nodes;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What a finished step of a guest's run establishes, one effect per step type that has one, chosen by the step's name and, for a
/// name whose effect depends on it, the run's operation. A step type with no entry in <see cref="StepEffects"/> has no effect, and
/// neither has a name no step type has. A new step type with an effect adds its entry in <see cref="StepTypes"/>, its effect, and
/// its line here.
/// </summary>
public sealed partial class RunEngine
{
    /// <summary>
    /// Records what a succeeded step established, under the engine's lock and in the unit of work that took its result.
    /// Called once the checks every plan goes through have passed and the run's resource is loaded.
    /// </summary>
    internal interface IStepEffect
    {
        /// <summary>
        /// Whether this is the step's effect in a run of <paramref name="operation"/>, a constant of <see cref="Operations"/> or a
        /// value the code does not know. When it is not, the step has no effect in that run, or another effect of its name has.
        /// </summary>
        bool AppliesTo(string operation) => true;

        Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken);
    }

    /// <summary>The effect of each step name that has one. Every key is a name in <see cref="StepTypes"/>.</summary>
    internal static readonly IReadOnlyDictionary<string, IStepEffect> StepEffects = new Dictionary<string, IStepEffect>(StringComparer.Ordinal)
    {
        [StepNames.Playbook] = new PlaybookEffect(),
        [StepNames.PlaybookApproved] = new PlaybookEffect(),
        [StepNames.VerifyCurrent] = new VerifyLoginEffect(),
        [StepNames.AuthorizeKey] = new AuthorizeKeyEffect(),
        [StepNames.VerifyKey] = new VerifyLoginEffect(),
        [StepNames.ConfirmRefused] = new ConfirmRefusedEffect(),
        [StepNames.Plan] = new PlanEffect(),
        [StepNames.Apply] = new ByOperation(new ProvisionApplyEffect(), new ForgetOutputsEffect()),
        [StepNames.Verify] = new ByOperation(new AttachVerifyEffect(), new ProvisionVerifyEffect()),
        [StepNames.Protect] = new AttachProtectEffect(),
        [StepNames.VerifyAbsent] = new VerifyAbsentEffect(),
    };

    /// <summary>
    /// The effect registered for <paramref name="name"/>, matched exactly, whatever the run's operation; null for a step type
    /// without one, or a name no step has. A finished step's effect is <see cref="StepEffectFor(string, string)"/>.
    /// </summary>
    internal static IStepEffect? StepEffectFor(string name) => StepEffects.GetValueOrDefault(name);

    /// <summary>
    /// The effect of <paramref name="name"/> in a run of <paramref name="operation"/>: the one of its effects that applies to that
    /// operation, or null when the step type has none, or none of its effects applies to that operation.
    /// </summary>
    internal static IStepEffect? StepEffectFor(string name, string operation) => StepEffectFor(name) switch
    {
        ByOperation byOperation => byOperation.For(operation),
        { } effect when effect.AppliesTo(operation) => effect,
        _ => null,
    };

    /// <summary>A configure run's playbook, and its approved rerun, finished: <see cref="ApplyConfigureEffectsAsync"/>.</summary>
    private sealed class PlaybookEffect : IStepEffect
    {
        public async Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            await engine.ApplyConfigureEffectsAsync(db, run, step, cancellationToken);
        }
    }

    /// <summary>
    /// <see cref="StepNames.Apply"/> of any run but a provision's, a retire's among them: what the machine's outputs were is gone.
    /// A provision's apply is <see cref="ProvisionApplyEffect"/>.
    /// </summary>
    private sealed class ForgetOutputsEffect : IStepEffect
    {
        public bool AppliesTo(string operation) => operation != Operations.Provision;

        public Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            resource.Outputs = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// <see cref="StepNames.Verify"/> of an attach run: the machine is at its VMID, on its host, of its kind. Nothing is bound here;
    /// that waits for protect, <see cref="AttachProtectEffect"/>. Any other run's verify, a provision's, is <see cref="ProvisionVerifyEffect"/>.
    /// </summary>
    private sealed class AttachVerifyEffect : IStepEffect
    {
        public bool AppliesTo(string operation) => operation == Operations.Attach;

        public Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            resource.VerifiedAt = now;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// <see cref="StepNames.Plan"/> of a guest's run, of any operation, once the plan has passed the checks every plan goes through:
    /// the refusal of a plan that acts on a machine that is not the guest's, and a provision's host-key replan, both in
    /// <c>ApplyEffectsAsync</c> ahead of this. It records the check and skips what a plan without changes leaves nothing to do for.
    /// A provision's or a retire's unprotect stays to run only when the plan removes the guest's machine, or changes its container
    /// in place, and the guest owns that machine. See docs/safety-boundary.md.
    /// </summary>
    private sealed class PlanEffect : IStepEffect
    {
        public async Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            resource.CheckedInputsHash = run.InputsHash;
            resource.CheckedRevisionId = run.RevisionId;
            resource.CheckedAt = now;
            if (outputs?["hasChanges"]?.GetValue<bool>() == false)
            {
                if (run.Operation == Operations.Provision)
                {
                    resource.SyncedInputsHash = run.InputsHash;
                }

                foreach (var later in run.Steps.Where(s => s.Name is StepNames.Unprotect or StepNames.Apply or StepNames.Approval))
                {
                    later.Status = StepStatuses.Skipped;
                    later.Detail = "The plan has no changes.";
                }
            }

            if (run.Operation is Operations.Provision or Operations.Retire
                && run.Steps.SingleOrDefault(s => s.Name == StepNames.Unprotect) is { Status: StepStatuses.Pending } unprotect)
            {
                var planned = RunContext.Parse(run.Context);
                if (!GuestVmPlan.LiftsProtection(outputs, planned))
                {
                    unprotect.Status = StepStatuses.Skipped;
                    unprotect.Detail = $"The plan does not replace or delete this guest's {planned.Proxmox.Machine}"
                        + (planned.Proxmox.MachineKind == "lxc" ? ", or change it in place" : "") + ", so its protection stays as it is.";
                }
                else if (resource.ParentAddress is not { } installation || planned.Proxmox.Vmid is not { } vmid
                         || !await SafetyBoundary.OwnsAsync(db, resource, installation, planned.Proxmox.Node, vmid, cancellationToken))
                {
                    // Only a VM the guest owns loses its protection: one it is bound to, or its own create that did not finish.
                    unprotect.Status = StepStatuses.Skipped;
                    unprotect.Detail = $"This guest does not own {planned.Proxmox.Machine} {planned.Proxmox.Vmid} on {planned.Proxmox.Node}, so its protection stays as it is.";
                }
            }
        }
    }

    /// <summary>
    /// <see cref="StepNames.VerifyAbsent"/>, a retire's last step: the guest's machine is gone. The guest is retired, its binding and
    /// reservation are cleared, the credentials Octopushy held for it are removed, and its services are retired with their runs.
    /// </summary>
    private sealed class VerifyAbsentEffect : IStepEffect
    {
        public async Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            resource.RetiredAt = now;
            resource.VerifiedAt = now;
            resource.BoundVmid = null;
            resource.BoundInstallation = null;
            resource.ReservedVmid = null;
            resource.ReservedNode = null;
            resource.ReservedInstallation = null;
            resource.ReservedAt = null;
            resource.ReservationAppliedAt = null;
            await GuestAccessCredentials.RemoveAsync(db, run.ResourceId, cancellationToken);
            await GuestHostCredentials.RemoveAsync(db, run.ResourceId, cancellationToken);
            await ContainerHostKeys.RemoveAsync(db, run.ResourceId, cancellationToken);
            await engine.RetireServicesAsync(db, run, now, cancellationToken);
        }
    }

    /// <summary>
    /// <see cref="StepNames.Apply"/> of a provision run: what was applied, and the outputs the module reported. The guest is not
    /// bound here: its reservation is marked applied only when it is for this run's machine and the approved plan made that
    /// machine, and the verify step binds it once the machine carries Octopushy's marks. A plan that creates or replaces something
    /// proposes the guest's setup and its services' again. Octopushy's login key is recorded as provisioned, and the guest's own
    /// host key as delivered by this run, in the same save. See docs/safety-boundary.md.
    /// </summary>
    private sealed class ProvisionApplyEffect : IStepEffect
    {
        public bool AppliesTo(string operation) => operation == Operations.Provision;

        public async Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            resource.AppliedInputsHash = run.InputsHash;
            resource.AppliedRevisionId = run.RevisionId;
            resource.AppliedAt = now;
            resource.SyncedInputsHash = run.InputsHash;
            resource.Outputs = outputs?["outputs"]?.ToJsonString();
            // Not bound here: a guest owns a VM once verify-guest confirms the create; see the verify step.
            if (resource.BoundVmid is null && SafetyBoundary.Reserves(resource, RunContext.Parse(run.Context))
                && run.Steps.Single(s => s.Name == StepNames.Plan).Outputs is { } planText
                && GuestVmPlan.MakesGuestMachine(JsonNode.Parse(planText), RunContext.Parse(run.Context)))
                resource.ReservationAppliedAt = now;
            await ProposeConfigurationAgainAsync(db, run, cancellationToken);
            await ProposeServicesAgainAsync(db, run, cancellationToken);
            await RecordProvisionedKeyAsync(db, run, now, cancellationToken);
            await RecordDeliveredHostKeyAsync(db, run, now, cancellationToken);
        }
    }

    /// <summary>
    /// <see cref="StepNames.Verify"/> of any run but an attach's, a provision's: ownership comes only from a confirmed creation,
    /// so a guest not bound yet is bound only by its own reservation that an apply marked applied, and only when the machine
    /// carries Octopushy's marks; without them the step fails. A container's host keys are pinned only from the answer of the
    /// attempt that was asked, about the machine the guest is bound to. See docs/safety-boundary.md.
    /// </summary>
    private sealed class ProvisionVerifyEffect : IStepEffect
    {
        public bool AppliesTo(string operation) => operation != Operations.Attach;

        public async Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            // Ownership comes only from a confirmed creation: an apply of an approved plan of this guest created
            // its VMID on its node, and the VM verified now carries Octopushy's marks. Nothing else binds.
            if (resource.BoundVmid is null)
            {
                var verifying = RunContext.Parse(run.Context);
                if (!SafetyBoundary.Reserves(resource, verifying) || resource.ReservationAppliedAt is null)
                {
                    step.Detail = $"{verifying.Proxmox.Machine} {verifying.Proxmox.Vmid} on {verifying.Proxmox.Node} is not recorded as this guest's: "
                        + "no apply of an approved plan of this guest has created it.";
                }
                else if (SafetyBoundary.MarksMissing(outputs, verifying) is { } missing)
                {
                    step.Status = StepStatuses.Failed;
                    step.Detail = missing;
                    return;
                }
                else
                {
                    resource.BoundInstallation = resource.ParentAddress;
                    resource.BoundVmid = verifying.Proxmox.Vmid;
                    resource.BoundAt = now;
                }
            }

            // A container's host keys, as the create task of the apply that made it logged them, are the pin of its
            // Ansible work. Only the answer of the attempt that was asked, about a container this guest owns: keys an
            // attempt reports without being asked, as after the token that applied was revoked, pin nothing.
            if (RunContext.Parse(run.Context) is { ContainerHostKey: true, HostKeysRequest: { } asked } pinning
                && step.Attempts.OrderByDescending(a => a.Number).FirstOrDefault()?.Id == asked.AttemptId
                && resource.BoundVmid == pinning.Proxmox.Vmid && resource.BoundInstallation == resource.ParentAddress)
            {
                if (ContainerHostKeys.Read(outputs) is { } fingerprints)
                    await ContainerHostKeys.RecordAsync(store, resource.Id, fingerprints, cancellationToken);
                else if (outputs?["hostKeysError"]?.ToString() is { Length: > 0 } unread)
                    step.Detail = $"The container's host keys could not be read again from the run that created it: {unread}";
            }

            resource.VerifiedAt = now;
        }
    }

    /// <summary>
    /// <see cref="StepNames.Protect"/> of an attach run: the machine was verified at its VMID, on its host, of its kind, and is now
    /// protected. Only then is the guest bound, on a person's word, recorded as the run's approval, once the attach is checked
    /// again. Octopushy did not create it, so nothing else could bind it. A protect of any other run has no effect.
    /// </summary>
    private sealed class AttachProtectEffect : IStepEffect
    {
        public bool AppliesTo(string operation) => operation == Operations.Attach;

        public async Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            var attaching = RunContext.Parse(run.Context);
            if (await SafetyBoundary.AttachProblemAsync(db, resource, resource.ParentAddress!, attaching.Proxmox.Vmid!.Value, cancellationToken) is { } problem)
            {
                step.Status = StepStatuses.Failed;
                step.Detail = $"Protection is on, and the guest is not attached: {problem}.";
                return;
            }
            resource.BoundInstallation = resource.ParentAddress;
            resource.BoundVmid = attaching.Proxmox.Vmid;
            resource.BoundAt = now;
            resource.AttachedAt = now;
            resource.AttachedBy = run.Steps.Single(s => s.Name == StepNames.Approval).DecidedBy;
            resource.ProvisionWaiting = null;
        }
    }

    /// <summary>
    /// The effects of one step name whose effect depends on the run's operation, each for the operations it applies to. At most
    /// one applies to any operation; a run of an operation none applies to has no effect for the name.
    /// </summary>
    private sealed class ByOperation(params IStepEffect[] effects) : IStepEffect
    {
        /// <summary>The effect for <paramref name="operation"/>, or null.</summary>
        public IStepEffect? For(string operation) => effects.SingleOrDefault(e => e.AppliesTo(operation));

        public bool AppliesTo(string operation) => For(operation) is not null;

        public Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken) =>
            For(run.Operation)!.ApplyAsync(engine, db, store, run, step, resource, outputs, now, cancellationToken);
    }
}
