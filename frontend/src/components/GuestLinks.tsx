import { ExternalLink } from "lucide-react";
import type { GuestSummary } from "../api/types";
import { opensInBrowser } from "../lib/inventory";

/** The pages a guest's definition links, such as its own admin console, as buttons that open in a new tab. */
export function GuestLinks({ guest, small }: { guest: GuestSummary; small?: boolean }) {
  // The server keeps only labeled http(s) links without credentials; anything else is not a link here either.
  return guest.links
    .filter((link) => opensInBrowser(link.url))
    .map((link) => (
      <a key={`${link.label} ${link.url}`} className={small ? "button button--small" : "button"} href={link.url} target="_blank" rel="noreferrer" title={link.url}>
        {link.label} <ExternalLink size={small ? 13 : 15} aria-hidden="true" />
      </a>
    ));
}
