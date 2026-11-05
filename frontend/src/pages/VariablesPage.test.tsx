import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useLocation } from "react-router";
import type { Variable, VariableVersion } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { VariablesPage } from "./VariablesPage";

const key = "proxmox/site-a/api-token";
const createdAt = "2026-10-20T12:00:00Z";
const viewStorageKey = "octopushy.variables.view";

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
  beforeEach(() => window.localStorage.setItem(viewStorageKey, "all"));
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

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

describe("VariablesPage folders", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  const siteAToken = "proxmox/site-a/api-token";
  const siteACa = "proxmox/site-a/ca";
  const siteBToken = "proxmox/site-b/api-token";

  function store(overrides: { entries?: Variable[]; missing?: { key: string; usedBy: string[] }[] } = {}) {
    return {
      entries: [
        entry({ key: siteAToken }),
        entry({ key: siteACa, visibility: "plain", value: "visible-certificate", description: "Root CA", version: 1, latestVersion: 1, usedBy: [] }),
        entry({ key: siteBToken, version: 2, latestVersion: 2, revoked: true, description: null, usedBy: ["recipe/site-b"] }),
        entry({ key: "solo", visibility: "plain", value: "1", description: null, version: 1, latestVersion: 1, usedBy: [] }),
      ],
      missing: [],
      system: [],
      ...overrides,
    };
  }

  function Address() {
    return <output data-testid="address">{useLocation().search}</output>;
  }

  function renderFolder(route = "/variables") {
    return renderAt(
      <>
        <VariablesPage />
        <Address />
      </>,
      { path: "/variables", route },
    );
  }

  const entriesTable = () => screen.findByRole("region", { name: "Variable store entries" });
  const address = () => screen.getByTestId("address").textContent;

  it("opens at the root with folders first and a single-segment key as an entry", async () => {
    mockApi({ "GET /api/variables": () => store() });
    renderFolder();
    const rows = within(await entriesTable()).getAllByRole("row");
    expect(rows[1]).toHaveTextContent("proxmox");
    expect(rows[1]).toHaveTextContent("3 entries");
    expect(rows[2]).toHaveTextContent("solo");
    expect(screen.getByRole("button", { name: "Folders" })).toHaveAttribute("aria-current", "page");
    expect(document.body).not.toHaveTextContent(siteAToken);
  });

  it("moves through folders by row and path, and opens one from a link", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    renderFolder();
    await user.click(await screen.findByRole("link", { name: "proxmox" }));
    await user.click(screen.getByRole("link", { name: "site-a" }));
    expect(address()).toBe("?folder=proxmox/site-a");
    const here = screen.getByRole("navigation", { name: "Folder" });
    expect(within(here).getByText("site-a")).toHaveAttribute("aria-current", "page");
    expect(await within(await entriesTable()).findByText("api-token")).toHaveAttribute("title", siteAToken);

    await user.click(within(here).getByRole("link", { name: "proxmox" }));
    expect(screen.getByRole("link", { name: "site-b" })).toBeInTheDocument();
    await user.click(within(screen.getByRole("navigation", { name: "Folder" })).getByRole("link", { name: "Entries" }));
    expect(screen.getByRole("link", { name: "proxmox" })).toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Folder" })).not.toBeInTheDocument();
  });

  it("shows a key that is also a folder as both", async () => {
    const consoleError = vi.spyOn(console, "error");
    mockApi({ "GET /api/variables": () => store({ entries: [entry({ key: "a/b" }), entry({ key: "a/b/c" })] }) });
    renderFolder("/variables?folder=a");
    const table = await entriesTable();
    expect(within(table).getByRole("link", { name: "b" })).toBeInTheDocument();
    expect(within(table).getByRole("button", { name: "Update a/b" })).toBeInTheDocument();
    expect(consoleError).not.toHaveBeenCalled();
    consoleError.mockRestore();
  });

  it("rolls revoked entries and newer versions up to folder rows", async () => {
    mockApi({ "GET /api/variables": () => store() });
    renderFolder("/variables?folder=proxmox");
    const table = await entriesTable();
    const siteA = within(table).getByRole("row", { name: /site-a/ });
    expect(within(siteA).getByText("1 with a newer version")).toBeInTheDocument();
    expect(within(siteA).queryByText(/revoked/)).not.toBeInTheDocument();
    const siteB = within(table).getByRole("row", { name: /site-b/ });
    expect(siteB).toHaveTextContent("1 entry");
    expect(within(siteB).getByText("1 revoked")).toBeInTheDocument();
  });

  it("keeps secrets concealed and full keys on the controls inside a folder", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("isSecureContext", true);
    mockApi({
      "GET /api/variables": () => store(),
      "GET /api/variables/versions": () => [version(3, "active")],
    });
    renderFolder("/variables?folder=proxmox/site-a");
    const table = await entriesTable();
    expect(within(within(table).getByRole("row", { name: /api-token/ })).getByText("Concealed")).toBeInTheDocument();
    expect(within(table).getByText("visible-certificate")).toBeInTheDocument();

    await user.click(within(table).getByRole("button", { name: `Copy ${siteAToken}` }));
    expect(await navigator.clipboard.readText()).toBe(siteAToken);

    await user.click(within(table).getByRole("button", { name: `Versions of ${siteAToken}` }));
    expect(screen.getByRole("dialog", { name: `Versions of ${siteAToken}` })).toBeInTheDocument();
  });

  it("shows keys that are not set from every folder and during a search", async () => {
    const user = userEvent.setup();
    const missing = "dns/technitium/admin-password";
    mockApi({ "GET /api/variables": () => store({ missing: [{ key: missing, usedBy: ["guest/krb-dns-01"] }] }) });
    renderFolder("/variables?folder=proxmox/site-a");
    const card = await screen.findByRole("region", { name: "Referenced keys that are not set" });
    expect(card).toHaveTextContent(missing);
    await user.type(screen.getByRole("searchbox", { name: "Search all keys" }), "nothing-matches-this");
    expect(card).toHaveTextContent(missing);

    await user.click(within(card).getByRole("button", { name: "Set" }));
    expect(screen.getByLabelText("Key")).toHaveValue(missing);
  });

  it("remembers All keys with only the view's name, and lets a folder link override it once", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    const { unmount } = renderFolder();
    await user.click(await screen.findByRole("button", { name: "All keys" }));
    expect(within(await entriesTable()).getByText(siteAToken)).toBeInTheDocument();
    expect(window.localStorage.length).toBe(1);
    expect(window.localStorage.getItem(viewStorageKey)).toBe("all");
    unmount();

    const again = renderFolder();
    expect(within(await entriesTable()).getByText(siteAToken)).toBeInTheDocument();
    again.unmount();

    renderFolder("/variables?folder=proxmox");
    expect(await screen.findByRole("link", { name: "site-a" })).toBeInTheDocument();
    expect(window.localStorage.getItem(viewStorageKey)).toBe("all");
    await user.click(screen.getByRole("button", { name: "All keys" }));
    expect(address()).toBe("");
    expect(within(await entriesTable()).getByText(siteAToken)).toBeInTheDocument();
  });

  it("ignores a stored view it does not know", async () => {
    window.localStorage.setItem(viewStorageKey, "something-else");
    mockApi({ "GET /api/variables": () => store() });
    renderFolder();
    expect(await screen.findByRole("link", { name: "proxmox" })).toBeInTheDocument();
  });

  it("searches every folder by key, description, and consumer, never values or the URL", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    renderFolder("/variables?folder=proxmox/site-a");
    await entriesTable();
    const box = screen.getByRole("searchbox", { name: "Search all keys" });

    await user.type(box, "SITE-B");
    let table = await entriesTable();
    expect(within(table).getByText(siteBToken)).toBeInTheDocument();
    expect(within(table).queryByText(siteAToken)).not.toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Folder" })).not.toBeInTheDocument();
    expect(screen.getByText("1 of 4")).toBeInTheDocument();
    expect(address()).toBe("?folder=proxmox/site-a");

    await user.clear(box);
    await user.type(box, "root ca");
    expect(within(await entriesTable()).getByText(siteACa)).toBeInTheDocument();

    await user.clear(box);
    await user.type(box, "recipe/site-b");
    expect(within(await entriesTable()).getByText(siteBToken)).toBeInTheDocument();

    await user.clear(box);
    await user.type(box, "visible-certificate");
    expect(screen.getByText("No key matches this search")).toBeInTheDocument();
    expect(address()).toBe("?folder=proxmox/site-a");

    await user.clear(box);
    table = await entriesTable();
    expect(within(table).getByText("api-token")).toHaveAttribute("title", siteAToken);
    expect(screen.getByRole("navigation", { name: "Folder" })).toBeInTheDocument();
  });

  it("starts a new key inside the current folder", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({ "GET /api/variables": () => store(), "PUT /api/variables": () => undefined });
    renderFolder("/variables?folder=proxmox/site-a");
    await entriesTable();
    await user.click(screen.getByRole("button", { name: "Set a variable" }));
    const field = screen.getByLabelText<HTMLInputElement>("Key");
    expect(field).toHaveValue("proxmox/site-a/");
    expect(field).toHaveFocus();
    expect(field.selectionStart).toBe("proxmox/site-a/".length);
    expect(field).not.toHaveAttribute("aria-invalid");
    expect(screen.getByText(/Keys under system\/ belong/)).toHaveClass("field__hint");
    await user.type(screen.getByLabelText("Value"), "private-value");
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();

    await user.type(field, "backup/token");
    await user.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(requests.some((request) => request.method === "PUT")).toBe(true));
    expect(requests.find((request) => request.method === "PUT")?.body).toMatchObject({ key: "proxmox/site-a/backup/token" });
  });

  it("starts from an empty key at the root, in All keys, and during a search", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    renderFolder("/variables");
    await entriesTable();
    await user.click(screen.getByRole("button", { name: "Set a variable" }));
    expect(screen.getByLabelText("Key")).toHaveValue("");
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    await user.click(screen.getByRole("link", { name: "proxmox" }));
    await user.type(screen.getByRole("searchbox", { name: "Search all keys" }), "site");
    await user.click(screen.getByRole("button", { name: "Set a variable" }));
    expect(screen.getByLabelText("Key")).toHaveValue("");
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    await user.click(screen.getByRole("button", { name: "All keys" }));
    await user.click(screen.getByRole("button", { name: "Set a variable" }));
    expect(screen.getByLabelText("Key")).toHaveValue("");
  });

  it("refuses a segment the server would reject", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    renderFolder();
    await entriesTable();
    await user.click(screen.getByRole("button", { name: "Set a variable" }));
    await user.type(screen.getByLabelText("Key"), "proxmox/.hidden");
    await user.type(screen.getByLabelText("Value"), "value");
    expect(screen.getByLabelText("Key")).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it.each(["system", "system/git"])("never opens the reserved scope from ?folder=%s", async (folder) => {
    mockApi({
      "GET /api/variables": () => ({
        ...store(),
        entries: [...store().entries, entry({ key: "system/git/credential" })],
        missing: [{ key: "system/git/other", usedBy: [] }],
        system: [{ key: "system/git/credential", visibility: "secret", updatedAt: createdAt }],
      }),
    });
    renderFolder(`/variables?folder=${folder}`);
    expect(await screen.findByRole("link", { name: "proxmox" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "system" })).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("credential");
    expect(screen.getByText("4")).toBeInTheDocument();
  });

  it("lists a single-segment key named system like any other", async () => {
    mockApi({ "GET /api/variables": () => store({ entries: [entry({ key: "system" }), entry({ key: "System/x" })] }) });
    renderFolder();
    const table = await entriesTable();
    expect(within(table).getByRole("button", { name: "Update system" })).toBeInTheDocument();
    expect(within(table).getByRole("link", { name: "System" })).toBeInTheDocument();
  });

  it("falls back to the deepest folder that still exists", async () => {
    mockApi({ "GET /api/variables": () => store() });
    renderFolder("/variables?folder=proxmox/site-a/gone/deeper");
    expect(await within(await entriesTable()).findByText("api-token")).toHaveAttribute("title", siteAToken);
    expect(within(screen.getByRole("navigation", { name: "Folder" })).getByText("site-a")).toHaveAttribute("aria-current", "page");
  });

  it("deletes a key that nothing uses, whatever its history", async () => {
    const user = userEvent.setup();
    let entries = store().entries;
    const { requests } = mockApi({
      "GET /api/variables": () => store({ entries }),
      "DELETE /api/variables": () => {
        entries = entries.filter((e) => e.key !== siteACa);
      },
    });
    renderFolder("/variables?folder=proxmox/site-a");
    await user.click(await screen.findByRole("button", { name: `Delete ${siteACa}` }));
    const dialog = screen.getByRole("dialog", { name: `Delete ${siteACa}?` });
    expect(dialog).toHaveTextContent("What each run was given stays on record");
    expect(dialog).not.toHaveTextContent("It is still used by these");
    await user.click(within(dialog).getByRole("button", { name: "Delete" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(requests.find((r) => r.method === "DELETE")?.search).toBe(`?key=${encodeURIComponent(siteACa)}`);
    expect(screen.queryByTitle(siteACa)).not.toBeInTheDocument();
  });

  it("names what still uses a key, and shows the server's exact reason when deleting it is refused", async () => {
    const user = userEvent.setup();
    const reason = `'${siteBToken}' is still used by recipe/site-b, run #12. Delete it once nothing uses it.`;
    mockApi({
      "GET /api/variables": () => store(),
      "DELETE /api/variables": () => problem(400, reason),
    });
    renderFolder("/variables?folder=proxmox/site-b");
    await user.click(await screen.findByRole("button", { name: `Delete ${siteBToken}` }));
    const dialog = screen.getByRole("dialog", { name: `Delete ${siteBToken}?` });
    expect(dialog).toHaveTextContent("It is still used by these");
    expect(within(dialog).getByText("recipe/site-b")).toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "Delete" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(reason);
    expect(screen.getByRole("dialog", { name: `Delete ${siteBToken}?` })).toBeInTheDocument();
  });

  it("lands on the parent when the last key of a folder is deleted", async () => {
    const user = userEvent.setup();
    let entries = store().entries;
    mockApi({
      "GET /api/variables": () => store({ entries }),
      "DELETE /api/variables": () => {
        entries = entries.filter((e) => e.key !== siteBToken);
      },
    });
    renderFolder("/variables?folder=proxmox/site-b");
    await user.click(await screen.findByRole("button", { name: `Delete ${siteBToken}` }));
    await user.click(within(screen.getByRole("dialog", { name: `Delete ${siteBToken}?` })).getByRole("button", { name: "Delete" }));
    expect(await screen.findByRole("link", { name: "site-a" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "site-b" })).not.toBeInTheDocument();
    expect(within(screen.getByRole("navigation", { name: "Folder" })).getByText("proxmox")).toHaveAttribute("aria-current", "page");
  });
});
