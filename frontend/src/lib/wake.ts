import type { ObservedHost } from "../api/types";

/**
 * Whether a host may need waking: its installation.yaml gives it wakeOnLan and it is not known to be online. A standalone host
 * that is down fails its observation and keeps the last one's "online", so a failed observation counts as not known.
 */
export function canWake(host: ObservedHost, observeFailed: boolean) {
  return Boolean(host.wakeOnLanMac) && (host.online !== true || observeFailed);
}
