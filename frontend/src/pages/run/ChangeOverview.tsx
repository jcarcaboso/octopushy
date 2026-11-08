import { useEffect, useId, useState } from "react";
import { emphasize, type Row, type Tile } from "../../lib/changes";

const markClass: Record<Row["mark"], string> = { "+": "add", "−": "remove", "±": "remove", "~": "", "": "" };

/** "32 → 64 GiB" with the old side quiet; "+agents −old" with each sign in its colour. */
function TileValue({ value }: { value: string }) {
  const [before, after] = value.split(" → ");
  if (after !== undefined) {
    const unit = /^(\S+) ([A-Za-z]+)$/.exec(after);
    return (
      <>
        <span className="overview-tile__old">{before}</span>
        <span className="overview-tile__arrow">→</span>
        {unit ? (
          <>
            {unit[1]}
            <span className="overview-tile__unit">{unit[2]}</span>
          </>
        ) : after}
      </>
    );
  }
  return value.split(" ").map((token, i) => (
    <span key={i} className={token.startsWith("+") ? "overview--add" : token.startsWith("−") ? "overview--remove" : undefined}>
      {i > 0 ? " " : ""}
      {token}
    </span>
  ));
}

function NewValue({ row }: { row: Row }) {
  if (row.after === undefined) return row.mark === "−" && row.before !== undefined ? "—" : null;
  if (row.before === undefined || row.mark !== "~") return <b>{row.after}</b>;
  return emphasize(row.before, row.after).map((segment, i) => (segment.changed ? <b key={i}>{segment.text}</b> : <span key={i}>{segment.text}</span>));
}

function RowLine({ row, unchanged = false }: { row: Row; unchanged?: boolean }) {
  const arrow = !unchanged && (row.before !== undefined || row.mark === "+") && (row.after !== undefined || row.mark === "−");
  const className = [unchanged ? "overview-row--unchanged" : "", row.mark === "−" ? "overview-row--gone" : ""].filter(Boolean).join(" ");
  return (
    <tr className={className || undefined}>
      <td className={`overview-row__mark ${markClass[row.mark] ? `overview--${markClass[row.mark]}` : ""}`}>{unchanged ? "=" : row.mark}</td>
      <td className="overview-row__name">{row.name}</td>
      <td className="overview-row__old">{row.before ?? (arrow ? "—" : "")}</td>
      <td className="overview-row__arrow" aria-hidden="true">{arrow ? "→" : ""}</td>
      <td className={`overview-row__new${row.mark === "+" ? " overview--add" : ""}`}>
        <NewValue row={row} />
      </td>
      <td className="overview-row__note">{unchanged ? "unchanged" : row.note ?? ""}</td>
    </tr>
  );
}

function Detail({ tile, id }: { tile: Tile; id: string }) {
  const [showUnchanged, setShowUnchanged] = useState(false);
  return (
    <div id={id} className="overview__panel" role="region" aria-label={tile.label}>
      {tile.heading ? <div className="overview__heading mono">{tile.heading}</div> : null}
      {tile.rows.length > 0 || showUnchanged ? (
        <table className="overview__rows">
          <tbody>
            {tile.rows.map((row, i) => <RowLine key={i} row={row} />)}
            {showUnchanged ? tile.unchanged.map((row, i) => <RowLine key={`u${i}`} row={row} unchanged />) : null}
          </tbody>
        </table>
      ) : (
        <p className="muted small">Nothing changes here.</p>
      )}
      {tile.unchanged.length > 0 ? (
        <button type="button" className="overview__more" aria-expanded={showUnchanged} onClick={() => setShowUnchanged((on) => !on)}>
          {showUnchanged ? "− hide unchanged" : `+ ${tile.unchanged.length} unchanged`}
        </button>
      ) : null}
    </div>
  );
}

/** What a run awaiting approval will change, as tiles; one tile's detail at a time. */
export function ChangeOverview({ tiles }: { tiles: Tile[] }) {
  const [open, setOpen] = useState<string | null>(null);
  const baseId = useId();

  useEffect(() => {
    if (open === null) return;
    const close = (event: KeyboardEvent) => {
      if (event.key === "Escape") setOpen(null);
    };
    document.addEventListener("keydown", close);
    return () => document.removeEventListener("keydown", close);
  }, [open]);

  if (tiles.length === 0) return null;
  const panelId = (i: number) => `${baseId}-panel-${i}`;
  return (
    <div className="overview">
      <div className="overview__tiles" role="group" aria-label="What changes">
        {tiles.map((tile, i) => (
          <button
            key={tile.key}
            type="button"
            className={`overview-tile overview-tile--${tile.tone}`}
            aria-expanded={open === tile.key}
            aria-controls={panelId(i)}
            onClick={() => setOpen((current) => (current === tile.key ? null : tile.key))}
          >
            <span className="overview-tile__label">
              {tile.label}
              <span className="overview-tile__chevron" aria-hidden="true" />
            </span>
            <span className="overview-tile__value">
              <TileValue value={tile.value} />
            </span>
            <span className="overview-tile__sub">{tile.sub}</span>
          </button>
        ))}
      </div>
      {tiles.map((tile, i) => (open === tile.key ? <Detail key={tile.key} tile={tile} id={panelId(i)} /> : null))}
      <p className="overview__honest">What this run will apply, not read from the guest.</p>
    </div>
  );
}
