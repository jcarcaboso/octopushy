using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Api;
using Octopushy.Server.Changes;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Agents;

/// <summary>
/// Updates an agent to the server's own version through the guest it runs on: a change to the guest's definition that sets
/// <c>inputs.worker_agent_version</c> and drops <c>inputs.worker_agent_digest</c>, approved as the operator who asked, rolled
/// out by the guest's setup run, which another agent runs. The agent is paused first and the pause lifted once it is back on
/// the target version, or the update failed. A step that may restart the agent is held by dispatch until the agent has no
/// other work (<see cref="RunEngine"/>). The version the agent ran before is recorded, and Revert is an update back to it.
/// One update at a time; the rest queue, and a failure empties the queue of updates. See docs/worker-routing.md.
/// </summary>
public sealed class AgentUpdates(
    RunEngine engine,
    ChangeService changes,
    RevisionCache revisions,
    AgentConnections connections,
    IServiceScopeFactory scopes,
    IOptions<OctopushyOptions> options,
    TimeProvider clock,
    ILogger<AgentUpdates> logger)
{
    public const string VersionInput = "worker_agent_version";
    public const string DigestInput = "worker_agent_digest";
    public const string NameInput = "worker_agent_name";

    /// <summary>The server's own version: the one version of the backend, the agent and the frontend.</summary>
    public static string TargetVersion { get; } = typeof(AgentUpdates).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "0.0.0";

    /// <summary>The agents whose update's change this process is committing now: claimed, with no change recorded yet.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> committing = new();

    /// <summary>The head revision as read for updates, or why it cannot be used.</summary>
    public sealed record Head(DefinitionRevision? Revision, LabRepository? Repository, string? Unusable);

    /// <summary>
    /// What the API shows of an agent's guest and update. <c>Plain</c> is the state with no update asked for: <c>current</c>,
    /// <c>available</c> or <c>unavailable</c>. <c>RevertRefused</c> says why Revert is not offered, null when it is.
    /// </summary>
    public sealed record Assessment(AgentGuest? Guest, AgentUpdateState Update, AgentUpdateState Plain, string? RevertRefused);

    /// <summary>Whether <paramref name="version"/> is <paramref name="target"/> or newer. A version that does not read as one is older.</summary>
    public static bool AtLeast(string? version, string target) =>
        Parse(version) is { } have && Parse(target) is { } want && have >= want;

    private static Version? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var core = version.Trim().TrimStart('v').Split('-', '+')[0];
        return Version.TryParse(core, out var parsed) ? new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0)) : null;
    }

    /// <summary>
    /// The guest an agent runs on: one whose inputs set <c>worker_agent_version</c> and whose <c>worker_agent_name</c>, or else
    /// its own name, is the agent's. Of several, the one at the address the agent connects from; when that does not settle
    /// it, none. <paramref name="ambiguous"/> says several matched.
    /// </summary>
    public static GuestDefinition? GuestOf(LabRepository repository, string agentName, IPAddress? remote, out bool ambiguous)
    {
        var matches = repository.Guests
            .Where(g => !g.Retired && g.Json["inputs"] is JsonObject inputs && inputs.ContainsKey(VersionInput)
                        && (Text(inputs[NameInput]) ?? g.Id) == agentName)
            .ToList();
        ambiguous = matches.Count > 1;
        if (matches.Count <= 1)
            return matches.FirstOrDefault();
        var at = remote is null ? [] : matches.Where(g => AddressesOf(g).Any(a => Same(a, remote))).ToList();
        return at.Count == 1 ? at[0] : null;
    }

    /// <summary>A guest's declared addresses: <c>network.address</c> without its prefix, and <c>network.reservedAddress</c>.</summary>
    private static IEnumerable<string> AddressesOf(GuestDefinition guest)
    {
        if (Text(guest.Json["network"]?["address"]) is { } address)
            yield return address.Split('/')[0];
        if (Text(guest.Json["network"]?["reservedAddress"]) is { } reserved)
            yield return reserved;
    }

    private static bool Same(string address, IPAddress remote) =>
        IPAddress.TryParse(address, out var parsed) && Unmapped(parsed).Equals(Unmapped(remote));

    private static IPAddress Unmapped(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.ToString() is { Length: > 0 } text ? text : null;

    /// <summary>The head revision, read.</summary>
    public async Task<Head> HeadAsync(OctopushyDb db, CancellationToken ct)
    {
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
        if (head is null)
            return new(null, null, "The lab repository has not been synced, so no guest can be found for this agent.");
        try
        {
            return new(head, await revisions.GetAsync(head, ct), null);
        }
        catch (GitException e)
        {
            return new(head, null, $"The lab repository's head revision cannot be read: {e.Message}");
        }
    }

    /// <summary>Each agent's guest and update state.</summary>
    public async Task<IReadOnlyDictionary<Guid, Assessment>> AssessAsync(OctopushyDb db, IReadOnlyList<AgentRecord> agents, Head head,
        CancellationToken ct)
    {
        var addresses = head.Repository?.Guests.Select(g => g.Address).ToList() ?? [];
        var resources = await db.Resources.AsNoTracking()
            .Where(r => (r.Kind == ResourceKinds.Guest && addresses.Contains(r.Address) || r.Kind == ResourceKinds.Installation) && r.RetiredAt == null)
            .ToListAsync(ct);
        var changeIds = agents.Where(a => a.UpdateChangeId is not null).Select(a => a.UpdateChangeId!.Value).ToList();
        var runs = await db.Changes.AsNoTracking().Where(c => changeIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.RunIds, ct);
        var runIds = runs.Values.SelectMany(ids => ids).ToList();
        var statuses = runIds.Count == 0
            ? []
            : await db.Runs.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Status, ct);
        var holdIds = agents.Where(a => a.UpdateHoldAttemptId is not null).Select(a => a.UpdateHoldAttemptId!.Value).ToList();
        var holdRuns = holdIds.Count == 0
            ? []
            : await db.Attempts.AsNoTracking().Where(a => holdIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.RunId, ct);
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        var credential = settings is null || settings.CredentialKind == "none"
                         || await db.StoreEntries.AsNoTracking().AnyAsync(e => e.Key == RepositorySync.CredentialKey, ct);
        var result = new Dictionary<Guid, Assessment>();
        foreach (var agent in agents)
        {
            GuestDefinition? guest = null;
            var ambiguous = false;
            if (head.Repository is { } repository)
                guest = GuestOf(repository, agent.Name, connections.Get(agent.Id)?.RemoteAddress, out ambiguous);
            var resource = guest is null ? null : resources.FirstOrDefault(r => r.Kind == ResourceKinds.Guest && r.Address == guest.Address);
            if (resource is null)
                guest = null;
            var declared = guest is null ? null : Text(guest.Json["inputs"]![VersionInput]);
            var dto = guest is null ? null : new AgentGuest(resource!.Id, guest.Address, declared);
            var changeRuns = agent.UpdateChangeId is { } changeId && runs.TryGetValue(changeId, out var ids) ? ids : [];
            var runId = changeRuns.Count > 0 ? changeRuns[^1] : (Guid?)null;
            string? Why(string target) => Unavailable(agent, guest, ambiguous, head, resources, settings, credential, agents, target);
            var revertRefused = RevertRefusal(agent, declared, Why);
            var inFlight = agent.UpdateState is AgentUpdateStates.Queued or AgentUpdateStates.Updating;
            var phase = agent.UpdateState == AgentUpdateStates.Updating
                ? PhaseOf(agent, changeRuns.Where(statuses.ContainsKey).Select(id => statuses[id]))
                : null;
            var waiting = phase is AgentUpdatePhases.WaitingForIdle or AgentUpdatePhases.WaitingForUnconfirmedChange;
            AgentUpdateState State(string state, string? reason = null) =>
                new(state,
                    state is "queued" or "updating" or "failed" && agent.UpdateRevert ? agent.UpdatePreviousVersion : agent.UpdateTargetVersion ?? TargetVersion,
                    reason,
                    state is "updating" or "failed" ? agent.UpdateChangeId : null,
                    state is "updating" or "failed" ? runId : null,
                    state == "updating" ? phase : null,
                    state == "failed" ? agent.UpdateFailure : null,
                    agent.UpdatePreviousVersion,
                    !inFlight && revertRefused is null,
                    state is "queued" or "updating" or "failed" && agent.UpdateRevert,
                    phase == AgentUpdatePhases.Restarting ? agent.UpdateReleasedAt + ComeBackWithin : null,
                    waiting ? agent.UpdateHoldAttemptId : null,
                    waiting && agent.UpdateHoldAttemptId is { } held ? holdRuns.GetValueOrDefault(held) : null);

            var plain = AtLeast(agent.Version, TargetVersion) ? State("current")
                : Why(TargetVersion) is { } why ? State("unavailable", why)
                : State("available");
            plain = plain with { TargetVersion = TargetVersion, Revert = false };
            var update = agent.UpdateState switch
            {
                AgentUpdateStates.Updating => State("updating"),
                AgentUpdateStates.Queued => State("queued"),
                // An agent current again hides a failed update, unless the failure is about the version it runs: a revert,
                // or an update that lost executors.
                AgentUpdateStates.Failed when plain.State != "current" || agent.UpdateRevert
                                              || agent.UpdateFailure == AgentUpdateFailures.LostExecutors => State("failed", agent.UpdateDetail),
                _ => plain,
            };
            result[agent.Id] = new Assessment(dto, update, plain, revertRefused);
        }

        return result;
    }

    /// <summary>
    /// Why a Revert is not offered to <paramref name="agent"/>, or null when it is: a version from before its last update is
    /// recorded, is older than the server's (otherwise it is an update), and the agent or its guest is not on it already.
    /// </summary>
    private static string? RevertRefusal(AgentRecord agent, string? declared, Func<string, string?> unavailable)
    {
        if (agent.UpdatePreviousVersion is not { } previous)
            return $"No version from before an update is recorded for {agent.Name}.";
        if (AtLeast(previous, TargetVersion))
            return $"{previous} is not older than the server's version; update {agent.Name} instead.";
        if (SameVersion(agent.Version, previous) && SameVersion(declared, previous))
            return $"{agent.Name} runs {previous} already, and its guest declares it.";
        return unavailable(previous);
    }

    /// <summary>Where an update in flight stands, from its agent and the statuses of its change's runs.</summary>
    private static string PhaseOf(AgentRecord agent, IEnumerable<string> runStatuses)
    {
        var statuses = runStatuses.ToList();
        if (statuses.Contains(RunStatuses.AwaitingApproval))
            return AgentUpdatePhases.AwaitingApproval;
        if (statuses.Contains(RunStatuses.Blocked))
            return AgentUpdatePhases.Blocked;
        if (agent.UpdateHold == AgentUpdateHolds.Busy)
            return AgentUpdatePhases.WaitingForIdle;
        if (agent.UpdateHold == AgentUpdateHolds.UnconfirmedChange)
            return AgentUpdatePhases.WaitingForUnconfirmedChange;
        return agent.UpdateReleasedAt is null ? AgentUpdatePhases.RollingOut : AgentUpdatePhases.Restarting;
    }

    /// <summary>Whether two versions are the same: by number when both read as one, otherwise as written.</summary>
    public static bool SameVersion(string? a, string? b) =>
        Parse(a) is { } x && Parse(b) is { } y ? x == y : a is not null && a == b;

    /// <summary>How long an agent has to come back on its update's version once the step that restarts it is handed out.</summary>
    private TimeSpan ComeBackWithin => TimeSpan.FromMinutes(Math.Max(1, options.Value.AgentUpdates.ComeBackMinutes));

    /// <summary>Why an agent on an older version cannot be updated to <paramref name="target"/> from here, or null when it can.</summary>
    private string? Unavailable(AgentRecord agent, GuestDefinition? guest, bool ambiguous, Head head, IReadOnlyList<Resource> resources,
        RepositorySettings? settings, bool credential, IReadOnlyList<AgentRecord> agents, string target)
    {
        if (head.Unusable is { } unusable)
            return unusable;
        if (guest is null)
            return ambiguous
                ? $"Several guests in the lab repository name {agent.Name} in {NameInput}, and none is at the address it connects from."
                : $"No guest in the lab repository runs {agent.Name}: none sets inputs.{VersionInput} with its name. "
                  + "An agent that ships with the server, such as the internal one, is updated with the server.";
        if (!head.Revision!.RepositoryValid)
            return "The lab repository's head revision has errors, and setup runs wait until they are fixed.";
        if (!options.Value.RepositoryEditor.PublishEnabled)
            return "An update is a commit to the lab repository, and committing from the UI is off. Set "
                   + "Octopushy__RepositoryEditor__PublishEnabled=true, with a repository credential that can push.";
        if (settings?.Url is null || !credential)
            return "The lab repository has no credential to push with. Add one that can push, such as a deploy key with write access.";
        if (resources.Any(r => r.Kind == ResourceKinds.Installation && r.Address == guest.InstallationAddress && r.ObservedOnly)
            || head.Repository!.Installation(guest.InstallationAddress) is { Managed: false })
            return $"{guest.InstallationAddress} is observed only (manage: false): Octopushy changes nothing there, so {guest.Address} cannot be updated from here.";
        if (!head.Repository!.IsValid(guest.Address))
            return $"{guest.Address} has errors in the lab repository; fix them first.";
        if (guest.Setup is null)
            return $"{guest.Address}'s workflow has no setup stage to roll the new version out.";
        var declared = Text(guest.Json["inputs"]![VersionInput]);
        if (guest.Json["inputs"]?[DigestInput] is null && (target == TargetVersion ? AtLeast(declared, target) : SameVersion(declared, target)))
            return $"{guest.Address} already declares {target}, and its setup has not rolled it out yet.";
        var labels = PolicyResolver.AgentLabels(head.Repository, guest).Value;
        var guestAddress = ConfigureInputs.AddressOf(guest);
        var others = agents.Where(a => a.Id != agent.Id && !a.Disabled && !a.Draining && labels.All(a.Labels.Contains)
                                       && connections.Get(a.Id) is { } session && session.Executors.Contains(Executors.Ansible)
                                       && !(guestAddress is not null && session.RemoteAddress is { } remote && Same(guestAddress, remote)));
        if (!others.Any())
            return $"No other connected agent that is not paused can set up {guest.Address}: it needs one with the ansible executor"
                   + (labels.Count > 0 ? $" and the labels {string.Join(", ", labels)}." : ".");
        return null;
    }

    /// <summary>What a request for an update came to.</summary>
    public sealed record Outcome(bool Found, string? Refused);

    /// <summary>
    /// Asks for an agent's update as <paramref name="actor"/>: starts it, or queues it behind the one in flight. Refused, with a
    /// sentence, when the agent is current, cannot be updated from here, or is queued or updating already.
    /// </summary>
    public Task<Outcome> RequestAsync(Guid agentId, string actor, CancellationToken ct) => AskAsync(agentId, actor, revert: false, ct);

    /// <summary>
    /// Asks for an agent's Revert as <paramref name="actor"/>: an update back to the version it ran before its last update, through
    /// the same flow. Refused, with a sentence, when no such version is recorded or the agent is on it already.
    /// </summary>
    public Task<Outcome> RevertAsync(Guid agentId, string actor, CancellationToken ct) => AskAsync(agentId, actor, revert: true, ct);

    private async Task<Outcome> AskAsync(Guid agentId, string actor, bool revert, CancellationToken ct)
    {
        var refused = await engine.ExecuteAsync(async (db, _) =>
        {
            var agents = await db.Agents.OrderBy(a => a.Name).ToListAsync(ct);
            var agent = agents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null)
                return (false, (string?)null);
            var assessment = (await AssessAsync(db, agents, await HeadAsync(db, ct), ct))[agent.Id];
            // A failed update is asked again as if none had been asked for.
            var update = assessment.Update.State == "failed" ? assessment.Plain : assessment.Update;
            var why = update.State switch
            {
                "queued" => $"{agent.Name}'s update is queued already.",
                "updating" => $"{agent.Name} is being updated already.",
                _ when revert => assessment.RevertRefused,
                "current" => $"{agent.Name} already runs {agent.Version}.",
                "unavailable" => update.Reason,
                _ => null,
            };
            if (why is null)
            {
                Queue(agent, actor, clock.GetUtcNow(), revert);
                await db.SaveChangesAsync(ct);
            }

            return (true, why);
        }, ct);
        if (!refused.Item1)
            return new(false, null);
        if (refused.Item2 is { } sentence)
            return new(true, sentence);
        var started = await StartNextAsync(ct);
        return new(true, started is { } s && s.AgentId == agentId ? s.Refused : null);
    }

    /// <summary>
    /// Asks for the update of every agent that is available, or whose update failed and that is still not current, in name
    /// order: the first starts, the rest queue. Never a Revert.
    /// </summary>
    public async Task RequestAllAsync(string actor, CancellationToken ct)
    {
        await engine.ExecuteAsync(async (db, _) =>
        {
            var agents = await db.Agents.OrderBy(a => a.Name).ToListAsync(ct);
            var assessed = await AssessAsync(db, agents, await HeadAsync(db, ct), ct);
            var now = clock.GetUtcNow();
            var order = 0;
            foreach (var agent in agents.Where(a => assessed[a.Id].Update.State == "available"
                                                    || assessed[a.Id].Update.State == "failed" && assessed[a.Id].Plain.State == "available"))
                Queue(agent, actor, now.AddTicks(order++), revert: false);
            await db.SaveChangesAsync(ct);
            return order;
        }, ct);
        await StartNextAsync(ct);
    }

    /// <summary>Takes a queued agent out of the queue. Returns false when its update is in flight and cannot be.</summary>
    public Task<bool> CancelAsync(Guid agentId, CancellationToken ct) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
            if (agent?.UpdateState == AgentUpdateStates.Updating)
                return false;
            if (agent?.UpdateState == AgentUpdateStates.Queued)
            {
                Clear(agent);
                await db.SaveChangesAsync(ct);
            }

            return true;
        }, ct);

    private static void Queue(AgentRecord agent, string actor, DateTimeOffset at, bool revert)
    {
        Clear(agent);
        agent.UpdateState = AgentUpdateStates.Queued;
        agent.UpdateRequestedAt = at;
        agent.UpdateRequestedBy = actor;
        agent.UpdateRevert = revert;
    }

    /// <summary>Forgets the update asked for. The version from before the last update is kept: it is what Revert brings back.</summary>
    private static void Clear(AgentRecord agent)
    {
        agent.UpdateState = null;
        agent.UpdateRequestedAt = null;
        agent.UpdateRequestedBy = null;
        agent.UpdateTargetVersion = null;
        agent.UpdateChangeId = null;
        agent.UpdateDetail = null;
        agent.UpdateFailure = null;
        agent.UpdateRevert = false;
        agent.UpdateResourceId = null;
        agent.UpdateHold = null;
        agent.UpdateHoldAttemptId = null;
        agent.UpdateReleasedAt = null;
        agent.UpdateBaselineConnection = null;
        agent.UpdatePreviousExecutors = null;
    }

    /// <summary>Lifts the pause the update set, never one a person set.</summary>
    private static void Unpause(AgentRecord agent)
    {
        if (agent.UpdatePaused)
            agent.Draining = false;
        agent.UpdatePaused = false;
    }

    /// <summary>
    /// Fails the update in flight with <paramref name="failure"/> and <paramref name="why"/>, lifts its pause and empties the
    /// queue of updates; a queued Revert stays, as it is the way back. The caller's save also adds the notification that tells it.
    /// </summary>
    private async Task FailAsync(OctopushyDb db, AgentRecord agent, string failure, string why, CancellationToken ct)
    {
        logger.LogWarning("The update of agent {Name} failed ({Failure}): {Why}", agent.Name, failure, why);
        agent.UpdateState = AgentUpdateStates.Failed;
        agent.UpdateFailure = failure;
        agent.UpdateDetail = why;
        // Nothing is held for it any more: dispatch holds only for an update in flight.
        agent.UpdateResourceId = null;
        agent.UpdateHold = null;
        agent.UpdateHoldAttemptId = null;
        Notifications.NotificationHub.AgentUpdateFailed(db, agent, clock.GetUtcNow());
        Unpause(agent);
        foreach (var queued in await db.Agents.Where(a => a.UpdateState == AgentUpdateStates.Queued && !a.UpdateRevert).ToListAsync(ct))
            Clear(queued);
    }

    /// <summary>The version an update in flight sets: the server's, or for a Revert the one recorded before.</summary>
    private static string TargetOf(AgentRecord agent) => agent.UpdateTargetVersion ?? TargetVersion;

    /// <summary>
    /// The agent's connection when it runs the version its update sets: the server's or newer, or for a Revert that version
    /// exactly. As the connection's own Hello said, so a connection the restart left behind is never taken for the new one.
    /// </summary>
    private AgentSession? Back(AgentRecord agent) =>
        connections.Get(agent.Id) is { Version: { } version } session
        && (agent.UpdateRevert ? SameVersion(version, TargetOf(agent)) : AtLeast(version, TargetOf(agent)))
            ? session
            : null;

    /// <summary>
    /// Moves updates on: settles the one in flight from its agent's connection, its change and its deadline, then starts the next
    /// queued one. Called by the orchestrator's loop and when an agent connects. Writes only when the update moves.
    /// </summary>
    public async Task AdvanceAsync(CancellationToken ct)
    {
        var updating = await engine.ExecuteAsync(async (db, _) =>
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.UpdateState == AgentUpdateStates.Updating, ct);
            if (agent is null)
                return (Guid?)null;
            if (agent.UpdateChangeId is null && !committing.ContainsKey(agent.Id))
            {
                // Claimed by a server that stopped before it recorded the change: nothing else would ever end it.
                await FailAsync(db, agent, AgentUpdateFailures.ChangeNotRecorded,
                    "The server stopped before the update's change was recorded. Look for its commit in the lab repository, then ask again.", ct);
                await db.SaveChangesAsync(ct);
                return null;
            }

            return agent.UpdateChangeId;
        }, ct);
        if (updating is { } changeId)
        {
            // Brought up to date outside the engine's lock, which reading a change takes itself.
            var change = await changes.GetAsync(changeId, ct);
            await engine.ExecuteAsync(async (db, _) =>
            {
                var agent = await db.Agents.FirstOrDefaultAsync(a => a.UpdateState == AgentUpdateStates.Updating && a.UpdateChangeId == changeId, ct);
                if (agent is null)
                    return 0;
                if (await DoneAsync(db, change, agent, ct) is { } session)
                {
                    // A new process must offer everything the old one did; gaining is fine.
                    var newProcess = session.Id != agent.UpdateBaselineConnection;
                    var lost = newProcess ? (agent.UpdatePreviousExecutors ?? []).Except(session.HelloExecutors).Order().ToList() : [];
                    if (lost.Count > 0)
                        await FailAsync(db, agent, AgentUpdateFailures.LostExecutors, LostSentence(agent, session, lost), ct);
                    else
                    {
                        logger.LogInformation("Agent {Name} is back on {Version}; its update is done.", agent.Name, session.Version);
                        Clear(agent);
                        Unpause(agent);
                    }
                }
                // Failing lifts the pause: never while another change to its guest may still restart it, or work handed to the
                // agent then would be interrupted. It fails once that change has settled.
                else if (Failure(change, agent, clock.GetUtcNow()) is { } failure && !await GuestChangingAsync(db, agent, ct))
                    await FailAsync(db, agent, failure.Code, failure.Why, ct);
                if (db.ChangeTracker.HasChanges())
                    await db.SaveChangesAsync(ct);
                return 0;
            }, ct);
        }

        await StartNextAsync(ct);
    }

    /// <summary>
    /// The agent's connection when its update is done: its change applied, every run of it ended, nothing left that may restart
    /// the agent (no change to its guest queued, assigned or running), and its connection reports the target. Null otherwise.
    /// </summary>
    private async Task<AgentSession?> DoneAsync(OctopushyDb db, ChangeDto? change, AgentRecord agent, CancellationToken ct)
    {
        if (change is not { Status: ChangeStatuses.Applied } || !change.Runs.All(r => RunStatuses.IsTerminal(r.Status)) || Back(agent) is not { } session)
            return null;
        return await GuestChangingAsync(db, agent, ct) ? null : session;
    }

    /// <summary>A change to the agent's guest is queued, assigned or running: it may still restart the agent.</summary>
    private static Task<bool> GuestChangingAsync(OctopushyDb db, AgentRecord agent, CancellationToken ct)
    {
        var guest = agent.UpdateResourceId;
        return db.Attempts.AnyAsync(a => a.Mutating && a.Step!.Run!.ResourceId == guest
            && (a.Status == AttemptStatuses.Queued || a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running), ct);
    }

    private static string LostSentence(AgentRecord agent, AgentSession session, IReadOnlyList<string> lost)
    {
        var each = lost.Select(e => session.HelloUnavailable.TryGetValue(e, out var reason) ? $"{e} ({reason})" : e);
        var back = agent.UpdateRevert || agent.UpdatePreviousVersion is not { } previous ? "" : $" Revert brings back {previous}.";
        return $"{agent.Name} is back on {session.Version} and no longer offers {string.Join(", ", each)}, which it did before. "
               + $"Nothing was rolled back.{back}";
    }

    /// <summary>
    /// Why an update in flight failed, or null while it may still succeed. A run of its change that waits for approval or is
    /// blocked waits as long as it takes; the deadline runs only from the release of a step that may restart the agent.
    /// </summary>
    private (string Code, string Why)? Failure(ChangeDto? change, AgentRecord agent, DateTimeOffset now)
    {
        if (change is null)
            return (AgentUpdateFailures.ChangeGone, "The change that carried the update is gone.");
        if (change.Runs.FirstOrDefault(r => r.Status == RunStatuses.Uncertain) is { } uncertain)
            return (AgentUpdateFailures.RunUncertain, $"Run #{uncertain.Number} ended uncertain: look at {agent.Name}'s guest, then close the run and ask again.");
        var target = TargetOf(agent);
        var previous = agent.UpdateRevert || agent.UpdatePreviousVersion is null ? "" : $" Revert brings back {agent.UpdatePreviousVersion}.";
        // Connected again since the restart was released, on another version: the change did not bring the target.
        var otherVersion = agent.UpdateReleasedAt is not null && Back(agent) is null
                           && connections.Get(agent.Id) is { Version: not null } session && session.Id != agent.UpdateBaselineConnection
            ? (AgentUpdateFailures.OtherVersion,
                $"{agent.Name} connected again on {session.Version}, not {target}. Check the image its guest's setup deployed.{previous}")
            : ((string, string)?)null;
        switch (change.Status)
        {
            case ChangeStatuses.Applied:
                // Every step that may restart the agent is released through dispatch; one applied otherwise starts the clock now.
                agent.UpdateReleasedAt ??= now;
                if (otherVersion is not null)
                    return otherVersion;
                break;
            case ChangeStatuses.Failed:
                return (AgentUpdateFailures.ChangeFailed, $"The change failed: {change.Detail}");
            case ChangeStatuses.Refused or ChangeStatuses.Dismissed:
                return (AgentUpdateFailures.ChangeRefused, $"The change was {change.Status}: {change.Detail}");
            case ChangeStatuses.Superseded:
                return (AgentUpdateFailures.Superseded, $"Another commit changed the guest first. {change.Detail}");
            case ChangeStatuses.NoChange:
                return (AgentUpdateFailures.NoRun, $"The change proposed no run, so nothing rolled the new version out. {change.Detail}");
        }

        // Never while a run of the change still works: failing would lift the pause while its setup may still restart the
        // agent. A run that loses its agent ends uncertain, which fails the update above.
        if (PhaseOf(agent, change.Runs.Select(r => r.Status)) == AgentUpdatePhases.Restarting && now - agent.UpdateReleasedAt >= ComeBackWithin
            && change.Runs.All(r => RunStatuses.IsTerminal(r.Status)))
            return otherVersion ?? (AgentUpdateFailures.DidNotComeBack,
                $"{agent.Name} did not connect again on {target} within {(int)ComeBackWithin.TotalMinutes} minutes of the step that restarts it. "
                + $"Check the agent on its guest.{previous}");
        return null;
    }

    /// <summary>
    /// Starts the earliest queued update when none is in flight: records the version and executors the agent has now, pauses it,
    /// commits the change and records it. Returns the agent started and, when its change was refused, why.
    /// </summary>
    private async Task<(Guid AgentId, string? Refused)?> StartNextAsync(CancellationToken ct)
    {
        var claimed = await engine.ExecuteAsync(async (db, _) =>
        {
            if (await db.Agents.AnyAsync(a => a.UpdateState == AgentUpdateStates.Updating, ct))
                return null;
            var agents = await db.Agents.OrderBy(a => a.Name).ToListAsync(ct);
            var next = agents.Where(a => a.UpdateState == AgentUpdateStates.Queued).OrderBy(a => a.UpdateRequestedAt).FirstOrDefault();
            if (next is null)
                return null;
            var assessment = (await AssessAsync(db, agents, await HeadAsync(db, ct), ct))[next.Id];
            // Claimed before the commit, so nothing else starts meanwhile.
            next.UpdateState = AgentUpdateStates.Updating;
            string? why;
            if (next.UpdateRevert)
            {
                next.UpdateTargetVersion = next.UpdatePreviousVersion;
                // Back on it already, and declared: nothing to do.
                why = SameVersion(next.Version, next.UpdatePreviousVersion) && SameVersion(assessment.Guest?.DeclaredVersion, next.UpdatePreviousVersion)
                    ? ""
                    : assessment.RevertRefused;
            }
            else
            {
                next.UpdateTargetVersion = TargetVersion;
                why = assessment.Plain.State == "available" && assessment.Guest is not null ? null
                    : assessment.Plain.State == "current" ? "" : assessment.Plain.Reason ?? "It cannot be updated from here now.";
            }

            if (why is not null)
            {
                // It became current, or cannot be updated from here, while it waited.
                if (why.Length == 0)
                    Clear(next);
                else
                    await FailAsync(db, next, AgentUpdateFailures.Unavailable, why, ct);
                await db.SaveChangesAsync(ct);
                return new { next.Id, Resource = (Guid?)null, Actor = "", Version = "", Refused = why.Length == 0 ? null : why };
            }

            // What it runs and offers now, as its own connection said: Revert brings the version back, and the new one must
            // offer every executor.
            // The guest, before the commit: dispatch holds changes to it from now on, not only once the change is recorded.
            next.UpdateResourceId = assessment.Guest!.ResourceId;
            var session = connections.Get(next.Id);
            next.UpdateBaselineConnection = session?.Id;
            if (!next.UpdateRevert)
                next.UpdatePreviousVersion = session?.Version ?? next.Version;
            next.UpdatePreviousExecutors = [.. (session?.Executors ?? next.Executors).Order(StringComparer.Ordinal)];
            if (!next.Draining)
            {
                next.Draining = true;
                next.UpdatePaused = true;
            }

            committing[next.Id] = 0;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                committing.TryRemove(next.Id, out var unclaimed);
                throw;
            }

            return new { next.Id, Resource = (Guid?)assessment.Guest!.ResourceId, Actor = next.UpdateRequestedBy ?? "octopushy",
                Version = next.UpdateTargetVersion!, Refused = (string?)null };
        }, ct);
        if (claimed is null)
            return null;
        if (claimed.Resource is not { } resourceId)
            return (claimed.Id, claimed.Refused);

        // Once claimed, the commit and its record do not stop with the request or the connection that asked: a claim left
        // without its change would stay "updating" and hold the queue.
        ChangeDto? change = null;
        string? refused;
        try
        {
            try
            {
                (change, refused) = await CommitAsync(resourceId, claimed.Actor, claimed.Version, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Committing the update of agent {Agent} failed.", claimed.Id);
                refused = $"Committing the update failed: {e.Message}";
            }

            await engine.ExecuteAsync(async (db, _) =>
            {
                var agent = await db.Agents.FirstAsync(a => a.Id == claimed.Id, CancellationToken.None);
                if (change is not null)
                {
                    agent.UpdateChangeId = change.Id;
                    logger.LogInformation("Agent {Name} is being updated to {Version} by change {Change}.", agent.Name, claimed.Version, change.Id);
                }
                else
                    await FailAsync(db, agent, AgentUpdateFailures.CommitRefused, refused!, CancellationToken.None);
                await db.SaveChangesAsync(CancellationToken.None);
                return 0;
            }, CancellationToken.None);
        }
        finally
        {
            committing.TryRemove(claimed.Id, out _);
        }

        // A refusal emptied the queue; a start may still be due when it did not.
        return (claimed.Id, refused);
    }

    /// <summary>
    /// Commits the guest's definition with the version set to <paramref name="version"/> and the digest dropped, every other input
    /// kept, as <paramref name="actor"/>. Returns the change, or why it was refused.
    /// </summary>
    private async Task<(ChangeDto? Change, string? Refused)> CommitAsync(Guid resourceId, string actor, string version, CancellationToken ct)
    {
        ChangeFormDto? form;
        await using (var scope = scopes.CreateAsyncScope())
            form = await changes.FormAsync(scope.ServiceProvider.GetRequiredService<OctopushyDb>(), resourceId, ct);
        if (form is null)
            return (null, "The guest is no longer recorded.");
        if (form.BlockedReason is { } blocked)
            return (null, blocked);
        if (form.Inputs is not JsonObject current || form.BaseCommit is null)
            return (null, "The guest's definition has no inputs to set the version in.");
        var inputs = current.DeepClone().AsObject();
        inputs[VersionInput] = version;
        inputs.Remove(DigestInput);
        var (change, refusal) = await changes.CommitAsync(new ChangeRequest(resourceId, form.BaseCommit, ChangeKinds.Edit, Inputs: inputs), actor, ct);
        return (change, refusal?.Message);
    }

    /// <summary>
    /// The change of an agent update that covers <paramref name="run"/>'s approval: an update in flight whose change is for the
    /// run's resource, at the revision the run is of, which is still the head. The run is approved as the operator who asked.
    /// </summary>
    internal static async Task<ResourceChange?> CoveringAsync(OctopushyDb db, Run run, Func<ResourceChange, Task> refresh, CancellationToken ct)
    {
        if (run.Reason != RunReasons.Revision || run.RevisionId is null)
            return null;
        var ids = await db.Agents.AsNoTracking().Where(a => a.UpdateState == AgentUpdateStates.Updating && a.UpdateChangeId != null)
            .Select(a => a.UpdateChangeId!.Value).ToListAsync(ct);
        if (ids.Count == 0)
            return null;
        var change = await db.Changes.FirstOrDefaultAsync(c => ids.Contains(c.Id) && c.ResourceId == run.ResourceId && c.Commit != null, ct);
        if (change is null || ChangeStatuses.IsFinal(change.Status))
            return null;
        if (change.RevisionId is null)
            await refresh(change);
        if (change.RevisionId != run.RevisionId)
            return null;
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        return head == run.RevisionId ? change : null;
    }

    /// <summary>
    /// What each reachability endpoint is, from the head revision: installations' API and snippet endpoints, and guests' SSH.
    /// Keyed by kind and endpoint.
    /// </summary>
    public static async Task<IReadOnlyDictionary<(string Kind, string Endpoint), (string Name, Guid? ResourceId)>> EndpointNamesAsync(
        OctopushyDb db, Head head, CancellationToken ct)
    {
        var names = new Dictionary<(string, string), (string, Guid?)>(new EndpointComparer());
        if (head.Repository is not { } repository)
            return names;
        foreach (var installation in repository.Installations)
        {
            foreach (var endpoint in installation.Hosts.Values.Select(h => ProvisionInputs.EndpointFor(installation, h)).Append(installation.Connection.Endpoint))
            {
                if (Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) && uri.DnsSafeHost.Length > 0)
                    names.TryAdd(("proxmox-api", NetworkTarget.EndpointOf("tls", uri.DnsSafeHost, uri.Port)), (installation.Address, null));
            }

            SnippetSettings? snippets = null;
            try
            {
                snippets = ManagedHostKey.Settings(installation);
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or KeyNotFoundException)
            {
                // An installation whose snippets do not read names none of them.
            }

            foreach (var host in snippets?.Hosts.Values ?? [])
                names.TryAdd(("snippets-ssh", NetworkTarget.EndpointOf("ssh", host.Address.Trim().TrimStart('[').TrimEnd(']'), host.Port)), (installation.Address, null));
        }

        var guests = repository.Guests.Select(g => g.Address).ToList();
        var ids = await db.Resources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Guest && r.RetiredAt == null && guests.Contains(r.Address))
            .Select(r => new { r.Address, r.Id }).ToListAsync(ct);
        foreach (var guest in repository.Guests.Where(g => !g.Retired))
        {
            if (ConfigureInputs.AddressOf(guest) is not { } address)
                continue;
            var port = guest.Json["access"]?["port"] is JsonValue p && p.TryGetValue<int>(out var given) ? given : 22;
            names.TryAdd(("guest-ssh", NetworkTarget.EndpointOf("ssh", address.Trim().TrimStart('[').TrimEnd(']'), port)),
                (guest.Address, ids.FirstOrDefault(r => r.Address == guest.Address)?.Id));
        }

        return names;
    }

    private sealed class EndpointComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            x.Item1 == y.Item1 && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj) => HashCode.Combine(obj.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}
