import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ChangeForm, ChangePreview } from "../api/types";
import worker from "../test/fixtures/octopushy-worker.inputs.schema.json";
import { mockApi, renderAt } from "../test/utils";
import { ChangeEditorPage } from "./ChangeEditorPage";

const base = "4b1d0c0ffee4b1d0c0ffee4b1d0c0ffee4b1d0c0";
const file = "labs/home/proxmox/pve01/guests/pve01-worker/guest.yaml";

function form(overrides: Partial<ChangeForm> = {}): ChangeForm {
  return {
    resourceId: "guest-1",
    resourceKind: "guest",
    address: "home/pve01/pve01-worker",
    name: "pve01-worker",
    baseCommit: base,
    file,
    text: "schemaVersion: 1\n",
    workflow: "octopushy-worker@1",
    resolved: { id: "octopushy-worker", version: "1.0", commit: base, usable: true, issue: null, manages: "vm", description: null,
      inputs: worker, ui: null, secrets: ["worker_enrollment_token"], setup: true },
    hardware: { cores: 2, memory: 4096, disks: [{ size: 32 }] },
    network: { bridge: "vmbr0", mode: "dhcp" },
    inputs: { worker_server_url: "https://octopushy.lan:8443", worker_agent_version: "0.5.0" },
    blockedReason: null,
    canRemove: true,
    upgrade: null,
    fieldIssues: [],
    ...overrides,
  };
}

const diff = `diff --git a/${file} b/${file}
index 1111111..2222222 100644
--- a/${file}
+++ b/${file}
@@ -5,3 +5,3 @@ vmid: 900
 hardware:
-  memory: 4096            # MiB
+  memory: 8192            # MiB
`;

function preview(overrides: Partial<ChangePreview> = {}): ChangePreview {
  return {
    kind: "edit",
    address: "home/pve01/pve01-worker",
    subject: "pve01-worker: memory 4096 → 8192",
    message: "pve01-worker: memory 4096 → 8192\n\nOctopushy-Change: 0190\n",
    baseCommit: base,
    files: [file],
    diff,
    valid: true,
    issues: [],
    fieldIssues: [],
    refused: null,
    ...overrides,
  };
}

const route = { path: "/guests/:id/change", route: "/guests/guest-1/change" };

describe("ChangeEditorPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("edits from the form, shows the exact diff, and commits that change", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/changes/form/guest-1": () => form(),
      "POST /api/changes/preview": () => preview(),
      "POST /api/changes": () => ({ id: "change-1" }),
      "GET /api/changes/change-1": () => new Response(null, { status: 404 }),
    });
    renderAt(<ChangeEditorPage />, route);

    const memory = await screen.findByLabelText(/^memory/);
    await user.clear(memory);
    await user.type(memory, "8192");
    await user.click(screen.getByRole("button", { name: "Review the change" }));

    const review = await screen.findByRole("region", { name: "The exact diff that will be committed" });
    expect(within(review).getByText(/^\+ memory: 8192/)).toHaveAttribute("data-kind", "add");
    expect(within(review).getByText(/^- memory: 4096/)).toHaveAttribute("data-kind", "del");
    expect(screen.getByText(/Octopushy-Change: 0190/)).toBeInTheDocument();
    const sent = requests.find((r) => r.path === "/api/changes/preview")!.body as Record<string, unknown>;
    expect(sent).toEqual({
      resourceId: "guest-1",
      baseCommit: base,
      kind: "edit",
      workflow: null,
      hardware: { cores: 2, memory: 8192, disks: [{ size: 32 }] },
      network: { bridge: "vmbr0", mode: "dhcp" },
      inputs: { worker_server_url: "https://octopushy.lan:8443", worker_agent_version: "0.5.0" },
    });

    await user.click(screen.getByRole("button", { name: "Commit change" }));
    await waitFor(() => expect(requests.some((r) => r.method === "POST" && r.path === "/api/changes")).toBe(true));
    expect(requests.find((r) => r.path === "/api/changes" && r.method === "POST")!.body).toEqual(sent);
  });

  it("keeps the form and shows the server's issues next to their fields when the change does not validate", async () => {
    const user = userEvent.setup();
    mockApi({
      "GET /api/changes/form/guest-1": () => form(),
      "POST /api/changes/preview": () =>
        preview({
          valid: false,
          fieldIssues: [{ pointer: "/hardware/memory", message: "Value should be at least 256" }],
          issues: [{ severity: "error", file, line: 7, message: "hardware.memory: Value should be at least 256 (workflow octopushy-worker@1.0)", address: null }],
        }),
    });
    renderAt(<ChangeEditorPage />, route);
    const memory = await screen.findByLabelText(/^memory/);
    await user.clear(memory);
    await user.type(memory, "64");
    await user.click(screen.getByRole("button", { name: "Review the change" }));

    expect(await screen.findByText("The change does not validate")).toBeInTheDocument();
    expect(within(memory.closest(".field") as HTMLElement).getByRole("alert")).toHaveTextContent("Value should be at least 256");
    expect(screen.queryByRole("region", { name: "The exact diff that will be committed" })).toBeNull();
  });

  it("is read-only, with the reason, when committing is off", async () => {
    mockApi({
      "GET /api/changes/form/guest-1": () => form({ blockedReason: "Changes from the UI are commits to the lab repository, and committing is off." }),
    });
    renderAt(<ChangeEditorPage />, route);
    expect(await screen.findByText("This form is read-only")).toBeInTheDocument();
    expect(screen.getByText(/committing is off/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^memory/)).toBeDisabled();
    expect(screen.getByRole("button", { name: "Review the change" })).toBeDisabled();
  });

  it("upgrades to the next major, opening the new schema when the inputs do not fit it", async () => {
    const next = structuredClone(worker) as Record<string, unknown>;
    const inputs = (next.properties as Record<string, Record<string, unknown>>).inputs;
    (inputs.properties as Record<string, unknown>).worker_pool = { enum: ["general", "gpu"], description: "Where the worker serves." };
    inputs.required = ["worker_server_url", "worker_agent_version", "worker_pool"];
    const { requests } = mockApi({
      "GET /api/changes/form/guest-1": () =>
        form({ upgrade: { id: "octopushy-worker", version: "2.0", commit: base, usable: true, issue: null, manages: "vm", description: null, inputs: next, ui: null, secrets: [], setup: false } }),
      "POST /api/changes/preview": (r) =>
        (r.body as { inputs: Record<string, unknown> }).inputs.worker_pool
          ? preview({ kind: "upgrade", subject: "pve01-worker: workflow octopushy-worker@1 → octopushy-worker@2, worker_pool set to gpu" })
          : preview({ kind: "upgrade", valid: false, fieldIssues: [{ pointer: "/inputs", message: "Required properties [\"worker_pool\"] are not present" }] }),
    });
    renderAt(<ChangeEditorPage />, { path: route.path, route: `${route.route}?mode=upgrade` });

    expect(await screen.findByText("octopushy-worker@2 needs changes to the inputs")).toBeInTheDocument();
    expect(screen.getByLabelText("Workflow")).toHaveValue("octopushy-worker@2");
    expect(screen.getByText(/Required properties \["worker_pool"\] are not present/)).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText(/^worker_pool/), "gpu");
    await userEvent.click(screen.getByRole("button", { name: "Review the change" }));
    expect(await screen.findByRole("region", { name: "The exact diff that will be committed" })).toBeInTheDocument();
    const last = requests.filter((r) => r.path === "/api/changes/preview").at(-1)!.body as Record<string, unknown>;
    expect(last.kind).toBe("upgrade");
    expect(last.workflow).toBe("octopushy-worker@2");
    expect((last.inputs as Record<string, unknown>).worker_pool).toBe("gpu");
  });

  it("says when the form is out of date and reloads it", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/changes/form/guest-1": () => form(),
      "POST /api/changes/preview": () => preview(),
      "POST /api/changes": () =>
        new Response(JSON.stringify({ changeId: "c-1", reason: "stale", message: "pve01-worker changed since the form was loaded; reload it.", issues: [], fieldIssues: [] }),
          { status: 409, headers: { "content-type": "application/json" } }),
    });
    renderAt(<ChangeEditorPage />, route);
    await user.click(await screen.findByRole("button", { name: "Review the change" }));
    await user.click(await screen.findByRole("button", { name: "Commit change" }));
    expect(await screen.findByText("pve01-worker changed since the form was loaded; reload it.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Reload the form" }));
    await waitFor(() => expect(requests.filter((r) => r.path === "/api/changes/form/guest-1").length).toBe(2));
  });
});
