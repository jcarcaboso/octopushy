/**
 * Container image versions of a service run: the references in its compose file before and after, compared by tag.
 * The references are repository content, so every function here accepts any text and only returns text.
 */

export type BumpLevel = "major" | "minor" | "patch" | "downgrade" | "changed" | "new" | "removed" | "unchanged";

export interface ImageEntry {
  service: string;
  before: string | null;
  after: string | null;
}

export interface ImageRef {
  repository: string;
  tag: string | null;
  digest: string | null;
}

export interface ImageChange {
  service: string;
  repository: string;
  /** The repository before, when it differs from `repository`. */
  previousRepository: string | null;
  /** The version as shown: the tag, else a short digest. Null when that side has no image. */
  before: string | null;
  after: string | null;
  level: BumpLevel;
}

export const levelLabels: Record<BumpLevel, string> = {
  major: "Major",
  minor: "Minor",
  patch: "Patch",
  downgrade: "Downgrade",
  changed: "Changed",
  new: "New",
  removed: "Removed",
  unchanged: "Unchanged",
};

/** "registry:5000/team/app:1.2@sha256:…": the tag is after the last colon that follows the last slash. */
export function parseImage(reference: string): ImageRef {
  const at = reference.indexOf("@");
  const digest = at >= 0 ? reference.slice(at + 1) || null : null;
  const name = at >= 0 ? reference.slice(0, at) : reference;
  const colon = name.lastIndexOf(":");
  if (colon > name.lastIndexOf("/")) {
    return { repository: name.slice(0, colon), tag: name.slice(colon + 1) || null, digest };
  }
  return { repository: name, tag: null, digest };
}

/** "sha256:0123456789ab", from a full digest. */
export function shortDigest(digest: string): string {
  const [algorithm, hex] = digest.includes(":") ? digest.split(":", 2) : ["", digest];
  const short = hex.slice(0, 12);
  return algorithm ? `${algorithm}:${short}` : short;
}

function versionText(image: ImageRef, withDigest: boolean): string {
  if (image.tag && image.digest && withDigest) return `${image.tag}@${shortDigest(image.digest)}`;
  if (image.tag) return image.tag;
  if (image.digest) return shortDigest(image.digest);
  return "no tag";
}

interface Version {
  prefix: string;
  numbers: number[];
}

/** The first run of up to three dotted numbers in a tag, with what comes before it: "dashboard-0.13.0", "v17.2-alpine". */
function version(tag: string | null): Version | null {
  if (!tag) return null;
  const match = /(\d+)(?:\.(\d+))?(?:\.(\d+))?/.exec(tag);
  if (!match) return null;
  const numbers = match.slice(1).map((part) => (part === undefined ? 0 : Number(part)));
  if (numbers.some((n) => !Number.isFinite(n))) return null;
  // A leading "v" is part of the version, not a prefix.
  return { prefix: tag.slice(0, match.index).replace(/v$/i, ""), numbers };
}

/** How the tag moved from `before` to `after`, both as written in the compose file. */
export function bumpLevel(before: string | null, after: string | null): BumpLevel {
  if (before === null && after === null) return "unchanged";
  if (before === null) return "new";
  if (after === null) return "removed";
  if (before === after) return "unchanged";
  const old = parseImage(before);
  const next = parseImage(after);
  const a = version(old.tag);
  const b = version(next.tag);
  if (a && b && a.prefix === b.prefix) {
    const names: BumpLevel[] = ["major", "minor", "patch"];
    for (let i = 0; i < 3; i++) {
      if (b.numbers[i] > a.numbers[i]) return names[i];
      if (b.numbers[i] < a.numbers[i]) return "downgrade";
    }
  }
  // The numbers are the same or cannot be compared, yet the reference differs: another suffix, digest or repository.
  return "changed";
}

/** One row per compose service, as the Versions line lists them. */
export function imageChanges(images: ImageEntry[]): ImageChange[] {
  return images.map((image) => {
    const old = image.before === null ? null : parseImage(image.before);
    const next = image.after === null ? null : parseImage(image.after);
    const repository = (next ?? old)?.repository ?? "";
    const previousRepository = old && next && old.repository !== next.repository ? old.repository : null;
    // Show digests only when the tags alone would read the same.
    const sameTag = old !== null && next !== null && old.tag === next.tag;
    return {
      service: image.service,
      repository,
      previousRepository,
      before: old ? versionText(old, sameTag) : null,
      after: next ? versionText(next, sameTag) : null,
      level: bumpLevel(image.before, image.after),
    };
  });
}

const rank: BumpLevel[] = ["major", "minor", "patch", "downgrade", "changed", "new", "removed"];

function lastSegment(repository: string) {
  return repository.slice(repository.lastIndexOf("/") + 1).toLowerCase();
}

export interface Headline {
  change: ImageChange;
  /** How many other images changed. */
  others: number;
}

/**
 * The image a service run is about: a changed one named like the service, else the changed one with the biggest bump.
 * Null when no image changed.
 */
export function headlineImage(changes: ImageChange[], serviceName: string): Headline | null {
  const changed = changes.filter((c) => c.level !== "unchanged");
  if (changed.length === 0) return null;
  const name = serviceName.toLowerCase();
  const byLevel = [...changed].sort((x, y) => rank.indexOf(x.level) - rank.indexOf(y.level));
  const pick =
    (name && byLevel.find((c) => lastSegment(c.repository) === name)) ||
    (name && byLevel.find((c) => lastSegment(c.repository).includes(name))) ||
    byLevel[0];
  return { change: pick, others: changed.length - 1 };
}

/** "2 bumped · 1 new · 1 unchanged". */
export function summarizeChanges(changes: ImageChange[]): string {
  const count = (levels: BumpLevel[]) => changes.filter((c) => levels.includes(c.level)).length;
  const parts: [number, string][] = [
    [count(["major", "minor", "patch"]), "bumped"],
    [count(["downgrade"]), "downgraded"],
    [count(["changed"]), "changed"],
    [count(["new"]), "new"],
    [count(["removed"]), "removed"],
    [count(["unchanged"]), "unchanged"],
  ];
  return parts.filter(([n]) => n > 0).map(([n, word]) => `${n} ${word}`).join(" · ");
}
