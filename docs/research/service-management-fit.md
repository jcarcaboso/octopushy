# Service-management fit: Komodo, Coolify, Portainer

**Reviewed: October 12, 2026.** Bounded first-party documentation review, not a deployment test or exhaustive market survey. Five cited pages; no claims about compatibility with the particular Nix-based/Fedora images. Proxmox identity/state and the secrets model are deliberately outside this note.

## Correcting the inference

**No—“nothing fits for services” is too strong.** All three have documented capabilities relevant to the service/container layer: Komodo offers container/Compose operations and automation; Coolify offers application/database/service deployment; Portainer offers Compose-stack deployment and updates. Those are real partial fits, not evidence of a complete match for the entire fleet workflow. [S1], [S2], [S4]

The requested workflow is broader: Proxmox API URL/token → OpenTofu-managed VMs/templates → Nix-based/Fedora images → service-host prerequisites and deployments, alongside AI-role VMs with frequent tool/harness changes through SSH/Ansible/bash. That is the user's requirement, not a verified capability of any one product.

## What actually fits

### Komodo: container operations plus automation

- **Verified:** create, start, stop and redeploy Docker containers; inspect status/logs and open shells; deploy Compose from the UI, host files or Git, including deployment on push; build images; run scheduled, multi-step Procedures and Actions. [S1]
- **Architecture:** Core provides the UI/API; a **Periphery agent on each connected server** performs actions and reports system usage/container logs. This is an additional managed-host agent, rather than merely reusing an SSH-only operations path. [S1]
- **Fit assessment:** a credible candidate for operating Docker service-host VMs and sequencing service deployment tasks. Its workflow primitives are integration opportunities, not proof that the complete requested infrastructure lifecycle is already modeled. [S1]

### Coolify: application and service platform on supplied servers

- **Verified:** deploy applications from Git, Dockerfiles, Compose or images; run supported databases with persistent storage and backup configuration; deploy preconfigured Compose service templates; manage domains/HTTPS, health checks, logs and container lifecycle across connected servers. [S2]
- **Architecture/prerequisites:** manages connected servers through **SSH**. Validation checks the OS/tools and installs or verifies Docker Engine and Compose; it can restart Docker and interrupt existing containers. That overlaps part of service-host preparation, not just application deployment. [S3]
- **Fit assessment:** a credible candidate when the desired service UX is “connect a server, deploy apps/databases, manage routing and operation.” SSH transport does not establish compatibility with arbitrary Ansible/bash workflows or declarative Nix host configuration. [S2], [S3]
- **Avoid an overly broad limitation:** the server overview also documents provisioning servers through named cloud providers. This review does not establish the requested Proxmox/OpenTofu integration; it would be inaccurate to reduce Coolify to a product with no provisioning features. [S3]

### Portainer: Compose-stack deployment and updates

- **Verified:** deploy stacks from an editor, uploaded Compose file, Git repository or custom template; supply environment variables; configure Git-based updates through polling/webhooks, image re-pulls and forced redeployment. The documented relative-path-volume feature is Business Edition only. [S4]
- **Architecture, verified route only:** the documented traditional Agent deployment adds an **agent container** on the Docker host, contacted by Portainer Server on port 9001. The page calls this a legacy option and recommends Edge Agent for most cases; do not infer this is the only connection architecture. [S5]
- **Fit assessment:** a credible candidate for deploying and updating services already expressed as Compose stacks. This is a service deployment interface, not evidence that the requested VM/image/AI-role lifecycle is covered end to end. [S4], [S5]

## Where the service boundary belongs

**Proposed division of responsibility, not a vendor restriction:**

1. Keep the Proxmox/OpenTofu and image/host-role workflow as a separately evaluated concern.
2. Hand a suitable service-host VM to one of these service managers; agree which side owns Docker preparation, since Coolify can change that setup during validation. [S3]
3. Use the selected product for its documented container/Compose/app operations instead of rebuilding that subset by default. [S1], [S2], [S4]
4. Evaluate host-level AI tool/harness upgrades separately. If a particular AI workload is packaged as a container/Compose service, its deployment may fit the same service layer; that is an inference from the deployment interfaces, not verified support for the user's AI-role lifecycle. [S1], [S2], [S4]
5. Validate the actual Nix-based/Fedora images, privilege/network requirements and ownership boundaries in a small proof of concept before adopting anything. No OS compatibility certification is implied here.

**Bottom line:** service-layer options do fit meaningful parts of the requirement. These pages do **not establish** one product implementing the complete Proxmox → OpenTofu → image/host configuration → services + frequently updated AI-role fleet workflow. That is a bounded evidence gap, **not** a finding that no product exists or that these products cannot be extended or integrated. [S1]–[S5]

## First-party sources

- **[S1] Komodo — What is Komodo?** https://komo.do/docs/intro
- **[S2] Coolify — What is Coolify:** https://coolify.io/docs/core/what-is-coolify
- **[S3] Coolify — Servers overview:** https://coolify.io/docs/core/infrastructure/servers/overview
- **[S4] Portainer — Add a new stack:** https://docs.portainer.io/user/docker/stacks/add
- **[S5] Portainer — Install Portainer Agent on Docker Standalone:** https://docs.portainer.io/admin/environments/add/docker/agent
