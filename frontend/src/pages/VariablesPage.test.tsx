import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Variable, VariableVersion } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { VariablesPage } from "./VariablesPage";

const key = "proxmox/site-a/api-token";
const createdAt = "2026-10-20T12:00:00Z";

function entry(overrides: Partial<Variable> = {}): Variable {
  return {
    key,
    visibility: "secret",
    description: "API credential",
    version: 3,
    latestVersion: 4,
    revoked: false,
    updatedAt: createdAt,
    value: null,
    usedBy: ["recipe/site-a"],
    ...overrides,
  };
}

function version(number: number, status: string, overrides: Partial<VariableVersion> = {}): VariableVersion {
  return {
    version: number,
    status,
    createdAt,
    activatedAt: status === "active" || status === "previous" ? createdAt : null,
    activatedBy: status === "active" || status === "previous" ? "alice" : null,
    activationNote: status === "active" || status === "previous" ? "Checked the provider API externally" : null,
    revokedAt: status === "revoked" ? createdAt : null,
    revokedBy: status === "revoked" ? "bob" : null,
    revocationNote: status === "revoked" ? "Retired credential" : null,
    usedBy: number === 3 ? ["run/example/step"] : [],
    ...overrides,
  };
}

function variables(current = entry()) {
  return { entries: [current], missing: [], system: [] };
}

function renderPage() {
  return renderAt(<VariablesPage />, { path: "/variables", route: "/variables" });
}

async function openVersions(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole("button", { name: `Versions of ${key}` }));
  return screen.getByRole("dialog", { name: `Versions of ${key}` });
}

describe("VariablesPage credential lifecycle", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("stages secret replacements without changing the active version or using PUT", async () => {
    const user = userEvent.setup();
    let latestVersion = 3;
    const { requests } = mockApi({
      "GET /api/variables": () => variables(entry({ latestVersion })),
      "POST /api/variables/candidates": () => {
        latestVersion = 4;
        return { version: 4 };
      },
    });
    renderPage();

    await user.click(await screen.findByRole("button", { name: `Update ${key}` }));
    const dialog = screen.getByRole("dialog", { name: `Update ${key}` });
    expect(within(dialog).getByText(/active version stays unchanged/)).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText("Value"), "new-private-credential");
    await user.click(within(dialog).getByRole("button", { name: "Stage candidate" }));

    await waitFor(() => expect(screen.queryByRole("dialog", { name: `Update ${key}` })).not.toBeInTheDocument());
    const table = screen.getByRole("region", { name: "Variable store entries" });
    expect(within(table).getByText("v3")).toBeInTheDocument();
    expect(await within(table).findByText("Latest: v4")).toBeInTheDocument();
    expect(requests.find((request) => request.path.endsWith("/candidates"))?.body).toEqual({
      key,
      value: "new-private-credential",
      description: "API credential",
    });
    expect(requests.some((request) => request.method === "PUT")).toBe(false);
    expect(document.body).not.toHaveTextContent("new-private-credential");
    expect(screen.queryByDisplayValue("new-private-credential")).not.toBeInTheDocument();
  });

  it("keeps description-only secret updates on PUT without sending a value", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/variables": () => variables(),
      "PUT /api/variables": () => undefined,
    });
    renderPage();
    await user.click(await screen.findByRole("button", { name: `Update ${key}` }));
    await user.clear(screen.getByLabelText(/Description/));
    await user.type(screen.getByLabelText(/Description/), "Updated description");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(requests.some((request) => request.method === "PUT")).toBe(true));
    expect(requests.find((request) => request.method === "PUT")?.body).toEqual({
      key,
      value: null,
      visibility: null,
      description: "Updated description",
    });
    expect(requests.some((request) => request.path.endsWith("/candidates"))).toBe(false);
  });

  it("creates an initial active secret through PUT", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/variables": () => ({ entries: [], missing: [], system: [] }),
      "PUT /api/variables": () => undefined,
    });
    renderPage();
    await user.click((await screen.findAllByRole("button", { name: "Set a variable" }))[0]);
    await user.type(screen.getByLabelText("Key"), key);
    await user.type(screen.getByLabelText("Value"), "initial-private-credential");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(requests.some((request) => request.method === "PUT")).toBe(true));
    expect(requests.find((request) => request.method === "PUT")?.body).toEqual({
      key,
      value: "initial-private-credential",
      visibility: "secret",
      description: null,
    });
    await waitFor(() => expect(screen.queryByDisplayValue("initial-private-credential")).not.toBeInTheDocument());
  });

  it("still edits plain values immediately", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/variables": () => variables(entry({ visibility: "plain", value: "old-value" })),
      "PUT /api/variables": () => undefined,
    });
    renderPage();
    await user.click(await screen.findByRole("button", { name: `Update ${key}` }));
    await user.clear(screen.getByLabelText("Value"));
    await user.type(screen.getByLabelText("Value"), "replacement");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(requests.some((request) => request.method === "PUT")).toBe(true));
    expect(requests.find((request) => request.method === "PUT")?.body).toMatchObject({ key, value: "replacement", visibility: "plain" });
    expect(screen.queryByRole("button", { name: `Versions of ${key}` })).not.toBeInTheDocument();
  });

  it("never echoes a rejected credential in errors and clears it when closed", async () => {
    const user = userEvent.setup();
    const credential = "do-not-echo-this-credential";
    mockApi({
      "GET /api/variables": () => variables(),
      "POST /api/variables/candidates": () => problem(400, `Rejected ${credential}`),
    });
    renderPage();
    await user.click(await screen.findByRole("button", { name: `Update ${key}` }));
    await user.type(screen.getByLabelText("Value"), credential);
    await user.click(screen.getByRole("button", { name: "Stage candidate" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("The candidate could not be staged.");
    expect(screen.getByRole("alert")).not.toHaveTextContent(credential);
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await user.click(screen.getByRole("button", { name: `Update ${key}` }));
    expect(screen.getByLabelText("Value")).toHaveValue("");
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("requires an operator confirmation note and promotes against the active, not latest, version", async () => {
    const user = userEvent.setup();
    let promoted = false;
    const { requests } = mockApi({
      "GET /api/variables": () => variables(entry({ version: promoted ? 4 : 3 })),
      "GET /api/variables/versions": () => [
        version(4, promoted ? "active" : "candidate"),
        version(3, promoted ? "previous" : "active"),
        version(2, "previous"),
        version(1, "revoked"),
      ],
      "POST /api/variables/promote": () => { promoted = true; },
    });
    renderPage();
    const dialog = await openVersions(user);
    await user.click(await within(dialog).findByRole("button", { name: "Promote v4" }));
    expect(within(dialog).getByText("Operator confirmation, not automated verification")).toBeInTheDocument();
    expect(within(dialog).getByText(/confirmation of externally performed checks/)).toBeInTheDocument();
    expect(within(dialog).getByText(/does not automatically verify the credential/)).toBeInTheDocument();
    const confirm = within(dialog).getByRole("button", { name: "Confirm promotion" });
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText(/Operator confirmation note/), "   ");
    expect(confirm).toBeDisabled();
    expect(requests.some((request) => request.path.endsWith("/promote"))).toBe(false);
    await user.type(within(dialog).getByLabelText(/Operator confirmation note/), "Provider API checks completed ");
    await user.click(confirm);

    await waitFor(() => expect(requests.some((request) => request.path.endsWith("/promote"))).toBe(true));
    expect(requests.find((request) => request.path.endsWith("/promote"))?.body).toEqual({
      key,
      version: 4,
      expectedCurrentVersion: 3,
      verificationNote: "Provider API checks completed",
    });
    await waitFor(() => expect(within(dialog).queryByRole("button", { name: "Confirm promotion" })).not.toBeInTheDocument());
    expect(within(screen.getByRole("region", { name: "Variable store entries" })).getByText("v4")).toBeInTheDocument();
    expect(new URLSearchParams(requests.find((request) => request.path.endsWith("/versions"))?.search).get("key")).toBe(key);
    expect(within(dialog).getByText("run/example/step")).toBeInTheDocument();
    expect(within(dialog).getAllByText("alice").length).toBeGreaterThan(0);
    expect(within(dialog).getByText("bob")).toBeInTheDocument();
    expect(within(dialog).getByText("Retired credential")).toBeInTheDocument();
    expect(within(dialog).queryByRole("button", { name: "Revoke v1" })).not.toBeInTheDocument();
    expect(within(dialog).queryByRole("button", { name: "Promote v1" })).not.toBeInTheDocument();
  });

  it("shows promotion conflicts and lets the operator reload before retrying", async () => {
    const user = userEvent.setup();
    mockApi({
      "GET /api/variables": () => variables(),
      "GET /api/variables/versions": () => [version(4, "candidate"), version(3, "active")],
      "POST /api/variables/promote": () => problem(409, "Active version changed"),
    });
    renderPage();
    const dialog = await openVersions(user);
    await user.click(await within(dialog).findByRole("button", { name: "Promote v4" }));
    await user.type(within(dialog).getByLabelText(/Operator confirmation note/), "External checks done");
    await user.click(within(dialog).getByRole("button", { name: "Confirm promotion" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("The version could not be promoted.");
    expect(within(dialog).getByRole("alert")).toHaveTextContent(/Reload versions and review/);
    await user.click(within(dialog).getByRole("button", { name: "Reload versions" }));
    expect(within(dialog).queryByRole("alert")).not.toBeInTheDocument();
    expect(within(dialog).queryByLabelText(/Operator confirmation note/)).not.toBeInTheDocument();
  });

  it("allows an explicit rollback promotion of a non-revoked previous version", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/variables": () => variables(),
      "GET /api/variables/versions": () => [version(4, "candidate"), version(3, "active"), version(2, "previous")],
      "POST /api/variables/promote": () => undefined,
    });
    renderPage();
    const dialog = await openVersions(user);
    await user.click(await within(dialog).findByRole("button", { name: "Promote v2" }));
    const note = within(dialog).getByLabelText(/Operator confirmation note/);
    expect(note).toHaveFocus();
    await user.type(note, "Externally checked the previous credential for rollback");
    await user.click(within(dialog).getByRole("button", { name: "Confirm promotion" }));

    await waitFor(() => expect(requests.find((request) => request.path.endsWith("/promote"))?.body).toEqual({
      key,
      version: 2,
      expectedCurrentVersion: 3,
      verificationNote: "Externally checked the previous credential for rollback",
    }));
  });

  it("requires a revocation reason, warns about delivered and upstream credentials, and refreshes status", async () => {
    const user = userEvent.setup();
    let revoked = false;
    const { requests } = mockApi({
      "GET /api/variables": () => variables(entry({ revoked })),
      "GET /api/variables/versions": () => [version(4, "candidate"), version(3, revoked ? "revoked" : "active")],
      "POST /api/variables/revoke": () => { revoked = true; },
    });
    renderPage();
    const dialog = await openVersions(user);
    await user.click(await within(dialog).findByRole("button", { name: "Revoke v3" }));
    expect(within(dialog).getByText(/Revocation blocks future assignments/)).toBeInTheDocument();
    expect(within(dialog).getByText(/cannot retract already delivered credentials/)).toBeInTheDocument();
    expect(within(dialog).getByText(/does not revoke the upstream provider token/)).toBeInTheDocument();
    const confirm = within(dialog).getByRole("button", { name: "Confirm revocation" });
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText(/Revocation reason/), "   ");
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText(/Revocation reason/), "Credential retired ");
    await user.click(confirm);

    await waitFor(() => expect(requests.some((request) => request.path.endsWith("/revoke"))).toBe(true));
    expect(requests.find((request) => request.path.endsWith("/revoke"))?.body).toEqual({
      key,
      version: 3,
      reason: "Credential retired",
    });
    expect(await within(dialog).findByText("The active version is revoked")).toBeInTheDocument();
    expect(within(dialog).getByText("run/example/step")).toBeInTheDocument();
    await waitFor(() => expect(within(dialog).queryByRole("button", { name: "Revoke v3" })).not.toBeInTheDocument());
  });

  it("keeps failed revocations visible and clears the reason on close", async () => {
    const user = userEvent.setup();
    mockApi({
      "GET /api/variables": () => variables(),
      "GET /api/variables/versions": () => [version(3, "active")],
      "POST /api/variables/revoke": () => problem(500, "Unavailable"),
    });
    renderPage();
    const dialog = await openVersions(user);
    await user.click(await within(dialog).findByRole("button", { name: "Revoke v3" }));
    await user.type(within(dialog).getByLabelText(/Revocation reason/), "Retired");
    await user.click(within(dialog).getByRole("button", { name: "Confirm revocation" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("The version could not be revoked.");
    expect(within(dialog).getByLabelText(/Revocation reason/)).toHaveValue("Retired");
    await user.click(within(dialog).getByRole("button", { name: "Done" }));
    const reopened = await openVersions(user);
    await user.click(await within(reopened).findByRole("button", { name: "Revoke v3" }));
    expect(within(reopened).getByLabelText(/Revocation reason/)).toHaveValue("");
    expect(within(reopened).queryByRole("alert")).not.toBeInTheDocument();
  });

  it("excludes system keys from entries, missing keys, history controls, and new keys", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/variables": () => ({
        entries: [entry(), entry({ key: "system/entry" })],
        missing: [{ key: "system/missing", usedBy: [] }],
        system: [{ key: "system/server", visibility: "secret", updatedAt: createdAt }],
      }),
    });
    renderPage();
    await screen.findByRole("button", { name: `Update ${key}` });
    expect(document.body).not.toHaveTextContent("system/entry");
    expect(document.body).not.toHaveTextContent("system/missing");
    expect(document.body).not.toHaveTextContent("system/server");
    await user.click(screen.getByRole("button", { name: "Set a variable" }));
    await user.type(screen.getByLabelText("Key"), "system/forbidden");
    await user.type(screen.getByLabelText("Value"), "never-sent");
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    expect(requests.every((request) => request.method === "GET")).toBe(true);
  });
});
