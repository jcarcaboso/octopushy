import { CopyButton } from "./CopyButton";

interface Props {
  code: string;
  label?: string;
  copy?: boolean;
  wrap?: boolean;
}

export function CodeBlock({ code, label, copy = true, wrap = false }: Props) {
  return (
    <div className="codeblock">
      {label || copy ? (
        <div className="codeblock__bar">
          <span className="codeblock__label">{label}</span>
          {copy ? <CopyButton text={code} /> : null}
        </div>
      ) : null}
      <pre className={wrap ? "codeblock__pre codeblock__pre--wrap" : "codeblock__pre"} tabIndex={0}>
        <code>{code}</code>
      </pre>
    </div>
  );
}

/** Any JSON value, pretty-printed. */
export function JsonBlock({ value, label }: { value: unknown; label?: string }) {
  return <CodeBlock code={JSON.stringify(value, null, 2)} label={label} />;
}
