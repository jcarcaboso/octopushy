import { screen } from "@testing-library/react";
import { expect, it } from "vitest";
import { renderAt } from "../test/utils";
import { ManagedAccessCard } from "./ManagedAccess";

const access = {
  publicKey: "ssh-ed25519 AAAAC3Nza octopushy@pve01-ai",
  fingerprint: "SHA256:abc",
  version: 1,
  provisionedAt: null,
  lastUsedAt: null,
};

it("shows the public key, and that it has not been used yet", () => {
  renderAt(<ManagedAccessCard access={access} />, { path: "/guests/:id", route: "/guests/guest-1" });
  expect(screen.getByText(access.publicKey)).toBeInTheDocument();
  expect(screen.getByText("SHA256:abc")).toBeInTheDocument();
  expect(screen.getByText("Not yet")).toBeInTheDocument();
  expect(screen.getByText("Never")).toBeInTheDocument();
});
