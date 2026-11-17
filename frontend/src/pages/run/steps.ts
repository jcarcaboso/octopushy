import type { Step } from "../../api/types";
import type { Tone } from "../../components/status";
import { humanize } from "../../lib/format";
import type { BumpLevel } from "../../lib/images";

const stepNames: Record<string, string> = {
  plan: "Plan",
  approval: "Approval",
  unprotect: "Remove protection",
  apply: "Apply",
  reprotect: "Restore protection",
  verify: "Verify",
  "verify-absent": "Verify absent",
  preview: "Preview",
  playbook: "Run the playbook",
  "outcome-approval": "Decide on the outcome",
  "playbook-approved": "Run the playbook again",
};

export function stepName(step: Step): string {
  return stepNames[step.name] ?? humanize(step.name);
}

/** "deploy · Run the playbook", or the name alone when the step has no stage. */
export function stepTitle(step: Step): string {
  return step.stage ? `${step.stage} · ${stepName(step)}` : stepName(step);
}

export function describe(step: Step, operation: string): string | null {
  switch (step.name) {
    case "plan":
      return operation === "retire"
        ? "OpenTofu plans destroying the machine; the saved plan is what gets applied."
        : "OpenTofu plans the change; the saved plan is what gets applied.";
    case "approval":
      return operation === "pipeline"
        ? "One approval, by policy or by you, of every stage's plan and preview at once."
        : operation === "configure"
        ? "The preview is approved by policy or by you."
        : "The saved plan is approved by policy or by you.";
    case "preview":
      return "Checks the playbook and lists its tasks, with no secrets and no contact with the guest. It shows what will run, not what will change.";
    case "playbook":
      return "Runs the playbook on the guest over SSH, against the pinned host key, with exactly what the preview listed.";
    case "outcome-approval":
      return "The playbook reported something that needs your decision. It ran once; this would run it once more.";
    case "playbook-approved":
      return "Runs the same commit and preview once more, with what the workflow says approving adds.";
    case "unprotect":
      return "Removes the machine's protection so it can be deleted, or so a container's disks can change.";
    case "apply":
      return "OpenTofu applies exactly the approved plan.";
    case "reprotect":
      return "Puts the machine's protection back, after a change in place or a plan that was not applied in full.";
    case "verify":
      return "Checks the VM or container through the Proxmox API.";
    case "verify-absent":
      return "Checks through the Proxmox API that the machine is gone.";
    default:
      return null;
  }
}

/** Who decided an approval step, in a few words; null while nobody has. */
export function decisionNote(step: Step): string | null {
  const approval = step.approval;
  if (!approval?.decision) return null;
  const who = approval.decidedBy === "policy" ? "policy" : approval.decidedBy;
  return approval.decision === "rejected" ? `rejected by ${who}` : `approved by ${who}`;
}

/** The last segment of an address or repository: "home/pve01/web/app" → "app". */
export function lastSegment(path: string): string {
  return path.slice(path.lastIndexOf("/") + 1);
}

export const levelTones: Record<BumpLevel, Tone> = {
  major: "warning",
  minor: "info",
  patch: "neutral",
  downgrade: "uncertain",
  changed: "neutral",
  new: "success",
  removed: "danger",
  unchanged: "muted",
};

