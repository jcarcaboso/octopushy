import { fetchDraftFile } from "../../api/queries";
import { errorMessage } from "../../api/client";
import { disposeModels, monaco } from "./monaco";

export interface Buffer {
  path: string;
  /** The file in the draft. */
  model: monaco.editor.ITextModel;
  /** The file at the draft's base; empty for a new file. */
  base: monaco.editor.ITextModel;
  /** A deleted file is shown, not edited. */
  deleted: boolean;
  savedVersion: number;
}

/** A file being opened, one that cannot be edited here, or an open buffer. */
export type Entry = { state: "loading" } | { state: "unavailable"; message: string } | { state: "open"; buffer: Buffer };

/** A model for a repository path; Monaco picks the language from its file name. */
function createModel(path: string, content: string, kind: "draft" | "base") {
  return monaco.editor.createModel(content, undefined, monaco.Uri.from({ scheme: `octopushy-${kind}`, path: `/${path}` }));
}

/**
 * The open files of one draft. It outlives the page, so unsaved edits survive moving to
 * another page and back; it is reset whenever the draft's files change underneath it.
 */
export class Workspace {
  private readonly buffers = new Map<string, Buffer>();
  private readonly pending = new Map<string, Entry>();
  private readonly listeners = new Set<() => void>();
  private generation = 0;
  version = 0;

  private readonly draftId: string;

  constructor(draftId: string) {
    this.draftId = draftId;
  }

  get(path: string): Buffer | undefined {
    return this.buffers.get(path);
  }

  entry(path: string): Entry | undefined {
    const buffer = this.buffers.get(path);
    return buffer ? { state: "open", buffer } : this.pending.get(path);
  }

  /** Loads a file from the draft unless it is open or loading already. */
  ensure(path: string) {
    if (this.entry(path)) return;
    const generation = this.generation;
    this.pending.set(path, { state: "loading" });
    this.emit();
    fetchDraftFile(this.draftId, path).then(
      (file) => {
        if (generation !== this.generation) return;
        this.pending.delete(path);
        if (!file.exists && !file.baseExists) this.pending.set(path, { state: "unavailable", message: "This file is not in the draft." });
        else if (!file.editable)
          this.pending.set(path, {
            state: "unavailable",
            message: "This file is binary, a link, or larger than 1 MiB, so it is not edited here.",
          });
        else this.open(path, file.exists ? file.content! : null, file.baseExists ? file.baseContent : null);
        this.emit();
      },
      (error: unknown) => {
        if (generation !== this.generation) return;
        this.pending.set(path, { state: "unavailable", message: errorMessage(error) });
        this.emit();
      },
    );
  }

  open(path: string, content: string | null, baseContent: string | null): Buffer {
    const previous = this.buffers.get(path);
    if (previous) disposeModels([previous.model, previous.base]);
    const model = createModel(path, content ?? "", "draft");
    const buffer: Buffer = {
      path,
      model,
      base: createModel(path, baseContent ?? "", "base"),
      deleted: content === null,
      savedVersion: model.getAlternativeVersionId(),
    };
    model.onDidChangeContent(() => this.emit());
    this.buffers.set(path, buffer);
    this.emit();
    return buffer;
  }

  isDirty(path: string): boolean {
    const buffer = this.buffers.get(path);
    return buffer !== undefined && !buffer.deleted && buffer.model.getAlternativeVersionId() !== buffer.savedVersion;
  }

  dirtyPaths(): string[] {
    return [...this.buffers.keys()].filter((path) => this.isDirty(path)).sort();
  }

  markSaved(path: string, version: number) {
    const buffer = this.buffers.get(path);
    if (buffer) buffer.savedVersion = version;
    this.emit();
  }

  close(path: string) {
    this.pending.delete(path);
    const buffer = this.buffers.get(path);
    if (!buffer) {
      this.emit();
      return;
    }
    disposeModels([buffer.model, buffer.base]);
    this.buffers.delete(path);
    this.emit();
  }

  /** Forgets every file, so each is read again from the draft's current head. */
  reset() {
    this.generation++;
    this.pending.clear();
    for (const path of [...this.buffers.keys()]) this.close(path);
    this.emit();
  }

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  private emit() {
    this.version++;
    for (const listener of this.listeners) listener();
  }
}

const workspaces = new Map<string, Workspace>();

export function workspaceFor(draftId: string): Workspace {
  let workspace = workspaces.get(draftId);
  if (!workspace) {
    workspace = new Workspace(draftId);
    workspaces.set(draftId, workspace);
  }
  return workspace;
}

export function forgetWorkspace(draftId: string) {
  workspaces.get(draftId)?.reset();
  workspaces.delete(draftId);
}
