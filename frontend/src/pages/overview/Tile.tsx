import type { ReactNode } from "react";
import { Link } from "react-router";

interface TileProps {
  id: string;
  title: string;
  /** A count beside the title. */
  meta?: number;
  link?: { to: string; label: string };
  wide?: boolean;
  children: ReactNode;
}

/** One tile of the overview: a small label as its heading, a link at the right, and what it shows. */
export function Tile({ id, title, meta, link, wide = false, children }: TileProps) {
  return (
    <section className={wide ? "bento-tile bento-tile--wide" : "bento-tile"} aria-labelledby={`${id}-title`}>
      <header className="bento-tile__head">
        <h2 className="bento-tile__title" id={`${id}-title`}>
          {title}
          {meta !== undefined ? <span className="bento-tile__meta num">{meta}</span> : null}
        </h2>
        {link ? (
          <Link to={link.to} className="bento-tile__link">
            {link.label}
          </Link>
        ) : null}
      </header>
      {children}
    </section>
  );
}

/** A large figure with a caption under it. */
export function Figure({ value, caption }: { value: ReactNode; caption: ReactNode }) {
  return (
    <p className="bento-figure">
      <span className="bento-figure__value num">{value}</span>
      <span className="bento-figure__caption">{caption}</span>
    </p>
  );
}
