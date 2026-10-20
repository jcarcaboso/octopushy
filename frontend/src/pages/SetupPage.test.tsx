import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { mockApi, renderAt } from "../test/utils";
import { SetupPage } from "./SetupPage";

describe("SetupPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("explains where the code is, checks the password, and creates the account", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/auth/state": () => ({ setupRequired: true, authenticated: false, username: null }),
      "POST /api/auth/setup": () => ({ setupRequired: false, authenticated: true, username: "ops" }),
    });

    renderAt(<SetupPage />, { path: "/setup", route: "/setup" });
    expect(await screen.findByRole("heading", { name: "Create the account" })).toBeInTheDocument();
    expect(screen.getByText("setup-code")).toBeInTheDocument();

    await user.type(screen.getByLabelText("Setup code"), " abcd2345wxyz ");
    await user.type(screen.getByLabelText("User name"), "ops");
    await user.type(screen.getByLabelText("Password"), "short");
    expect(screen.getByText(/At least 12 characters/)).toHaveClass("field__error");

    await user.type(screen.getByLabelText("Password"), "-but-long-now");
    await user.type(screen.getByLabelText("Repeat the password"), "short-but-long-now");
    await user.click(screen.getByRole("button", { name: "Create the account" }));

    await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
    expect(requests.find((r) => r.method === "POST")!.body).toEqual({
      setupCode: "abcd2345wxyz",
      username: "ops",
      password: "short-but-long-now",
    });
  });

  it("does not send mismatched passwords", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/auth/state": () => ({ setupRequired: true, authenticated: false, username: null }),
    });

    renderAt(<SetupPage />, { path: "/setup", route: "/setup" });
    await user.type(await screen.findByLabelText("Setup code"), "CODE");
    await user.type(screen.getByLabelText("User name"), "ops");
    await user.type(screen.getByLabelText("Password"), "a-long-password-1");
    await user.type(screen.getByLabelText("Repeat the password"), "a-long-password-2");
    expect(screen.getByText("The passwords differ.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Create the account" }));
    expect(requests.filter((r) => r.method === "POST")).toHaveLength(0);
  });
});
