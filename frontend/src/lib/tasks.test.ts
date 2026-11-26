import { expect, it } from "vitest";
import { taskLines } from "./tasks";

it("reads plays and tasks out of what ansible-playbook --list-tasks printed", () => {
  expect(taskLines([
    "playbook: update-tools.yml",
    "  play #1 (all): Update the tools\tTAGS: []",
    "    tasks:",
    "      Check for active work\tTAGS: []",
    "      machine-tools : Install t3\tTAGS: [tools, t3]",
    "  play #2 (localhost): \tTAGS: []",
    "    tasks:",
    "      Record the versions\tTAGS: []",
  ])).toEqual([
    { play: true, text: "Update the tools" },
    { play: false, text: "Check for active work" },
    { play: false, text: "machine-tools : Install t3" },
    { play: true, text: "Play on localhost" },
    { play: false, text: "Record the versions" },
  ]);
});

it("keeps a line it does not know as a task", () => {
  expect(taskLines(["  Install tools", "", "something else"])).toEqual([
    { play: false, text: "Install tools" },
    { play: false, text: "something else" },
  ]);
});
