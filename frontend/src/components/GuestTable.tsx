import { Link } from "react-router";
import type { GuestSummary } from "../api/types";
import { StatusBadge } from "./Badge";
import { EmptyRow, Table } from "./Table";

function Power({ guest }: { guest: GuestSummary }) {
  const status = guest.observed?.status;
  if (!status) return <span className="muted">—</span>;
  return (
    <span className="power">
      <span className={status === "running" ? "conn conn--on" : "conn conn--off"} aria-hidden="true" />
      {status}
    </span>
  );
}

export function GuestTable({ guests, label, emptyText = "No guests declared." }: { guests: GuestSummary[]; label: string; emptyText?: string }) {
  return (
    <Table label={label} className="table--guests">
      <thead>
        <tr>
          <th scope="col">Guest</th>
          <th scope="col">VMID</th>
          <th scope="col">Host</th>
          <th scope="col">Status</th>
          <th scope="col">Power</th>
          <th scope="col">Last run</th>
        </tr>
      </thead>
      <tbody>
        {guests.length === 0 ? <EmptyRow colSpan={6}>{emptyText}</EmptyRow> : null}
        {guests.map((guest) => {
          const run = guest.activeRun ?? guest.lastRun;
          return (
            <tr key={guest.id}>
              <th scope="row">
                <Link to={`/guests/${guest.id}`} className="strong-link mono">
                  {guest.name}
                </Link>
                {guest.lifecycle === "retired" && guest.status !== "retired" ? (
                  <span className="muted small"> · retiring</span>
                ) : null}
              </th>
              <td className="num">{guest.vmid ?? <span className="muted">—</span>}</td>
              <td className="mono">{guest.host ?? <span className="muted">—</span>}</td>
              <td className="cell-status">
                <StatusBadge status={guest.status} />
                {guest.statusDetail ? <div className="muted small cell-status__detail">{guest.statusDetail}</div> : null}
              </td>
              <td>
                <Power guest={guest} />
              </td>
              <td className="nowrap">
                {run ? (
                  <Link to={`/runs/${run.id}`} className="run-link" title={run.waiting ?? undefined}>
                    #{run.number} <StatusBadge status={run.status} />
                  </Link>
                ) : (
                  <span className="muted">None</span>
                )}
              </td>
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}
