import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Inventory, Uptime } from "../api/types";
import { check, guest, inventoryOf, service } from "../test/inventory";
import { mockApi, renderAt } from "../test/utils";
import { ServicesPage } from "./ServicesPage";

const inventory = inventoryOf([
  guest("media", [service("jellyfin", ["https://jellyfin.home.example.org/", "https://media.home.example.org/"])]),
  guest("web", [service("traefik", ["https://traefik.home.example.org/dashboard/"], { icon: { url: "/api/icons/traefik.svg", source: "name" } })]),
  guest("dns", [service("technitium", ["http://10.0.0.21:5380/"])]),
]);

const uptime: Uptime = {
  checking: true, intervalSeconds: 60, generatedAt: "2026-11-12T09:00:00Z",
  checks: [
    check("home/pve01/media/jellyfin", "https://jellyfin.home.example.org/", "down", new Date(Date.now() - 40 * 60_000).toISOString()),
    check("home/pve01/media/jellyfin", "https://media.home.example.org/"),
    check("home/pve01/web/traefik", "https://traefik.home.example.org/dashboard/"),
  ],
};

function show(data: Inventory = inventory) {
  mockApi({ "GET /api/inventory": () => data, "GET /api/uptime": () => uptime });
  renderAt(<ServicesPage />, { path: "/services", route: "/services" });
}

describe("Services", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  it("opens on the list, one row per link, grouped by domain", async () => {
    show();
    const home = await screen.findByRole("list", { name: "home.example.org" });
    expect(screen.getByRole("button", { name: "List" })).toHaveAttribute("aria-pressed", "true");
    expect(within(home).getAllByRole("listitem")).toHaveLength(3);
    expect(within(home).getByRole("link", { name: "jellyfin" })).toHaveAttribute("href", "/services/svc-jellyfin");
    expect(within(home).getByRole("link", { name: "Open media.home.example.org" })).toHaveAttribute("rel", "noreferrer");
    expect(within(home).getAllByText("21 ms")).toHaveLength(2);
    expect(screen.getByRole("list", { name: "No domain" })).toBeVisible();
  });

  it("shows a down link and counts it on its section", async () => {
    show();
    const section = await screen.findByRole("region", { name: "home.example.org" });
    expect(within(section).getByRole("button", { expanded: true })).toHaveTextContent("1 down");
    expect(within(section).getByText(/^Down 40 min/)).toBeVisible();
  });

  it("switches to tiles and remembers it", async () => {
    const user = userEvent.setup();
    show();
    await user.click(await screen.findByRole("button", { name: "Tiles" }));
    const tile = within(screen.getByRole("list", { name: "home.example.org" })).getByTitle("media.home.example.org");
    expect(tile).toHaveTextContent("jellyfinmedia");
    expect(screen.getByRole("button", { name: "Tiles" })).toHaveAttribute("aria-pressed", "true");
    expect(window.localStorage.getItem("octopushy.services.view")).toBe("tiles");

    cleanup();
    show();
    expect(await screen.findByTitle("traefik.home.example.org")).toBeVisible();
  });

  it("finds by name, guest or host, and hides sections without a match", async () => {
    const user = userEvent.setup();
    show();
    const search = await screen.findByRole("searchbox", { name: "Find a service" });
    await user.type(search, "DNS");
    expect(screen.queryByRole("region", { name: "home.example.org" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "technitium" })).toBeVisible();
    await user.clear(search);
    await user.type(search, "nothing here");
    expect(screen.getByText("No service matches.")).toBeVisible();
  });

  it("folds a section", async () => {
    const user = userEvent.setup();
    show();
    const section = await screen.findByRole("region", { name: "home.example.org" });
    await user.click(within(section).getByRole("button", { expanded: true }));
    expect(within(section).getByRole("button", { expanded: false })).toBeVisible();
    expect(within(section).queryByRole("list")).not.toBeInTheDocument();
  });

  it("shows the icon Octopushy serves, else the service's letter", async () => {
    show();
    const row = (await screen.findByRole("link", { name: "traefik" })).closest("li")!;
    const img = row.querySelector("img")!;
    expect(img).toHaveAttribute("src", "/api/icons/traefik.svg");
    fireEvent.error(img);
    expect(row.querySelector("img")).toBeNull();
    expect(row).toHaveTextContent(/^T/);
    // Without an icon, the letter and no request anywhere else.
    const media = screen.getByRole("link", { name: "jellyfin" }).closest("li")!;
    expect(media.querySelector("img")).toBeNull();
    expect(media).toHaveTextContent(/^J/);
  });

  it("lists a tcp:// link without offering to open it", async () => {
    const user = userEvent.setup();
    show(inventoryOf([guest("data", [service("store", ["tcp://store.home.example.org:5432"])])]));
    const rows = await screen.findByRole("list", { name: "home.example.org" });
    expect(within(rows).getByText("tcp://")).toBeVisible();
    expect(within(rows).getByText("store", { selector: ".services-row__label" })).toBeVisible();
    expect(within(rows).queryByRole("link", { name: /^Open / })).not.toBeInTheDocument();
    expect(within(rows).getAllByRole("link").map((a) => a.getAttribute("href"))).toEqual(["/services/svc-store", "/guests/guest-data"]);

    await user.click(screen.getByRole("button", { name: "Tiles" }));
    const tile = within(screen.getByRole("list", { name: "home.example.org" })).getByTitle("tcp://store.home.example.org:5432");
    expect(tile.tagName).toBe("DIV");
  });

  it("says when no service has a link", async () => {
    show(inventoryOf([guest("web", [service("app", [])])]));
    expect(await screen.findByText("No service has a link")).toBeVisible();
  });
});
