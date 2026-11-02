import { screen } from "@testing-library/react";
import { expect, it } from "vitest";
import type { ManagedAccess } from "../api/types";
import { renderAt } from "../test/utils";
import { ManagedAccessCard } from "./ManagedAccess";

const key = {
  version: 1,
  fingerprint: "SHA256:abc",
  state: "in-use",
  authorizedAt: null,
  verifiedAt: null,
  promotedAt: null,
  promotedBy: null,
  promotionEvidence: null,
  removedAt: null,
};

const access: ManagedAccess = {
  publicKey: "ssh-ed25519 AAAAC3Nza octopushy@pve01-ai",
  fingerprint: "SHA256:abc",
  version: 1,
  provisionedAt: null,
  lastUsedAt: null,
  keys: [key],
};

const render = (value: ManagedAccess) =>
  renderAt(<ManagedAccessCard id="guest-1" access={value} />, { path: "/guests/:id", route: "/guests/guest-1" });

it("shows the public key, and that it has not been used yet", () => {
  render(access);
  expect(screen.getByText(access.publicKey)).toBeInTheDocument();
  expect(screen.getByText("SHA256:abc")).toBeInTheDocument();
  expect(screen.getByText("Not yet")).toBeInTheDocument();
  expect(screen.getByText("Never")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Rotate key" })).toBeInTheDocument();
  expect(screen.queryByRole("list", { name: "Other versions" })).not.toBeInTheDocument();
});

it("keeps an old key still on the guest in view", () => {
  render({
    ...access,
    version: 2,
    keys: [{ ...key, version: 2 }, { ...key, version: 1, state: "old", fingerprint: "SHA256:old" }],
  });
  expect(screen.getByText("Old, still on the guest")).toBeInTheDocument();
  expect(screen.getByText(/v1 SHA256:old/)).toBeInTheDocument();
});
