import {
  ChevronRight,
  Cpu,
  FileText,
  HardDrive,
  KeyRound,
  Layers,
  ListChecks,
  MemoryStick,
  Network,
  SlidersHorizontal,
  Tag,
  Tags,
  type LucideIcon,
} from "lucide-react";
import { useId, useState, type ReactNode } from "react";
import type { RunDetail } from "../../api/types";
import { Badge } from "../../components/Badge";
import { Commit } from "../../components/Mono";
import { emphasize, type Tile } from "../../lib/changes";
import { imageChanges, levelLabels, summarizeChanges, type BumpLevel, type ImageChange } from "../../lib/images";
import { levelTones } from "./steps";
import { ChangeRows, TileValue, UnchangedToggle } from "./ChangeOverview";

export function LevelChip({ level }: { level: BumpLevel }) {
  return <Badge tone={levelTones[level]}>{levelLabels[level]}</Badge>;
}

/** The new value with only the part that differs from the old one in bold. */
export function Emphasized({ before, after }: { before: string | null; after: string }) {
  if (before === null) return <b>{after}</b>;
  return emphasize(before, after).map((segment, i) => (segment.changed ? <b key={i}>{segment.text}</b> : <span key={i}>{segment.text}</span>));
}

function VersionRow({ change }: { change: ImageChange }) {
  const unchanged = change.level === "unchanged";
  return (
    <tr className={unchanged ? "overview-row--unchanged" : change.level === "removed" ? "overview-row--gone" : undefined}>
      <td className="versions__service">
        <span className="overview-row__name">{change.service}</span>
        <span className="versions__repository">
          {change.previousRepository ? `${change.previousRepository} → ` : ""}
          {change.repository}
        </span>
      </td>
      <td className="overview-row__old">{unchanged ? "" : change.before ?? "—"}</td>
      <td className="overview-row__arrow" aria-hidden="true">{unchanged ? "" : "→"}</td>
      <td className="overview-row__new">
        {unchanged ? change.after : change.after === null ? "—" : <Emphasized before={change.before} after={change.after} />}
      </td>
      <td className="versions__level">{unchanged ? <span className="muted">unchanged</span> : <LevelChip level={change.level} />}</td>
    </tr>
  );
}

function Versions({ changes, commit, baseline }: { changes: ImageChange[]; commit: string | null; baseline: string | null }) {
  const [showUnchanged, setShowUnchanged] = useState(false);
  const changed = changes.filter((c) => c.level !== "unchanged");
  const unchanged = changes.filter((c) => c.level === "unchanged");
  const shown = showUnchanged ? [...changed, ...unchanged] : changed;
  return (
    <>
      {shown.length > 0 ? (
        <table className="overview__rows versions">
          <tbody>
            {shown.map((change, i) => <VersionRow key={i} change={change} />)}
          </tbody>
        </table>
      ) : (
        <p className="muted small">No image changes.</p>
      )}
      <UnchangedToggle count={unchanged.length} shown={showUnchanged} onToggle={() => setShowUnchanged((on) => !on)} />
      <p className="run-line__source">
        Images in the compose file at <Commit commit={commit} />
        {baseline ? (
          <>
            , compared with <Commit commit={baseline} />, the commit last applied.
          </>
        ) : (
          ", with nothing applied before to compare with."
        )}
      </p>
    </>
  );
}

const tileIcons: Record<string, LucideIcon> = {
  Disk: HardDrive,
  Memory: MemoryStick,
  CPU: Cpu,
  Network,
  Tags,
  Resources: Layers,
  Prepared: ListChecks,
  "Recipe files": FileText,
  Secrets: KeyRound,
};

type LineTone = "neutral" | "add" | "remove" | "warn" | "danger" | "quiet";

interface Line {
  key: string;
  label: string;
  icon: LucideIcon;
  summary: ReactNode;
  tone: LineTone;
  body: ReactNode;
}

function tileLine(tile: Tile): Line {
  // A stage prefixes the label as "infra · Disk"; the icon follows the kind of change.
  const kind = tile.label.split(" · ").pop() ?? tile.label;
  return {
    key: tile.key,
    label: tile.label,
    icon: tileIcons[kind] ?? SlidersHorizontal,
    tone: tile.tone,
    summary: (
      <>
        <TileValue value={tile.value} />
        {tile.sub ? <span className="run-line__sub"> · {tile.sub}</span> : null}
      </>
    ),
    body: <ChangeRows tile={tile} />,
  };
}

function LineItem({ line, open, onToggle }: { line: Line; open: boolean; onToggle: () => void }) {
  const bodyId = useId();
  const Icon = line.icon;
  return (
    <li className="run-line" data-tone={line.tone}>
      <button type="button" className="run-line__head" aria-expanded={open} aria-controls={bodyId} onClick={onToggle}>
        <Icon className="run-line__icon" size={15} aria-hidden="true" />
        <span className="run-line__label">{line.label}</span>
        <span className="run-line__summary">{line.summary}</span>
        <ChevronRight className="run-chevron" size={15} aria-hidden="true" />
      </button>
      {open ? (
        <div id={bodyId} className="run-line__body" role="region" aria-label={line.label}>
          {line.body}
        </div>
      ) : null}
    </li>
  );
}

function sectionTitle(status: string, finished: boolean) {
  if (status === "awaiting-approval") return "What will change";
  return finished ? "What changed" : "What is changing";
}

/** What the run changes, one line per kind of change; the first opens by itself. */
export function RunChanges({ detail, tiles, finished }: { detail: RunDetail; tiles: Tile[]; finished: boolean }) {
  const lines: Line[] = [];
  if (detail.images && detail.images.length > 0) {
    const changes = imageChanges(detail.images);
    const changed = changes.some((c) => c.level !== "unchanged");
    lines.push({
      key: "versions",
      label: "Versions",
      icon: Tag,
      tone: changed ? "neutral" : "quiet",
      summary: summarizeChanges(changes),
      body: <Versions changes={changes} commit={detail.run.commit} baseline={detail.baselineCommit ?? null} />,
    });
  }
  lines.push(...tiles.map(tileLine));

  const first = lines[0]?.key ?? null;
  const [open, setOpen] = useState<Set<string>>(() => new Set(first ? [first] : []));
  const [touched, setTouched] = useState(false);
  // On a live run the changes can appear after the page loaded: then open the first one as if it had been there.
  if (!touched && first && open.size === 0) setOpen(new Set([first]));

  if (lines.length === 0) return null;
  const toggle = (key: string) => {
    setTouched(true);
    setOpen((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };
  return (
    <section className="run-section" aria-labelledby="run-changes-title">
      <h2 id="run-changes-title" className="run-section__title">{sectionTitle(detail.run.status, finished)}</h2>
      <ul className="run-list">
        {lines.map((line) => (
          <LineItem key={line.key} line={line} open={open.has(line.key)} onToggle={() => toggle(line.key)} />
        ))}
      </ul>
      {tiles.length > 0 ? (
        <p className="run-section__note">
          {finished ? "What this run planned, not read from the guest." : "What this run will apply, not read from the guest."}
        </p>
      ) : null}
    </section>
  );
}
