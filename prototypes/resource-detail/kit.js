// Shared by every resource-detail prototype: sample data for one node, one machine and one
// service, the app's shell, and small helpers that return HTML strings.
//
// Everything is on window.RD. A prototype reads RD.res (the resource for ?kind=node|machine|service),
// calls RD.mount() for the shell and fills the <main> it returns. Nothing writes to the data.
//
// The data follows what the API returns today for an installation, a guest and a service
// (frontend/src/api/schema.d.ts), with the addresses and domains changed. Fields the API does
// not have yet are under a key named `sample`, so a design that leans on them says what it costs.
(function () {
  const NOW = Date.now();
  const MIN = 60_000, HOUR = 60 * MIN, DAY = 24 * HOUR, GiB = 1024 ** 3;
  const ago = (ms) => new Date(NOW - ms).toISOString();
  const ahead = (ms) => new Date(NOW + ms).toISOString();

  // ---- Small formatters --------------------------------------------------------------
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
  function rel(iso) {
    if (!iso) return "never";
    const d = new Date(iso).getTime() - NOW, a = Math.abs(d);
    const [n, u] = a < HOUR ? [Math.max(1, Math.round(a / MIN)), "min"] : a < 2 * DAY ? [Math.round(a / HOUR), "h"] : [Math.round(a / DAY), "d"];
    return d > 0 ? `in ${n} ${u}` : `${n} ${u} ago`;
  }
  const clock = (iso) => new Date(iso).toLocaleString("en-GB", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
  const day = (iso) => new Date(iso).toLocaleDateString("en-GB", { weekday: "short", day: "numeric", month: "short" });
  function bytes(n) {
    if (n == null) return "—";
    const g = n / GiB;
    return g >= 1024 ? `${(g / 1024).toFixed(1)} TiB` : g >= 10 || Number.isInteger(g) ? `${Math.round(g)} GiB` : `${g.toFixed(1)} GiB`;
  }
  function duration(sec) {
    if (sec == null) return "—";
    if (sec < 90) return `${Math.round(sec)} s`;
    if (sec < 5400) return `${Math.round(sec / 60)} min`;
    if (sec < 2 * 86400) return `${Math.round(sec / 3600)} h`;
    return `${Math.floor(sec / 86400)} d ${Math.round((sec % 86400) / 3600)} h`;
  }
  const pct = (f, digits = 0) => `${(f * 100).toFixed(digits)}%`;

  // ---- Icons: a subset of lucide, as the app uses ------------------------------------
  const ICONS = {
    overview: '<rect width="7" height="9" x="3" y="3" rx="1"/><rect width="7" height="5" x="14" y="3" rx="1"/><rect width="7" height="9" x="14" y="12" rx="1"/><rect width="7" height="5" x="3" y="16" rx="1"/>',
    server: '<rect width="20" height="8" x="2" y="2" rx="2" ry="2"/><rect width="20" height="8" x="2" y="14" rx="2" ry="2"/><line x1="6" x2="6.01" y1="6" y2="6"/><line x1="6" x2="6.01" y1="18" y2="18"/>',
    globe: '<circle cx="12" cy="12" r="10"/><path d="M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20"/><path d="M2 12h20"/>',
    rocket: '<path d="M4.5 16.5c-1.5 1.26-2 5-2 5s3.74-.5 5-2c.71-.84.7-2.13-.09-2.91a2.18 2.18 0 0 0-2.91-.09z"/><path d="m12 15-3-3a22 22 0 0 1 2-3.95A12.88 12.88 0 0 1 22 2c0 2.72-.78 7.5-6 11a22.35 22.35 0 0 1-4 2z"/><path d="M9 12H4s.55-3.03 2-4c1.62-1.08 5 0 5 0"/><path d="M12 15v5s3.03-.55 4-2c1.08-1.62 0-5 0-5"/>',
    runs: '<circle cx="12" cy="12" r="10"/><polygon points="10 8 16 12 10 16 10 8"/>',
    key: '<path d="M2.586 17.414A2 2 0 0 0 2 18.828V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.172a2 2 0 0 0 1.414-.586l.814-.814a6.5 6.5 0 1 0-4-4z"/><circle cx="16.5" cy="7.5" r=".5" fill="currentColor"/>',
    branch: '<line x1="6" x2="6" y1="3" y2="15"/><circle cx="18" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><path d="M18 9a9 9 0 0 1-9 9"/>',
    commit: '<circle cx="12" cy="12" r="3"/><line x1="3" x2="9" y1="12" y2="12"/><line x1="15" x2="21" y1="12" y2="12"/>',
    filePen: '<path d="M12.5 22H18a2 2 0 0 0 2-2V7l-5-5H6a2 2 0 0 0-2 2v9.5"/><path d="M14 2v4a2 2 0 0 0 2 2h4"/><path d="M13.378 15.626a1 1 0 1 0-3.004-3.004l-5.01 5.012a2 2 0 0 0-.506.854l-.837 2.87a.5.5 0 0 0 .62.62l2.87-.837a2 2 0 0 0 .854-.506z"/>',
    file: '<path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z"/><path d="M14 2v4a2 2 0 0 0 2 2h4"/><path d="M10 9H8"/><path d="M16 13H8"/><path d="M16 17H8"/>',
    bot: '<path d="M12 8V4H8"/><rect width="16" height="12" x="4" y="8" rx="2"/><path d="M2 14h2"/><path d="M20 14h2"/><path d="M15 13v2"/><path d="M9 13v2"/>',
    user: '<path d="M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/>',
    play: '<polygon points="6 3 20 12 6 21 6 3"/>',
    pencil: '<path d="M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z"/><path d="m15 5 4 4"/>',
    trash: '<path d="M3 6h18"/><path d="M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6"/><path d="M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2"/><line x1="10" x2="10" y1="11" y2="17"/><line x1="14" x2="14" y1="11" y2="17"/>',
    copy: '<rect width="14" height="14" x="8" y="8" rx="2" ry="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/>',
    check: '<path d="M20 6 9 17l-5-5"/>',
    circleCheck: '<circle cx="12" cy="12" r="10"/><path d="m9 12 2 2 4-4"/>',
    circleX: '<circle cx="12" cy="12" r="10"/><path d="m15 9-6 6"/><path d="m9 9 6 6"/>',
    circleDot: '<circle cx="12" cy="12" r="10"/><circle cx="12" cy="12" r="1"/>',
    hand: '<circle cx="12" cy="12" r="10"/><line x1="10" x2="10" y1="15" y2="9"/><line x1="14" x2="14" y1="15" y2="9"/>',
    x: '<path d="M18 6 6 18"/><path d="m6 6 12 12"/>',
    chevronRight: '<path d="m9 18 6-6-6-6"/>',
    chevronDown: '<path d="m6 9 6 6 6-6"/>',
    chevronsUpDown: '<path d="m7 15 5 5 5-5"/><path d="m7 9 5-5 5 5"/>',
    arrowRight: '<path d="M5 12h14"/><path d="m12 5 7 7-7 7"/>',
    external: '<path d="M15 3h6v6"/><path d="M10 14 21 3"/><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>',
    eye: '<path d="M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0"/><circle cx="12" cy="12" r="3"/>',
    cpu: '<rect width="16" height="16" x="4" y="4" rx="2"/><rect width="6" height="6" x="9" y="9" rx="1"/><path d="M15 2v2"/><path d="M15 20v2"/><path d="M2 15h2"/><path d="M2 9h2"/><path d="M20 15h2"/><path d="M20 9h2"/><path d="M9 2v2"/><path d="M9 20v2"/>',
    memory: '<path d="M6 19v-3"/><path d="M10 19v-3"/><path d="M14 19v-3"/><path d="M18 19v-3"/><path d="M8 11V9"/><path d="M16 11V9"/><path d="M12 11V9"/><path d="M2 15h20"/><path d="M2 7a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v1.1a2 2 0 0 0 0 3.837V17a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-5.1a2 2 0 0 0 0-3.837Z"/>',
    disk: '<line x1="22" x2="2" y1="12" y2="12"/><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/><line x1="6" x2="6.01" y1="16" y2="16"/><line x1="10" x2="10.01" y1="16" y2="16"/>',
    network: '<rect x="16" y="16" width="6" height="6" rx="1"/><rect x="2" y="16" width="6" height="6" rx="1"/><rect x="9" y="2" width="6" height="6" rx="1"/><path d="M5 16v-3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v3"/><path d="M12 12V8"/>',
    shield: '<path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/><path d="m9 12 2 2 4-4"/>',
    clock: '<circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/>',
    calendar: '<path d="M8 2v4"/><path d="M16 2v4"/><rect width="18" height="18" x="3" y="4" rx="2"/><path d="M3 10h18"/>',
    history: '<path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5"/><path d="M12 7v5l4 2"/>',
    terminal: '<polyline points="4 17 10 11 4 5"/><line x1="12" x2="20" y1="19" y2="19"/>',
    code: '<polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/>',
    wrench: '<path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/>',
    alert: '<path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3"/><path d="M12 9v4"/><path d="M12 17h.01"/>',
    info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4"/><path d="M12 8h.01"/>',
    lock: '<rect width="18" height="11" x="3" y="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/>',
    box: '<path d="M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z"/><path d="m3.3 7 8.7 5 8.7-5"/><path d="M12 22V12"/>',
    monitor: '<rect width="20" height="14" x="2" y="3" rx="2"/><line x1="8" x2="16" y1="21" y2="21"/><line x1="12" x2="12" y1="17" y2="21"/>',
    layers: '<path d="M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z"/><path d="M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12"/><path d="M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17"/>',
    activity: '<path d="M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2"/>',
    refresh: '<path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"/><path d="M21 3v5h-5"/><path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16"/><path d="M8 16H3v5"/>',
    link: '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>',
    power: '<path d="M12 2v10"/><path d="M18.4 6.6a9 9 0 1 1-12.77.04"/>',
    search: '<circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/>',
    more: '<circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/><circle cx="5" cy="12" r="1"/>',
    tag: '<path d="M12.586 2.586A2 2 0 0 0 11.172 2H4a2 2 0 0 0-2 2v7.172a2 2 0 0 0 .586 1.414l8.704 8.704a2.426 2.426 0 0 0 3.42 0l6.58-6.58a2.426 2.426 0 0 0 0-3.42z"/><circle cx="7.5" cy="7.5" r=".5" fill="currentColor"/>',
    workflow: '<rect width="8" height="8" x="3" y="3" rx="2"/><path d="M7 11v4a2 2 0 0 0 2 2h4"/><rect width="8" height="8" x="13" y="13" rx="2"/>',
  };
  const icon = (name, size = 16) =>
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[name] ?? ICONS.circleDot}</svg>`;

  // ---- Status vocabulary: the app's statuses, their label, tone and icon ---------------
  const STATUS = {
    "in-sync": ["In sync", "success", "circleCheck"],
    succeeded: ["Succeeded", "success", "circleCheck"],
    up: ["Up", "success", "circleCheck"],
    running: ["Running", "info", "refresh"],
    "awaiting-approval": ["Waiting for approval", "attention", "hand"],
    pending: ["Pending", "neutral", "clock"],
    "out-of-date": ["Out of date", "warning", "alert"],
    warning: ["Warning", "warning", "alert"],
    degraded: ["Degraded", "warning", "alert"],
    failed: ["Failed", "danger", "circleX"],
    down: ["Down", "danger", "circleX"],
    uncertain: ["Uncertain", "uncertain", "alert"],
    cancelled: ["Cancelled", "muted", "x"],
    stopped: ["Stopped", "muted", "power"],
    unmanaged: ["Unmanaged", "neutral", "circleDot"],
    template: ["Template", "neutral", "copy"],
  };
  const tone = (status) => (STATUS[status] ?? [status, "neutral"])[1];
  const statusLabel = (status) => (STATUS[status] ?? [status])[0];
  const badge = (label, t = "neutral", ic) => `<span class="badge" data-tone="${t}">${ic ? icon(ic, 13) : ""}${esc(label)}</span>`;
  const status = (s) => badge(statusLabel(s), tone(s), (STATUS[s] ?? [])[2]);
  const chip = (text, { mono = false, tone: t } = {}) => `<span class="chip${mono ? " chip--mono" : ""}"${t ? ` data-tone="${t}"` : ""}>${esc(text)}</span>`;
  const commit = (sha) => (sha ? `<span class="chip chip--mono">${esc(sha.slice(0, 7))}</span>` : `<span class="muted">—</span>`);

  // ---- Uptime of one check: 96 beats of 15 min, 30 days, latency ------------------------
  function hash(s) { let h = 2166136261; for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619); return h >>> 0; }
  function rng(seed) { let a = hash(seed); return () => { a = (a + 0x6d2b79f5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
  function check(key, kind, url, { baseMs = 20, incidents = [], agent = "agent-moraband" } = {}) {
    const r = rng(key);
    const beats = new Array(96).fill("up");
    const daily = new Array(30).fill(100);
    for (const inc of incidents) {
      const dayIndex = 29 - Math.floor(inc.hoursAgo / 24);
      if (dayIndex >= 0) daily[dayIndex] = Math.round((1 - inc.minutes / 1440) * 1000) / 10;
      if (inc.hoursAgo < 24) for (let i = 0; i < Math.max(1, Math.round(inc.minutes / 15)); i++) beats[Math.min(95, 95 - Math.round(inc.hoursAgo * 4) + i)] = inc.state ?? "down";
    }
    const latency = beats.map((b) => (b === "down" ? null : Math.round(baseMs * (0.75 + r() * 0.5))));
    // A slow answer is still an answer: only a "down" incident counts against the percentage.
    const outages = incidents.filter((i) => (i.state ?? "down") === "down");
    const down = outages.reduce((m, i) => m + i.minutes, 0);
    const within = (h) => outages.filter((i) => i.hoursAgo < h).reduce((m, i) => m + i.minutes, 0);
    const p = (mins, total) => Math.round((1 - mins / total) * 100000) / 1000;
    return {
      key, kind, url, status: "up", detail: null, agent,
      since: ago((incidents[0]?.hoursAgo ?? 30 * 24) * HOUR), checkedAt: ago(40_000), elapsedMs: latency[95],
      uptime: { day: p(within(24), 1440), week: p(within(168), 10080), month: p(down, 43200) },
      beats, latency, daily,
      incidents: incidents.map((i) => ({ at: ago(i.hoursAgo * HOUR), minutes: i.minutes, state: i.state ?? "down", detail: i.detail })),
    };
  }

  // ---- Runs -------------------------------------------------------------------------
  let runId = 0;
  function run(number, title, operation, st, hoursAgo, reason, extra = {}) {
    const names = extra.stepNames ?? (operation === "provision" ? ["plan", "approval", "apply", "verify"] : operation === "deploy" ? ["preview", "approval", "deploy", "dns", "verify"] : ["preview", "run", "verify"]);
    const stop = st === "awaiting-approval" ? names.indexOf("approval") : st === "failed" ? (extra.failedAt ? names.indexOf(extra.failedAt) : names.length - 2) : st === "running" ? names.length - 2 : names.length;
    return {
      id: `run-${++runId}`, number, title, operation, status: st, reason, address: extra.address,
      detail: extra.detail ?? null, commit: extra.commit ?? null, by: extra.by ?? null,
      createdAt: ago(hoursAgo * HOUR), finishedAt: ["awaiting-approval", "running"].includes(st) ? null : ago(hoursAgo * HOUR - (extra.seconds ?? 95) * 1000),
      seconds: ["awaiting-approval", "running"].includes(st) ? null : (extra.seconds ?? 95),
      steps: names.map((name, i) => ({ name, status: i < stop ? "succeeded" : i === stop ? (st === "succeeded" ? "succeeded" : st) : "todo" })),
    };
  }

  // ---- Definitions: the YAML, and the same thing as sections of fields --------------------
  // A field's `line` is its 1-based line in the YAML, so a design can link a value to its source.
  function definition(file, text, sections) {
    const lines = text.split("\n");
    const lineOf = (needle, from = 0) => { const i = lines.findIndex((l, n) => n >= from && l.includes(needle)); return i < 0 ? null : i + 1; };
    for (const section of sections) {
      // A section is a top-level key, so it starts its line; "snippets:" also appears inside a host's datastores.
      const head = section.find ?? `${section.id}:`;
      const top = lines.findIndex((l) => l.startsWith(head));
      section.line = top < 0 ? lineOf(head) : top + 1;
      let from = (section.line ?? 1) - 1;
      for (const f of section.fields) { f.line = lineOf(f.find ?? `${f.key}:`, from) ?? section.line; delete f.find; }
      const last = Math.max(section.line ?? 1, ...section.fields.map((f) => f.line ?? 1));
      section.lines = [section.line ?? 1, last];
      delete section.find;
    }
    return { file, text, lineCount: lines.length, sections };
  }
  const secretRef = (key, version) => ({ key, activeVersion: version });

  const machineYaml = `schemaVersion: 1
type: vm
lifecycle: active
host: moraband
vmid: 410
template: fedora-44
workflow: fedora-vm@1
hardware:
  cores: 4
  memory: 8192                            # MiB
  disks:
    - { size: 300 }                       # GiB; the system disk, on moraband's local-lvm
network:
  # falcon's fixed address on moraband's vmbr1, outside the DHCP pool. It must be in
  # OCTOPUSHY_TARGETS, or no agent reaches it.
  bridge: vmbr1
  mode: static
  address: 10.1.0.99/24
  gateway: 10.1.0.1
  dns: [10.1.0.178, 10.1.0.179]
inputs:
  # A machine for developing with AI tools, and the first real test of Octopushy 0.5.0.
  machine_profile: ai-node
  machine_features: [tailscale]
  machine_project_categories: [infra, apps, tools, labs]
  machine_fleet_enrollment_link: https://fleet.hlab.example.org/enroll#fleet-v1=eyJ2ZXJzaW9uIjoxLCJ0ZWFtIjoiaGxhYiJ9
secrets:
  machine_tailscale_auth_key: { store: proxmox/moraband/falcon/machine_tailscale_auth_key }
  machine_github_token: { store: shared/github/machines-token }
access:
  address: 10.1.0.99
  user: ops
schedules:
  - { action: update-tools, cron: "0 4 * * *", timezone: Europe/Madrid }
  - { action: update-system, cron: "0 5 * * 0", timezone: Europe/Madrid }
policy:
  approval: manual
  destructive: manual
  actions:
    update-tools: { approval: automatic }`;

  const serviceYaml = `schemaVersion: 1
workflow: compose@1
description: Chat interface for the local models on falcon.
links:
  - https://chat.hlab.example.org/
inputs:
  compose_project: open-webui
  image: ghcr.io/open-webui/open-webui:0.6.34
  port: 3000
  dns_name: chat.hlab.example.org         # the dns stage sets this record, pointing at traefik
  env:
    OLLAMA_BASE_URL: http://ollama:11434
    ENABLE_SIGNUP: "false"
    DEFAULT_MODELS: qwen3:14b
secretFiles:
  env_file: { store: proxmox/moraband/falcon/open-webui/env }
secrets:
  webui_secret_key: { store: proxmox/moraband/falcon/open-webui/secret_key }
policy:
  approval: manual                        # a deploy waits for approval`;

  const nodeYaml = `schemaVersion: 1
type: standalone
connection:
  endpoint: https://10.1.0.50:8006/
  tls: { verify: false }
  readOnlyToken: { store: proxmox/moraband/read-only-token }
  privilegedToken: { store: proxmox/moraband/privileged-token }
hosts:
  moraband:
    datastores: { images: local, disks: local-lvm, snippets: local }
    bridges: [vmbr0, vmbr1]
    limits: { concurrentOperations: 1 }
snippets:
  user: octopushy                         # uploads cloud-init snippets over SSH
policy:
  approval: manual                        # every change on this installation waits for approval`;

  // ---- The machine: home/moraband/falcon ------------------------------------------------
  const falconServices = [
    { id: "open-webui", name: "open-webui", address: "home/moraband/falcon/open-webui", status: "awaiting-approval", statusDetail: "Deploy #214 waits for approval", workflow: "compose@1", links: ["https://chat.hlab.example.org/"], uptimeDay: 100, kind: "service" },
    { id: "ollama", name: "ollama", address: "home/moraband/falcon/ollama", status: "in-sync", statusDetail: null, workflow: "compose@1", links: ["tcp://10.1.0.99:11434"], uptimeDay: 100 },
    { id: "litellm", name: "litellm", address: "home/moraband/falcon/litellm", status: "in-sync", statusDetail: null, workflow: "compose@1", links: ["https://llm.hlab.example.org/"], uptimeDay: 99.3 },
    { id: "langfuse", name: "langfuse", address: "home/moraband/falcon/langfuse", status: "failed", statusDetail: "Deploy #209 failed at verify", workflow: "compose@1", links: ["https://traces.hlab.example.org/"], uptimeDay: 0 },
  ];

  const machine = {
    kind: "machine", noun: "VM", id: "falcon", name: "falcon", address: "home/moraband/falcon",
    crumbs: [{ label: "Inventory" }, { label: "home/moraband", kind: "node" }, { label: "falcon" }],
    description: "A machine for developing with AI tools, and the first real test of Octopushy 0.5.0.",
    status: "in-sync", statusDetail: null, lifecycle: "active", type: "vm", host: "moraband", vmid: 410, template: "fedora-44",
    accessAddress: "10.1.0.99",
    workflow: { reference: "fedora-vm@1", resolved: "1.10", latest: "1.10", manages: "vm",
      stages: [{ name: "provision", executor: "opentofu", detail: "modules/opentofu/proxmox-vm" }, { name: "setup", executor: "ansible", detail: "modules/ansible/machine-tools" }] },
    observed: { vmid: 410, name: "falcon", host: "moraband", status: "running", kind: "qemu", template: false, tags: ["octopushy", "ai"], maxCpu: 4, maxMemoryBytes: 8 * GiB, uptimeSeconds: 12 * 86400 + 4 * 3600 },
    // Not in the API today: Proxmox reports them, Octopushy does not store them.
    sample: { cpu: 0.12, memoryBytes: 5.1 * GiB, diskBytes: 118 * GiB, maxDiskBytes: 300 * GiB },
    definition: definition("labs/home/proxmox/moraband/guests/falcon/guest.yaml", machineYaml, [
      { id: "placement", title: "Placement", icon: "server", find: "type:", fields: [
        { key: "type", label: "Type", value: "VM" },
        { key: "host", label: "Host", value: "moraband", link: "node" },
        { key: "vmid", label: "VMID", value: "410", type: "number" },
        { key: "template", label: "Template", value: "fedora-44" },
        { key: "lifecycle", label: "Lifecycle", value: "active" },
      ] },
      { id: "hardware", title: "Hardware", icon: "cpu", fields: [
        { key: "cores", label: "Cores", value: "4", type: "number" },
        { key: "memory", label: "Memory", value: "8 GiB", raw: "8192 MiB", type: "number" },
        { key: "disks", label: "Disk", value: "300 GiB", note: "The system disk, on moraband's local-lvm", find: "size: 300", type: "number" },
      ] },
      { id: "network", title: "Network", icon: "network", note: "falcon's fixed address on moraband's vmbr1, outside the DHCP pool. It must be in OCTOPUSHY_TARGETS, or no agent reaches it.", fields: [
        { key: "bridge", label: "Bridge", value: "vmbr1" },
        { key: "mode", label: "Mode", value: "static" },
        { key: "address", label: "Address", value: "10.1.0.99/24" },
        { key: "gateway", label: "Gateway", value: "10.1.0.1" },
        { key: "dns", label: "DNS", value: "10.1.0.178, 10.1.0.179", type: "list", items: ["10.1.0.178", "10.1.0.179"] },
      ] },
      { id: "inputs", title: "Inputs", icon: "wrench", note: "A machine for developing with AI tools, and the first real test of Octopushy 0.5.0.", fields: [
        { key: "machine_profile", label: "Machine profile", value: "ai-node" },
        { key: "machine_features", label: "Features", value: "tailscale", type: "list", items: ["tailscale"] },
        { key: "machine_project_categories", label: "Project categories", value: "infra, apps, tools, labs", type: "list", items: ["infra", "apps", "tools", "labs"] },
        { key: "machine_fleet_enrollment_link", label: "Fleet enrollment link", value: "https://fleet.hlab.example.org/enroll#fleet-v1=eyJ2ZXJzaW9uIjoxLCJ0ZWFtIjoiaGxhYiJ9", type: "url" },
      ] },
      { id: "secrets", title: "Secrets", icon: "key", fields: [
        { key: "machine_tailscale_auth_key", label: "Tailscale auth key", value: "proxmox/moraband/falcon/machine_tailscale_auth_key", type: "secret", secret: secretRef("proxmox/moraband/falcon/machine_tailscale_auth_key", 3) },
        { key: "machine_github_token", label: "GitHub token", value: "shared/github/machines-token", type: "secret", secret: secretRef("shared/github/machines-token", 1) },
      ] },
      { id: "access", title: "Access", icon: "terminal", fields: [
        { key: "address", label: "SSH address", value: "10.1.0.99" },
        { key: "user", label: "User", value: "ops" },
      ] },
      { id: "schedules", title: "Schedules", icon: "calendar", fields: [
        { key: "update-tools", label: "update-tools", value: "Every day at 04:00", raw: "0 4 * * * Europe/Madrid", find: "action: update-tools" },
        { key: "update-system", label: "update-system", value: "Sundays at 05:00", raw: "0 5 * * 0 Europe/Madrid", find: "action: update-system" },
      ] },
      { id: "policy", title: "Policy", icon: "shield", fields: [
        { key: "approval", label: "Approval", value: "manual" },
        { key: "destructive", label: "Destructive", value: "manual" },
        { key: "update-tools", label: "Approval of update-tools", value: "automatic", find: "update-tools: { approval" },
      ] },
    ]),
    policy: { approval: "manual", approvalSetBy: "home/moraband/falcon", destructive: "manual", destructiveSetBy: "home/moraband/falcon",
      actions: [{ action: "update-tools", approval: "automatic", setBy: "home/moraband/falcon, action update-tools" }] },
    agentLabels: [], agentLabelsSetBy: "default",
    configuration: {
      workflow: "fedora-vm@1.10", inSync: true, appliedAction: "update-tools", appliedCommit: "895ec0a41c2", appliedAt: ago(6 * HOUR), hasSnapshot: true,
      actions: [
        { id: "setup", description: "Apply the machine profile, features and tools", destructive: false, onChange: true },
        { id: "update-tools", description: "Update the managed AI tools to their pinned policy", destructive: false, onChange: false },
        { id: "update-system", description: "Upgrade operating-system packages; restarts if the kernel changed", destructive: false, onChange: false },
        { id: "reset-workspace", description: "Delete and recreate the projects workspace", destructive: true, onChange: false },
      ],
      schedules: [
        { action: "update-tools", cron: "0 4 * * *", timezone: "Europe/Madrid", human: "Every day at 04:00", nextAt: ahead(18 * HOUR), lastRunNumber: 212, lastRunStatus: "succeeded", lastRunDetail: "3 tools updated" },
        { action: "update-system", cron: "0 5 * * 0", timezone: "Europe/Madrid", human: "Sundays at 05:00", nextAt: ahead(2 * DAY + 19 * HOUR), lastRunNumber: 190, lastRunStatus: "succeeded", lastRunDetail: "41 packages, restarted" },
      ],
      secrets: [
        { key: "proxmox/moraband/falcon/machine_tailscale_auth_key", activeVersion: 3, startedVersion: 3, pending: false },
        { key: "shared/github/machines-token", activeVersion: 1, startedVersion: 1, pending: false },
      ],
    },
    managedAccess: { fingerprint: "SHA256:u1Yq0c7mW3nQ8sVbT4hZ2pLx9KfJ6dRgE5aNoCyHtI0", version: 2, provisionedAt: ago(21 * DAY), lastUsedAt: ago(6 * HOUR),
      keys: [{ version: 2, state: "active", promotedAt: ago(9 * DAY), promotedBy: "skorcius" }, { version: 1, state: "removed", removedAt: ago(9 * DAY) }] },
    hostKey: { pinned: { version: 1, fingerprint: "SHA256:Qm3vX8tLr2bN7kYc1ZpW5sHdF0gJ9uAeI6oTnVyB4xE", createdAt: ago(23 * DAY), deliveredAt: ago(23 * DAY) }, next: null },
    binding: { installation: "home/moraband", vmid: 410, boundAt: ago(23 * DAY) },
    revisions: { desiredCommit: "895ec0a41c2", checkedCommit: "895ec0a41c2", checkedAt: ago(6 * HOUR), appliedCommit: "3c1f9d27be0", appliedAt: ago(9 * DAY), verifiedAt: ago(6 * HOUR), inSync: true },
    outputs: { ipv4: "10.1.0.99", mac: "BC:24:11:5E:9A:10", node: "moraband", vmid: 410, disks: { scsi0: "local-lvm:vm-410-disk-0" } },
    checks: [check("guest-ssh:falcon", "guest-ssh", "ssh://10.1.0.99:22", { baseMs: 3, incidents: [{ hoursAgo: 9 * 24 - 0.05, minutes: 4, detail: "Restarted by apply #171" }] })],
    services: falconServices,
    issues: [],
    changes: [
      { id: "c-31", message: "Memory 4096 → 8192 MiB", author: "skorcius", commit: "3c1f9d27be0", status: "applied", createdAt: ago(9 * DAY), runs: [171] },
      { id: "c-27", message: "Add the tailscale feature", author: "skorcius", commit: "a41b7e09d55", status: "applied", createdAt: ago(14 * DAY), runs: [156] },
    ],
    runs: [
      run(212, "update-tools", "configure", "succeeded", 6, "Schedule", { commit: "895ec0a41c2", detail: "3 tools updated", seconds: 142 }),
      run(205, "update-tools", "configure", "succeeded", 30, "Schedule", { commit: "895ec0a41c2", detail: "Nothing to update", seconds: 61 }),
      run(198, "update-tools", "configure", "succeeded", 54, "Schedule", { commit: "7d20c9e1a3f", detail: "1 tool updated", seconds: 88 }),
      run(190, "update-system", "configure", "succeeded", 4 * 24 + 7, "Schedule", { commit: "7d20c9e1a3f", detail: "41 packages, restarted", seconds: 540 }),
      run(171, "Apply", "provision", "succeeded", 9 * 24, "Change by skorcius", { commit: "3c1f9d27be0", by: "skorcius", detail: "memory 4096 → 8192", seconds: 204 }),
      run(156, "setup", "configure", "succeeded", 14 * 24, "Change by skorcius", { commit: "a41b7e09d55", by: "skorcius", detail: "tailscale enabled", seconds: 311 }),
      run(149, "update-tools", "configure", "failed", 15 * 24, "Schedule", { commit: "5be8830c0d1", detail: "npm registry timed out", seconds: 600 }),
      run(120, "Apply", "provision", "succeeded", 23 * 24, "Commit 61f0a2c", { commit: "61f0a2c7e44", by: "skorcius", detail: "Created VM 410", seconds: 388 }),
    ],
    actions: [
      { id: "edit", label: "Edit", icon: "pencil" },
      { id: "remove", label: "Remove", icon: "trash", danger: true },
      { id: "plan", label: "Plan now", icon: "play", primary: true },
    ],
    siblings: [
      { name: "falcon", kind: "VM", status: "in-sync", current: true }, { name: "mrb-app-01", kind: "VM", status: "in-sync" },
      { name: "mrb-crt-01", kind: "VM", status: "awaiting-approval" }, { name: "mrb-dns-01", kind: "Container", status: "in-sync" }, { name: "gocd-mrb-01", kind: "VM", status: "failed" },
    ],
  };
  machine.events = [
    { at: ahead(18 * HOUR), type: "schedule", tone: "neutral", title: "update-tools is scheduled", detail: "Every day at 04:00" },
    { at: ago(6 * HOUR), type: "run", tone: "success", title: "update-tools succeeded", detail: "3 tools updated · schedule", run: 212 },
    { at: ago(30 * HOUR), type: "run", tone: "success", title: "update-tools succeeded", detail: "Nothing to update · schedule", run: 205 },
    { at: ago(54 * HOUR), type: "run", tone: "success", title: "update-tools succeeded", detail: "1 tool updated · schedule", run: 198 },
    { at: ago(4 * DAY + 7 * HOUR), type: "run", tone: "success", title: "update-system succeeded", detail: "41 packages, restarted · schedule", run: 190 },
    { at: ago(9 * DAY - 2 * MIN), type: "uptime", tone: "warning", title: "SSH did not answer for 4 min", detail: "Restarted by apply #171" },
    { at: ago(9 * DAY), type: "run", tone: "success", title: "Apply succeeded", detail: "memory 4096 → 8192 · approved by skorcius", run: 171 },
    { at: ago(9 * DAY + 20 * MIN), type: "change", tone: "info", title: "Memory 4096 → 8192 MiB", detail: "Change by skorcius · 3c1f9d2" },
    { at: ago(9 * DAY + 30 * MIN), type: "key", tone: "neutral", title: "Managed SSH key rotated to v2", detail: "Promoted by skorcius" },
    { at: ago(14 * DAY), type: "run", tone: "success", title: "setup succeeded", detail: "tailscale enabled · change by skorcius", run: 156 },
    { at: ago(14 * DAY + 10 * MIN), type: "secret", tone: "neutral", title: "machine_tailscale_auth_key is at v3", detail: "Saved by skorcius" },
    { at: ago(15 * DAY), type: "run", tone: "danger", title: "update-tools failed", detail: "npm registry timed out · schedule", run: 149 },
    { at: ago(23 * DAY), type: "run", tone: "success", title: "Apply succeeded", detail: "Created VM 410 · bound to home/moraband", run: 120 },
  ];

  // ---- The service: home/moraband/falcon/open-webui ---------------------------------------
  const service = {
    kind: "service", noun: "Service", id: "open-webui", name: "open-webui", address: "home/moraband/falcon/open-webui",
    crumbs: [{ label: "Inventory" }, { label: "home/moraband", kind: "node" }, { label: "falcon", kind: "machine" }, { label: "open-webui" }],
    description: "Chat interface for the local models on falcon.",
    status: "awaiting-approval", statusDetail: "Deploy #214 waits for approval: image 0.6.30 → 0.6.34",
    guest: { name: "falcon", address: "home/moraband/falcon", accessAddress: "10.1.0.99" },
    icon: { letter: "O", source: "letter" },
    links: ["https://chat.hlab.example.org/"],
    names: [{ name: "chat.hlab.example.org", stage: "dns", url: "https://chat.hlab.example.org/" }],
    workflow: { reference: "compose@1", resolved: "1.4", latest: "1.5", manages: "service",
      stages: [
        { name: "deploy", executor: "ansible", detail: "Copies the compose project and its env file, then brings it up" },
        { name: "dns", executor: "opentofu", detail: "Sets chat.hlab.example.org, pointing at traefik" },
        { name: "verify", executor: "ansible", detail: "Waits until the containers are healthy" },
      ] },
    files: ["service.yaml", "compose.yaml"],
    definition: definition("labs/home/proxmox/moraband/guests/falcon/services/open-webui/service.yaml", serviceYaml, [
      { id: "service", title: "Service", icon: "box", find: "workflow:", fields: [
        { key: "description", label: "Description", value: "Chat interface for the local models on falcon." },
        { key: "links", label: "Link", value: "https://chat.hlab.example.org/", type: "url", find: "- https://chat" },
      ] },
      { id: "inputs", title: "Inputs", icon: "wrench", fields: [
        { key: "compose_project", label: "Compose project", value: "open-webui" },
        { key: "image", label: "Image", value: "ghcr.io/open-webui/open-webui:0.6.34", pending: "0.6.30 → 0.6.34" },
        { key: "port", label: "Port", value: "3000", type: "number" },
        { key: "dns_name", label: "DNS name", value: "chat.hlab.example.org", note: "The dns stage sets this record, pointing at traefik" },
        { key: "OLLAMA_BASE_URL", label: "env · OLLAMA_BASE_URL", value: "http://ollama:11434" },
        { key: "ENABLE_SIGNUP", label: "env · ENABLE_SIGNUP", value: "false" },
        { key: "DEFAULT_MODELS", label: "env · DEFAULT_MODELS", value: "qwen3:14b" },
      ] },
      { id: "secrets", title: "Secrets", icon: "key", find: "secretFiles:", fields: [
        { key: "env_file", label: "Env file", value: "proxmox/moraband/falcon/open-webui/env", type: "secret", secret: secretRef("proxmox/moraband/falcon/open-webui/env", 4), note: "Delivered as a file" },
        { key: "webui_secret_key", label: "Secret key", value: "proxmox/moraband/falcon/open-webui/secret_key", type: "secret", secret: secretRef("proxmox/moraband/falcon/open-webui/secret_key", 1) },
      ] },
      { id: "policy", title: "Policy", icon: "shield", fields: [
        { key: "approval", label: "Approval", value: "manual", note: "A deploy waits for approval" },
      ] },
    ]),
    policy: { approval: "manual", approvalSetBy: "home/moraband/falcon/open-webui", destructive: "manual", destructiveSetBy: "home/moraband/falcon", actions: [] },
    agentLabels: [], agentLabelsSetBy: "default",
    appliedCommit: "7d20c9e1a3f", appliedAt: ago(2 * DAY + 3 * HOUR),
    pending: { summary: "image 0.6.30 → 0.6.34", commit: "895ec0a41c2", author: "skorcius", run: 214, since: ago(38 * MIN) },
    checks: [check("service-http:open-webui", "service-http", "https://chat.hlab.example.org/", { baseMs: 42, incidents: [{ hoursAgo: 2 * 24 + 3 - 0.05, minutes: 6, detail: "Restarted by deploy #201" }, { hoursAgo: 11, minutes: 30, state: "warn", detail: "Slow answers, over 2 s" }] })],
    issues: [],
    changes: [
      { id: "c-34", message: "Image 0.6.30 → 0.6.34", author: "skorcius", commit: "895ec0a41c2", status: "awaiting-approval", createdAt: ago(40 * MIN), runs: [214] },
      { id: "c-29", message: "Default model qwen3:14b", author: "skorcius", commit: "7d20c9e1a3f", status: "applied", createdAt: ago(2 * DAY + 3 * HOUR), runs: [201] },
    ],
    runs: [
      run(214, "Deploy", "deploy", "awaiting-approval", 0.63, "Change by skorcius", { commit: "895ec0a41c2", by: "skorcius", detail: "image 0.6.30 → 0.6.34" }),
      run(201, "Deploy", "deploy", "succeeded", 2 * 24 + 3, "Change by skorcius", { commit: "7d20c9e1a3f", by: "skorcius", detail: "DEFAULT_MODELS changed", seconds: 96 }),
      run(188, "Deploy", "deploy", "succeeded", 5 * 24, "Commit 5be8830", { commit: "5be8830c0d1", detail: "image 0.6.28 → 0.6.30", seconds: 131 }),
      run(177, "Deploy", "deploy", "failed", 8 * 24, "Commit 2aa91f3", { commit: "2aa91f3b6c8", detail: "verify: container unhealthy after 120 s", seconds: 214, failedAt: "verify" }),
      run(164, "Deploy", "deploy", "succeeded", 12 * 24, "Commit 9e04d1b", { commit: "9e04d1b22f7", detail: "First deploy", seconds: 187 }),
    ],
    actions: [
      { id: "edit", label: "Edit", icon: "pencil" },
      { id: "remove", label: "Remove", icon: "trash", danger: true },
      { id: "deploy", label: "Deploy", icon: "rocket", primary: true },
    ],
    siblings: falconServices.map((s) => ({ name: s.name, kind: "Service", status: s.status, current: s.id === "open-webui" })),
  };
  service.events = [
    { at: ago(38 * MIN), type: "run", tone: "attention", title: "Deploy waits for approval", detail: "image 0.6.30 → 0.6.34 · change by skorcius", run: 214 },
    { at: ago(40 * MIN), type: "change", tone: "info", title: "Image 0.6.30 → 0.6.34", detail: "Change by skorcius · 895ec0a" },
    { at: ago(11 * HOUR), type: "uptime", tone: "warning", title: "Slow answers for 30 min", detail: "https://chat.hlab.example.org/ answered in over 2 s" },
    { at: ago(2 * DAY + 3 * HOUR - 2 * MIN), type: "uptime", tone: "danger", title: "Down for 6 min", detail: "Restarted by deploy #201" },
    { at: ago(2 * DAY + 3 * HOUR), type: "run", tone: "success", title: "Deploy succeeded", detail: "DEFAULT_MODELS changed · change by skorcius", run: 201 },
    { at: ago(3 * DAY), type: "secret", tone: "neutral", title: "open-webui/env is at v4", detail: "Saved by skorcius" },
    { at: ago(5 * DAY), type: "run", tone: "success", title: "Deploy succeeded", detail: "image 0.6.28 → 0.6.30 · commit 5be8830", run: 188 },
    { at: ago(8 * DAY), type: "run", tone: "danger", title: "Deploy failed", detail: "verify: container unhealthy after 120 s", run: 177 },
    { at: ago(12 * DAY), type: "run", tone: "success", title: "Deploy succeeded", detail: "First deploy · commit 9e04d1b", run: 164 },
  ];

  // ---- The node: home/moraband, a standalone Proxmox installation ---------------------------
  const guestRow = (name, kind, vmid, st, power, cores, memGiB, services, extra = {}) => ({ name, address: `home/moraband/${name}`, kind, vmid, status: st, power, cores, memoryBytes: memGiB * GiB, services, uptimeDay: power === "running" ? 100 : 0, ...extra });
  const node = {
    kind: "node", noun: "Proxmox installation", id: "moraband", name: "moraband", address: "home/moraband",
    crumbs: [{ label: "Inventory" }, { label: "home" }, { label: "moraband" }],
    description: "A standalone Proxmox host in the home lab.",
    status: "warning", statusDetail: "1 issue · 1 guest waits for approval · 1 guest failed",
    type: "standalone", managed: true, endpoint: "https://10.1.0.50:8006/", proxmoxUrl: "https://10.1.0.50:8006/", proxmoxVersion: "8.4.1", clusterName: null,
    observedAt: ago(4 * MIN), observing: false, observeError: null, tokenKey: "proxmox/moraband/read-only-token", verifyTls: false,
    trust: { endpoints: ["https://10.1.0.50:8006/"], untrusted: [], trustedAt: ago(23 * DAY), trustedBy: "skorcius", managementChanged: false },
    hosts: [{ name: "moraband", declared: true, online: true, cpu: 0.31, maxCpu: 16, memoryBytes: 41.2 * GiB, maxMemoryBytes: 64 * GiB, uptimeSeconds: 41 * 86400 + 3 * 3600, concurrentOperations: 0, limit: 1, endpoint: "https://10.1.0.50:8006/" }],
    // Not in the API today.
    sample: { cpuModel: "AMD Ryzen 9 5950X", datastores: [
      { name: "local", role: "images, snippets", usedBytes: 61 * GiB, totalBytes: 94 * GiB },
      { name: "local-lvm", role: "disks", usedBytes: 0.94 * 1024 * GiB, totalBytes: 1.8 * 1024 * GiB },
    ] },
    guests: [
      guestRow("falcon", "VM", 410, "in-sync", "running", 4, 8, 4, { current: true }),
      guestRow("mrb-app-01", "VM", 420, "in-sync", "running", 4, 12, 6),
      guestRow("mrb-crt-01", "VM", 501, "awaiting-approval", "running", 6, 16, 9, { statusDetail: "Apply #213 waits for approval" }),
      guestRow("mrb-dns-01", "Container", 800, "in-sync", "running", 1, 1, 1),
      guestRow("gocd-mrb-01", "VM", 301, "failed", "stopped", 2, 4, 0, { statusDetail: "setup #207 failed" }),
    ],
    unmanaged: [
      { vmid: 100, name: "haos", kind: "VM", status: "running", maxCpu: 2, maxMemoryBytes: 4 * GiB, tags: ["home-assistant"], template: false },
      { vmid: 110, name: "win11-scratch", kind: "VM", status: "stopped", maxCpu: 8, maxMemoryBytes: 16 * GiB, tags: [], template: false },
      { vmid: 210, name: "pihole-old", kind: "Container", status: "stopped", maxCpu: 1, maxMemoryBytes: 0.5 * GiB, tags: ["legacy"], template: false },
      { vmid: 9000, name: "fedora-44", kind: "VM", status: "stopped", maxCpu: 2, maxMemoryBytes: 2 * GiB, tags: ["template"], template: true },
    ],
    snippets: { declared: true, datastore: "local", user: "octopushy", fingerprint: "SHA256:Jd82kLmQp0vX3nRt6yBz1cWsA7hGfE4uNoIiV5TqYe8", createdAt: ago(23 * DAY), hosts: [{ name: "moraband", reachable: true }] },
    definition: definition("labs/home/proxmox/moraband/installation.yaml", nodeYaml, [
      { id: "connection", title: "Connection", icon: "link", find: "type:", fields: [
        { key: "type", label: "Type", value: "Standalone" },
        { key: "endpoint", label: "Endpoint", value: "https://10.1.0.50:8006/", type: "url" },
        { key: "tls", label: "TLS", value: "Not verified", note: "tls.verify is false", tone: "warning" },
        { key: "readOnlyToken", label: "Read-only token", value: "proxmox/moraband/read-only-token", type: "secret", secret: secretRef("proxmox/moraband/read-only-token", 2) },
        { key: "privilegedToken", label: "Privileged token", value: "proxmox/moraband/privileged-token", type: "secret", secret: secretRef("proxmox/moraband/privileged-token", 2) },
      ] },
      { id: "hosts", title: "Host moraband", icon: "server", fields: [
        { key: "datastores", label: "Datastores", value: "images local · disks local-lvm · snippets local", type: "list", items: ["images: local", "disks: local-lvm", "snippets: local"] },
        { key: "bridges", label: "Bridges", value: "vmbr0, vmbr1", type: "list", items: ["vmbr0", "vmbr1"] },
        { key: "limits", label: "Concurrent operations", value: "1", type: "number" },
      ] },
      { id: "snippets", title: "Snippets", icon: "file", fields: [
        { key: "user", label: "SSH user", value: "octopushy", note: "Uploads cloud-init snippets over SSH" },
      ] },
      { id: "policy", title: "Policy", icon: "shield", fields: [
        { key: "approval", label: "Approval", value: "manual", note: "Every change on this installation waits for approval" },
      ] },
    ]),
    policy: { approval: "manual", approvalSetBy: "home/moraband", destructive: "manual", destructiveSetBy: "home/moraband", actions: [] },
    agentLabels: [], agentLabelsSetBy: "default",
    checks: [check("proxmox-api:moraband", "proxmox-api", "https://10.1.0.50:8006/", { baseMs: 7, incidents: [{ hoursAgo: 19 * 24, minutes: 25, detail: "Host restarted for a kernel update" }] })],
    issues: [{ severity: "warning", file: "labs/home/proxmox/moraband/installation.yaml", line: 5, message: "tls.verify is false: the Proxmox certificate is not checked." }],
    changes: [],
    runs: [
      run(213, "Apply", "provision", "awaiting-approval", 1.2, "Commit 895ec0a", { address: "home/moraband/mrb-crt-01", commit: "895ec0a41c2", detail: "disk 120 → 160 GiB" }),
      run(212, "update-tools", "configure", "succeeded", 6, "Schedule", { address: "home/moraband/falcon", commit: "895ec0a41c2", detail: "3 tools updated", seconds: 142 }),
      run(211, "update-system", "configure", "succeeded", 9, "Schedule", { address: "home/moraband/mrb-dns-01", commit: "895ec0a41c2", detail: "6 packages", seconds: 77 }),
      run(207, "setup", "configure", "failed", 26, "Commit 7d20c9e", { address: "home/moraband/gocd-mrb-01", commit: "7d20c9e1a3f", detail: "SSH did not answer", seconds: 300 }),
      run(205, "update-tools", "configure", "succeeded", 30, "Schedule", { address: "home/moraband/falcon", commit: "895ec0a41c2", detail: "Nothing to update", seconds: 61 }),
      run(171, "Apply", "provision", "succeeded", 9 * 24, "Change by skorcius", { address: "home/moraband/falcon", commit: "3c1f9d27be0", by: "skorcius", detail: "memory 4096 → 8192", seconds: 204 }),
    ],
    actions: [
      { id: "proxmox", label: "Open Proxmox", icon: "external" },
      { id: "observe", label: "Observe now", icon: "eye", primary: true },
    ],
    siblings: [{ name: "moraband", kind: "Standalone", status: "warning", current: true }, { name: "korriban", kind: "Standalone", status: "uncertain" }, { name: "umbrel", kind: "Standalone", status: "in-sync" }],
  };
  node.events = [
    { at: ago(4 * MIN), type: "observe", tone: "neutral", title: "Observed", detail: "1 host, 9 guests, 4 of them unmanaged" },
    { at: ago(72 * MIN), type: "run", tone: "attention", title: "Apply of mrb-crt-01 waits for approval", detail: "disk 120 → 160 GiB · commit 895ec0a", run: 213 },
    { at: ago(6 * HOUR), type: "run", tone: "success", title: "update-tools of falcon succeeded", detail: "3 tools updated · schedule", run: 212 },
    { at: ago(9 * HOUR), type: "run", tone: "success", title: "update-system of mrb-dns-01 succeeded", detail: "6 packages · schedule", run: 211 },
    { at: ago(26 * HOUR), type: "run", tone: "danger", title: "setup of gocd-mrb-01 failed", detail: "SSH did not answer · commit 7d20c9e", run: 207 },
    { at: ago(9 * DAY), type: "run", tone: "success", title: "Apply of falcon succeeded", detail: "memory 4096 → 8192 · approved by skorcius", run: 171 },
    { at: ago(19 * DAY), type: "uptime", tone: "danger", title: "Proxmox API down for 25 min", detail: "Host restarted for a kernel update" },
    { at: ago(23 * DAY), type: "trust", tone: "info", title: "Trusted by skorcius", detail: "https://10.1.0.50:8006/" },
  ];

  const data = { node, machine, service };
  const params = new URLSearchParams(location.search);
  const kind = data[params.get("kind")] ? params.get("kind") : "machine";
  const res = data[kind];
  const href = (k) => `${location.pathname.split("/").pop()}?kind=${k}`;

  // ---- HTML helpers -------------------------------------------------------------------
  /** YAML with line numbers. `highlight` is [from, to] (1-based, inclusive); `dim` fades the other lines. */
  function yaml(text, { highlight, dim = false } = {}) {
    const lines = text.split("\n").map((line, i) => {
      const n = i + 1;
      const m = line.match(/^(\s*(?:- )?)(#.*|[\w.-]+:(?=\s|$))?(.*)$/);
      let body = esc(line);
      if (m) {
        const [, indent, head = "", rest] = m;
        const comment = rest.match(/^(.*?)(\s+#.*)$/);
        const tail = comment ? `${esc(comment[1])}<span class="yaml__comment">${esc(comment[2])}</span>` : esc(rest);
        body = head.startsWith("#") ? `${esc(indent)}<span class="yaml__comment">${esc(head + rest)}</span>` : `${esc(indent)}${head ? `<span class="yaml__key">${esc(head)}</span>` : ""}${tail}`;
      }
      const hl = highlight && n >= highlight[0] && n <= highlight[1];
      return `<div class="yaml__line${hl ? " yaml__line--hl" : highlight && dim ? " yaml__line--dim" : ""}" data-line="${n}"><span class="yaml__n">${n}</span><span>${body || " "}</span></div>`;
    });
    return `<div class="yaml">${lines.join("")}</div>`;
  }
  /** The definition file in a code box with its path and a Copy button. */
  const yamlBox = (def, opts) => `<div class="code"><div class="code__bar"><span>${esc(def.file)}</span><button type="button" class="button button--small" data-copy>${icon("copy", 13)}Copy</button></div>${yaml(def.text, opts)}</div>`;

  /** A field's value as HTML: chips for a list, a lock and store key for a secret, a link for a URL. */
  function value(f) {
    if (f.type === "list") return `<span class="chips">${f.items.map((i) => chip(i, { mono: true })).join("")}</span>`;
    if (f.type === "secret") return `<span class="row"><span class="muted" style="display:inline-flex">${icon("lock", 13)}</span><a href="#" class="mono break">${esc(f.secret.key)}</a><span class="chip">v${f.secret.activeVersion}</span></span>`;
    if (f.type === "url") return `<a href="#" class="mono break">${esc(f.value)}</a>`;
    return `<span class="${f.type === "number" ? "num" : ""}">${esc(f.value)}</span>`;
  }
  const kv = (pairs) => `<dl class="kv">${pairs.map(([k, v]) => `<dt>${esc(k)}</dt><dd>${v}</dd>`).join("")}</dl>`;
  function card(title, body, { meta, actions, flush = false, id } = {}) {
    return `<section class="card"${id ? ` id="${id}"` : ""}><header class="card__header"><h2 class="card__title">${esc(title)}${meta != null ? `<span class="card__meta">${esc(meta)}</span>` : ""}</h2>${actions ? `<div class="card__actions">${actions}</div>` : ""}</header><div class="card__body${flush ? " card__body--flush" : ""}">${body}</div></section>`;
  }
  const meter = (fraction) => `<span class="meter" data-level="${fraction > 0.9 ? "danger" : fraction > 0.75 ? "warning" : "ok"}"><span style="width:${Math.round(fraction * 100)}%"></span></span>`;
  /** Heartbeat bar of a check. `last` keeps only the newest n beats. */
  const beats = (c, { last = 96 } = {}) => `<span class="beats" role="img" aria-label="Uptime, last ${last / 4} hours">${c.beats.slice(-last).map((b) => `<span class="beat${b === "up" ? "" : ` beat--${b}`}"></span>`).join("")}</span>`;
  /** 30 day bar: one beat per day, from its percentage up. */
  const days = (c) => `<span class="beats" role="img" aria-label="Uptime, last 30 days">${c.daily.map((d) => `<span class="beat${d >= 99.9 ? "" : d >= 99 ? " beat--warn" : " beat--down"}" title="${d}%"></span>`).join("")}</span>`;
  function spark(values, { width = 160, height = 32 } = {}) {
    const v = values.map((x) => x ?? 0), max = Math.max(...v, 1), step = width / (v.length - 1);
    const pts = v.map((y, i) => `${(i * step).toFixed(1)},${(height - 2 - (y / max) * (height - 4)).toFixed(1)}`);
    return `<svg class="spark" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" aria-hidden="true"><path class="area" d="M0,${height} L${pts.join(" L")} L${width},${height} Z"/><path class="line" d="M${pts.join(" L")}"/></svg>`;
  }
  /** The stage track of a run, as on the Runs page. */
  const track = (r) => `<span class="track" title="${esc(r.steps.map((s) => `${s.name}: ${s.status}`).join(", "))}">${r.steps.map((s) => `<span class="track__segment track__segment--${s.status === "todo" ? "todo" : tone(s.status)}"></span>`).join("")}</span>`;
  const svcIcon = (name, large) => `<span class="svc-icon${large ? " svc-icon--large" : ""}" aria-hidden="true">${esc(name.slice(0, 1).toUpperCase())}</span>`;
  const crumbs = (r = res) => r.crumbs.map((c, i) => (c.kind ? `<a href="${href(c.kind)}">${esc(c.label)}</a>` : i === 0 ? `<a href="#">${esc(c.label)}</a>` : esc(c.label))).join(" / ");
  /** The page's action buttons. `small` for a tight header. */
  const actions = (r = res, { small = false } = {}) => r.actions.map((a) => `<button type="button" class="button${a.primary ? " button--primary" : a.danger ? " button--danger-outline" : ""}${small ? " button--small" : ""}" data-action="${a.id}">${icon(a.icon, 15)}${esc(a.label)}</button>`).join("");

  // ---- Drawer and toast ---------------------------------------------------------------
  function toast(message) {
    document.querySelector(".toast")?.remove();
    const el = document.createElement("div");
    el.className = "toast"; el.setAttribute("role", "status"); el.textContent = message;
    document.body.appendChild(el);
    setTimeout(() => el.remove(), 2200);
  }
  function drawer(title, html) {
    closeDrawer();
    const scrim = document.createElement("div"); scrim.className = "drawer-scrim"; scrim.onclick = closeDrawer;
    const el = document.createElement("aside"); el.className = "drawer"; el.setAttribute("role", "dialog"); el.setAttribute("aria-label", title);
    el.innerHTML = `<div class="drawer__header"><span class="drawer__title">${esc(title)}</span><button type="button" class="icon-button" aria-label="Close" data-close>${icon("x", 16)}</button></div><div class="drawer__body">${html}</div>`;
    el.querySelector("[data-close]").onclick = closeDrawer;
    document.body.append(scrim, el);
    el.querySelector("[data-close]").focus();
    return el;
  }
  function closeDrawer() { document.querySelectorAll(".drawer, .drawer-scrim").forEach((e) => e.remove()); }
  /** Opens the definition's YAML in the drawer, with one section's lines marked when given. */
  const showYaml = (section) => drawer("Definition", yamlBox(res.definition, section ? { highlight: section.lines } : undefined));

  // Page-wide: Escape closes the drawer, [data-copy] copies the YAML, [data-action] and [data-yaml] do the obvious.
  addEventListener("keydown", (e) => { if (e.key === "Escape") closeDrawer(); });
  addEventListener("click", (e) => {
    const t = e.target.closest?.("[data-copy], [data-action], [data-yaml]");
    if (!t) return;
    if (t.hasAttribute("data-copy")) { navigator.clipboard?.writeText(res.definition.text).catch(() => {}); toast("Copied"); }
    else if (t.hasAttribute("data-yaml")) showYaml(res.definition.sections.find((s) => s.id === t.getAttribute("data-yaml")));
    else toast(`${res.actions.find((a) => a.id === t.getAttribute("data-action"))?.label ?? t.textContent.trim()}: not wired in a prototype`);
  });

  // ---- The shell ---------------------------------------------------------------------
  const NAV = [
    ["Operate", [["Overview", "overview"], ["Inventory", "server", true], ["Services", "globe"], ["Deploy", "rocket"], ["Runs", "runs"]]],
    ["Configure", [["Variables", "key"], ["Repository", "branch"], ["Editor", "filePen"]]],
    ["Access", [["Agents", "bot"], ["CLI access", "user"]]],
  ];
  /** Builds the sidebar and returns the empty <main id="main" class="content">. Pass { bare: true } to drop the content box. */
  function mount({ bare = false } = {}) {
    document.title = `${res.name} · ${document.title}`;
    const shell = document.createElement("div");
    shell.className = "shell";
    shell.innerHTML = `
      <aside class="sidebar">
        <div class="brand"><img src="../../frontend/public/favicon.svg" alt="" width="26" height="26"><span>octopushy</span></div>
        <nav aria-label="Main" class="nav-groups">${NAV.map(([label, items]) => `
          <div class="nav__group"><div class="nav__heading">${label}</div><ul class="nav">${items.map(([name, ic, active]) => `
            <li><a href="#" class="nav__link${active ? " nav__link--active" : ""}">${icon(ic, 18)}<span>${name}</span></a></li>`).join("")}</ul></div>`).join("")}
        </nav>
        <div class="account"><button type="button" class="account__toggle"><span class="account__avatar">S</span><span>skorcius</span>${icon("chevronsUpDown", 14)}</button></div>
      </aside>
      <div class="shell__main"><main id="main"${bare ? "" : ' class="content"'}></main></div>`;
    document.body.prepend(shell);
    return shell.querySelector("#main");
  }

  window.RD = {
    NOW, GiB, data, kind, res, href,
    esc, rel, clock, day, bytes, duration, pct,
    icon, tone, statusLabel, badge, status, chip, commit,
    yaml, yamlBox, value, kv, card, meter, beats, days, spark, track, svcIcon, crumbs, actions,
    toast, drawer, closeDrawer, showYaml, mount,
  };
})();
