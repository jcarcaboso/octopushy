import { LoaderCircle, type LucideIcon } from "lucide-react";
import type { ReactNode } from "react";
import { ApiError, errorMessage } from "../api/client";
import { Callout } from "./Callout";

export function Spinner({ label = "Loading" }: { label?: string }) {
  return (
    <span className="spinner" role="status">
      <LoaderCircle className="spinner__icon" size={16} aria-hidden="true" />
      <span className="visually-hidden">{label}</span>
    </span>
  );
}

export function Skeleton({ height = 16, width }: { height?: number; width?: number | string }) {
  return <span className="skeleton" style={{ height, width }} aria-hidden="true" />;
}

/** A quiet placeholder while a page's data loads. */
export function PageSkeleton({ rows = 3 }: { rows?: number }) {
  return (
    <div className="page-skeleton" role="status" aria-label="Loading">
      <Skeleton height={28} width="40%" />
      <Skeleton height={14} width="25%" />
      {Array.from({ length: rows }, (_, i) => (
        <Skeleton key={i} height={120} />
      ))}
    </div>
  );
}

interface EmptyProps {
  icon?: LucideIcon;
  title: ReactNode;
  children?: ReactNode;
  actions?: ReactNode;
  compact?: boolean;
}

export function EmptyState({ icon: Icon, title, children, actions, compact = false }: EmptyProps) {
  return (
    <div className={compact ? "empty empty--compact" : "empty"}>
      {Icon ? <Icon className="empty__icon" size={compact ? 20 : 28} aria-hidden="true" /> : null}
      <div className="empty__title">{title}</div>
      {children ? <div className="empty__body">{children}</div> : null}
      {actions ? <div className="empty__actions">{actions}</div> : null}
    </div>
  );
}

/** The title of a problem response (or a plain explanation), inline where it happened. */
export function ErrorMessage({ error, title }: { error: unknown; title?: string }) {
  if (!error) return null;
  const problem = error instanceof ApiError ? error.problem : null;
  const message = errorMessage(error);
  const detail = problem?.detail && problem.detail !== message ? problem.detail : null;
  return (
    <Callout kind="danger" title={title ?? message} alert>
      {title ? message : null}
      {detail ? <div>{detail}</div> : null}
    </Callout>
  );
}

export function NotFound({ what }: { what: string }) {
  return (
    <EmptyState title={`This ${what} does not exist`}>
      It may have been removed from the lab repository, or the link is wrong.
    </EmptyState>
  );
}
