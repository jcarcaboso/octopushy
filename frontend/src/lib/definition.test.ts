import { describe, expect, it } from "vitest";
import { readDefinition, type DefinitionSection } from "./definition";

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

function read(text: string, kind: "guest" | "service" | "installation" = "guest"): DefinitionSection[] {
  const sections = readDefinition(text, kind);
  if (!sections) throw new Error("expected sections");
  return sections;
}

function section(sections: DefinitionSection[], id: string): DefinitionSection {
  const found = sections.find((item) => item.id === id);
  if (!found) throw new Error(`no section ${id}`);
  return found;
}

function field(sections: DefinitionSection[], id: string, key: string) {
  const found = section(sections, id).fields.find((item) => item.key === key);
  if (!found) throw new Error(`no field ${key} in ${id}`);
  return found;
}

describe("readDefinition", () => {
  it("returns null for text that does not parse or is not a mapping, and never throws", () => {
    expect(readDefinition("a: [1,", "guest")).toBeNull();
    expect(readDefinition("a: 1\na: 2", "guest")).toBeNull();
    expect(readDefinition("- a\n- b", "guest")).toBeNull();
    expect(readDefinition("just text", "guest")).toBeNull();
    expect(readDefinition("", "guest")).toBeNull();
  });

  it("skips schemaVersion, and gives no general section without top-level scalars", () => {
    expect(read("schemaVersion: 1\npolicy:\n  approval: manual").map((item) => item.id)).toEqual(["policy"]);
    expect(read("schemaVersion: 1\n")).toEqual([]);
  });

  it("puts the top-level scalars and lists of scalars first, titled by kind", () => {
    const text = "schemaVersion: 1\npolicy:\n  approval: manual\nhost: a\nprofiles: [x, y]\nempty:\n";
    const sections = read(text);
    expect(sections.map((item) => [item.id, item.title, item.lines])).toEqual([
      ["general", "Placement", [4, 6]],
      ["policy", "Policy", [2, 3]],
    ]);
    expect(section(sections, "general").fields.map((item) => [item.key, item.value])).toEqual([
      ["host", { kind: "text", text: "a" }],
      ["profiles", { kind: "list", items: ["x", "y"] }],
      ["empty", { kind: "text", text: "—" }],
    ]);
    expect(read("type: standalone", "installation")[0].title).toBe("Installation");
    expect(read("workflow: web@1", "service")[0].title).toBe("Service");
  });

  it("gives a link field per item of links, block or flow", () => {
    const sections = read('links: ["https://a.example/", "https://b.example/"]   # where people open it', "service");
    expect(section(sections, "general").fields).toEqual([
      { key: "links[0]", label: "Link", value: { kind: "link", url: "https://a.example/" }, line: 1, note: "where people open it" },
      { key: "links[1]", label: "Link", value: { kind: "link", url: "https://b.example/" }, line: 1, note: null },
    ]);
    const block = read("links:\n  - https://a.example/   # the main one\n  - https://b.example/", "service");
    expect(section(block, "general").fields.map((item) => [item.line, item.note])).toEqual([
      [2, "the main one"],
      [3, null],
    ]);
  });

  it("merges secrets and secretFiles where the first of them is, and notes a file", () => {
    const text = [
      "inputs: { a: 1 }",
      "secrets:",
      "  token: { store: x/token, version: 3 }",
      "policy: { approval: manual }",
      "secretFiles:",
      "  env_file: { store: x/env }   # the whole env",
      "  other: { store: x/other }",
    ].join("\n");
    const sections = read(text, "service");
    expect(sections.map((item) => item.id)).toEqual(["inputs", "secrets", "policy"]);
    const secrets = section(sections, "secrets");
    expect(secrets.title).toBe("Secrets");
    expect(secrets.lines).toEqual([2, 7]);
    expect(secrets.fields).toEqual([
      { key: "token", label: "Token", value: { kind: "secret", key: "x/token", version: 3 }, line: 3, note: null },
      { key: "secretFiles.env_file", label: "Env file", value: { kind: "secret", key: "x/env", version: null }, line: 6, note: "the whole env. Delivered as a file" },
      { key: "secretFiles.other", label: "Other", value: { kind: "secret", key: "x/other", version: null }, line: 7, note: "Delivered as a file" },
    ]);
  });

  it("reads only exact store references as secrets", () => {
    const sections = read("inputs:\n  a: { store: x, version: 0 }\n  b: { store: x, extra: 1 }\n  c: { store: 2 }");
    expect(section(sections, "inputs").fields.map((item) => item.value.kind)).toEqual(["list", "list", "list"]);
  });

  it("gives an installation one section per host", () => {
    const text = "hosts:\n  a:\n    bridges: [vmbr0]\n  b:   # the spare\n    bridges: [vmbr1]\n";
    const sections = read(text, "installation");
    expect(sections.map((item) => [item.id, item.title, item.lines, item.note])).toEqual([
      ["hosts.a", "Host a", [2, 3], null],
      ["hosts.b", "Host b", [4, 5], "the spare"],
    ]);
    expect(read(text, "guest").map((item) => item.id)).toEqual(["hosts"]);
  });

  it("formats scalars, links and lists of scalars", () => {
    const text = "inputs:\n  on: true\n  off: false\n  none: null\n  n: 3\n  url: http://x:1/\n  ftp: ftp://x/\n  tags: [a, 2]";
    expect(section(read(text), "inputs").fields.map((item) => item.value)).toEqual([
      { kind: "text", text: "true" },
      { kind: "text", text: "false" },
      { kind: "text", text: "—" },
      { kind: "text", text: "3" },
      { kind: "link", url: "http://x:1/" },
      { kind: "text", text: "ftp://x/" },
      { kind: "list", items: ["a", "2"] },
    ]);
  });

  it("keeps the comments on the items of a list of scalars, in the field's note", () => {
    const text = "network:\n  dns:\n    - 10.1.0.178   # primary\n    - 10.1.0.179   # secondary\n  search: [a, b]";
    expect(section(read(text), "network").fields.map((item) => [item.value, item.note])).toEqual([
      [{ kind: "list", items: ["10.1.0.178", "10.1.0.179"] }, "10.1.0.178: primary; 10.1.0.179: secondary"],
      [{ kind: "list", items: ["a", "b"] }, null],
    ]);
  });

  it("flattens a nested block mapping, and reads a short one-line mapping as a list", () => {
    const text = [
      "connection:",
      "  tls:",
      "    verify: true",
      "    caCertificate: { store: ca }",
      "  short: { a: 1, b: x }",
      "  long: { a: 1, b: 2, c: 3, d: 4, e: 5 }",
      "  deep: { x: { y: 1 } }",
    ].join("\n");
    expect(section(read(text, "installation"), "connection").fields.map((item) => [item.key, item.label, item.line, item.value])).toEqual([
      ["tls.verify", "TLS · verify", 3, { kind: "text", text: "true" }],
      ["tls.caCertificate", "TLS · caCertificate", 4, { kind: "secret", key: "ca", version: null }],
      ["short", "Short", 5, { kind: "list", items: ["a: 1", "b: x"] }],
      ["long.a", "Long · a", 6, { kind: "text", text: "1" }],
      ["long.b", "Long · b", 6, { kind: "text", text: "2" }],
      ["long.c", "Long · c", 6, { kind: "text", text: "3" }],
      ["long.d", "Long · d", 6, { kind: "text", text: "4" }],
      ["long.e", "Long · e", 6, { kind: "text", text: "5" }],
      ["deep.x", "Deep · x", 7, { kind: "list", items: ["y: 1"] }],
    ]);
  });

  it("gives one field per item of a list of mappings, numbered when there are several", () => {
    const text = "inputs:\n  volumes:\n    - { name: data, size: 2 }\n    - name: logs   # rotated\n      size: 1\n  only:\n    - { a: [1, 2] }";
    expect(section(read(text, "service"), "inputs").fields.map((item) => [item.key, item.label, item.line, item.value, item.note])).toEqual([
      ["volumes[0]", "Volume", 3, { kind: "list", items: ["name: data", "size: 2"] }, null],
      ["volumes[1]", "Volume 2", 4, { kind: "list", items: ["name: logs", "size: 1"] }, "rotated"],
      ["only[0]", "Only", 7, { kind: "list", items: ["a: [1, 2]"] }, null],
    ]);
  });

  it("reads a guest's memory and disks in friendlier units", () => {
    const text = "hardware:\n  memory: 8192\n  disks:\n    - { size: 32, datastore: local-lvm }\n    - { size: 100 }\n";
    const fields = section(read(text), "hardware").fields;
    expect(fields.map((item) => [item.key, item.label, item.value])).toEqual([
      ["memory", "Memory", { kind: "text", text: "8 GiB", raw: "8192 MiB" }],
      ["disks[0]", "Disk", { kind: "text", text: "32 GiB", raw: "on local-lvm" }],
      ["disks[1]", "Disk 2", { kind: "text", text: "100 GiB" }],
    ]);
    expect(field(read("hardware:\n  memory: 1500"), "hardware", "memory").value).toEqual({ kind: "text", text: "1500 MiB" });
    expect(field(read("hardware:\n  memory: 8192", "service"), "hardware", "memory").value).toEqual({ kind: "text", text: "8192" });
  });

  it("reads schedules by action, with simple crons in words", () => {
    const text = [
      "schedules:",
      '  - { action: a, cron: "0 4 * * *", timezone: Europe/Madrid }',
      '  - { action: b, cron: "30 5 * * 0" }   # weekly',
      '  - { action: c, cron: "*/5 * * * *", timezone: UTC }',
      '  - { action: d, cron: "0 1 1 * *" }',
    ].join("\n");
    expect(section(read(text), "schedules").fields).toEqual([
      { key: "schedules[0]", label: "a", value: { kind: "text", text: "Every day at 04:00", raw: "0 4 * * * Europe/Madrid" }, line: 2, note: null },
      { key: "schedules[1]", label: "b", value: { kind: "text", text: "Sundays at 05:30", raw: "30 5 * * 0" }, line: 3, note: "weekly" },
      { key: "schedules[2]", label: "c", value: { kind: "text", text: "*/5 * * * *", raw: "*/5 * * * * UTC" }, line: 4, note: null },
      { key: "schedules[3]", label: "d", value: { kind: "text", text: "0 1 1 * *" }, line: 5, note: null },
    ]);
  });

  it("labels the approval of an action", () => {
    const text = "policy:\n  actions:\n    deploy:\n      approval: automatic\n    backup: { approval: manual }";
    expect(section(read(text, "service"), "policy").fields.map((item) => [item.key, item.label, item.line])).toEqual([
      ["actions.deploy.approval", "Approval of deploy", 4],
      ["actions.backup.approval", "Approval of backup", 5],
    ]);
  });

  it("labels keys in sentence case with acronyms, and keeps upper-case keys", () => {
    const text = "inputs:\n  machine_profile: a\n  readOnlyToken: b\n  vmid: 1\n  sshKey: c\n  dns-name: d\n  CPU_LIMIT: e\n  hostIp: f\n  url_id: g";
    expect(section(read(text), "inputs").fields.map((item) => item.label)).toEqual([
      "Machine profile",
      "Read only token",
      "VMID",
      "SSH key",
      "DNS name",
      "CPU_LIMIT",
      "Host IP",
      "URL ID",
    ]);
  });

  it("ends a section at its last own line, before comments that lead to the next key", () => {
    const text = "network:\n  bridge: vmbr0\n\n  # about the address\n  address: 10.0.0.2\n\n# about access\naccess:\n  user: ops\n";
    const sections = read(text);
    expect(section(sections, "network").lines).toEqual([1, 5]);
    expect(section(sections, "access")).toMatchObject({ lines: [8, 9], note: "about access" });
    expect(field(sections, "network", "address").note).toBe("about the address");
  });

  it("does not read a block scalar's lines as keys", () => {
    const text = "inputs:\n  script: |\n    # not a comment\n    key: not a key\n  after: 1\n";
    const fields = section(read(text, "service"), "inputs").fields;
    expect(fields.map((item) => [item.key, item.line, item.note])).toEqual([
      ["script", 2, null],
      ["after", 5, null],
    ]);
    expect(section(read(text, "service"), "inputs").lines).toEqual([1, 5]);
  });

  it("finds comments outside quotes and URL fragments only", () => {
    const text = [
      "inputs:",
      "  a: \"x # y\"   # real",
      "  b: 'it''s # here'",
      "  c: https://x/enroll#fleet-v1=abc",
      "  d: plain#text # after",
    ].join("\n");
    expect(section(read(text, "service"), "inputs").fields.map((item) => [item.key, item.note])).toEqual([
      ["a", "real"],
      ["b", null],
      ["c", null],
      ["d", "after"],
    ]);
  });

  it("collects a section's note from above its key, its key's line and before its first child", () => {
    const text = "# one\n# two\ninputs:   # three\n  # four\n  a: 1   # of a\n  # of b,\n  # in two lines\n  b: 2\n";
    const inputs = section(read(text, "service"), "inputs");
    expect(inputs.note).toBe("one two three four");
    expect(inputs.fields.map((item) => item.note)).toEqual(["of a", "of b, in two lines"]);
  });

  it("reads the machine end to end", () => {
    const sections = read(machineYaml, "guest");
    expect(sections.map((item) => [item.id, item.title, item.lines])).toEqual([
      ["general", "Placement", [2, 7]],
      ["hardware", "Hardware", [8, 12]],
      ["network", "Network", [13, 20]],
      ["inputs", "Inputs", [21, 26]],
      ["secrets", "Secrets", [27, 29]],
      ["access", "Access", [30, 32]],
      ["schedules", "Schedules", [33, 35]],
      ["policy", "Policy", [36, 40]],
    ]);
    expect(field(sections, "general", "vmid")).toMatchObject({ label: "VMID", value: { kind: "text", text: "410" }, line: 5 });
    expect(field(sections, "hardware", "memory")).toMatchObject({ value: { kind: "text", text: "8 GiB", raw: "8192 MiB" }, line: 10, note: "MiB" });
    expect(field(sections, "hardware", "disks[0]")).toMatchObject({
      label: "Disk",
      value: { kind: "text", text: "300 GiB" },
      line: 12,
      note: "GiB; the system disk, on moraband's local-lvm",
    });
    expect(section(sections, "network").note).toBe(
      "falcon's fixed address on moraband's vmbr1, outside the DHCP pool. It must be in OCTOPUSHY_TARGETS, or no agent reaches it.",
    );
    expect(field(sections, "network", "dns")).toMatchObject({ label: "DNS", value: { kind: "list", items: ["10.1.0.178", "10.1.0.179"] }, note: null });
    expect(section(sections, "inputs").note).toBe("A machine for developing with AI tools, and the first real test of Octopushy 0.5.0.");
    expect(field(sections, "inputs", "machine_fleet_enrollment_link").value).toEqual({
      kind: "link",
      url: "https://fleet.hlab.example.org/enroll#fleet-v1=eyJ2ZXJzaW9uIjoxLCJ0ZWFtIjoiaGxhYiJ9",
    });
    expect(field(sections, "secrets", "machine_github_token").value).toEqual({ kind: "secret", key: "shared/github/machines-token", version: null });
    expect(section(sections, "schedules").fields.map((item) => [item.label, item.value])).toEqual([
      ["update-tools", { kind: "text", text: "Every day at 04:00", raw: "0 4 * * * Europe/Madrid" }],
      ["update-system", { kind: "text", text: "Sundays at 05:00", raw: "0 5 * * 0 Europe/Madrid" }],
    ]);
    expect(section(sections, "policy").fields.map((item) => [item.label, item.line])).toEqual([
      ["Approval", 37],
      ["Destructive", 38],
      ["Approval of update-tools", 40],
    ]);
  });

  it("reads the service end to end", () => {
    const sections = read(serviceYaml, "service");
    expect(sections.map((item) => [item.id, item.title, item.lines, item.note])).toEqual([
      ["general", "Service", [2, 5], null],
      ["inputs", "Inputs", [6, 14], null],
      ["secrets", "Secrets", [15, 18], null],
      ["policy", "Policy", [19, 20], null],
    ]);
    expect(section(sections, "general").fields.map((item) => [item.key, item.label, item.line])).toEqual([
      ["workflow", "Workflow", 2],
      ["description", "Description", 3],
      ["links[0]", "Link", 5],
    ]);
    expect(field(sections, "inputs", "dns_name")).toMatchObject({ label: "DNS name", note: "the dns stage sets this record, pointing at traefik" });
    expect(field(sections, "inputs", "env.OLLAMA_BASE_URL")).toMatchObject({
      label: "Env · OLLAMA_BASE_URL",
      value: { kind: "link", url: "http://ollama:11434" },
      line: 12,
    });
    expect(field(sections, "inputs", "env.ENABLE_SIGNUP").value).toEqual({ kind: "text", text: "false" });
    expect(section(sections, "secrets").fields.map((item) => [item.key, item.line, item.note])).toEqual([
      ["secretFiles.env_file", 16, "Delivered as a file"],
      ["webui_secret_key", 18, null],
    ]);
    expect(field(sections, "policy", "approval").note).toBe("a deploy waits for approval");
  });

  it("reads the installation end to end", () => {
    const sections = read(nodeYaml, "installation");
    expect(sections.map((item) => [item.id, item.title, item.lines])).toEqual([
      ["general", "Installation", [2, 2]],
      ["connection", "Connection", [3, 7]],
      ["hosts.moraband", "Host moraband", [9, 12]],
      ["snippets", "Snippets", [13, 14]],
      ["policy", "Policy", [15, 16]],
    ]);
    expect(field(sections, "connection", "endpoint").value).toEqual({ kind: "link", url: "https://10.1.0.50:8006/" });
    expect(field(sections, "connection", "tls")).toMatchObject({ label: "TLS", value: { kind: "list", items: ["verify: false"] }, line: 5 });
    expect(field(sections, "connection", "readOnlyToken")).toMatchObject({
      label: "Read only token",
      value: { kind: "secret", key: "proxmox/moraband/read-only-token", version: null },
    });
    expect(field(sections, "hosts.moraband", "datastores").value).toEqual({
      kind: "list",
      items: ["images: local", "disks: local-lvm", "snippets: local"],
    });
    expect(field(sections, "snippets", "user").note).toBe("uploads cloud-init snippets over SSH");
    expect(field(sections, "policy", "approval").note).toBe("every change on this installation waits for approval");
  });
});
