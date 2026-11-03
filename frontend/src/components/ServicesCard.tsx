import { Link } from "react-router";
import type { ServiceSummary } from "../api/types";
import { StatusBadge } from "./Badge";
import { Card } from "./Card";
import { Table } from "./Table";

/** The services on a guest, read from the repository. Each has its own status: a failure of one says nothing about the guest or the others. */
export function ServicesCard({ services }: { services: ServiceSummary[] }) {
  return (
    <Card title="Services" meta={services.length} flush id="services">
      <Table label="Services">
        <thead>
          <tr>
            <th scope="col">Service</th>
            <th scope="col">Status</th>
            <th scope="col">Last run</th>
          </tr>
        </thead>
        <tbody>
          {services.map((service) => {
            const run = service.activeRun ?? service.lastRun;
            return (
              <tr key={service.id}>
                <th scope="row">
                  <Link to={`/services/${service.id}`} className="strong-link mono">
                    {service.name}
                  </Link>
                </th>
                <td className="cell-status">
                  <StatusBadge status={service.status} />
                  {service.statusDetail ? <div className="muted small cell-status__detail">{service.statusDetail}</div> : null}
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
    </Card>
  );
}
