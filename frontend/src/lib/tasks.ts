/** A line of a preview's task list, which is what `ansible-playbook --list-tasks` printed: a play, or a task under one. */
export interface TaskLine {
  play: boolean;
  text: string;
}

/**
 * The plays and tasks of a preview, in order, without the listing's own lines (`playbook: …`, `tasks:`) and tags.
 * The lines are tool output about repository content, so anything that is not one of those stays as a task.
 */
export function taskLines(lines: string[]): TaskLine[] {
  return lines.flatMap((line): TaskLine[] => {
    const text = line.replace(/\s+TAGS:.*$/, "").trim();
    if (!text || /^playbook:/.test(text) || text === "tasks:") return [];
    const play = /^play #\d+ \(([^)]*)\):\s*(.*)$/.exec(text);
    return [play ? { play: true, text: play[2] || `Play on ${play[1]}` } : { play: false, text }];
  });
}
