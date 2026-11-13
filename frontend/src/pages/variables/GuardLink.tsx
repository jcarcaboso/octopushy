import type { MouseEvent, ReactNode } from "react";
import { Link, useNavigate } from "react-router";
import { useLeave } from "./leave";

interface Props {
  to: string;
  className?: string;
  title?: string;
  "aria-current"?: "page" | "true";
  "data-key"?: string;
  /** Runs after the link is followed, such as closing the drawer. */
  onFollow?: () => void;
  children: ReactNode;
}

/** A link inside the Variables page that asks before it drops edits that are not saved. */
export function GuardLink({ to, onFollow, children, ...rest }: Props) {
  const leave = useLeave();
  const navigate = useNavigate();
  function click(event: MouseEvent<HTMLAnchorElement>) {
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    leave(() => {
      void navigate(to);
      onFollow?.();
    });
  }
  return (
    <Link to={to} onClick={click} {...rest}>
      {children}
    </Link>
  );
}
