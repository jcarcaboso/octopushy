// Monaco, bundled with the frontend: no CDN, and its workers are files of this origin.
// Only the editor page imports this module, so the rest of the app stays small.
import * as monaco from "monaco-editor/editor";
import "monaco-editor/features/register.all";
import "monaco-editor/languages/definitions/register.all";
import "monaco-editor/languages/features/json/register";
import EditorWorker from "monaco-editor/editor/editor.worker?worker";
import JsonWorker from "monaco-editor/languages/features/json/json.worker?worker";

self.MonacoEnvironment = {
  getWorker: (_moduleId, label) => (label === "json" ? new JsonWorker() : new EditorWorker()),
};

type Model = monaco.editor.ITextModel;
const views = new Set<(disposing: Set<Model>) => void>();

/** Registers an editor view, so it lets go of models before they are disposed. */
export function trackView(release: (disposing: Set<Model>) => void) {
  views.add(release);
  return () => {
    views.delete(release);
  };
}

/** Disposes models after every view showing one has let go of it, as Monaco requires. */
export function disposeModels(models: Model[]) {
  const disposing = new Set(models);
  views.forEach((release) => release(disposing));
  models.forEach((model) => model.dispose());
}

export { monaco };
export type Monaco = typeof monaco;
