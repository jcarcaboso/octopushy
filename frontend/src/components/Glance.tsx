import type { ReactNode } from "react";

export interface GlanceCell {
  label: string;
  value: ReactNode;
  note?: ReactNode;
}

/** A strip of figures under a page's title: each a label, a large value and one muted line. Null cells are left out. */
export function Glance({ cells }: { cells: (GlanceCell | null)[] }) {
  const shown = cells.filter((cell): cell is GlanceCell => cell !== null);
  if (shown.length === 0) return null;
  return (
    <dl className="glance">
      {shown.map((cell) => (
        <div key={cell.label} className="glance__cell">
          <dt className="glance__label">{cell.label}</dt>
          <dd className="glance__value">{cell.value}</dd>
          {cell.note ? <dd className="glance__note">{cell.note}</dd> : null}
        </div>
      ))}
    </dl>
  );
}
