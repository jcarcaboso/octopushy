import type { ReactNode } from "react";

interface TableProps {
  children: ReactNode;
  /** Describes the table for assistive technology when no visible heading names it. */
  label?: string;
  className?: string;
}

/** A table that scrolls sideways on narrow screens instead of breaking the layout. */
export function Table({ children, label, className }: TableProps) {
  return (
    <div className="table-wrap" role="region" aria-label={label} tabIndex={label ? 0 : undefined}>
      <table className={`table${className ? ` ${className}` : ""}`}>{children}</table>
    </div>
  );
}

export function EmptyRow({ colSpan, children }: { colSpan: number; children: ReactNode }) {
  return (
    <tr className="table__empty">
      <td colSpan={colSpan}>{children}</td>
    </tr>
  );
}
