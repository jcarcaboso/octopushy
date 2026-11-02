import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { mockApi, renderAt } from "../test/utils";
import { OperatorAccessPage } from "./OperatorAccessPage";

afterEach(() => vi.unstubAllGlobals());

it("requires explicit authority consent and shows the token only until dismissed", async () => {
  const create = vi.fn(() => ({ id: "grant-1", token: "synthetic-secret", expiresAt: "2026-12-01T00:00:00Z" }));
  mockApi({
    "GET /api/auth/operator-tokens": () => [],
    "POST /api/auth/operator-tokens": create,
  });
  const user = userEvent.setup();
  renderAt(<OperatorAccessPage />, { path: "/operator-access", route: "/operator-access" });
  expect(await screen.findByText("No operator grants.")).toBeVisible();
  expect(screen.getByRole("button", { name: "Create grant" })).toBeDisabled();
  await user.type(screen.getByLabelText("Grant name"), "assistant");
  await user.click(screen.getByRole("checkbox"));
  await user.click(screen.getByRole("button", { name: "Create grant" }));
  expect(create).toHaveBeenCalledOnce();
  expect(await screen.findByLabelText("Operator token")).toHaveValue("synthetic-secret");
  await user.click(screen.getByRole("button", { name: "I saved it privately" }));
  expect(screen.queryByLabelText("Operator token")).not.toBeInTheDocument();
  expect(window.localStorage.length).toBe(0);
});

async function createGrant() {
  mockApi({
    "GET /api/auth/operator-tokens": () => [],
    "POST /api/auth/operator-tokens": () => ({ id: "grant-1", token: "synthetic-secret", expiresAt: "2026-12-01T00:00:00Z" }),
  });
  const user = userEvent.setup();
  renderAt(<OperatorAccessPage />, { path: "/operator-access", route: "/operator-access" });
  await user.type(screen.getByLabelText("Grant name"), "assistant");
  await user.click(screen.getByRole("checkbox"));
  await user.click(screen.getByRole("button", { name: "Create grant" }));
  await screen.findByLabelText("Operator token");
}

it("warns on plain HTTP that the token is readable, and leaves the CA out of the login command", async () => {
  vi.stubGlobal("location", new URL("http://10.15.30.5:8081/operator-access"));
  await createGrant();
  expect(screen.getByText("This address uses plain HTTP")).toBeVisible();
  expect(screen.getByText(/Anyone on the network path can read it/)).toBeVisible();
  expect(screen.getByText("octopushy operator login --server http://10.15.30.5:8081 --token-stdin")).toBeVisible();
});

it("keeps the CA option and shows no cleartext warning on HTTPS", async () => {
  vi.stubGlobal("location", new URL("https://octopushy.example/operator-access"));
  await createGrant();
  expect(screen.queryByText("This address uses plain HTTP")).not.toBeInTheDocument();
  expect(screen.getByText(
    "octopushy operator login --server https://octopushy.example --token-stdin --ca-file /path/to/public-ca.pem",
  )).toBeVisible();
});
