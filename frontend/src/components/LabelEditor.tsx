export function LabelList({ labels, empty = "None" }: { labels: string[]; empty?: string }) {
  if (labels.length === 0) return <span className="muted">{empty}</span>;
  return (
    <span className="chips chips--static">
      {labels.map((l) => (
        <span className="chip" key={l}>
          <span className="mono">{l}</span>
        </span>
      ))}
    </span>
  );
}
