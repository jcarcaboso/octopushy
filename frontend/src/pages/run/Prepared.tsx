import { ChevronRight } from "lucide-react";
import { useState } from "react";
import { emphasize } from "../../lib/changes";
import { isHash, shortValue, type PreparedComparison, type PreparedItem, type PreparedLeaf, type PreparedMark } from "../../lib/prepared";

const markClass: Record<PreparedMark, string> = { "+": " overview--add", "−": " overview--remove", "~": "", "": "" };

/** A value, with a checksum cut short and whole in its title. */
function Value({ text }: { text: string }) {
  return isHash(text) ? <span title={text}>{shortValue(text)}</span> : text;
}

/** The new value with only what differs from the old one bright. Checksums differ everywhere, so they are just shown. */
function NewValue({ before, after }: { before: string | undefined; after: string }) {
  if (before === undefined || isHash(after)) {
    return (
      <b>
        <Value text={after} />
      </b>
    );
  }
  return emphasize(before, after).map((segment, i) => (segment.changed ? <b key={i}>{segment.text}</b> : <span key={i}>{segment.text}</span>));
}

function Transition({ mark, before, after }: { mark: PreparedMark; before?: string; after?: string }) {
  if (mark === "~" && before !== undefined && after !== undefined && before !== after) {
    return (
      <>
        <span className="prepared__old">
          <Value text={before} />
        </span>
        <span className="prepared__arrow" aria-label="to">→</span>
        <span className="prepared__new">
          <NewValue before={before} after={after} />
        </span>
      </>
    );
  }
  if (mark === "−") return <span className="prepared__old prepared__old--gone">{before !== undefined ? <Value text={before} /> : null}</span>;
  if (mark === "+") return <span className="prepared__new overview--add">{after !== undefined ? <Value text={after} /> : null}</span>;
  const value = after ?? before;
  return <span className={mark === "" ? "prepared__same" : "prepared__new"}>{value !== undefined ? <Value text={value} /> : null}</span>;
}

/** One value behind a thing's line: a changed one as the old line over the new one, so a long address still reads. */
function LeafRow({ leaf }: { leaf: PreparedLeaf }) {
  const changed = leaf.mark === "~" && leaf.before !== undefined && leaf.after !== undefined;
  return (
    <>
      <dt title={leaf.path}>{leaf.name}</dt>
      <dd>
        {changed ? (
          <>
            <span className="prepared__line prepared__line--old">
              <span aria-hidden="true">−</span>
              <span title={leaf.before}>{leaf.before}</span>
            </span>
            <span className="prepared__line prepared__line--new">
              <span aria-hidden="true">+</span>
              <span title={leaf.after}>{isHash(leaf.after) ? leaf.after : <NewValue before={leaf.before} after={leaf.after!} />}</span>
            </span>
          </>
        ) : (
          <span className="prepared__line">
            <span aria-hidden="true">{leaf.mark || "="}</span>
            <span>{leaf.after ?? leaf.before}</span>
          </span>
        )}
      </dd>
    </>
  );
}

function Item({ item, compared }: { item: PreparedItem; compared: boolean }) {
  const [open, setOpen] = useState(false);
  const quiet = compared && item.mark === "";
  return (
    <li className={quiet ? "prepared__item prepared__item--quiet" : "prepared__item"}>
      <div className="prepared__row">
        <span className={`prepared__mark${markClass[item.mark]}`} aria-hidden="true">{quiet ? "=" : item.mark}</span>
        <span className="prepared__name" title={item.path}>{item.name}</span>
        <span className="prepared__value">
          <Transition mark={item.mark} before={item.before} after={item.after} />
          {item.note ? <span className="prepared__note">{item.note}</span> : quiet ? <span className="prepared__note">unchanged</span> : null}
        </span>
        {item.leaves.length > 0 ? (
          <button type="button" className="prepared__more" aria-expanded={open} onClick={() => setOpen((on) => !on)}>
            {item.leaves.map((leaf) => leaf.name).join(", ")}
            <ChevronRight className="run-chevron" size={13} aria-hidden="true" />
          </button>
        ) : null}
      </div>
      {open ? (
        <dl className="prepared__leaves">
          {item.leaves.map((leaf) => <LeafRow key={leaf.path} leaf={leaf} />)}
        </dl>
      ) : null}
    </li>
  );
}

/** What a run prepared: the things that differ from the last successful run, and the rest behind a toggle. */
export function PreparedItems({ comparison }: { comparison: PreparedComparison }) {
  const [showSame, setShowSame] = useState(false);
  const { compared, items } = comparison;
  const changed = compared ? items.filter((item) => item.mark !== "") : items;
  const same = compared ? items.filter((item) => item.mark === "") : [];
  const shown = showSame ? [...changed, ...same] : changed;
  return (
    <>
      {shown.length > 0 ? (
        <ul className="prepared">
          {shown.map((item) => <Item key={item.path} item={item} compared={compared} />)}
        </ul>
      ) : (
        <p className="prepared__nothing">Nothing new: the same values as the last successful run.</p>
      )}
      {same.length > 0 ? (
        <button type="button" className="overview__more" aria-expanded={showSame} onClick={() => setShowSame((on) => !on)}>
          {showSame ? "− hide unchanged" : `+ ${same.length} unchanged`}
        </button>
      ) : null}
    </>
  );
}
