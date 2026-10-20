export function shortCommit(commit: string | null | undefined, length = 7): string {
  return commit ? commit.slice(0, length) : "—";
}

export function shortHash(hash: string, length = 12): string {
  return hash.length > length ? `${hash.slice(0, length)}…` : hash;
}

const units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

export function formatBytes(bytes: number | null): string {
  if (bytes === null || !Number.isFinite(bytes)) return "—";
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  const digits = value >= 100 || unit === 0 ? 0 : 1;
  return `${value.toFixed(digits)} ${units[unit]}`;
}

/** "1 guest", "3 guests". */
export function plural(count: number, one: string, many = `${one}s`): string {
  return `${count} ${count === 1 ? one : many}`;
}

/** "verify-absent" → "Verify absent". */
export function humanize(value: string): string {
  const text = value.replace(/[-_]+/g, " ").trim();
  return text.charAt(0).toUpperCase() + text.slice(1);
}

const operations: Record<string, string> = {
  provision: "Provision",
  retire: "Retire",
  observe: "Observe",
};

export function operationLabel(operation: string): string {
  return operations[operation] ?? humanize(operation);
}

const reasons: Record<string, string> = {
  revision: "New revision",
  manual: "Started by hand",
  schedule: "Scheduled",
};

export function reasonLabel(reason: string): string {
  return reasons[reason] ?? humanize(reason);
}

/** The page of the resource a run belongs to; labs have none of their own, so they go to the inventory. */
export function runResourcePath(run: { resourceKind: string; resourceId: string }): string {
  switch (run.resourceKind) {
    case "guest":
      return `/guests/${run.resourceId}`;
    case "installation":
      return `/installations/${run.resourceId}`;
    default:
      return "/inventory";
  }
}
