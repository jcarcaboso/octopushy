import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import { PolicyView } from "./PolicyView";

const guest = { approval: "manual", approvalSetBy: "home/ziost/dns-ct-01", destructive: "manual", destructiveSetBy: "home/ziost/dns-ct-01" };

it("shows each action that takes its own approval, and where it was set", () => {
  render(
    <PolicyView
      policy={{ ...guest, actions: [{ action: "verify", approval: "automatic", setBy: "workflow technitium-dns-lxc@1.2, action verify" }] }}
    />,
  );
  expect(screen.getByText("Approval")).toBeInTheDocument();
  expect(screen.getByText("Approval of verify")).toBeInTheDocument();
  expect(screen.getByText("Automatic")).toBeInTheDocument();
  expect(screen.getByText("set by workflow technitium-dns-lxc@1.2, action verify")).toBeInTheDocument();
});

it("shows no action rows when nothing overrides the guest", () => {
  render(<PolicyView policy={{ ...guest, actions: [] }} />);
  expect(screen.queryByText(/Approval of/)).not.toBeInTheDocument();
});
