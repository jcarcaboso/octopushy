import { afterEach, expect, it } from "vitest";
import { markSeen } from "./notifications";

afterEach(() => localStorage.clear());

it("never moves what was read back, so a bell that knows less leaves the page's mark as it is", () => {
  markSeen(21);
  markSeen(20);
  expect(localStorage.getItem("octopushy.notifications.seen")).toBe("21");
  markSeen(22);
  expect(localStorage.getItem("octopushy.notifications.seen")).toBe("22");
});
