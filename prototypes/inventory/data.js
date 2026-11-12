// Shared sample data for the inventory prototypes.
//
// The inventory (hosts, guests, services, DNS records and which checks were up) is modelled
// on a real lab, with its domains and addresses changed. Resources, run status, the uptime
// history and the unmanaged VMs are made up so every prototype has something to show.
//
// Everything is on window.INV. Prototypes read it; nothing writes to it.
(function () {
  const NOW = Date.now();
  const MIN = 60_000;
  const HOUR = 60 * MIN;
  const DAY = 24 * HOUR;
  const GiB = 1024 ** 3;
  const SLOT_MIN = 5; // history resolution
  const SLOTS = (30 * DAY) / (SLOT_MIN * MIN); // 8640 slots = 30 days

  function hash(s) {
    let h = 2166136261;
    for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619);
    return h >>> 0;
  }
  function rng(seed) {
    let a = hash(seed);
    return () => {
      a |= 0;
      a = (a + 0x6d2b79f5) | 0;
      let t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  /**
   * Builds the uptime of one check.
   * incidents: [{ ago: hours before now it started, min: minutes it lasted (omit = still going), state: "down"|"warn" }]
   */
  function monitor(id, { baseMs = 20, incidents = [], blips = 3, stoppedHoursAgo } = {}) {
    const r = rng(id);
    const slots = new Array(SLOTS).fill("up");
    const slotAt = (hoursAgo) => Math.max(0, Math.round(SLOTS - (hoursAgo * 60) / SLOT_MIN));
    for (const inc of incidents) {
      const start = slotAt(inc.ago);
      const end = inc.min == null ? SLOTS : Math.min(SLOTS, start + Math.max(1, Math.round(inc.min / SLOT_MIN)));
      for (let i = start; i < end; i++) slots[i] = inc.state ?? "down";
    }
    for (let b = 0; b < blips; b++) {
      const i = Math.floor(r() * SLOTS);
      if (slots[i] === "up") slots[i] = "warn";
    }
    if (stoppedHoursAgo != null) for (let i = slotAt(stoppedHoursAgo); i < SLOTS; i++) slots[i] = "down";

    const pct = (from) => {
      const part = slots.slice(from);
      const ok = part.filter((s) => s !== "down").length;
      return Math.round((ok / part.length) * 100_000) / 1000;
    };
    const worst = (arr) => (arr.includes("down") ? "down" : arr.includes("warn") ? "warn" : "up");
    // 96 beats of 15 minutes = the last 24 hours, oldest first.
    const beats = [];
    for (let b = 0; b < 96; b++) {
      const start = SLOTS - 288 + b * 3;
      beats.push(worst(slots.slice(start, start + 3)));
    }
    // 30 days, oldest first, each the percentage of time up.
    const daily = [];
    for (let d = 0; d < 30; d++) {
      const part = slots.slice(d * 288, (d + 1) * 288);
      daily.push(Math.round((part.filter((s) => s !== "down").length / 288) * 1000) / 10);
    }
    // Response time per beat; null while down.
    const latency = beats.map((s) =>
      s === "down" ? null : Math.round(baseMs * (0.7 + r() * 0.6) * (s === "warn" ? 6 + r() * 6 : 1)),
    );
    const status = slots[SLOTS - 1];
    let k = SLOTS - 1;
    while (k > 0 && slots[k - 1] === status) k--;
    const since = NOW - (SLOTS - k) * SLOT_MIN * MIN;
    const events = [];
    for (let i = 1; i < SLOTS; i++) {
      if (slots[i] !== slots[i - 1]) events.push({ at: NOW - (SLOTS - i) * SLOT_MIN * MIN, from: slots[i - 1], to: slots[i] });
    }
    return {
      status, // "up" | "down" | "warn"
      since,
      ms: status === "down" ? null : latency[latency.length - 1],
      uptime: { h24: pct(SLOTS - 288), d7: pct(SLOTS - 2016), d30: pct(0) },
      beats,
      daily,
      latency,
      events: events.slice(-12).reverse(), // newest first
    };
  }

  const lab = { id: "home", name: "Home lab", address: "10.20.0.0/24" };

  // ---- Proxmox hosts ("nodes") --------------------------------------------------------
  const nodes = [
    {
      id: "moraband",
      name: "moraband",
      lab: "home",
      installation: "moraband",
      proxmoxUrl: "https://10.20.0.50:8006/",
      address: "10.20.0.50",
      version: "8.4.1",
      online: true,
      cpu: { cores: 16, used: 0.31, model: "AMD Ryzen 9 5950X" },
      memory: { total: 64 * GiB, used: 41.2 * GiB },
      disk: { total: 1.8 * 1024 * GiB, used: 0.94 * 1024 * GiB },
      uptimeSeconds: 41 * 86400 + 3 * 3600,
      check: monitor("proxmox:moraband", { baseMs: 7, incidents: [{ ago: 24 * 19.2, min: 25 }] }),
      unmanaged: [
        { vmid: 100, name: "haos", kind: "qemu", status: "running", cores: 2, memory: 4 * GiB, uptimeSeconds: 41 * 86400, tags: ["home-assistant"] },
        { vmid: 110, name: "win11-scratch", kind: "qemu", status: "stopped", cores: 8, memory: 16 * GiB, uptimeSeconds: 0, tags: [] },
        { vmid: 210, name: "pihole-old", kind: "lxc", status: "stopped", cores: 1, memory: 0.5 * GiB, uptimeSeconds: 0, tags: ["legacy"] },
        { vmid: 9000, name: "debian-12-cloud", kind: "qemu", status: "stopped", template: true, cores: 2, memory: 2 * GiB, uptimeSeconds: 0, tags: ["template"] },
      ],
    },
    {
      id: "umbrel",
      name: "umbrel",
      lab: "home",
      installation: "umbrel",
      proxmoxUrl: "https://10.20.0.85:8006/",
      address: "10.20.0.85",
      version: "8.3.5",
      online: true,
      cpu: { cores: 8, used: 0.07, model: "Intel N305" },
      memory: { total: 32 * GiB, used: 9.6 * GiB },
      disk: { total: 0.93 * 1024 * GiB, used: 0.21 * 1024 * GiB },
      uptimeSeconds: 12 * 86400 + 17 * 3600,
      check: monitor("proxmox:umbrel", { baseMs: 10, incidents: [{ ago: 24 * 12.7, min: 40 }] }),
      unmanaged: [
        { vmid: 101, name: "umbrel-os", kind: "qemu", status: "running", cores: 4, memory: 8 * GiB, uptimeSeconds: 12 * 86400, tags: [] },
      ],
    },
    {
      id: "korriban",
      name: "korriban",
      lab: "home",
      installation: "korriban",
      proxmoxUrl: "https://10.20.0.10:8006/",
      address: "10.20.0.10",
      version: "8.4.1",
      online: false,
      offlineReason: "host unreachable",
      // Last values seen before it went away.
      cpu: { cores: 12, used: null, model: "Intel i5-12500" },
      memory: { total: 48 * GiB, used: null },
      disk: { total: 0.93 * 1024 * GiB, used: 0.4 * 1024 * GiB },
      uptimeSeconds: null,
      check: monitor("proxmox:korriban", { baseMs: 9, incidents: [{ ago: 48 }] }),
      unmanaged: [
        { vmid: 120, name: "nas-old", kind: "qemu", status: "unknown", cores: 2, memory: 4 * GiB, uptimeSeconds: null, tags: ["legacy"] },
      ],
    },
  ];

  // ---- Managed guests ("machines") ---------------------------------------------------
  // runStatus is Octopushy's view (does the guest match its definition); check is liveness.
  const g = (o) => ({ kind: "qemu", tags: [], workflow: "debian-vm@2", runStatus: "in-sync", ...o, hostname: `${o.id}.home.arpa` });
  const guests = [
    g({ id: "apps-mrb-01", node: "moraband", vmid: 600, address: "10.20.0.183", cores: 4, memory: 12 * GiB, disk: 80 * GiB, power: "running", uptimeSeconds: 9 * 86400, workflow: "docker-host@3", description: "Testing apps behind Traefik", check: monitor("tcp:apps-mrb-01:22", { baseMs: 3, incidents: [{ ago: 9 * 24, min: 6 }] }), lastRun: { status: "succeeded", at: NOW - 3 * HOUR } }),
    g({ id: "gocd-mrb-01", node: "moraband", vmid: 301, address: null, cores: 2, memory: 4 * GiB, disk: 32 * GiB, power: "stopped", uptimeSeconds: 0, workflow: "ci-runner@1", runStatus: "failed", description: "GoCD trial, setup failing", check: monitor("tcp:gocd-mrb-01:22", { baseMs: 2, stoppedHoursAgo: 26 }), lastRun: { status: "failed", at: NOW - 26 * HOUR } }),
    g({ id: "jenkins-test-01", node: "moraband", vmid: 302, address: null, tags: ["pipeline-verified"], cores: 2, memory: 4 * GiB, disk: 32 * GiB, power: "running", uptimeSeconds: 2 * 86400, workflow: "ci-runner@1", runStatus: "awaiting-approval", description: "Jenkins agent for pipeline tests", check: monitor("tcp:jenkins-test-01:22", { baseMs: 2, incidents: [{ ago: 50, min: 15 }] }), lastRun: { status: "awaiting-approval", at: NOW - 40 * MIN } }),
    g({ id: "mrb-crt-01", node: "moraband", vmid: 501, address: "10.20.0.246", cores: 6, memory: 16 * GiB, disk: 120 * GiB, power: "running", uptimeSeconds: 23 * 86400, workflow: "docker-host@3", description: "Core services: secrets, passwords, fleet", check: monitor("tcp:mrb-crt-01:22", { baseMs: 3 }), lastRun: { status: "succeeded", at: NOW - 20 * HOUR } }),
    g({ id: "mrb-dns-01", node: "moraband", vmid: 800, address: "10.20.0.178", kind: "lxc", cores: 1, memory: 1 * GiB, disk: 8 * GiB, power: "running", uptimeSeconds: 41 * 86400, workflow: "dns-resolver@2", description: "Resolver for home.arpa (primary)", check: monitor("tcp:mrb-dns-01:22", { baseMs: 3 }), lastRun: { status: "succeeded", at: NOW - 5 * DAY } }),
    g({ id: "umb-dns-01", node: "umbrel", vmid: 801, address: "10.20.0.179", kind: "lxc", cores: 1, memory: 1 * GiB, disk: 8 * GiB, power: "running", uptimeSeconds: 12 * 86400, workflow: "dns-resolver@2", description: "Resolver for home.arpa (secondary)", check: monitor("tcp:umb-dns-01:22", { baseMs: 4, incidents: [{ ago: 24 * 12.7, min: 45 }] }), lastRun: { status: "succeeded", at: NOW - 5 * DAY } }),
    g({ id: "krb-crt-01", node: "korriban", vmid: 500, address: null, cores: 4, memory: 8 * GiB, disk: 64 * GiB, power: "unknown", uptimeSeconds: null, workflow: "docker-host@3", runStatus: "uncertain", description: "Spare core host", check: monitor("tcp:krb-crt-01:22", { baseMs: 4, incidents: [{ ago: 48 }] }), lastRun: { status: "succeeded", at: NOW - 6 * DAY } }),
    g({ id: "krb-dns-01", node: "korriban", vmid: 802, address: "10.20.0.177", kind: "lxc", cores: 1, memory: 1 * GiB, disk: 8 * GiB, power: "unknown", uptimeSeconds: null, workflow: "dns-resolver@2", runStatus: "uncertain", description: "Resolver for home.arpa (tertiary)", check: monitor("tcp:krb-dns-01:22", { baseMs: 3, incidents: [{ ago: 48 }] }), lastRun: { status: "succeeded", at: NOW - 5 * DAY } }),
  ];

  // ---- Services ---------------------------------------------------------------------
  // urls: what a person opens (from Traefik routes and CNAME records). Each has its own check.
  // health: the declared health check. When it only answers on the guest, it is not probed.
  // dns: records the service declares.
  const svc = (o) => ({ runStatus: "in-sync", urls: [], dns: [], health: null, notes: [], ...o });
  const url = (u, opts = {}) => ({ url: u, host: new URL(u).host, check: monitor(`http:${u}`, opts) });
  const cname = (name, zone) => ({ type: "CNAME", name, expected: zone, zone, status: "ok" });
  const services = [
    // apps-mrb-01 (testing.example.org behind its own Traefik)
    svc({ id: "apps-mrb-01/traefik", name: "traefik", guest: "apps-mrb-01", category: "network", workflow: "traefik@2", description: "Reverse proxy for testing.example.org",
      urls: [url("https://traefik.testing.example.org/dashboard", { baseMs: 14 }), url("https://jenkins.testing.example.org/", { baseMs: 22 })],
      dns: [{ type: "A", name: "testing.example.org", expected: "10.20.0.183", zone: "testing.example.org", status: "ok" }, cname("traefik.testing.example.org", "testing.example.org"), cname("jenkins.testing.example.org", "testing.example.org")] }),
    svc({ id: "apps-mrb-01/outline", name: "outline", guest: "apps-mrb-01", category: "docs", workflow: "compose-app@2", description: "Team wiki",
      urls: [url("https://outline.testing.example.org/", { baseMs: 33, incidents: [{ ago: 30, min: 20 }] })],
      health: { url: "https://outline.testing.example.org/_health", probed: true },
      dns: [cname("outline.testing.example.org", "testing.example.org")] }),
    svc({ id: "apps-mrb-01/plane", name: "plane", guest: "apps-mrb-01", category: "docs", workflow: "compose-app@2", description: "Kanban and issues",
      urls: [url("https://kanban.testing.example.org/", { baseMs: 44, blips: 12 })],
      health: { url: "https://kanban.testing.example.org/", probed: true },
      dns: [cname("kanban.testing.example.org", "testing.example.org")] }),
    svc({ id: "apps-mrb-01/cliproxy-api", name: "cliproxy-api", guest: "apps-mrb-01", category: "ai", workflow: "compose-app@2", description: "CLI proxy API",
      urls: [url("https://cliproxy.mlab.example.org/", { baseMs: 27 })],
      health: { url: "https://cliproxy.mlab.example.org/", probed: true } }),
    svc({ id: "apps-mrb-01/arcane", name: "arcane", guest: "apps-mrb-01", category: "ops", workflow: "compose-app@2", description: "Docker manager (testing)", notes: ["No URL or health check declared."] }),
    svc({ id: "apps-mrb-01/renovate", name: "renovate", guest: "apps-mrb-01", category: "ops", workflow: "compose-job@1", runStatus: "pending", description: "Dependency update bot (scheduled job)", notes: ["Runs on a schedule; nothing to probe."] }),

    // mrb-crt-01 (hlab.example.org)
    svc({ id: "mrb-crt-01/traefik", name: "traefik", guest: "mrb-crt-01", category: "network", workflow: "traefik@2", description: "Reverse proxy for hlab.example.org",
      urls: [url("https://traefik.hlab.example.org/dashboard", { baseMs: 18 }), url("https://jenkins.hlab.example.org/", { baseMs: 32 })],
      dns: [{ type: "A", name: "hlab.example.org", expected: "10.20.0.246", zone: "hlab.example.org", status: "ok" }, cname("traefik.hlab.example.org", "hlab.example.org"), cname("jenkins.hlab.example.org", "hlab.example.org")] }),
    svc({ id: "mrb-crt-01/vaultwarden", name: "vaultwarden", guest: "mrb-crt-01", category: "security", workflow: "compose-app@2", description: "Password manager",
      urls: [url("https://vaultwarden.hlab.example.org/", { baseMs: 24, incidents: [{ ago: 21, min: 35 }] })],
      health: { url: "https://vaultwarden.hlab.example.org/alive", probed: true },
      dns: [cname("vaultwarden.hlab.example.org", "hlab.example.org")] }),
    svc({ id: "mrb-crt-01/infisical", name: "infisical", guest: "mrb-crt-01", category: "security", workflow: "compose-app@2", description: "Secrets manager",
      urls: [url("https://infisical.hlab.example.org/", { baseMs: 24 })],
      health: { url: "http://127.0.0.1:8083/api/status", probed: false },
      notes: ["Health check only answers on the guest; not probed from here."],
      dns: [cname("infisical.hlab.example.org", "hlab.example.org")] }),
    svc({ id: "mrb-crt-01/fleet", name: "fleet", guest: "mrb-crt-01", category: "ops", workflow: "compose-app@2", runStatus: "failed", description: "Device fleet manager",
      urls: [url("https://fleet.hlab.example.org/", { baseMs: 4, incidents: [{ ago: 3.4 }] }), url("https://fleet.dashboard.hlab.example.org/", { baseMs: 26 }), url("https://fleet.api.hlab.example.org/", { baseMs: 15 })],
      dns: [cname("fleet.hlab.example.org", "hlab.example.org"), cname("fleet.dashboard.hlab.example.org", "hlab.example.org"), cname("fleet.api.hlab.example.org", "hlab.example.org")] }),
    svc({ id: "mrb-crt-01/vessel", name: "vessel", guest: "mrb-crt-01", category: "ops", workflow: "compose-app@2", description: "Container registry UI",
      urls: [url("https://vessel.hlab.example.org/", { baseMs: 38, incidents: [{ ago: 24 * 6, min: 90 }] })],
      health: { url: "https://vessel.hlab.example.org/health/ready", probed: true },
      dns: [cname("vessel.hlab.example.org", "hlab.example.org")] }),
    svc({ id: "mrb-crt-01/arcane", name: "arcane", guest: "mrb-crt-01", category: "ops", workflow: "compose-app@2", description: "Docker manager",
      urls: [url("https://arcane.hlab.example.org/", { baseMs: 13 })],
      dns: [cname("arcane.hlab.example.org", "hlab.example.org")] }),
    svc({ id: "mrb-crt-01/homeassistant-databases", name: "homeassistant-databases", guest: "mrb-crt-01", category: "data", workflow: "compose-app@2", description: "InfluxDB and Postgres for Home Assistant",
      health: { url: "http://127.0.0.1:8086/health", probed: false },
      notes: ["Health check only answers on the guest; not probed from here."] }),
    svc({ id: "mrb-crt-01/watchtower", name: "watchtower", guest: "mrb-crt-01", category: "ops", workflow: "compose-app@2", description: "Container auto-updates",
      health: { url: "http://127.0.0.1:8080/health/ready", probed: false },
      notes: ["Health check only answers on the guest; not probed from here."] }),
    svc({ id: "mrb-crt-01/notifyme", name: "notifyme", guest: "mrb-crt-01", category: "ops", workflow: "compose-app@2", runStatus: "awaiting-approval", description: "Notification relay", notes: ["No URL or health check declared."] }),
  ];

  // A service's liveness rolls up its URL checks: all up = up, all down = down, mixed = warn.
  for (const s of services) {
    const checks = s.urls.map((u) => u.check);
    if (checks.length === 0) {
      s.status = "unknown";
      s.check = null;
      continue;
    }
    const down = checks.filter((c) => c.status === "down").length;
    s.status = down === 0 ? (checks.some((c) => c.status === "warn") ? "warn" : "up") : down === checks.length ? "down" : "warn";
    // Combined history: worst beat across URLs; uptime from the primary URL, minimum across URLs.
    const beats = checks[0].beats.map((_, i) => {
      const states = checks.map((c) => c.beats[i]);
      if (states.every((x) => x === "down")) return "down";
      return states.some((x) => x !== "up") ? "warn" : "up";
    });
    const min = (k) => Math.min(...checks.map((c) => c.uptime[k]));
    s.check = {
      status: s.status,
      since: Math.max(...checks.map((c) => c.since)),
      ms: checks[0].ms,
      uptime: { h24: min("h24"), d7: min("d7"), d30: min("d30") },
      beats,
      daily: checks[0].daily.map((_, d) => Math.min(...checks.map((c) => c.daily[d]))),
      latency: checks[0].latency,
      events: checks.flatMap((c) => c.events).sort((a, b) => b.at - a.at).slice(0, 12),
    };
  }

  // Guests and nodes: status from their own check.
  for (const x of [...nodes, ...guests]) x.status = x.check.status;

  // ---- Lookups and helpers -------------------------------------------------------------
  const byId = (arr) => Object.fromEntries(arr.map((x) => [x.id, x]));
  const nodeById = byId(nodes);
  const guestById = byId(guests);
  const guestsOf = (nodeId) => guests.filter((x) => x.node === nodeId);
  const servicesOf = (guestId) => services.filter((s) => s.guest === guestId);
  const servicesOfNode = (nodeId) => services.filter((s) => guestById[s.guest].node === nodeId);

  const fmt = {
    bytes(b) {
      if (b == null) return "—";
      const units = ["B", "KiB", "MiB", "GiB", "TiB"];
      let i = 0;
      while (b >= 1024 && i < units.length - 1) {
        b /= 1024;
        i++;
      }
      return `${b >= 100 || i === 0 ? Math.round(b) : b.toFixed(1)} ${units[i]}`;
    },
    pct(p, digits = 2) {
      if (p == null) return "—";
      if (p === 100) return "100%";
      return `${p.toFixed(digits)}%`;
    },
    duration(seconds) {
      if (seconds == null) return "—";
      if (seconds < 60) return `${Math.round(seconds)}s`;
      const m = Math.floor(seconds / 60);
      if (m < 60) return `${m}m`;
      const h = Math.floor(m / 60);
      if (h < 48) return `${h}h ${m % 60}m`;
      return `${Math.floor(h / 24)}d ${h % 24}h`;
    },
    ago(ts) {
      if (ts == null) return "never";
      const s = Math.max(0, (NOW - ts) / 1000);
      if (s < 90) return "just now";
      return `${fmt.duration(s)} ago`;
    },
    time(ts) {
      return new Date(ts).toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
    },
    ms(ms) {
      return ms == null ? "—" : `${ms} ms`;
    },
  };

  /** Liveness labels shared by all prototypes. */
  const liveness = {
    up: { label: "Up", tone: "success" },
    warn: { label: "Degraded", tone: "warning" },
    down: { label: "Down", tone: "danger" },
    unknown: { label: "No check", tone: "muted" },
  };
  /** Octopushy run status (does it match its definition). */
  const runLabels = {
    "in-sync": { label: "In sync", tone: "success" },
    pending: { label: "Pending", tone: "neutral" },
    failed: { label: "Failed", tone: "danger" },
    "awaiting-approval": { label: "Awaiting approval", tone: "attention" },
    uncertain: { label: "Uncertain", tone: "uncertain" },
    succeeded: { label: "Succeeded", tone: "success" },
  };

  /** Lower-cased text a search box matches against. */
  function haystack(kind, x) {
    if (kind === "node") return [x.name, x.address, x.proxmoxUrl, x.version, x.cpu.model].join(" ").toLowerCase();
    if (kind === "guest") return [x.id, x.hostname, x.address, x.vmid, x.node, x.workflow, x.description, ...x.tags].join(" ").toLowerCase();
    if (kind === "service") return [x.name, x.guest, x.workflow, x.category, x.description, ...x.urls.map((u) => u.url), ...x.dns.map((d) => d.name)].join(" ").toLowerCase();
    if (kind === "unmanaged") return [x.name, x.vmid, x.kind, x.status, ...(x.tags || [])].join(" ").toLowerCase();
    return "";
  }

  /** The Proxmox web UI link for a guest on its node. */
  const proxmoxGuestUrl = (guest) => {
    const n = nodeById[guest.node];
    return `${n.proxmoxUrl}#v1:0:=${guest.kind}%2F${guest.vmid}:4:::::::`;
  };

  /** Counts for headers and summary strips. */
  function totals() {
    const count = (arr, st) => arr.filter((x) => x.status === st).length;
    return {
      nodes: { total: nodes.length, up: count(nodes, "up"), down: count(nodes, "down") },
      guests: { total: guests.length, up: count(guests, "up"), down: count(guests, "down") },
      services: {
        total: services.length,
        up: count(services, "up"),
        warn: count(services, "warn"),
        down: count(services, "down"),
        unknown: count(services, "unknown"),
      },
      unmanaged: nodes.reduce((n, x) => n + x.unmanaged.length, 0),
    };
  }

  window.INV = {
    now: NOW,
    revision: { commit: "ecdd9da", subject: "Move fleet behind the hlab router" },
    observedAt: NOW - 42_000,
    intervalSeconds: 30,
    lab,
    nodes,
    guests,
    services,
    zones: [
      { name: "home.arpa", managed: false, resolvers: ["10.20.0.178", "10.20.0.179"] },
      { name: "hlab.example.org", managed: true, resolvers: ["10.20.0.178", "10.20.0.179"] },
      { name: "testing.example.org", managed: true, resolvers: ["10.20.0.178", "10.20.0.179"] },
    ],
    nodeById,
    guestById,
    guestsOf,
    servicesOf,
    servicesOfNode,
    proxmoxGuestUrl,
    haystack,
    totals,
    fmt,
    liveness,
    runLabels,
  };
})();
