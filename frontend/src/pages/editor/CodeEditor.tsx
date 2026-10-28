import { useEffect, useRef } from "react";
import { monaco, trackView } from "./monaco";

type Model = monaco.editor.ITextModel;

interface Props {
  /** The file being edited; null shows an empty editor. */
  model: Model | null;
  /** When set, shows the changes from this content to `model` instead of the editor alone. */
  original: Model | null;
  readOnly: boolean;
  onSave: () => void;
}

const dark = window.matchMedia?.("(prefers-color-scheme: dark)");

function applyTheme() {
  monaco.editor.setTheme(dark?.matches ? "vs-dark" : "vs");
}

const options = {
  automaticLayout: true,
  minimap: { enabled: false },
  fontSize: 13,
  scrollBeyondLastLine: false,
  renderWhitespace: "selection",
  tabSize: 2,
  detectIndentation: true,
} satisfies monaco.editor.IStandaloneEditorConstructionOptions;

/** Monaco's editor, or its diff editor when `original` is set. Ctrl/Cmd+S calls `onSave`. */
export function CodeEditor({ model, original, readOnly, onSave }: Props) {
  const container = useRef<HTMLDivElement>(null);
  const save = useRef(onSave);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const diff = useRef<monaco.editor.IStandaloneDiffEditor | null>(null);
  const comparing = original !== null;

  useEffect(() => {
    save.current = onSave;
  }, [onSave]);

  useEffect(() => {
    applyTheme();
    dark?.addEventListener("change", applyTheme);
    return () => dark?.removeEventListener("change", applyTheme);
  }, []);

  // One editor per mode; switching between editing and comparing replaces it.
  useEffect(() => {
    const element = container.current!;
    const bindSave = (target: monaco.editor.IStandaloneCodeEditor) =>
      target.addAction({
        id: "octopushy.saveDraft",
        label: "Save to draft",
        keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS],
        run: () => save.current(),
      });
    if (comparing) {
      const created = monaco.editor.createDiffEditor(element, { ...options, renderSideBySide: true, originalEditable: false });
      // The diff editor's sides are standalone editors, though typed as plain ones.
      bindSave(created.getModifiedEditor() as monaco.editor.IStandaloneCodeEditor);
      diff.current = created;
    } else {
      const created = monaco.editor.create(element, { ...options, model: null });
      bindSave(created);
      editor.current = created;
    }
    return () => {
      // Models belong to the page, which keeps unsaved edits across files; only the view goes.
      editor.current?.setModel(null);
      editor.current?.dispose();
      diff.current?.setModel(null);
      diff.current?.dispose();
      editor.current = null;
      diff.current = null;
    };
  }, [comparing]);

  useEffect(() => {
    if (editor.current && editor.current.getModel() !== model) editor.current.setModel(model);
    if (diff.current && model && original) diff.current.setModel({ original, modified: model });
  }, [model, original, comparing]);

  // The workspace may dispose a file (deleted, published) before the page renders without it.
  useEffect(
    () =>
      trackView((disposing) => {
        const shown = diff.current?.getModel();
        if (shown && (disposing.has(shown.original) || disposing.has(shown.modified))) diff.current!.setModel(null);
        const single = editor.current?.getModel();
        if (single && disposing.has(single)) editor.current!.setModel(null);
      }),
    [],
  );

  useEffect(() => {
    editor.current?.updateOptions({ readOnly });
    diff.current?.updateOptions({ readOnly });
  }, [readOnly, comparing]);

  return <div ref={container} className="code-editor" data-testid="code-editor" />;
}
