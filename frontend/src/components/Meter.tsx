interface Props {
  label: string;
  /** 0 to 1, or null when unknown. */
  fraction: number | null;
  /** Text after the bar, such as "3.1 GiB of 16 GiB". */
  text?: string;
}

export function Meter({ label, fraction, text }: Props) {
  if (fraction === null || !Number.isFinite(fraction)) {
    return <span className="muted">—</span>;
  }
  const clamped = Math.min(1, Math.max(0, fraction));
  const percent = Math.round(clamped * 100);
  const tone = clamped >= 0.9 ? "danger" : clamped >= 0.75 ? "warning" : "ok";
  return (
    <span className="meter-row">
      <span
        className={`meter meter--${tone}`}
        role="meter"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent}
        aria-valuetext={text ? `${percent}%, ${text}` : `${percent}%`}
      >
        <span className="meter__fill" style={{ width: `${percent}%` }} />
      </span>
      <span className="meter__text">
        <span className="num">{percent}%</span>
        {text ? <span className="muted"> · {text}</span> : null}
      </span>
    </span>
  );
}
