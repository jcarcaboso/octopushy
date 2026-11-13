import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import type { ServiceDetail, ServiceSummary } from "../api/types";
import { check } from "../test/inventory";
import { runDetail } from "../test/runs";
import { mockApi, problem, renderAt } from "../test/utils";
import { ServicePage } from "./ServicePage";

function detail(overrides: Partial<ServiceDetail> = {}, status = "in-sync", statusDetail: string | null = null): ServiceDetail {
  return {
    service: { id: "svc-1", address: "home/korriban/krb-app-01/app", name: "app", status, statusDetail, valid: true, activeRun: null, lastRun: null,
      workflow: { reference: "web@1", resolved: "web@1.2" }, links: [], names: [], icon: null },
    guestId: "guest-1",
    guestAddress: "home/korriban/krb-app-01",
    definitionFile: "labs/home/proxmox/korriban/guests/krb-app-01/services/app/service.yaml",
    definitionText: "schemaVersion: 1\n",
    issues: [],
    policy: { approval: "automatic", approvalSetBy: "default", destructive: "automatic", destructiveSetBy: "default, following approval" },
    agentLabels: [],
    agentLabelsSetBy: null,
    stages: [
      { name: "expose", executor: "opentofu" },
      { name: "deploy", executor: "ansible" },
    ],
    appliedCommit: "e6b1282aa4d1c0ffee",
    appliedAt: "2026-11-02T08:00:00Z",
    missingSince: null,
    retiredAt: null,
    runs: [],
    ...overrides,
  };
}

const route = { path: "/services/:id", route: "/services/svc-1" };

describe("ServicePage", () => {
  it("shows the workflow and its stages and deploys on request", async () => {
    const { requests } = mockApi({
      "GET /api/services/svc-1": () => detail(),
      "POST /api/services/svc-1/deploy": () => ({ runId: "run-2" }),
      "GET /api/runs/run-2": () => new Response(null, { status: 404 }),
    });
    renderAt(<ServicePage />, route);
    const workflow = (await screen.findByRole("heading", { name: "Workflow" })).closest("section")!;
    expect(within(workflow).getByText("web@1.2")).toBeInTheDocument();
    expect(within(workflow).getByText("from @1")).toBeInTheDocument();
    expect(within(workflow).getByText("opentofu")).toBeInTheDocument();
    expect(within(workflow).getByText("ansible")).toBeInTheDocument();
    expect(within(workflow).getByText("Last success")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Deploy" }));
    await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  });

  it("shows its figures: how it answers, its month, where it runs, its workflow and its last deploy", async () => {
    const service = detail().service;
    mockApi({
      "GET /api/services/svc-1": () => detail({}, "in-sync"),
      "GET /api/uptime": () => ({ checks: [check(service.address, "https://app.test/")] }),
    });
    renderAt(<ServicePage />, route);
    const figure = async (label: string) => (await screen.findByText(label, { selector: "dt" })).parentElement!;
    expect(await figure("Answers")).toHaveTextContent("Up21 ms");
    expect(await figure("Uptime, 30 days")).toHaveTextContent("100%");
    expect(within(await figure("Runs on")).getByRole("link", { name: "krb-app-01" })).toHaveAttribute("href", "/guests/guest-1");
    expect(await figure("Workflow")).toHaveTextContent("web@1.2named web@1");
    expect(await figure("Last deployed")).toHaveTextContent("e6b1282");
  });

  it("brings its pipeline that waits for approval to the page", async () => {
    const run = runDetail({ operation: "pipeline", resourceId: "svc-1", resourceKind: "service" });
    mockApi({
      "GET /api/services/svc-1": () => {
        const d = detail({ runs: [run.run] }, "awaiting-approval");
        return { ...d, service: { ...d.service, activeRun: run.run } };
      },
      "GET /api/runs/run-1": () => run,
    });
    renderAt(<ServicePage />, route);
    const panel = await screen.findByRole("region", { name: "Pipeline #12 waits for your approval" });
    expect(await within(panel).findByText(/Approving covers every stage above/)).toBeInTheDocument();
    expect(within(panel).getByText("Pipeline document SHA-256")).toBeInTheDocument();
    expect(within(panel).getByRole("button", { name: "Approve" })).toBeInTheDocument();
  });

  it("says what a waiting service waits for", async () => {
    mockApi({ "GET /api/services/svc-1": () => detail({}, "waiting", "Waiting for the guest's setup to be applied.") });
    renderAt(<ServicePage />, route);
    expect(await screen.findByText("Waiting for the guest's setup to be applied.")).toBeInTheDocument();
  });

  it("shows the reason a deploy was refused", async () => {
    mockApi({
      "GET /api/services/svc-1": () => detail(),
      "POST /api/services/svc-1/deploy": () => problem(409, "Waiting for the guest's setup to be applied."),
    });
    renderAt(<ServicePage />, route);
    await userEvent.click(await screen.findByRole("button", { name: "Deploy" }));
    expect(await screen.findByText(/setup to be applied/)).toBeInTheDocument();
  });

  it("says a deleted folder removed nothing, and offers no deploy", async () => {
    mockApi({ "GET /api/services/svc-1": () => detail({ missingSince: "2026-11-02T08:00:00Z" }, "missing") });
    renderAt(<ServicePage />, route);
    expect(await screen.findByText(/Nothing was removed, and nothing will be/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Deploy" })).toBeNull();
  });

  describe("Change icon", () => {
    const withIcon = (icon: ServiceSummary["icon"]) => {
      const d = detail();
      return { ...d, service: { ...d.service, icon } };
    };

    async function open(icon: ServiceSummary["icon"], names: string[] = ["alpha.svg", "beta.png"], extra: Parameters<typeof mockApi>[0] = {}) {
      const api = mockApi({
        "GET /api/services/svc-1": () => withIcon(icon),
        "GET /api/icons": () => ({ names }),
        "GET /api/inventory": () => new Response(null, { status: 404 }),
        ...extra,
      });
      renderAt(<ServicePage />, route);
      await userEvent.click(await screen.findByRole("button", { name: "Change icon" }));
      return { ...api, dialog: await screen.findByRole("dialog", { name: "Icon for app" }) };
    }

    it("says where the icon comes from", async () => {
      const cases: [ServiceSummary["icon"], string][] = [
        [{ url: "/api/services/svc-1/icon?v=3", source: "picture" }, "Uploaded picture"],
        [{ url: "/api/icons/alpha.svg", source: "chosen" }, "Chosen: alpha.svg"],
        [{ url: "/api/icons/app.svg", source: "name" }, "Matches the service's name: app.svg"],
        [null, "No icon: the page shows its first letter"],
      ];
      for (const [icon, line] of cases) {
        const { dialog } = await open(icon);
        expect(within(dialog).getByText(line)).toBeInTheDocument();
        cleanup();
      }
    });

    it("chooses a predefined icon and closes", async () => {
      const { dialog, requests } = await open(null, ["alpha.svg", "beta.png"], { "PUT /api/services/svc-1/icon/predefined": () => undefined });
      expect(within(dialog).getByRole("button", { name: "beta.png" })).toHaveTextContent("beta");
      await userEvent.click(within(dialog).getByRole("button", { name: "beta.png" }));
      await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
      expect(requests.find((r) => r.method === "PUT")).toMatchObject({ path: "/api/services/svc-1/icon/predefined", body: { name: "beta.png" } });
    });

    it("keeps the dialog open with the server's message when a choice fails", async () => {
      const { dialog } = await open(null, ["alpha.svg"], { "PUT /api/services/svc-1/icon/predefined": () => problem(400, "There is no icon alpha.svg.") });
      await userEvent.click(within(dialog).getByRole("button", { name: "alpha.svg" }));
      expect(await within(dialog).findByText("There is no icon alpha.svg.")).toBeInTheDocument();
      expect(screen.getByRole("dialog", { name: "Icon for app" })).toBeInTheDocument();
    });

    it("narrows the grid by search and shows at most 60", async () => {
      const names = Array.from({ length: 75 }, (_, i) => `icon-${String(i).padStart(2, "0")}.svg`);
      const { dialog } = await open(null, names);
      expect(within(dialog).getAllByRole("listitem")).toHaveLength(60);
      expect(within(dialog).getByText("15 more: keep typing")).toBeInTheDocument();
      await userEvent.type(within(dialog).getByRole("searchbox", { name: "Find an icon" }), "icon-7");
      expect(within(dialog).getAllByRole("listitem")).toHaveLength(5);
      expect(within(dialog).queryByText(/more: keep typing/)).toBeNull();
    });

    it("says so when there are no predefined icons", async () => {
      const { dialog } = await open(null, []);
      expect(within(dialog).getByText(/^No predefined icons\. Map a folder of images/)).toBeInTheDocument();
      expect(within(dialog).queryByRole("searchbox")).toBeNull();
      expect(within(dialog).queryByRole("list")).toBeNull();
    });

    it("refuses a picture that is too large or of another type without a request", async () => {
      const { dialog, requests } = await open(null);
      const input = within(dialog).getByLabelText("Picture");
      await userEvent.upload(input, new File([new Uint8Array(1024 * 1024 + 1)], "big.png", { type: "image/png" }));
      expect(within(dialog).getByText("That picture is larger than 1 MiB.")).toBeInTheDocument();
      await userEvent.upload(input, new File(["GIF89a"], "anim.gif", { type: "image/gif" }), { applyAccept: false });
      expect(within(dialog).getByText("That is not a PNG, JPEG, WebP or SVG image.")).toBeInTheDocument();
      expect(requests.some((r) => r.method === "PUT")).toBe(false);
    });

    it("uploads a picture with its own type and closes", async () => {
      const { dialog, requests } = await open(null, [], { "PUT /api/services/svc-1/icon/picture": () => undefined });
      await userEvent.upload(within(dialog).getByLabelText("Picture"), new File(["<svg xmlns='http://www.w3.org/2000/svg'/>"], "logo.svg", { type: "image/svg+xml" }));
      await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
      const put = requests.find((r) => r.method === "PUT")!;
      expect(put).toMatchObject({ path: "/api/services/svc-1/icon/picture", type: "image/svg+xml" });
      expect(await (put.body as Blob).text()).toBe("<svg xmlns='http://www.w3.org/2000/svg'/>");
    });

    it("offers the default only for a picture or a chosen file, and deletes the service's own icon", async () => {
      for (const icon of [null, { url: "/api/icons/app.svg", source: "name" }]) {
        const { dialog } = await open(icon);
        expect(within(dialog).queryByRole("button", { name: "Use the default" })).toBeNull();
        cleanup();
      }
      const chosen = await open({ url: "/api/icons/alpha.svg", source: "chosen" });
      expect(within(chosen.dialog).getByRole("button", { name: "Use the default" })).toBeInTheDocument();
      cleanup();
      const { dialog, requests } = await open({ url: "/api/services/svc-1/icon?v=1", source: "picture" }, [], { "DELETE /api/services/svc-1/icon": () => undefined });
      await userEvent.click(within(dialog).getByRole("button", { name: "Use the default" }));
      await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
      expect(requests.find((r) => r.method === "DELETE")?.path).toBe("/api/services/svc-1/icon");
    });
  });
});
