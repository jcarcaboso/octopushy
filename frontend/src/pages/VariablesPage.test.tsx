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
    lines: 1,
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
    activationNote: status === "active" || status === "previous" ? "Checked the provider API" : null,
    revokedAt: status === "revoked" ? createdAt : null,
    revokedBy: status === "revoked" ? "bob" : null,
    revocationNote: status === "revoked" ? "Retired credential" : null,
    usedBy: number === 3 ? ["run/example/step"] : [],
    ...overrides,
  };
}

function Address() {
  return <output data-testid="address">{useLocation().search}</output>;
}

function renderPage(route = "/variables") {
  return renderAt(
    <>
      <VariablesPage />
      <Address />
    </>,
    { path: "/variables", route },
  );
}

const address = () => decodeURIComponent(screen.getByTestId("address").textContent ?? "");
const list = () => screen.findByRole("region", { name: "Variable store entries" });
const pane = () => screen.findByRole("region", { name: "Selected entry" });
const tree = () => screen.getByRole("navigation", { name: "Folders" });
const revealOf = (values: Record<string, string | null>) => ({ body }: { body: unknown }) => ({
  values: (body as { keys: string[] }).keys.map((k) => ({ key: k, value: values[k] ?? null })),
});
const opened = (k: string) => `/variables?key=${encodeURIComponent(k)}`;

afterEach(() => {
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

describe("VariablesPage entry pane", () => {
  const store = (current = entry()) => ({ entries: [current, entry({ key: "proxmox/site-b/api-token", version: 1, latestVersion: 1 })], missing: [], system: [] });

  it("opens the entry named in the address, with nothing open without one", async () => {
    mockApi({ "GET /api/variables": () => store() });
    const { unmount } = renderPage();
    expect(await screen.findByText("No entry open")).toBeInTheDocument();
    unmount();

    renderPage(opened(key));
    const selected = await pane();
    expect(within(selected).getByRole("heading", { name: "api-token" })).toBeInTheDocument();
    expect(within(selected).getByText("proxmox/site-a/")).toBeInTheDocument();
    expect(within(selected).getByText("v3 active")).toBeInTheDocument();
    expect(within(selected).getByText("Latest v4")).toBeInTheDocument();
  });

  it("modifies a secret from its revealed value and saves it active with the note and the expected version", async () => {
    const user = userEvent.setup();
    let saved = false;
    const { requests } = mockApi({
      "GET /api/variables": () => store(entry(saved ? { version: 5, latestVersion: 5 } : {})),
      "POST /api/variables/reveal": revealOf({ [key]: "old-token" }),
      "PUT /api/variables": () => {
        saved = true;
      },
    });
    renderPage(opened(key));
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Modify" }));

    const box = await within(selected).findByLabelText("Value");
    expect(box).toHaveValue("old-token");
    expect(requests.filter((r) => r.path.endsWith("/reveal")).map((r) => r.body)).toEqual([{ keys: [key] }]);
    expect(within(selected).getByRole("button", { name: "Save and make active" })).toBeDisabled();
    expect(within(selected).getByText("Nothing is changed yet.")).toBeInTheDocument();
    expect(within(selected).getByText(/Saved as v5 and active right away/)).toHaveTextContent("get a setup run");

    await user.clear(box);
    await user.type(box, "new-token");
    expect(within(selected).getByText("1 line changed.")).toBeInTheDocument();
    await user.type(within(selected).getByLabelText("Note, optional"), "Rotated after the audit");
    await user.click(within(selected).getByRole("button", { name: "Save and make active" }));

    await waitFor(() => expect(requests.find((r) => r.method === "PUT")?.body).toEqual({
      key,
      value: "new-token",
      visibility: "secret",
      description: "API credential",
      note: "Rotated after the audit",
      expectedCurrentVersion: 3,
    }));
    await waitFor(() => expect(within(selected).queryByLabelText("Value")).not.toBeInTheDocument());
    expect(address()).toBe(`?key=${key}`);
    expect(await within(selected).findByText("v5 active")).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("new-token");
    expect(document.body).not.toHaveTextContent("old-token");
  });

  it("shows the server's message when another version became active meanwhile, and refreshes", async () => {
    const user = userEvent.setup();
    let loads = 0;
    mockApi({
      "GET /api/variables": () => {
        loads += 1;
        return store();
      },
      "PUT /api/variables": () => problem(409, "v4 became active meanwhile."),
    });
    renderPage(opened(key));
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Update" }));
    await user.type(within(selected).getByLabelText("Value"), "replacement");
    await user.click(within(selected).getByRole("button", { name: "Save and make active" }));

    const alert = await within(selected).findByRole("alert");
    expect(alert).toHaveTextContent("v4 became active meanwhile.");
    expect(alert).not.toHaveTextContent("replacement");
    const before = loads;
    await user.click(within(alert).getByRole("button", { name: "Refresh" }));
    await waitFor(() => expect(loads).toBeGreaterThan(before));
    expect(within(selected).queryByRole("alert")).not.toBeInTheDocument();
    expect(within(selected).getByLabelText("Value")).toHaveValue("replacement");
  });

  it("asks before leaving an editor with changes, and stays when told to", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    window.localStorage.setItem(viewStorageKey, "all");
    renderPage(opened(key));
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Update" }));
    await user.type(within(selected).getByLabelText("Value"), "unsaved");

    const other = within(await list()).getByRole("link", { name: /site-b/ });
    await user.click(other);
    let dialog = screen.getByRole("dialog", { name: "Drop the changes?" });
    await user.click(within(dialog).getByRole("button", { name: "Keep as is" }));
    expect(address()).toBe(`?key=${key}`);
    expect(within(selected).getByLabelText("Value")).toHaveValue("unsaved");

    await user.click(within(selected).getByRole("button", { name: "Close the entry" }));
    dialog = screen.getByRole("dialog", { name: "Drop the changes?" });
    await user.click(within(dialog).getByRole("button", { name: "Drop them" }));
    expect(address()).toBe("");
    expect(await screen.findByText("No entry open")).toBeInTheDocument();
  });

  it("leaves an editor without changes at once", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    window.localStorage.setItem(viewStorageKey, "all");
    renderPage(opened(key));
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Update" }));
    await user.click(within(await list()).getByRole("link", { name: /site-b/ }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(address()).toBe("?key=proxmox/site-b/api-token");
  });

  it("saves a replacement as a candidate without changing the active version", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/variables": () => store(),
      "POST /api/variables/candidates": () => ({ version: 5 }),
    });
    renderPage(opened(key));
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Update" }));
    await user.type(within(selected).getByLabelText("Value"), "candidate-value");
    await user.click(within(selected).getByRole("button", { name: "Save as a candidate" }));

    expect(await within(selected).findByText(/Saved as v5, a candidate/)).toBeInTheDocument();
    expect(requests.find((r) => r.path.endsWith("/candidates"))?.body).toEqual({ key, value: "candidate-value", description: "API credential" });
    expect(requests.some((r) => r.method === "PUT")).toBe(false);
    expect(document.body).not.toHaveTextContent("candidate-value");
  });

  it("never echoes a rejected value in errors", async () => {
    const user = userEvent.setup();
    const credential = "do-not-echo-this-credential";
    mockApi({
      "GET /api/variables": () => store(),
      "PUT /api/variables": () => problem(400, `Rejected ${credential}`),
    });
    renderPage(opened(key));
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Update" }));
    await user.type(within(selected).getByLabelText("Value"), credential);
    await user.click(within(selected).getByRole("button", { name: "Save and make active" }));
    const alert = await within(selected).findByRole("alert");
    expect(alert).toHaveTextContent("The value could not be saved.");
    expect(alert).not.toHaveTextContent(credential);
  });

  it("modifies a plain value without revealing anything", async () => {
    const user = userEvent.setup();
    const plain = entry({ key: "region", visibility: "plain", value: "eu-west", version: 1, latestVersion: 1 });
    const { requests } = mockApi({ "GET /api/variables": () => ({ entries: [plain], missing: [], system: [] }), "PUT /api/variables": () => undefined });
    renderPage(opened("region"));
    const selected = await pane();
    expect(within(selected).queryByRole("button", { name: "Versions" })).not.toBeInTheDocument();
    expect(within(selected).queryByRole("button", { name: "Update" })).not.toBeInTheDocument();
    await user.click(within(selected).getByRole("button", { name: "Modify" }));
    const box = within(selected).getByLabelText("Value");
    await user.clear(box);
    await user.type(box, "eu-central");
    await user.click(within(selected).getByRole("button", { name: "Save and make active" }));
    await waitFor(() => expect(requests.find((r) => r.method === "PUT")?.body).toMatchObject({ key: "region", value: "eu-central", visibility: "plain", expectedCurrentVersion: 1 }));
    expect(requests.some((r) => r.path.endsWith("/reveal"))).toBe(false);
  });

  it("reports what changed in an env file and warns about its problems", async () => {
    const user = userEvent.setup();
    const env = "PORT=3000\nSMTP_PASSWORD=old";
    mockApi({
      "GET /api/variables": () => store(entry({ lines: 2 })),
      "POST /api/variables/reveal": revealOf({ [key]: env }),
    });
    renderPage(opened(key));
    const selected = await pane();
    expect(within(selected).getByText("Concealed. 2 lines.")).toBeInTheDocument();
    await user.click(within(selected).getByRole("button", { name: "Modify" }));
    const box = await within(selected).findByLabelText("Value");
    expect(within(selected).getByText("Reads as .env with 2 variables, no problems.")).toBeInTheDocument();
    await user.clear(box);
    await user.type(box, "PORT=3000{Enter}SMTP_PASSWORD=new{Enter}SMTP_PORT=587{Enter}oops");
    expect(within(selected).getByText("SMTP_PASSWORD changed, SMTP_PORT added.")).toBeInTheDocument();
    expect(within(selected).getByText("Line 4: there is no = between the name and the value")).toBeInTheDocument();
    expect(within(selected).getByRole("button", { name: "Save and make active" })).toBeEnabled();
  });

  it("edits the description without sending a value", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({ "GET /api/variables": () => store(), "PUT /api/variables": () => undefined });
    renderPage(opened(key));
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Details" }));
    expect(within(selected).getByText("recipe/site-a")).toBeInTheDocument();
    await user.click(within(selected).getByRole("button", { name: "Edit the description" }));
    const field = within(selected).getByRole("textbox", { name: "Description" });
    await user.clear(field);
    await user.type(field, "Updated description");
    await user.click(within(selected).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(requests.find((r) => r.method === "PUT")?.body).toEqual({
      key,
      value: null,
      visibility: null,
      description: "Updated description",
    }));
  });
});

describe("VariablesPage versions", () => {
  const variables = (current = entry()) => ({ entries: [current], missing: [], system: [] });

  async function openVersions(user: ReturnType<typeof userEvent.setup>) {
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Versions" }));
    return selected;
  }

  it("makes a candidate active with an optional note, against the active version", async () => {
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
      "POST /api/variables/promote": () => {
        promoted = true;
      },
    });
    renderPage(opened(key));
    const selected = await openVersions(user);
    expect(await within(selected).findByText("run/example/step")).toBeInTheDocument();
    expect(within(selected).getAllByText(/alice/).length).toBeGreaterThan(0);
    expect(within(selected).getByText("Retired credential")).toBeInTheDocument();
    expect(within(selected).queryByRole("button", { name: "Revoke v1" })).not.toBeInTheDocument();

    await user.click(within(selected).getByRole("button", { name: "Make active v4" }));
    await user.click(within(selected).getByRole("button", { name: "Make v4 active" }));
    await waitFor(() => expect(requests.find((r) => r.path.endsWith("/promote"))?.body).toEqual({
      key,
      version: 4,
      expectedCurrentVersion: 3,
      verificationNote: null,
    }));
    expect(new URLSearchParams(requests.find((r) => r.path.endsWith("/versions"))?.search).get("key")).toBe(key);
    expect(await within(selected).findByText("v4 active")).toBeInTheDocument();
  });

  it("makes a previous version active again with a note", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/variables": () => variables(),
      "GET /api/variables/versions": () => [version(3, "active"), version(2, "previous")],
      "POST /api/variables/promote": () => undefined,
    });
    renderPage(opened(key));
    const selected = await openVersions(user);
    await user.click(await within(selected).findByRole("button", { name: "Make active again v2" }));
    const note = within(selected).getByLabelText("Note, optional");
    expect(note).toHaveFocus();
    await user.type(note, "Rolling back");
    await user.click(within(selected).getByRole("button", { name: "Make v2 active" }));
    await waitFor(() => expect(requests.find((r) => r.path.endsWith("/promote"))?.body).toEqual({
      key,
      version: 2,
      expectedCurrentVersion: 3,
      verificationNote: "Rolling back",
    }));
  });

  it("shows a conflict when making a version active, and reloads", async () => {
    const user = userEvent.setup();
    mockApi({
      "GET /api/variables": () => variables(),
      "GET /api/variables/versions": () => [version(4, "candidate"), version(3, "active")],
      "POST /api/variables/promote": () => problem(409, "Active version changed"),
    });
    renderPage(opened(key));
    const selected = await openVersions(user);
    await user.click(await within(selected).findByRole("button", { name: "Make active v4" }));
    await user.click(within(selected).getByRole("button", { name: "Make v4 active" }));
    const alert = await within(selected).findByRole("alert");
    expect(alert).toHaveTextContent("Active version changed");
    await user.click(within(alert).getByRole("button", { name: "Refresh" }));
    expect(within(selected).queryByRole("alert")).not.toBeInTheDocument();
    expect(within(selected).queryByLabelText("Note, optional")).not.toBeInTheDocument();
  });

  it("requires a reason to revoke and shows the revoked state", async () => {
    const user = userEvent.setup();
    let revoked = false;
    const { requests } = mockApi({
      "GET /api/variables": () => variables(entry({ revoked, lines: revoked ? null : 1 })),
      "GET /api/variables/versions": () => [version(4, "candidate"), version(3, revoked ? "revoked" : "active")],
      "POST /api/variables/revoke": () => {
        revoked = true;
      },
    });
    renderPage(opened(key));
    const selected = await openVersions(user);
    await user.click(await within(selected).findByRole("button", { name: "Revoke v3" }));
    expect(within(selected).getByText(/cannot retract already delivered credentials/)).toBeInTheDocument();
    const confirm = within(selected).getByRole("button", { name: "Revoke v3" });
    expect(confirm).toBeDisabled();
    await user.type(within(selected).getByLabelText("Reason for revoking"), "   ");
    expect(confirm).toBeDisabled();
    await user.type(within(selected).getByLabelText("Reason for revoking"), "Credential retired ");
    await user.click(confirm);
    await waitFor(() => expect(requests.find((r) => r.path.endsWith("/revoke"))?.body).toEqual({ key, version: 3, reason: "Credential retired" }));
    expect(await within(selected).findByText("The active version is revoked")).toBeInTheDocument();
    expect(within(selected).getByText("v3 revoked")).toBeInTheDocument();
  });
});

describe("VariablesPage new entry", () => {
  const store = () => ({
    entries: [entry(), entry({ key: "proxmox/site-a/ca", visibility: "plain", value: "cert", version: 1, latestVersion: 1 })],
    missing: [{ key: "dns/technitium/admin-password", usedBy: ["guest/krb-dns-01"] }],
    system: [],
  });

  it("starts inside the current folder, saves with the key required to be new, and opens the new key", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({ "GET /api/variables": () => store(), "PUT /api/variables": () => undefined });
    renderPage("/variables?folder=proxmox/site-a");
    await list();
    await user.click(screen.getByRole("button", { name: "Set a variable" }));
    const form = await screen.findByRole("region", { name: "New entry" });
    const field = within(form).getByLabelText<HTMLInputElement>("Key");
    expect(field).toHaveValue("proxmox/site-a/");
    expect(field).toHaveFocus();
    expect(field.selectionStart).toBe("proxmox/site-a/".length);
    await user.type(within(form).getByLabelText("Value"), "private-value");
    expect(within(form).getByRole("button", { name: "Save" })).toBeDisabled();

    await user.type(field, "backup/token");
    await user.click(within(form).getByRole("button", { name: "Plain" }));
    await user.click(within(form).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(requests.find((r) => r.method === "PUT")?.body).toEqual({
      key: "proxmox/site-a/backup/token",
      value: "private-value",
      visibility: "plain",
      description: null,
      expectedCurrentVersion: 0,
    }));
    await waitFor(() => expect(address()).toBe("?folder=proxmox/site-a/backup&key=proxmox/site-a/backup/token"));
  });

  it("offers to open a key that is already set instead of saving it", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    renderPage("/variables?folder=proxmox/site-a");
    await list();
    await user.click(screen.getByRole("button", { name: "Set a variable" }));
    const form = await screen.findByRole("region", { name: "New entry" });
    await user.type(within(form).getByLabelText("Key"), "ca");
    await user.type(within(form).getByLabelText("Value"), "x");
    expect(within(form).getByText("This key is already set.")).toBeInTheDocument();
    expect(within(form).getByRole("button", { name: "Save" })).toBeDisabled();
    await user.click(within(form).getByRole("button", { name: "Open it" }));
    await user.click(within(screen.getByRole("dialog", { name: "Drop the changes?" })).getByRole("button", { name: "Drop them" }));
    expect(address()).toBe("?folder=proxmox/site-a&key=proxmox/site-a/ca");
  });

  it("starts from a key that is referenced but not set, from every folder", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    renderPage("/variables?folder=proxmox/site-a");
    const strip = await screen.findByRole("region", { name: "Referenced keys that are not set" });
    expect(strip).toHaveTextContent("1 key is referenced but not set");
    expect(strip).toHaveTextContent("dns/technitium/admin-password");
    expect(strip).toHaveTextContent("Used by guest/krb-dns-01");
    await user.type(screen.getByRole("searchbox", { name: "Search all keys" }), "nothing-matches");
    expect(strip).toHaveTextContent("dns/technitium/admin-password");
    await user.click(within(strip).getByRole("button", { name: "Set dns/technitium/admin-password" }));
    expect(within(await screen.findByRole("region", { name: "New entry" })).getByLabelText("Key")).toHaveValue("dns/technitium/admin-password");
  });

  it("starts from an empty key at the root and in All keys, and refuses keys the server would", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({ "GET /api/variables": () => store() });
    renderPage();
    await list();
    await user.click(screen.getByRole("button", { name: "Set a variable" }));
    let form = await screen.findByRole("region", { name: "New entry" });
    expect(within(form).getByLabelText("Key")).toHaveValue("");
    await user.click(within(form).getAllByRole("button", { name: "Cancel" })[0]);

    await user.click(screen.getByRole("button", { name: "All keys" }));
    await user.click(screen.getByRole("button", { name: "Set a variable" }));
    form = await screen.findByRole("region", { name: "New entry" });
    const field = within(form).getByLabelText("Key");
    expect(field).toHaveValue("");
    await user.type(within(form).getByLabelText("Value"), "never-sent");
    await user.type(field, "proxmox/.hidden");
    expect(field).toHaveAttribute("aria-invalid", "true");
    expect(within(form).getByRole("button", { name: "Save" })).toBeDisabled();
    await user.clear(field);
    await user.type(field, "system/forbidden");
    expect(within(form).getByRole("button", { name: "Save" })).toBeDisabled();
    expect(requests.every((r) => r.method === "GET")).toBe(true);
  });
});

describe("VariablesPage folders", () => {
  const siteAToken = "proxmox/site-a/api-token";
  const siteACa = "proxmox/site-a/ca";
  const siteBToken = "proxmox/site-b/api-token";

  function store(overrides: { entries?: Variable[] } = {}) {
    return {
      entries: [
        entry({ key: siteAToken }),
        entry({ key: siteACa, visibility: "plain", value: "visible-certificate", description: "Root CA", version: 1, latestVersion: 1, usedBy: [] }),
        entry({ key: siteBToken, version: 2, latestVersion: 2, revoked: true, lines: null, description: null, usedBy: ["recipe/site-b"] }),
        entry({ key: "solo", visibility: "plain", value: "1", description: null, version: 1, latestVersion: 1, usedBy: [] }),
      ],
      missing: [],
      system: [],
      ...overrides,
    };
  }

  it("opens at the root with folders first and a single-segment key as an entry", async () => {
    mockApi({ "GET /api/variables": () => store() });
    renderPage();
    const items = within(await list()).getAllByRole("listitem");
    expect(items[0]).toHaveTextContent("proxmox");
    expect(items[0]).toHaveTextContent("3 entries");
    expect(items[1]).toHaveTextContent("solo");
    expect(screen.getByRole("button", { name: "Folders" })).toHaveAttribute("aria-current", "page");
    expect(within(tree()).getByRole("link", { name: /Entries/ })).toHaveAttribute("aria-current", "page");
    expect(document.body).not.toHaveTextContent(siteAToken);
  });

  it("moves through folders by the list, the tree and the path", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    renderPage();
    await user.click(within(await list()).getByRole("link", { name: /proxmox/ }));
    await user.click(within(await list()).getByRole("link", { name: /site-a/ }));
    expect(address()).toBe("?folder=proxmox/site-a");
    const here = screen.getByRole("navigation", { name: "Folder" });
    expect(within(here).getByText("site-a")).toHaveAttribute("aria-current", "page");
    expect(within(tree()).getByRole("link", { name: /site-a/ })).toHaveAttribute("aria-current", "page");
    expect(within(await list()).getByRole("link", { name: /api-token/ })).toHaveAttribute("title", siteAToken);

    await user.click(within(tree()).getByRole("link", { name: /site-b/ }));
    expect(address()).toBe("?folder=proxmox/site-b");
    await user.click(within(screen.getByRole("navigation", { name: "Folder" })).getByRole("link", { name: "Entries" }));
    expect(address()).toBe("?folder=");
    expect(screen.queryByRole("navigation", { name: "Folder" })).not.toBeInTheDocument();
  });

  it("expands and collapses folders in the tree", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    renderPage();
    await list();
    expect(within(tree()).queryByRole("link", { name: /site-a/ })).not.toBeInTheDocument();
    await user.click(within(tree()).getByRole("button", { name: "Expand proxmox" }));
    expect(within(tree()).getByRole("link", { name: /site-a 2/ })).toBeInTheDocument();
    await user.click(within(tree()).getByRole("button", { name: "Collapse proxmox" }));
    expect(within(tree()).queryByRole("link", { name: /site-a/ })).not.toBeInTheDocument();
  });

  it("opens an entry from the list, marks it, and keeps the folder in the address", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    renderPage("/variables?folder=proxmox/site-a");
    const rows = await list();
    expect(within(within(rows).getByRole("link", { name: /api-token/ })).getByText("Concealed")).toBeInTheDocument();
    expect(within(rows).getByText("visible-certificate")).toBeInTheDocument();
    await user.click(within(rows).getByRole("link", { name: /api-token/ }));
    expect(address()).toBe(`?folder=proxmox/site-a&key=${siteAToken}`);
    expect(within(rows).getByRole("link", { name: /api-token/ })).toHaveAttribute("aria-current", "true");
    expect(within(await pane()).getByRole("heading", { name: "api-token" })).toBeInTheDocument();
  });

  it("copies the full key from the pane", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("isSecureContext", true);
    mockApi({ "GET /api/variables": () => store() });
    renderPage(opened(siteAToken));
    await user.click(within(await pane()).getByRole("button", { name: `Copy ${siteAToken}` }));
    expect(await navigator.clipboard.readText()).toBe(siteAToken);
  });

  it("shows a key that is also a folder as both", async () => {
    const consoleError = vi.spyOn(console, "error");
    mockApi({ "GET /api/variables": () => store({ entries: [entry({ key: "a/b" }), entry({ key: "a/b/c" })] }) });
    renderPage("/variables?folder=a");
    const rows = await list();
    expect(within(rows).getByRole("link", { name: /^b 1 entry/ })).toBeInTheDocument();
    expect(within(rows).getByRole("link", { name: /^b .*v3/ })).toHaveAttribute("title", "a/b");
    expect(consoleError).not.toHaveBeenCalled();
    consoleError.mockRestore();
  });

  it("rolls revoked entries and newer versions up to folder rows, and marks them on entries", async () => {
    mockApi({ "GET /api/variables": () => store() });
    renderPage("/variables?folder=proxmox");
    const rows = await list();
    const siteA = within(rows).getByRole("link", { name: /site-a/ });
    expect(within(siteA).getByText("1 with a newer version")).toBeInTheDocument();
    expect(within(siteA).queryByText(/revoked/)).not.toBeInTheDocument();
    const siteB = within(rows).getByRole("link", { name: /site-b/ });
    expect(siteB).toHaveTextContent("1 entry");
    expect(within(siteB).getByText("1 revoked")).toBeInTheDocument();
  });

  it("remembers All keys with only the view's name, and lets a folder link override it once", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    const { unmount } = renderPage();
    await user.click(await screen.findByRole("button", { name: "All keys" }));
    expect(within(await list()).getByRole("link", { name: /api-token.*proxmox\/site-a\// })).toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Folders" })).not.toBeInTheDocument();
    expect(window.localStorage.length).toBe(1);
    expect(window.localStorage.getItem(viewStorageKey)).toBe("all");
    unmount();

    const again = renderPage();
    expect(await screen.findByText("4 keys")).toBeInTheDocument();
    again.unmount();

    renderPage("/variables?folder=proxmox");
    expect(within(await list()).getByRole("link", { name: /site-a/ })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "All keys" }));
    expect(address()).toBe("");
  });

  it("ignores a stored view it does not know", async () => {
    window.localStorage.setItem(viewStorageKey, "something-else");
    mockApi({ "GET /api/variables": () => store() });
    renderPage();
    expect(within(await list()).getByRole("link", { name: /proxmox/ })).toBeInTheDocument();
  });

  it("searches every folder by key, description, and consumer, never values or the URL", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store() });
    renderPage("/variables?folder=proxmox/site-a");
    await list();
    const box = screen.getByRole("searchbox", { name: "Search all keys" });

    await user.type(box, "SITE-B");
    expect(within(await list()).getByRole("link", { name: /api-token/ })).toHaveAttribute("title", siteBToken);
    expect(screen.queryByRole("navigation", { name: "Folder" })).not.toBeInTheDocument();
    expect(screen.getByText("1 of 4")).toBeInTheDocument();
    expect(address()).toBe("?folder=proxmox/site-a");

    await user.clear(box);
    await user.type(box, "root ca");
    expect(within(await list()).getByRole("link", { name: /ca/ })).toHaveAttribute("title", siteACa);

    await user.clear(box);
    await user.type(box, "recipe/site-b");
    expect(within(await list()).getByRole("link", { name: /api-token/ })).toHaveAttribute("title", siteBToken);

    await user.clear(box);
    await user.type(box, "visible-certificate");
    expect(screen.getByText("No key matches this search")).toBeInTheDocument();

    await user.clear(box);
    expect(screen.getByRole("navigation", { name: "Folder" })).toBeInTheDocument();
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
    renderPage(`/variables?folder=${folder}`);
    expect(within(await list()).getByRole("link", { name: /proxmox/ })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /^system/ })).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("credential");
    expect(screen.queryByRole("region", { name: "Referenced keys that are not set" })).not.toBeInTheDocument();
  });

  it("lists a single-segment key named system like any other", async () => {
    mockApi({ "GET /api/variables": () => store({ entries: [entry({ key: "system" }), entry({ key: "System/x" })] }) });
    renderPage();
    const rows = await list();
    expect(within(rows).getByRole("link", { name: /^system .*v3/ })).toBeInTheDocument();
    expect(within(rows).getByRole("link", { name: /^System/ })).toBeInTheDocument();
  });

  it("falls back to the deepest folder that still exists", async () => {
    mockApi({ "GET /api/variables": () => store() });
    renderPage("/variables?folder=proxmox/site-a/gone/deeper");
    expect(within(await list()).getByRole("link", { name: /api-token/ })).toHaveAttribute("title", siteAToken);
    expect(within(screen.getByRole("navigation", { name: "Folder" })).getByText("site-a")).toHaveAttribute("aria-current", "page");
  });

  it("deletes a key that nothing uses and closes it", async () => {
    const user = userEvent.setup();
    let entries = store().entries;
    const { requests } = mockApi({
      "GET /api/variables": () => store({ entries }),
      "DELETE /api/variables": () => {
        entries = entries.filter((e) => e.key !== siteACa);
      },
    });
    renderPage(`/variables?folder=proxmox/site-a&key=${siteACa}`);
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Details" }));
    await user.click(within(selected).getByRole("button", { name: `Delete ${siteACa}` }));
    const dialog = screen.getByRole("dialog", { name: `Delete ${siteACa}?` });
    expect(dialog).toHaveTextContent("What each run was given stays on record");
    expect(dialog).not.toHaveTextContent("It is still used by these");
    await user.click(within(dialog).getByRole("button", { name: "Delete" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(requests.find((r) => r.method === "DELETE")?.search).toBe(`?key=${encodeURIComponent(siteACa)}`);
    expect(address()).toBe("?folder=proxmox/site-a");
    await waitFor(() => expect(screen.queryByTitle(siteACa)).not.toBeInTheDocument());
  });

  it("names what still uses a key, and shows the server's exact reason when deleting it is refused", async () => {
    const user = userEvent.setup();
    const reason = `'${siteBToken}' is still used by recipe/site-b, run #12. Delete it once nothing uses it.`;
    mockApi({ "GET /api/variables": () => store(), "DELETE /api/variables": () => problem(400, reason) });
    renderPage(opened(siteBToken));
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Details" }));
    await user.click(within(selected).getByRole("button", { name: `Delete ${siteBToken}` }));
    const dialog = screen.getByRole("dialog", { name: `Delete ${siteBToken}?` });
    expect(dialog).toHaveTextContent("It is still used by these");
    expect(within(dialog).getByText("recipe/site-b")).toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "Delete" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(reason);
  });
});

describe("VariablesPage revealing secrets", () => {
  beforeEach(() => window.localStorage.setItem(viewStorageKey, "all"));

  const other = "proxmox/site-b/api-token";
  const env = "# app\nPORT=3000\nTOKEN=abc";
  const store = () => ({
    entries: [
      entry(),
      entry({ key: other, version: 1, latestVersion: 1, lines: 3 }),
      entry({ key: "region", visibility: "plain", value: "eu-west", version: 1, latestVersion: 1 }),
    ],
    missing: [],
    system: [],
  });
  const secrets = { [key]: "token-a-value", [other]: env };
  const reveals = (requests: { method: string; path: string; body?: unknown }[]) =>
    requests.filter((r) => r.method === "POST" && r.path.endsWith("/api/variables/reveal")).map((r) => r.body);

  it("reveals and hides one secret in the pane, asking only for its key", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({ "GET /api/variables": () => store(), "POST /api/variables/reveal": revealOf(secrets) });
    renderPage(opened(key));
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Reveal" }));
    expect(await within(selected).findByText("token-a-value")).toBeInTheDocument();
    expect(within(await list()).getByText("token-a-value")).toBeInTheDocument();
    expect(reveals(requests)).toEqual([{ keys: [key] }]);
    await user.click(within(selected).getByRole("button", { name: `Hide ${key}` }));
    expect(screen.queryByText("token-a-value")).not.toBeInTheDocument();
  });

  it("shows a revealed file with line numbers and lets it be hidden", async () => {
    const user = userEvent.setup();
    mockApi({ "GET /api/variables": () => store(), "POST /api/variables/reveal": revealOf(secrets) });
    renderPage(opened(other));
    const selected = await pane();
    expect(within(selected).getByText("3 lines")).toBeInTheDocument();
    await user.click(within(selected).getByRole("button", { name: "Reveal" }));
    const viewer = await within(selected).findByRole("group", { name: `Value of ${other}` });
    expect(viewer).toHaveTextContent("PORT=3000");
    expect(within(selected).getByRole("button", { name: "Download" })).toBeInTheDocument();
    await user.click(within(selected).getByRole("button", { name: "Hide" }));
    expect(within(selected).getByText("Concealed. 3 lines.")).toBeInTheDocument();
  });

  it("reveals every listed secret with one request, then hides them all", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({ "GET /api/variables": () => store(), "POST /api/variables/reveal": revealOf(secrets) });
    renderPage();
    const rows = await list();
    await user.click(screen.getByRole("button", { name: "Reveal all 2 secrets listed" }));
    expect(await within(rows).findByText("token-a-value")).toBeInTheDocument();
    expect(within(rows).getByText("3 lines")).toBeInTheDocument();
    expect(reveals(requests)).toEqual([{ keys: [key, other] }]);
    await user.click(screen.getByRole("button", { name: "Hide all" }));
    expect(within(rows).getAllByText("Concealed")).toHaveLength(1);
  });

  it("reveals only the secrets a search lists", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({ "GET /api/variables": () => store(), "POST /api/variables/reveal": revealOf(secrets) });
    renderPage();
    await user.type(await screen.findByRole("searchbox", { name: "Search all keys" }), "site-b");
    await user.click(screen.getByRole("button", { name: "Reveal the 1 secret listed" }));
    expect(await screen.findByRole("button", { name: "Hide all" })).toBeInTheDocument();
    expect(reveals(requests)).toEqual([{ keys: [other] }]);
  });

  it("says when a secret has no usable version, and shows the failure of a reveal", async () => {
    const user = userEvent.setup();
    let fail = false;
    mockApi({
      "GET /api/variables": () => store(),
      "POST /api/variables/reveal": (request) => (fail ? problem(500, "boom") : revealOf({})(request)),
    });
    renderPage(opened(key));
    const selected = await pane();
    await user.click(within(selected).getByRole("button", { name: "Reveal" }));
    expect(await within(selected).findByText("No usable version.")).toBeInTheDocument();

    fail = true;
    await user.click(within(await list()).getByRole("link", { name: /site-b/ }));
    await user.click(within(await pane()).getByRole("button", { name: "Reveal" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The values could not be revealed.");
    expect(within(await pane()).getByText("Concealed. 3 lines.")).toBeInTheDocument();
  });
});
