import type { ConfigurePreview, RunDetail } from "../../api/types";
import { taskLines } from "../../lib/tasks";

/** One playbook: what it is, where it runs, and its tasks in order under their plays. */
function PlaybookTasks({ preview, workflow, stage }: { preview: ConfigurePreview; workflow: string | null; stage: string | null }) {
  const lines = taskLines(preview.tasks);
  const tasks = lines.filter((line) => !line.play).length;
  return (
    <div className="run-plays">
      <p className="run-plays__what">
        {stage ? <span className="mono run-plays__stage">{stage}</span> : null}
        <span className="mono">{preview.playbook}</span>
        {workflow ? (
          <span className="muted">
            {" "}
            of <span className="mono">{workflow}</span>
          </span>
        ) : null}
        {preview.host ? (
          <span className="muted">
            {" "}
            on <span className="mono">{preview.host}</span>
          </span>
        ) : null}
        <span className="muted">
          {" "}
          · {tasks} {tasks === 1 ? "task" : "tasks"}
        </span>
      </p>
      <ol className="run-plays__tasks">
        {lines.map((line, i) => (
          <li key={i} className={line.play ? "run-plays__play" : undefined}>
            {line.text}
          </li>
        ))}
      </ol>
    </div>
  );
}

/** What a run's playbooks run, task by task, as each preview listed them. Nothing for a run without a preview. */
export function RunTasks({ detail, finished }: { detail: RunDetail; finished: boolean }) {
  const steps = [...detail.steps].sort((a, b) => Number(a.ordinal) - Number(b.ordinal)).filter((step) => step.configurePreview);
  if (steps.length === 0) return null;
  const workflow = detail.workflow ? `${detail.workflow.id}@${detail.workflow.version}` : null;
  return (
    <section className="run-section" aria-labelledby="run-tasks-title">
      <h2 id="run-tasks-title" className="run-section__title">{finished ? "What it ran" : "What it runs"}</h2>
      <div className="run-card">
        {steps.map((step) => (
          <PlaybookTasks key={step.id} preview={step.configurePreview!} workflow={workflow} stage={step.stage ?? null} />
        ))}
      </div>
      <p className="run-section__note">The tasks the preview listed, in order. A task may find nothing to do.</p>
    </section>
  );
}
