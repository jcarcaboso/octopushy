# Homelab Jenkins alternatives: a bounded comparison

**Reviewed: 2026-10-12.** Scope: orchestration around existing OpenTofu, Ansible, scripts, and Git, across multiple Proxmox hosts. Priorities are an excellent operational dashboard, easy failure diagnosis and recovery, outbound-connecting execution agents, self-hosting, and eventual GitHub/GitLab/Forgejo/Gitea use.

This is **documentation/source-based research, not hands-on UX validation**. “Best fit” below is a judgment, not a benchmark or usability result. Sources are official product documentation, pricing pages, and repositories. Documentation and repository branches move independently of releases; no “latest version” is asserted. Verify the listed capabilities against the actual image and edition evaluated. Blacksmith, custom architecture, and OpenTofu execution/state correctness are deliberately outside scope.

## Scope clarification: resources-as-code, not merely a Jenkins replacement

The follow-up requires persistent node/service status, independent parallel
deployments, notifications, optional Git, live health, and future Talos support.
The original recommendation below was for the narrower execution/dashboard
comparison; it is **not a claim that Semaphore or Kestra supplies this complete
resource control plane**.

Revised screening recommendation:

- Do not select a CI runner/controller as the product's center merely because it
  can execute OpenTofu and Ansible. Keep CI as an optional build/release integration.
- Treat Semaphore/Kestra/Windmill as possible execution or application-building
  components, not verified turnkey implementations of the clarified product.
- For a Docker/Compose-heavy service layer, **Komodo deserves the next targeted
  product check**: its resource sync accepts TOML from local files or Git, and
  its procedures run executions in parallel within sequential stages. [C2], [C3]
  Its server/container views and alerts cover part of the requested picture. [C1]
- Komodo is not a proven complete answer here: Core calls per-server Periphery
  agents, rather than the proposed outbound site-worker topology. [C1] Native
  OpenTofu plan/recovery, failure containment between procedure stages, and
  Talos lifecycle integration still require validation. Parallel execution alone
  does not prove that a failed branch leaves unrelated later stages runnable.
- Prefer a focused custom control plane only if that test leaves a material gap.
  Pure IaC tools or cluster-specific management can still be components rather
  than the dashboard for the whole mixed homelab.

These are scope/fit judgments, not new hands-on product test results. The updated
[architecture and PoC](../architecture-and-poc.md) makes two-target parallelism,
isolated recovery, one notification channel, and live health acceptance criteria.

## Original execution-focused recommendation

**Trial Semaphore UI first and Kestra second.** Semaphore is closest to the existing Ansible/OpenTofu operating model; Kestra is the stronger hypothesis for understanding multi-step failures and recovering without restarting everything. Neither is a verified turnkey Proxmox fleet dashboard. Their documented UI and recovery differences are detailed below. [S1], [S2], [S5], [K3], [K4]

Keep **Woodpecker** as the forge-centric, open-source CI option, especially if free distributed execution matters more than a target-oriented operations console. Keep **Windmill** as the script/workflow/application platform option, but price its HTTP agents and deployed-flow recovery before investing in a prototype. [P1], [P2], [W1], [W2], [W4]

Do not treat “clean UI” as a sufficient requirement. The desired view is **environment → target → last operation → current failure → safe recovery action**, not merely a prettier job list. An inventory records targets; a successful execution records an operation; neither proves current host or service health. This is the evaluation criterion, not a claim that any candidate already supplies that complete view.

### Concise decision table

| Candidate | Main fit | Outbound execution and edition boundary | Dashboard/recovery assessment |
|---|---|---|---|
| **Semaphore UI — first trial** | Native Ansible and OpenTofu task templates; least conceptual change. [S1], [S2] | **Global runners are Community**. Project-scoped runners and tag routing are Pro; runners poll the server. [S1], [S3] | Template status, inventories/workspaces, task logs and rerun controls. Structured task summaries are Pro. Promising simpler operations UI; not verified fleet health or failed-step resume. [S1], [S2], [S5] |
| **Kestra — second trial** | Multi-step orchestration with strong execution inspection and replay. [K3], [K4] | Current architecture docs describe outbound **gRPC** workers, including OSS's single implicit default group. Named worker groups and their authorization are Enterprise. [K1], [K2] | Execution graph, Gantt, per-task logs and replay are compelling on paper. Custom dashboard creation/editing is Enterprise in the documented 2.0 migration. [K3], [K4], [K5] |
| **Woodpecker CI** | Git-triggered pipelines and the clearest native four-forge story. [P1] | Agents connect to the server; distributed agents are part of the open-source project. Use the documented TLS configuration. [P2], [P3] | Fundamentally repository/pipeline-oriented. Restart creates a new pipeline, not a verified resume from a failed deployment step. [P4] |
| **Windmill** | Scripts, flows, and custom operational apps. [W1] | Normal workers use PostgreSQL directly; HTTP-only agent workers require Enterprise. [W2] | Runs and per-step results/logs are documented; custom target dashboards require building an app. Restarting a **deployed** flow from a step requires Enterprise. [W1], [W3], [W4] |

## 1. Semaphore UI

**Automation fit.** Modern Semaphore is not just an Ansible front end: templates support Terraform/OpenTofu, Ansible, Shell, Python, PowerShell, and Terragrunt. Its OpenTofu documentation describes native workspaces and run controls. Running OpenTofu is included in Community; storing state in Semaphore's own HTTP backend is Pro. An existing externally configured backend does not imply buying that feature. [S1], [S2], [S4]

**Remote runners: the important correction.** It is inaccurate to say “Semaphore remote runners are paid.” The pricing matrix distinguishes free **global runners**, paid **project runners**, and paid **tag-based routing**. Runners initiate communication and poll for work. The administration guide describes distributing tasks among runners; consequently, a free shared pool is not the same as selecting a particular network zone for a particular project. If each Proxmox segment needs its own restricted executor, treat routing/isolation as a purchasing or topology decision, not an assumed Community feature. [S1], [S3], [S6]

**Dashboard and recovery.** The documented template list exposes last-task status and associated inventory/repository; template pages expose task history and workspace tabs. Task records retain logs, status, execution details and repository revision, and the UI provides rerun controls. That is useful evidence for a simpler operator experience, **not** evidence of an environment-wide health dashboard. Structured execution summaries and file-based log export are paid; validate the free failure-view experience rather than judging only Pro screenshots. [S1], [S2], [S5]

Treat a task rerun as another execution, not as a proven checkpointed continuation within an Ansible playbook or arbitrary script. Failed-host-only recovery and a workflow's failed-node retry behavior were **not verified** in this review. Make both explicit trial questions. [S5]

**Git/forges.** Semaphore accepts ordinary remote Git repositories over HTTPS or SSH. That makes repositories on all four requested forges plausible without special adapters; this is an inference from protocol support, **not** verified parity for forge login, PR checks, commit-status reporting, or webhook authentication. [S7]

**Unresolved edition conflict.** The live pricing matrix lists “Workflows” in Community, but the official workflow documentation explicitly labels its visual DAG/approval functionality **Pro**. Do not promise free visual chaining or approvals until the vendor or the tested Community image resolves this discrepancy. This is material to replacing Jenkins pipelines. [S1], [S8]

**Fit judgment:** best first test for “operate the automation I already have.” Reject it if diagnosing a failed host still means hunting through a long console log, or if the required routing/workflow edition is outside budget.

## 2. Kestra

**Execution model and isolation.** Current official architecture documentation says workers initiate persistent bidirectional gRPC streams to a Worker Controller, which dispatches work and receives results/logs. It explicitly describes outbound-only deployment and an OSS implicit default worker group. This is more specific than assuming every distributed worker needs direct database access; verify that the chosen release implements this documented architecture. [K1], [K2]

Enterprise worker groups provide named execution pools and authorization. They cover all task/plugin types; task runners are a different mechanism for executing supported scripting tasks, including container-based execution. A Docker task runner does not automatically give an OSS installation Enterprise-style placement and access controls across all plugins. Network reachability, credentials, and untrusted-code isolation still need separate checks. [K1], [K6]

**Dashboard.** Documented execution views include a task-state topology, Gantt, logs filtered by severity, per-task outputs, and filters such as namespace, labels and state. That gives Kestra the strongest **docs-based failure-diagnosis hypothesis** in this shortlist. Namespace/label filtering is still an execution view, not proof of live target health. [K3]

**Do not assume every dashboard screenshot is OSS.** The current dashboard page is edition-marked Enterprise, and the official 2.0 migration explicitly moves dashboard create/update/delete APIs and Git dashboard synchronization to EE. It also documents Enterprise asset-inventory data sources, but this review did not establish automatic Proxmox discovery. Check what the free default dashboard actually exposes in the installed build. [K5], [K7]

**Recovery.** Replay can start at a selected task and reuse earlier task outputs. The documentation describes choosing the original, latest, or a specific revision, and rejecting incompatible changes to upstream tasks, inputs or variables. This is materially different from restarting an entire shell script. Execution documentation also names bulk Restart and warns that Force Run can duplicate task executions; do not conflate these controls. [K3], [K4]

Replay is useful only at boundaries represented as Kestra tasks. It does not document resuming inside a single arbitrary script. Whether a failed task already changed an external system remains an operational question; a green replay screen is not a rollback guarantee. Test the exact workflow structure and artifact retention you intend to use.

**Git/forges.** Official Git tooling includes flow/file synchronization, pushing flow changes, and cloning repositories at runtime. These can support Git-authoritative definitions without selecting one CI forge. Native PR/status/login parity across GitHub, GitLab, Forgejo and Gitea was **not verified**; generic Git connectivity should not be advertised as that parity. [K8]

**Fit judgment:** second trial if legible multi-step operations and replay matter more than minimizing workflow translation. Worker-group and dashboard customization licensing are the principal blockers to check early.

## 3. Woodpecker CI

**Forge support, precisely.** The official matrix lists GitHub, GitLab, Forgejo and Gitea, including push, tag and pull-request events. There are event differences: external deployment-event webhooks are GitHub-specific in the matrix, although Woodpecker can trigger deployment events itself for the other forges. [P1]

“Multiple forges supported” does **not** mean mature single-instance federation. Current documentation says:

- Multiple forges on one server are **experimental**, unsuitable for public or otherwise untrusted environments.
- Environment configuration defines **one** forge; an administrator adds additional forges through the UI/database. Enabling multiple environment-variable drivers is not the way to create multiple connections.
- `WOODPECKER_ADMIN` matches login names across connected forges, and the same person receives separate accounts when signing in through different forges. [P1]

For a conservative deployment, plan **separate server instances per forge** unless accepting and testing those limitations. That is a recommendation, not a product requirement that multiple forges are impossible.

**Agents.** Agents initiate server connections, authenticate with agent/system tokens, and pull work through gRPC. This matches the outbound-executor requirement without a paid remote-agent feature. Agent labels are routing metadata, not authorization: current docs explicitly warn agents can report arbitrary labels and describe server-side agent filters as the restrictive counterpart. [P2], [P3], [P5]

**Dashboard/recovery.** Woodpecker's primary model is repositories, pipelines, workflows and steps, rather than an infrastructure inventory. Its official implementation and integration test show restart creating a new pipeline with new workflow records and linkage to the original. Failed-step-only resume was **not verified**; plan on pipeline restart unless the tested release demonstrates a narrower operation. [P4], [P5]

OpenTofu and Ansible would be pipeline commands/tooling rather than Semaphore-like infrastructure-specific templates. This is a fit inference from the documented workflow model, not a claim of a dedicated Proxmox/OpenTofu control plane. [P5]

**Fit judgment:** strongest when Git events and open-source CI are primary. Less promising for “show me every environment and failed host, then recover only the affected operation.” Do not replace Jenkins with a cleaner jobs-only screen if that leaves the original operational problem intact.

## 4. Windmill

**Scope and self-hosting.** Windmill combines scripts, flows and applications and can be self-hosted. Its repository distinguishes the AGPL source build from distributed Community binaries/images that include additional features under additional terms; do not equate “Community” with an unrestricted Enterprise feature set. [W1]

**Workers.** Normal workers access PostgreSQL; Enterprise agent workers communicate through authenticated HTTP APIs instead. The latter match the desired remote/outbound-agent shape much better for separated networks. Agent workers cannot execute dependency jobs or the flow state machine, so the documented deployment still needs an appropriate normal worker. Do not price only remote machines and overlook that central component. [W2]

**Important current Git-sync qualification.** Managed Git sync is available in **Community workspaces with at most two users**, not exclusively Enterprise. The official March 7, 2026 changelog confirms this, and the current feature documentation repeats it. Larger-workspace managed sync is paid; CLI-driven sync is a separate option. [W5], [W6], [W7]

Git sync documents ordinary Git remotes as well as managed forge integrations. Current instructions label the managed GitLab-token integration Enterprise; GitHub App and managed GitLab connections add capabilities beyond a plain repository URL. Forgejo/Gitea ordinary Git compatibility is plausible, but equivalent managed PR/webhook/authentication features were **not verified**. Do not collapse these different integration levels into one “supports all Git hosts” checkbox. [W5]

**Dashboard/recovery.** Runs expose execution history and flow-step inputs, results and logs. Apps provide a way to build a target-oriented console, but building that console is additional product work, not an included homelab fleet dashboard. For this request, assess the stock operator experience before proposing a custom app. [W1], [W3]

Automatic step retries are documented separately from manual recovery. Restart-from-step/iteration/branch exists in flow preview; using it on **deployed flows** is a Cloud/Enterprise feature. Current docs allow selecting another deployed version and reusing earlier steps with matching IDs. Ordinary rerun, development preview, automatic retry, and production partial restart are not interchangeable. [W4], [W8]

**Fit judgment:** attractive if rich scripts, flows and operator apps are the goal and Enterprise is acceptable. Less compelling as a free, ready-made dashboard plus HTTP-agent replacement.

## Specialized alternatives — do not expand the primary bake-off yet

| Alternative | When it earns another look | Why not a primary trial here |
|---|---|---|
| **Octopus Deploy** | Deployment-oriented environments/targets, operational runbooks, worker pools, and guided failure handling. Workers can use polling Tentacles; scripts can execute the existing tools. [O1], [O2] | A credible conditional contender, not dismissed: check whether its personal-use free edition and resource limits fit this homelab. Native OpenTofu/Ansible integration and the exact desired replay behavior were not validated; script support alone is not that proof. Pricing pages are mutable, so verify eligibility and caps rather than design around a remembered allowance. [O3] |
| **Komodo** | Especially relevant if most service operations are Docker/Compose: its docs describe a server/container dashboard, Git-managed configuration, remote agents, and reusable procedures/actions. [C1] | Provisioning and host-maintenance semantics would still need evaluation around scripts/actions. The requested outbound-only agent topology, saved-plan approvals, and safe partial recovery were not established here. Do not assume its service-management fit makes it a full OpenTofu operations replacement. |
| **Rundeck / PagerDuty Runbook Automation** | Most directly relevant specialist for target-first UX: documented node attributes, tags, filtering and node-selected operations. Its commercial Runner polls over outbound HTTPS. [A1], [A2] | The runner matching this requirement is explicitly **commercial**, not a feature to assume in free Rundeck. Consider it if a node-centric console outweighs license cost; UI usability and failed-node recovery were not hands-on tested. |
| **AWX** | Officially provides a UI/API/task engine on Ansible; appropriate to revisit if Ansible becomes the dominant operating model. [A3] | Its repository currently warns that releases are paused for major refactoring and identifies **July 2, 2024** as the last release date. Treat that as a current maintenance/evaluation risk, not proof of abandonment. Remote execution topology and recovery behavior were not verified here. [A3] |
| **Terrakube** | Open-source IaC workspaces, OpenTofu workflows and VCS integration; useful if the missing product is primarily an IaC management console. [A4] | Narrower than general host maintenance/deployment orchestration. Its README lists GitHub/GitLab among integrations; Forgejo/Gitea parity and the required outbound executor topology were not established. [A4] |
| **Flux Tofu Controller** | Reconciles OpenTofu/Terraform resources through Flux, including drift-oriented models. [A5] | Kubernetes/GitOps reconciliation is a different operating model, not evidence of a ready-made operator dashboard for Ansible and arbitrary scripts. Only consider if Kubernetes/Flux is already wanted. |
| **Spacelift** | Documented self-hosted control plane and private workers; private workers pull work and do not require inbound control-plane connections. [A6], [A7] | Commercial, IaC-focused option. Self-hosted deployment has substantial infrastructure requirements; exact homelab pricing and desired four-forge parity were not verified. Not an assumed free replacement. [A6], [A7] |

## Time-boxed evaluation: 75–90 minutes per contender

Run this **same checklist first on Semaphore, then optionally on Kestra**. This is a proposed test, not a report of completed testing. Use the Community/OSS image first, record its exact version/digest, and identify any feature that silently depends on a trial license. Stop at 90 minutes and record setup friction rather than expanding the experiment.

Use two disposable targets labeled by Proxmox host and environment. Use existing non-destructive automation plus a three-stage fixture: an observable preparatory step, a deliberately failing configuration/deployment step, and a verification step. Count executions so a retry that repeats successful work is visible. Do not run production provisioning just to test a dashboard.

| Time | Check | Required evidence |
|---|---|---|
| **0–15 min** | Install the selected edition; connect one remote executor. If installation overruns, record that and stop adding scope. | Exact edition/image; executor online/offline visibility; no undocumented paid features required. |
| **15–25 min** | Block inbound control-plane connections to the executor while allowing the documented outbound path; run the harmless fixture. | Work and logs still arrive. Record HTTPS versus gRPC requirements, proxies/TLS, and any extra dependency access. |
| **25–40 min** | Create two distinguishable environments/targets and one failure. Start from the normal landing page, not a saved execution link. | Can a fresh operator identify the affected environment, target and failed operation within **30 seconds**, and reach the relevant error in **two or three clicks**? Record actual clicks/time and missing target context. |
| **40–55 min** | Compare successful and failed stages, logs, inputs and revision. Inspect the free dashboard and then any paid-only summary separately. | Failure does not disappear among successful jobs; step/host identity and timestamps are clear; a large log remains navigable. Screenshots should state the edition. |
| **55–70 min** | Fix the deliberate failure, choose the advertised recovery action, and inspect the execution counter. | Exact behavior: whole-task rerun, whole-pipeline restart, selected-task replay, or failed-target retry. Identify what repeats, which revision/inputs are used, and whether earlier outputs are reused. |
| **70–80 min** | Disconnect the worker during a harmless operation; reconnect it. Submit work intended for the other target/zone. | Honest queued/offline/running state; understandable cancellation/recovery; no wrong-zone execution. Record any paid routing requirement. |
| **80–90 min** | Change a Git-controlled definition and repeat the failed-step test, if time permits. Summarize costs and gaps. | Git versus UI source of truth is clear; recovery revision is visible; no assumption of PR integration merely because cloning works. |

**Semaphore-specific acceptance questions**

- Are Community task logs sufficient to diagnose a failed host, or is the Pro summary necessary?
- Do global runners provide the required placement, or do project runners/tag routing make Pro mandatory?
- Is visual workflow chaining actually available in the tested edition despite the conflicting docs?
- What exactly does **Rerun** preserve: inputs, inventory and repository revision? Does recovery require manually limiting an Ansible run?

**Kestra-specific acceptance questions**

- Does the chosen image implement the documented outbound Worker Controller architecture?
- Is the default-group model sufficient, or are Enterprise worker groups necessary for placement?
- Does replay visibly skip the successful preparation stage, preserve needed outputs, and explain an incompatible-revision refusal?
- Does the OSS dashboard meet the everyday operator need without Enterprise custom dashboard editing?

**Decision rule:** reject a candidate if it improves aesthetics but not failure discovery and recovery. Prefer the least complex one that passes those tests within the intended license budget. If neither provides a credible target-first view, that is a meaningful shortlist result—not a reason to silently turn the evaluation into building a custom dashboard.

## Sources

All links below are first-party. Repository links are also provided where they expose edition distinctions more clearly than marketing pages.

[S1]: https://semaphoreui.com/pricing
[S2]: https://github.com/semaphoreui/semaphore-docs/blob/main/docs/user-guide/task-templates/README.md
[S3]: https://github.com/semaphoreui/semaphore-docs/blob/main/docs/admin-guide/runners.md
[S4]: https://github.com/semaphoreui/semaphore-docs/blob/main/docs/user-guide/apps/terraform/README.md
[S5]: https://github.com/semaphoreui/semaphore-docs/blob/main/docs/user-guide/tasks.md
[S6]: https://github.com/semaphoreui/semaphore-docs/blob/main/docs/user-guide/projects/runners.md
[S7]: https://github.com/semaphoreui/semaphore-docs/blob/main/docs/user-guide/repositories.md
[S8]: https://github.com/semaphoreui/semaphore-docs/blob/main/docs/user-guide/workflows.md

[K1]: https://github.com/kestra-io/docs/blob/main/src/contents/docs/08.architecture/02.server-components/index.md
[K2]: https://github.com/kestra-io/docs/blob/main/src/contents/docs/08.architecture/03.deployment-architecture/index.md
[K3]: https://github.com/kestra-io/docs/blob/main/src/contents/docs/09.ui/02.executions/index.md
[K4]: https://github.com/kestra-io/docs/blob/main/src/contents/docs/06.concepts/10.replay/index.md
[K5]: https://github.com/kestra-io/docs/blob/main/src/contents/docs/11.migration-guide/v2.0.0/git-dashboard-tasks-moved-to-ee/index.md
[K6]: https://github.com/kestra-io/docs/blob/main/src/contents/docs/task-runners/03.task-runners-vs-worker-groups/index.md
[K7]: https://github.com/kestra-io/docs/blob/main/src/contents/docs/09.ui/00.dashboard/index.md
[K8]: https://github.com/kestra-io/docs/blob/main/src/contents/docs/version-control-cicd/04.git/index.md

[P1]: https://woodpecker-ci.org/docs/administration/configuration/forges/overview
[P2]: https://github.com/woodpecker-ci/woodpecker/blob/main/docs/docs/30-administration/10-configuration/30-agent.md
[P3]: https://github.com/woodpecker-ci/woodpecker
[P4]: https://github.com/woodpecker-ci/woodpecker/blob/main/server/pipeline/restart.go
[P5]: https://github.com/woodpecker-ci/woodpecker/blob/main/docs/docs/92-development/05-architecture.md

[W1]: https://github.com/windmill-labs/windmill
[W2]: https://github.com/windmill-labs/windmilldocs-public/blob/main/docs/core_concepts/28_agent_workers/index.mdx
[W3]: https://github.com/windmill-labs/windmilldocs-public/blob/main/docs/core_concepts/5_monitor_past_and_future_runs/index.mdx
[W4]: https://github.com/windmill-labs/windmilldocs-public/blob/main/docs/core_concepts/23_instant_preview/index.mdx
[W5]: https://github.com/windmill-labs/windmilldocs-public/blob/main/docs/advanced/11_git_sync/index.mdx
[W6]: https://github.com/windmill-labs/windmilldocs-public/blob/main/changelog/2026-03-07-git-sync-community-edition/index.md
[W7]: https://www.windmill.dev/docs/advanced/cli/sync
[W8]: https://github.com/windmill-labs/windmilldocs-public/blob/main/docs/flows/14_retries.md

[A1]: https://docs.rundeck.com/docs/manual/05-nodes.html
[A2]: https://docs.rundeck.com/docs/administration/runner/
[A3]: https://github.com/ansible/awx
[A4]: https://github.com/terrakube-io/terrakube
[A5]: https://github.com/flux-iac/tofu-controller
[A6]: https://docs.spacelift.io/self-hosted/latest/
[A7]: https://docs.spacelift.io/concepts/worker-pools

[O1]: https://octopus.com/docs/runbooks
[O2]: https://octopus.com/docs/infrastructure/workers
[O3]: https://octopus.com/pricing/overview
[C1]: https://komo.do/docs/intro
[C2]: https://komo.do/docs/automate/sync-resources
[C3]: https://komo.do/docs/automate/procedures
