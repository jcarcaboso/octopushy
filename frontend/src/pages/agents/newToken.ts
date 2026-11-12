import type { TokenRequest } from "../../api/types";

/** What "Enroll an agent" asks for: no name, an hour, two runs at once. */
export function newToken(name = "", expiresInMinutes = 60): TokenRequest {
  return { name: name.trim() || null, labels: [], maxConcurrency: 2, expiresInMinutes };
}
