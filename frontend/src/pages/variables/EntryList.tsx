import { FileText, Folder as FolderIcon, KeyRound, Lock } from "lucide-react";
import type { Variable } from "../../api/types";
import { GuardLink } from "./GuardLink";
import { folderHref, lastSegment, parentFolder, type Folder } from "./folders";

interface Props {
  /** Subfolders, listed first; empty in a flat list. */
  folders: Folder[];
  entries: Variable[];
  /** A flat list of full keys, for All keys and a search. */
  flat: boolean;
  selected: string | null;
  hrefFor: (key: string) => string;
  /** A secret's value once revealed: null when it has none that may be used, undefined while concealed. */
  revealedValue: (entry: Variable) => string | null | undefined;
  onPick: () => void;
}

const plural = (n: number, one: string, many: string) => (n === 1 ? `1 ${one}` : `${n} ${many}`);

function Sub({ entry, revealed }: { entry: Variable; revealed: string | null | undefined }) {
  const value = entry.visibility === "plain" ? entry.value : revealed;
  const lines = entry.lines;
  if (lines !== null && lines > 1) return <span className="vars-row__sub">{plural(lines, "line", "lines")}</span>;
  if (typeof value === "string") return <code className="vars-row__sub vars-row__sub--value">{value}</code>;
  if (value === null || entry.lines === null) return <span className="vars-row__sub">No usable version</span>;
  return <span className="vars-row__sub">Concealed</span>;
}

export function EntryList({ folders, entries, flat, selected, hrefFor, revealedValue, onPick }: Props) {
  return (
    <ul className="vars-rows">
      {folders.map((folder) => (
        <li key={`folder:${folder.path}`}>
          <GuardLink to={folderHref(folder.path)} className="vars-row vars-row--folder">
            <span className="vars-row__main">
              <FolderIcon size={15} aria-hidden="true" className="vars-row__icon vars-row__icon--folder" />
              <span className="vars-row__name">{folder.name}</span>{" "}
              <span className="vars-row__end">{plural(folder.entries, "entry", "entries")}</span>
            </span>
            {folder.revoked > 0 || folder.newer > 0 ? " " : null}
            {folder.revoked > 0 || folder.newer > 0 ? (
              <span className="vars-row__sub vars-row__flags">
                {folder.revoked > 0 ? <span className="run-chip vars-chip--danger">{folder.revoked} revoked</span> : null}
                {folder.newer > 0 ? <span className="run-chip vars-chip--warning">{folder.newer} with a newer version</span> : null}
              </span>
            ) : null}
          </GuardLink>
        </li>
      ))}
      {entries.map((entry) => {
        const revealed = revealedValue(entry);
        const multi = (entry.lines ?? 0) > 1 || (typeof revealed === "string" && revealed.includes("\n"));
        const Icon = multi ? FileText : entry.visibility === "secret" ? Lock : KeyRound;
        const folder = parentFolder(entry.key);
        return (
          <li key={`entry:${entry.key}`}>
            <GuardLink
              to={hrefFor(entry.key)}
              className="vars-row"
              title={entry.key}
              aria-current={selected === entry.key ? "true" : undefined}
              data-key={entry.key}
              onFollow={onPick}
            >
              <span className="vars-row__main">
                <Icon size={15} aria-hidden="true" className={multi ? "vars-row__icon vars-row__icon--file" : "vars-row__icon"} />
                <span className="vars-row__name">{lastSegment(entry.key)}</span>{" "}
                <span className="vars-row__end">
                  {entry.revoked ? <span className="run-chip vars-chip--danger">Revoked</span> : null}{" "}
                  {entry.latestVersion > entry.version ? <span className="run-chip vars-chip--warning">Latest v{entry.latestVersion}</span> : null}{" "}
                  v{entry.version}
                </span>
              </span>
              {" "}
              {flat ? <span className="vars-row__folder">{folder ? `${folder}/` : "No folder"}</span> : null}{" "}
              <Sub entry={entry} revealed={revealed} />
            </GuardLink>
          </li>
        );
      })}
    </ul>
  );
}
