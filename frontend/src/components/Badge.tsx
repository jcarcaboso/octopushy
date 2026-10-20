import {
  Archive,
  Ban,
  CircleCheck,
  CircleHelp,
  CircleX,
  Clock,
  Dot,
  FileQuestion,
  Hand,
  LoaderCircle,
  Minus,
  TriangleAlert,
  type LucideIcon,
} from "lucide-react";
import type { ReactNode } from "react";
import { statusStyle, type StatusIcon, type Tone } from "./status";

const icons: Record<StatusIcon, LucideIcon> = {
  check: CircleCheck,
  clock: Clock,
  spinner: LoaderCircle,
  hand: Hand,
  ban: Ban,
  x: CircleX,
  question: CircleHelp,
  alert: TriangleAlert,
  missing: FileQuestion,
  archive: Archive,
  minus: Minus,
  dot: Dot,
};

interface BadgeProps {
  tone?: Tone;
  icon?: LucideIcon;
  title?: string;
  children: ReactNode;
}

/** A small label with a tone; the text always carries the meaning, the color only supports it. */
export function Badge({ tone = "neutral", icon: Icon, title, children }: BadgeProps) {
  return (
    <span className={`badge badge--${tone}`} title={title}>
      {Icon ? <Icon className="badge__icon" aria-hidden="true" size={13} strokeWidth={2.25} /> : null}
      {children}
    </span>
  );
}

export function StatusBadge({ status, title }: { status: string; title?: string }) {
  const style = statusStyle(status);
  const Icon = icons[style.icon];
  return (
    <span className={`badge badge--${style.tone}`} title={title} data-status={status}>
      <Icon
        className={style.icon === "spinner" ? "badge__icon badge__icon--spin" : "badge__icon"}
        aria-hidden="true"
        size={13}
        strokeWidth={2.25}
      />
      {style.label}
    </span>
  );
}
