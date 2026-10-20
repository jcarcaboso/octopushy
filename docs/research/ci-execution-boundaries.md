# CI execution boundaries for Octopushy

**Reviewed: October 12, 2026.** Scope: Octopushy as a forge-independent homelab-as-code operations control plane; only GitHub/GitLab execution integration. Official documentation review, not a tested integration. Recommendations below are engineering judgments, not existing Octopushy capabilities or vendor endorsements.

## Conclusion

**Reuse native CI through workflows and APIs, not by treating its runners as interchangeable Octopushy workers.** The documented contracts bind runners to their respective CI controllers: GitHub assigns Actions jobs; GitLab Runner requests GitLab jobs using runner authentication. No generic third-party-controller registration interface is documented in these sources. This is a conclusion about supported interfaces, not proof that a custom adapter is impossible. [S1], [S2]

The useful directions are:

- **CI → Octopushy:** a native workflow/job invokes an Octopushy CLI or authenticated HTTP API, once supplied by the product. This is ordinary job execution/external-service integration, not replacing the runner's controller. GitHub documents external OIDC-authenticated access; GitLab Runner supports shell execution. [S2], [S5]
- **Octopushy → CI:** Octopushy authenticates to the forge API and starts a native workflow/pipeline, retaining correlation with its own operation and attempts. The forge still schedules its runners. [S3], [S4], [S6]

## What the native workers actually accept

**GitHub Actions:** self-hosted runners register with GitHub and connect outward to receive assignments. GitHub matches individual workflow jobs to online runners using `runs-on` labels/groups; these are Actions-specific job assignments, not an arbitrary external work queue. Required outbound HTTPS connectivity to GitHub does not establish connectivity from hosted CI into a homelab. [S1]

**GitLab Runner:** its documented execution flow requests jobs through `POST /api/v4/jobs/request` using a runner token, receives a job payload with a job token, and reports output/status to GitLab. Executors determine how those jobs run; a shell/Docker/SSH executor does not change the controller protocol. The overview's registration diagram includes legacy registration tokens; it should not be copied as a current provisioning recipe. [S2]

**Assessment:** a custom protocol adapter/controller emulator is conceivable, but would introduce authentication, payload, lifecycle and compatibility maintenance. Not worthwhile for this scope when supported workflow/API boundaries already exist. This is a cost/benefit judgment, not a technical impossibility claim. [S1], [S2], [S3], [S6]

## Authenticated dispatch and attempt correlation

**GitHub:** call `POST /repos/{owner}/{repo}/actions/workflows/{workflow_id}/dispatches` with a ref and declared inputs; the workflow must enable `workflow_dispatch`. GitHub App installation tokens and fine-grained personal tokens are supported, with repository **Actions: write** permission. The documentation currently illustrates API version `2026-03-10` returning HTTP **200** with `workflow_run_id`, `run_url` and `html_url`; do not hard-code an older assumption that dispatch always returns an empty response. Verify against the deployed GitHub/API version. [S3]

Store the returned run ID; the workflow-runs API exposes `run_attempt`, status/conclusion, and attempt-specific retrieval, plus rerun/cancel/log operations. Recommended correlation: Octopushy operation ID + Octopushy attempt ID + repository/run ID + native run attempt. [S4]

**GitLab:** call `POST /projects/:id/trigger/pipeline` with a ref and a pipeline trigger token; CI/CD job tokens are also supported for CI-originated triggering. Inputs/variables can carry correlation data. The response includes pipeline ID, project ID, SHA, status and web URL. [S6]

**Integration recommendation:** record each dispatch and its returned native identifier separately from deployment success. Pass an Octopushy attempt ID as an input/variable, preserve it in callbacks, and deduplicate deployment requests. A repeated dispatch is not evidence that the previous deployment did nothing. Do not invent a GitLab equivalent of GitHub's `run_attempt`; retain Octopushy's own attempt key. This is proposed integration behavior, not a claim of built-in exactly-once delivery. [S3], [S4], [S6]

## Networking and the recommended security boundary

GitHub-hosted runners have public-internet access by default; private resources require deliberate connectivity. GitHub documents an OIDC-authenticated API gateway, WireGuard overlay, and Azure VNET options. These are options, not automatic homelab reachability or interchangeable entitlements. [S5]

GitLab distinguishes hosted runners, whose infrastructure/environment control is limited, from self-managed runners. Do not infer private homelab reachability from the availability of either vendor's hosted CI; select and verify an explicit network path. The particular GitLab-hosted private-network mechanism is outside this bounded verification. [S2], [S5]

**Recommendation for Octopushy, not a vendor requirement:**

1. CI builds, tests and publishes an artifact, then submits its immutable digest, source revision and correlation IDs in a deployment request.
2. CI receives only narrowly scoped permission to request deployment—not homelab administrator credentials.
3. Dedicated Octopushy agents hold/use homelab mutation credentials and perform the deployment under Octopushy's authorization and operational controls.
4. Make only the necessary request interface reachable/authenticated; do not expose the whole homelab merely to reuse hosted CI. A digest identifies content; authorization to deploy it remains a separate decision.

This division uses the documented external-call/dispatch boundaries while avoiding CI ownership of homelab mutation. It is an integration recommendation inferred from those interfaces and networking constraints. [S3], [S5], [S6]

## Small tradeoff comparison

| Approach | Reuse | Cost / boundary | Judgment |
|---|---|---|---|
| CI calls Octopushy CLI/API | Existing build/test/publish pipeline | Request authentication and API reachability; keep mutations on Octopushy agents | Preferred deployment handoff. [S2], [S5] |
| Octopushy dispatches native CI | Existing workflows and runner fleets | Forge credentials, definitions and attempt/status correlation | Useful optional build/test integration; not a forge-independent execution substrate. [S3], [S4], [S6] |
| Co-host native CI runner and Octopushy agent | Same physical/virtual capacity | Separate processes, identities, queues and permissions; assess contention and isolation | Shares a machine, **not a worker protocol**; not the default for privileged mutation. Inference from [S1], [S2]. |
| Custom runner-protocol adapter | Potential reuse of runner implementation | Compatibility/authentication/lifecycle maintenance | Conceivable, but not justified here. Assessment from [S1], [S2]. |

Co-hosting a GitHub runner and a GitLab runner likewise means two independently configured agents, not one agent serving both protocols. Co-location itself is not a security isolation boundary; separate VMs/hosts may be appropriate. These are deployment judgments, not a verified vendor co-hosting certification. [S1], [S2]

## Official sources

- **[S1] GitHub — Self-hosted runners reference:** https://docs.github.com/en/actions/reference/runners/self-hosted-runners
- **[S2] GitLab — GitLab Runner (execution flow, executors, hosted/self-managed):** https://docs.gitlab.com/runner/
- **[S3] GitHub — REST workflows (create workflow dispatch):** https://docs.github.com/en/rest/actions/workflows#create-a-workflow-dispatch-event
- **[S4] GitHub — REST workflow runs (status, attempts and reruns):** https://docs.github.com/en/rest/actions/workflow-runs
- **[S5] GitHub — Private networking with GitHub-hosted runners:** https://docs.github.com/en/actions/concepts/runners/private-networking
- **[S6] GitLab — Pipeline trigger tokens API:** https://docs.gitlab.com/api/pipeline_triggers/#trigger-a-pipeline-with-a-token
