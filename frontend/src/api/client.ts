import createClient, { type Middleware } from "openapi-fetch";
import type { paths } from "./schema";
import type { Problem } from "./types";

/** The one line to show for a problem: the server puts its message in `title`. */
export function problemMessage(problem: Problem | null): string | null {
  return problem?.title?.trim() || problem?.detail?.trim() || null;
}

export class ApiError extends Error {
  readonly status: number;
  readonly problem: Problem | null;

  constructor(status: number, problem: Problem | null, fallback: string) {
    super(problemMessage(problem) ?? fallback);
    this.name = "ApiError";
    this.status = status;
    this.problem = problem;
  }
}

const statusText: Record<number, string> = {
  400: "The request was not accepted.",
  401: "Sign in to continue.",
  403: "This is not allowed.",
  404: "Not found.",
  409: "This conflicts with the current state.",
  429: "Too many attempts. Wait a minute and try again.",
  502: "The server could not reach the repository.",
};

export function toApiError(response: Response, body: unknown): ApiError {
  const problem = body && typeof body === "object" ? (body as Problem) : null;
  const fallback =
    statusText[response.status] ??
    (response.status >= 500 ? "The server had a problem. Try again." : `Request failed (${response.status}).`);
  return new ApiError(response.status, problem, fallback);
}

export function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** The message to show for any error a call can produce. */
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message;
  if (error instanceof TypeError) return "The server cannot be reached.";
  if (error instanceof Error) return error.message;
  return "Something went wrong.";
}

type Listener = () => void;
let unauthorized: Listener | null = null;

/** Registers what happens when a call returns 401 (the session ended). */
export function setUnauthorizedHandler(listener: Listener | null) {
  unauthorized = listener;
}

const sessionMiddleware: Middleware = {
  onResponse({ request, response }) {
    // The account endpoints use 401 for a wrong password; that is not a lost session.
    if (response.status === 401 && !new URL(request.url).pathname.startsWith("/api/auth/")) {
      unauthorized?.();
    }
    return undefined;
  },
};

export const api = createClient<paths>({
  // A full origin keeps Request happy outside browsers (tests); in the browser it is this page's origin.
  baseUrl: typeof window === "undefined" ? "http://localhost" : window.location.origin,
  // Resolved per call, so tests can replace the global fetch.
  fetch: (request: Request) => globalThis.fetch(request),
  credentials: "same-origin",
  headers: { Accept: "application/json" },
});
api.use(sessionMiddleware);

interface FetchResult<T> {
  data?: T;
  error?: unknown;
  response: Response;
}

/** Awaits an openapi-fetch call and returns its data, or throws an ApiError. */
export async function unwrap<T>(call: Promise<FetchResult<T>>): Promise<T> {
  const { data, error, response } = await call;
  if (!response.ok) throw toApiError(response, error);
  return data as T;
}
