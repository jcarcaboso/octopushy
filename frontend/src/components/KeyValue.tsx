import type { ReactNode } from "react";

export interface Pair {
  term: ReactNode;
  value: ReactNode;
  key?: string;
}

export function KeyValue({ items, columns = 1 }: { items: Pair[]; columns?: 1 | 2 | 3 }) {
  return (
    <dl className={`kv kv--cols-${columns}`}>
      {items.map((item, i) => (
        <div className="kv__item" key={item.key ?? i}>
          <dt>{item.term}</dt>
          <dd>{item.value}</dd>
        </div>
      ))}
    </dl>
  );
}
