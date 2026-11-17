import { screen } from "@testing-library/react";
import { expect, it } from "vitest";
import type { Overview } from "../../api/types";
import { statusStyle } from "../../components/status";
import { renderAt } from "../../test/utils";
import { NeedsTile } from "./NeedsTile";

const overview = (observeErrors: Overview["observeErrors"]): Overview => ({
  repository: {
    url: null, branch: "main", pollSeconds: 60, credentialKind: "none", credentialUsername: null,
    credentialSet: false, lastFetchAt: null, lastFetchError: null, headCommit: null, revision: null,
  },
  counts: { guests: 0, inSync: 0, pending: 0, running: 0, awaitingApproval: 0, blocked: 0, failed: 0, uncertain: 0, invalid: 0, missing: 0 },
  agentsConnected: 1,
  agentsTotal: 1,
  attention: [],
  observeErrors,
  recent: [],
  failed: [],
  week: { total: 0, succeeded: 0, failed: 0, cancelled: 0, unfinished: 0 },
});

const failure = (error: string, blockReason: string | null) => ({
  installationId: "installation-pve01", address: "home/pve01", name: "pve01", error, at: "2026-11-17T08:00:00Z", blockReason,
});

const markOf = (text: string) => screen.getByText(text).closest("li")!.querySelector(".status-mark")!;

it("marks an observation held for credentials as blocked, whatever its words", () => {
  renderAt(<NeedsTile overview={overview([failure("The token is not there yet.", "credentials")])} />, { path: "/", route: "/" });
  expect(markOf("The token is not there yet.")).toHaveClass(`status-mark--${statusStyle("blocked").tone}`);
});

it("marks the words of a blocked observation without the reason as failed", () => {
  renderAt(<NeedsTile overview={overview([failure("Blocked credentials: home/pve01/token: missing.", null)])} />, { path: "/", route: "/" });
  expect(markOf("Blocked credentials: home/pve01/token: missing.")).toHaveClass(`status-mark--${statusStyle("failed").tone}`);
});
