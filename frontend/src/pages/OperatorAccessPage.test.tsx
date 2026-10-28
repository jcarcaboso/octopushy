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
