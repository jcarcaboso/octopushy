import { screen } from "@testing-library/react";
import { expect, it } from "vitest";
import type { GuestSummary } from "../api/types";
import { renderAt } from "../test/utils";
import { GuestTable } from "./GuestTable";

function guest(name: string, type: string): GuestSummary {
  return {
    id: name,
    address: `home/korriban/${name}`,
    name,
    type,
    host: "korriban",
    vmid: 310,
    lifecycle: "active",
    status: "in-sync",
    statusDetail: null,
    valid: true,
    observed: null,
    activeRun: null,
    lastRun: null,
  };
}

it("marks containers and leaves VMs as they are", () => {
  renderAt(<GuestTable guests={[guest("krb-app-01", "vm"), guest("krb-dns-01", "container")]} label="Guests" />, { path: "/", route: "/" });
  expect(screen.getAllByText("Container")).toHaveLength(1);
  expect(screen.getByRole("row", { name: /krb-dns-01/ })).toHaveTextContent("Container");
  expect(screen.getByRole("row", { name: /krb-app-01/ })).not.toHaveTextContent("Container");
});
