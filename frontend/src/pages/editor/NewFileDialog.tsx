import { FilePlus } from "lucide-react";
import { useState } from "react";
import { Dialog } from "../../components/Dialog";
import { ErrorMessage } from "../../components/States";

interface Props {
  open: boolean;
  folder: string;
  existing: Set<string>;
  pending: boolean;
  error: unknown;
  onCreate: (path: string) => void;
  onClose: () => void;
}

function problem(path: string, existing: Set<string>): string | null {
  if (!path) return null;
  if (path.startsWith("/") || path.endsWith("/")) return "Write a path relative to the repository root, ending in a file name.";
  if (path.split("/").some((part) => part === "" || part === "." || part === ".." || part.toLowerCase() === ".git"))
    return "The path cannot contain empty, “.”, “..” or “.git” parts.";
  if (/[\\:]/.test(path)) return "The path cannot contain backslashes or colons.";
  if (existing.has(path)) return "A file with this path already exists.";
  return null;
}

/** Asks for the path of a new, empty file; folders are created as needed. */
export function NewFileDialog({ open, folder, existing, pending, error, onCreate, onClose }: Props) {
  const [path, setPath] = useState(folder ? `${folder}/` : "");
  const trimmed = path.trim();
  const invalid = problem(trimmed, existing);

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="New file"
      size="small"
      footer={
        <>
          <button type="button" className="button" onClick={onClose}>
            Cancel
          </button>
          <button
            type="submit"
            form="new-file"
            className="button button--primary"
            disabled={!trimmed || invalid !== null || pending}
          >
            <FilePlus size={15} aria-hidden="true" />
            {pending ? "Creating…" : "Create"}
          </button>
        </>
      }
    >
      <form
        id="new-file"
        className="stack"
        onSubmit={(e) => {
          e.preventDefault();
          if (trimmed && !invalid) onCreate(trimmed);
        }}
      >
        <div className="field">
          <label htmlFor="new-file-path">Path</label>
          <input
            id="new-file-path"
            className="mono"
            value={path}
            onChange={(e) => setPath(e.target.value)}
            placeholder="labs/home/notes.md"
            autoComplete="off"
            spellCheck={false}
            aria-describedby="new-file-hint"
          />
          <div id="new-file-hint" className="field__hint">
            {invalid ?? "The file is added to the draft empty; nothing is pushed until you publish."}
          </div>
        </div>
        <ErrorMessage error={error} />
      </form>
    </Dialog>
  );
}
