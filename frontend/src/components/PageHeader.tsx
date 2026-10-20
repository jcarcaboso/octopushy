import type { ReactNode } from "react";
import { useDocumentTitle } from "./useDocumentTitle";

interface Props {
  title: ReactNode;
  /** The plain page title for the browser tab, when `title` is not plain text. */
  documentTitle?: string;
  eyebrow?: ReactNode;
  meta?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
}

export function PageHeader({ title, documentTitle, eyebrow, meta, actions, children }: Props) {
  useDocumentTitle(documentTitle ?? (typeof title === "string" ? title : undefined));
  return (
    <header className="page-header">
      <div className="page-header__main">
        {eyebrow ? <div className="page-header__eyebrow">{eyebrow}</div> : null}
        <h1 className="page-header__title">{title}</h1>
        {meta ? <div className="page-header__meta">{meta}</div> : null}
      </div>
      {actions ? <div className="page-header__actions">{actions}</div> : null}
      {children ? <div className="page-header__extra">{children}</div> : null}
    </header>
  );
}
