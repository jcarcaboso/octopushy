import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { mockApi, renderAt } from "../test/utils";
import { Layout } from "./Layout";

describe("shell", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("keeps signing out behind the user's name and marks a failed repository fetch on its item", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/auth/state": () => ({ setupRequired: false, authenticated: true, username: "ops" }),
      "GET /api/repository": () => ({ url: "https://git.example.org/lab.git", lastFetchError: "exit status 128" }),
      "POST /api/auth/logout": () => undefined,
    });
    renderAt(<Layout />, { path: "/", route: "/" });

    expect(await screen.findByRole("link", { name: "Repository Last fetch failed" })).toBeVisible();
    expect(screen.queryByRole("banner")).not.toBeInTheDocument();
    const account = await screen.findByRole("button", { name: "ops" });
    expect(screen.getByText("Sign out")).not.toBeVisible();
    await user.click(account);
    await user.click(screen.getByRole("button", { name: "Sign out" }));
    await waitFor(() => expect(requests.map((request) => `${request.method} ${request.path}`)).toContain("POST /api/auth/logout"));
  });
});
