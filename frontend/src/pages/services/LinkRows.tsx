import { ArrowUpRight } from "lucide-react";
import { Link } from "react-router";
import { DayStrip } from "../../components/DayStrip";
import type { ServiceLink } from "../../lib/services";
import { AnswersIn } from "./LinkState";
import { ServiceIcon } from "../../components/ServiceIcon";

export function LinkHead() {
  return (
    <div className="services-head" aria-hidden="true">
      <span />
      <span>Service</span>
      <span>Link</span>
      <span>Guest</span>
      <span>Last 30 days</span>
      <span className="services-row__answer">Answers in</span>
      <span />
    </div>
  );
}

/** One row per link; a service's name, icon and guest only on its first. */
export function LinkRows({ links, label }: { links: ServiceLink[]; label: string }) {
  return (
    <ul className="services-rows" aria-label={label}>
      {links.map((link, i) => {
        const first = i === 0 || links[i - 1].service.id !== link.service.id;
        return (
          <li key={link.key} className="services-row">
            {first ? <ServiceIcon service={link.service} /> : <span />}
            {first ? (
              <Link to={`/services/${link.service.id}`} className="services-row__name">
                {link.service.name}
              </Link>
            ) : (
              <span />
            )}
            <a href={link.url} target="_blank" rel="noreferrer" className="services-row__link mono">
              <span className="services-row__label">{link.label}</span>
              <span className="muted">
                {link.tail}
                {link.path}
              </span>
            </a>
            {first ? (
              <Link to={`/guests/${link.guest.id}`} className="services-row__guest">
                {link.guest.name}
              </Link>
            ) : (
              <span />
            )}
            {link.check ? <DayStrip label={link.host} checks={[link.check]} /> : <span />}
            <span className="services-row__answer">
              <AnswersIn check={link.check} />
            </span>
            <a href={link.url} target="_blank" rel="noreferrer" className="services-row__open" aria-label={`Open ${link.host}`}>
              <ArrowUpRight size={14} aria-hidden="true" />
            </a>
          </li>
        );
      })}
    </ul>
  );
}
