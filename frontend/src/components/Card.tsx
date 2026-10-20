import type { ReactNode } from "react";

interface CardProps {
  title?: ReactNode;
  /** Small text next to the title, such as a count. */
  meta?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  /** No padding around the body, for tables that run edge to edge. */
  flush?: boolean;
  id?: string;
  className?: string;
}

export function Card({ title, meta, actions, children, flush = false, id, className }: CardProps) {
  return (
    <section className={`card${className ? ` ${className}` : ""}`} id={id} aria-labelledby={id && title ? `${id}-title` : undefined}>
      {title || actions ? (
        <header className="card__header">
          <h2 className="card__title" id={id ? `${id}-title` : undefined}>
            {title}
            {meta !== undefined && meta !== null ? <span className="card__meta">{meta}</span> : null}
          </h2>
          {actions ? <div className="card__actions">{actions}</div> : null}
        </header>
      ) : null}
      <div className={flush ? "card__body card__body--flush" : "card__body"}>{children}</div>
    </section>
  );
}
