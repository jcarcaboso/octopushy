import type { ConfigurePreview } from "../../api/types";
import { Badge } from "../../components/Badge";
import { JsonBlock } from "../../components/CodeBlock";
import { KeyValue } from "../../components/KeyValue";
import { Commit } from "../../components/Mono";

/** What a configure run will do. It deliberately makes no claim about what will change. */
export function ConfigurePreviewView({ preview }: { preview: ConfigurePreview }) {
  return (
    <div className="stack stack--small">
      <KeyValue
        items={[
          { term: "Action", value: <span className="mono">{preview.action}</span> },
          { term: "Playbook", value: <span className="mono break">{preview.playbook}</span> },
          { term: "Commit", value: <Commit commit={preview.commit} /> },
          { term: "Target", value: <span className="mono break">{preview.host ?? "—"}</span> },
          {
            term: "Host key",
            value: (
              <span className="mono break">
                {preview.hostKeyFingerprints.length > 0 ? preview.hostKeyFingerprints.join(", ") : "—"}
              </span>
            ),
          },
          ...(preview.accessRotation
            ? [
                { term: "Key in use", value: <span className="mono break">{preview.accessRotation.current}</span> },
                { term: "Candidate", value: <span className="mono break">{preview.accessRotation.candidate}</span> },
                { term: "Comes off", value: <span className="mono break">{preview.accessRotation.remove.join(", ") || "—"}</span> },
              ]
            : []),
          { term: "Tool", value: <span className="small">{preview.toolVersion ?? "—"}</span> },
          { term: "Image", value: <span className="mono break small">{preview.image ?? "—"}</span> },
          {
            term: "Destructive",
            value: preview.destructive ? <Badge tone="danger">Declared by the workflow</Badge> : <Badge tone="neutral">Not declared</Badge>,
          },
        ]}
      />
      {preview.changedFiles ? (
        <details className="outputs" open={preview.changedFiles.length > 0}>
          <summary>Changed since the last successful run ({preview.changedFiles.length})</summary>
          {preview.changedFiles.length === 0 ? (
            <p className="muted small">No files of this workflow changed.</p>
          ) : (
            <pre className="mono small">{preview.changedFiles.join("\n")}</pre>
          )}
        </details>
      ) : null}
      <details className="outputs">
        <summary>Tasks ({preview.tasks.length} lines)</summary>
        <pre className="mono small">{preview.tasks.join("\n")}</pre>
      </details>
      {preview.secrets.length > 0 ? (
        <details className="outputs">
          <summary>Secrets the playbook receives ({preview.secrets.length})</summary>
          <ul className="small">
            {preview.secrets.map((secret) => (
              <li key={secret.name} className="mono break">
                {secret.reference}
              </li>
            ))}
          </ul>
        </details>
      ) : null}
      {preview.vars ? (
        <details className="outputs">
          <summary>Inputs</summary>
          <JsonBlock value={preview.vars} />
        </details>
      ) : null}
      {preview.prepared ? (
        <details className="outputs" open>
          <summary>Prepared for this run</summary>
          <JsonBlock value={preview.prepared} />
        </details>
      ) : null}
    </div>
  );
}
