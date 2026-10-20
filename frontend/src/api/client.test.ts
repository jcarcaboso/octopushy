import { describe, expect, it } from "vitest";
import { ApiError, problemMessage, toApiError } from "./client";

describe("problemMessage", () => {
  it("uses the title the server wrote", () => {
    expect(problemMessage({ title: "Run #1 is still blocked.", status: 409 })).toBe("Run #1 is still blocked.");
  });

  it("falls back to the detail when there is no title", () => {
    expect(problemMessage({ status: 400, detail: "Poll between every 10 seconds and once a day." })).toBe(
      "Poll between every 10 seconds and once a day.",
    );
  });

  it("falls back to the status text when there is no problem body", () => {
    const error = toApiError(new Response(null, { status: 409 }), "");
    expect(error).toBeInstanceOf(ApiError);
    expect(error.message).toBe("This conflicts with the current state.");
  });
});
