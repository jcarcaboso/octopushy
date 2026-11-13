import { opensInBrowser } from "../../lib/inventory";
import type { ServiceLink } from "../../lib/services";
import { DownFor } from "./LinkState";
import { ServiceIcon } from "../../components/ServiceIcon";

/** One tile per link; the whole tile opens it, when a browser can. */
export function LinkTiles({ links, label }: { links: ServiceLink[]; label: string }) {
  return (
    <ul className="service-tiles" aria-label={label}>
      {links.map((link) => {
        const body = (
          <>
            <ServiceIcon service={link.service} large />
            <span className="service-tile__name">{link.service.name}</span>
            {link.check?.status === "down" ? (
              <DownFor check={link.check} />
            ) : link.label !== link.service.name ? (
              <span className="service-tile__label mono muted">{link.label}</span>
            ) : null}
          </>
        );
        return (
          <li key={link.key}>
            {opensInBrowser(link.url) ? (
              <a href={link.url} target="_blank" rel="noreferrer" className="service-tile" title={link.host}>
                {body}
              </a>
            ) : (
              // A tcp:// link is checked, never opened.
              <div className="service-tile" title={link.url}>
                {body}
              </div>
            )}
          </li>
        );
      })}
    </ul>
  );
}
