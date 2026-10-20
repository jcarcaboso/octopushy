import { CircleAlert, CircleCheck, CircleHelp, Info, TriangleAlert, type LucideIcon } from "lucide-react";
import type { ReactNode } from "react";

type Kind = "info" | "success" | "warning" | "danger" | "uncertain";

const icons: Record<Kind, LucideIcon> = {
  info: Info,
  success: CircleCheck,
  warning: TriangleAlert,
  danger: CircleAlert,
  uncertain: CircleHelp,
};

interface Props {
  kind?: Kind;
  title?: ReactNode;
  children?: ReactNode;
  actions?: ReactNode;
  /** Announce it to screen readers when it appears. */
  alert?: boolean;
}

export function Callout({ kind = "info", title, children, actions, alert = false }: Props) {
  const Icon = icons[kind];
  return (
    <div className={`callout callout--${kind}`} role={alert ? "alert" : undefined}>
      <Icon className="callout__icon" size={18} aria-hidden="true" />
      <div className="callout__content">
        {title ? <div className="callout__title">{title}</div> : null}
        {children ? <div className="callout__body">{children}</div> : null}
        {actions ? <div className="callout__actions">{actions}</div> : null}
      </div>
    </div>
  );
}
