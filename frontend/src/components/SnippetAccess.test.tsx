import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import type { SnippetAccess } from "../api/types";
import { mockApi, renderAt } from "../test/utils";
import { SnippetAccessCard } from "./SnippetAccess";

const none: SnippetAccess = {
  publicKey: null,
  fingerprint: null,
  createdAt: null,
  declared: false,
  datastore: "local",
  user: "octopushy-snippets",
  hosts: [],
  command: null,
};

const ready: SnippetAccess = {
  publicKey: "ssh-ed25519 AAAAC3Nza octopushy-snippets@home/pve01",
  fingerprint: "SHA256:abc",
  createdAt: "2026-11-03T10:00:00Z",
  declared: true,
  datastore: "local",
  user: "octopushy-snippets",
  hosts: [{ host: "pve01", address: "10.15.5.100", port: 22, hostKeyStore: "proxmox/pve01/ssh-host-key", hostKeySet: false }],
  command:
    "OCTOPUSHY_SNIPPETS_KEY='ssh-ed25519 AAAAC3Nza octopushy-snippets@home/pve01' bash -c \"$(curl -fsSL https://raw.githubusercontent.com/jcarcaboso/octopushy/v0.5.0/deploy/proxmox/snippets.sh)\"",
};

it("offers to make the upload key, and shows no command before there is one", async () => {
  const { requests } = mockApi({ "POST /api/installations/inst-1/snippets-key": () => ready });
  renderAt(<SnippetAccessCard id="inst-1" snippets={none} />, { path: "/installations/:id", route: "/installations/inst-1" });
  expect(screen.getByText("None yet")).toBeInTheDocument();
  expect(screen.queryByLabelText("Setup command")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Create upload key" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  expect(requests.find((r) => r.method === "POST")?.path).toBe("/api/installations/inst-1/snippets-key");
});

it("shows the command for a host and which pins are still missing", () => {
  renderAt(<SnippetAccessCard id="inst-1" snippets={ready} />, { path: "/installations/:id", route: "/installations/inst-1" });
  expect(screen.getByLabelText("Setup command")).toHaveTextContent("OCTOPUSHY_SNIPPETS_KEY='ssh-ed25519 AAAAC3Nza");
  expect(screen.getByLabelText("Setup command")).toHaveTextContent("/deploy/proxmox/snippets.sh");
  expect(screen.getByRole("list", { name: "Hosts set up for uploads" })).toHaveTextContent("pve01 10.15.5.100:22, pinned to proxmox/pve01/ssh-host-key Key not set");
  expect(screen.queryByRole("button", { name: "Create upload key" })).not.toBeInTheDocument();
});
