import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import type { ManagedHostKey } from "../api/types";
import { ManagedHostKeyCard } from "./ManagedHostKey";

const v1 = { version: 1, publicKey: "ssh-ed25519 AAAAone root@bb", fingerprint: "SHA256:one", createdAt: "2026-11-05T10:00:00Z", deliveredAt: "2026-11-05T10:05:00Z" };
const v2 = { version: 2, publicKey: "ssh-ed25519 AAAAtwo root@bb", fingerprint: "SHA256:two", createdAt: "2026-11-05T11:00:00Z", deliveredAt: null };

it("says configure runs wait while no apply has delivered the key", () => {
  const key: ManagedHostKey = { pinned: null, next: { ...v1, deliveredAt: null } };
  render(<ManagedHostKeyCard hostKey={key} />);
  expect(screen.getByText(/configure runs wait until an apply delivers it/)).toBeInTheDocument();
  expect(screen.getByText("SHA256:one")).toBeInTheDocument();
});

it("shows the pin and, for a new machine, the key that replaces it once its apply finishes", () => {
  render(<ManagedHostKeyCard hostKey={{ pinned: v1, next: v2 }} />);
  expect(screen.getByText("Pinned")).toBeInTheDocument();
  expect(screen.getByText("Next")).toBeInTheDocument();
  expect(screen.getByText("SHA256:two")).toBeInTheDocument();
  expect(screen.getByText("Pinned once the apply that delivers it finishes.")).toBeInTheDocument();
});
