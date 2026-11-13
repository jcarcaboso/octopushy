import { ChevronRight, LayoutGrid, Link2, List } from "lucide-react";
import { useState } from "react";
import { useInventory, useUptime } from "../api/queries";
import { PageHeader } from "../components/PageHeader";
import { EmptyState, ErrorMessage, PageSkeleton } from "../components/States";
import { buildTree } from "../lib/inventory";
import { filterGroups, groupLinks, type LinkGroup } from "../lib/services";
import { LinkHead, LinkRows } from "./services/LinkRows";
import { LinkTiles } from "./services/LinkTiles";

type View = "list" | "tiles";

const viewStorageKey = "octopushy.services.view";

function storedView(): View {
  try {
    return window.localStorage.getItem(viewStorageKey) === "tiles" ? "tiles" : "list";
  } catch {
    return "list";
  }
}

function storeView(view: View) {
  try {
    window.localStorage.setItem(viewStorageKey, view);
  } catch {
    // The choice then lasts only until the page is left.
  }
}

/** The domain with its first label in full and the rest muted. */
function DomainName({ domain }: { domain: string | null }) {
  if (domain === null) return <span>No domain</span>;
  const dot = domain.indexOf(".");
  return (
    <span className="mono">
      {dot < 0 ? domain : domain.slice(0, dot)}
      <span className="muted">{dot < 0 ? "" : domain.slice(dot)}</span>
    </span>
  );
}

function Section({ group, view, open, onToggle }: { group: LinkGroup; view: View; open: boolean; onToggle: () => void }) {
  const label = group.domain ?? "No domain";
  const down = group.links.filter((l) => l.check?.status === "down").length;
  return (
    <section className="runs-section" aria-label={label}>
      <button type="button" className="runs-section__summary services-section__toggle" aria-expanded={open} onClick={onToggle}>
        <ChevronRight className="runs-section__chevron" size={14} aria-hidden="true" />
        <DomainName domain={group.domain} />
        <span className="runs-section__count">{group.links.length}</span>
        {down > 0 ? <span className="services-down services-section__down">{down} down</span> : null}
      </button>
      {open ? view === "list" ? <LinkRows links={group.links} label={label} /> : <LinkTiles links={group.links} label={label} /> : null}
    </section>
  );
}

export function ServicesPage() {
  const { data, error, isPending } = useInventory();
  const uptime = useUptime();
  const [view, setView] = useState<View>(storedView);
  const [search, setSearch] = useState("");
  const [closed, setClosed] = useState<Record<string, boolean>>({});

  const chooseView = (next: View) => {
    setView(next);
    storeView(next);
  };

  const header = (
    <PageHeader
      title="Services"
      actions={
        <div className="runs-filters">
          <div className="segmented segmented--inline" role="group" aria-label="View">
            <button type="button" className="segmented__item" aria-pressed={view === "list"} onClick={() => chooseView("list")}>
              <List size={14} aria-hidden="true" /> List
            </button>
            <button type="button" className="segmented__item" aria-pressed={view === "tiles"} onClick={() => chooseView("tiles")}>
              <LayoutGrid size={14} aria-hidden="true" /> Tiles
            </button>
          </div>
          <input
            type="search"
            className="runs-search"
            aria-label="Find a service"
            placeholder="Find a service"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
      }
    />
  );

  if (isPending) return <PageSkeleton />;
  if (!data) return <ErrorMessage error={error} title="The inventory could not be loaded" />;

  const groups = groupLinks(buildTree(data, uptime.data));
  const shown = filterGroups(groups, search);

  return (
    <>
      {header}
      {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
      {groups.length === 0 ? (
        <EmptyState icon={Link2} title="No service has a link">
          Add <code>links</code> to a service's <code>service.yaml</code> to open it from here.
        </EmptyState>
      ) : shown.length === 0 ? (
        <p className="muted">No service matches.</p>
      ) : (
        <div className={view === "list" ? "services services--list" : "services"}>
          <div className="services__inner">
            {view === "list" ? <LinkHead /> : null}
            {shown.map((group) => {
              const key = group.domain ?? "";
              return (
                <Section
                  key={key}
                  group={group}
                  view={view}
                  open={!closed[key]}
                  onToggle={() => setClosed((current) => ({ ...current, [key]: !current[key] }))}
                />
              );
            })}
          </div>
        </div>
      )}
    </>
  );
}
