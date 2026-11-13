import { Link } from "react-router";
import type { ServiceSummary } from "../api/types";
import { StatusBadge } from "./Badge";
import { Card } from "./Card";
import { WorkflowLabel } from "./WorkflowLabel";

/** The services on a guest, read from the repository. Each has its own status: a failure of one says nothing about the guest or the others. */
export function ServicesCard({ services }: { services: ServiceSummary[] }) {
  return (
    <Card title="Services" meta={services.length} flush id="services">
      <ul className="service-list" aria-label="Services">
        {services.map((service) => {
          const run = service.activeRun ?? service.lastRun;
          return (
            <li key={service.id} className="service-list__item">
              <span className="service-list__main">
                <Link to={`/services/${service.id}`} className="strong-link mono">
                  {service.name}
                </Link>
                <span className="small muted">
                  <WorkflowLabel workflow={service.workflow} />
                </span>
                {service.statusDetail ? <span className="muted small">{service.statusDetail}</span> : null}
              </span>
              <span className="service-list__state">
                <StatusBadge status={service.status} />
                {run ? (
                  <Link to={`/runs/${run.id}`} className="small" title={run.waiting ?? undefined}>
                    #{run.number}
                  </Link>
                ) : null}
              </span>
            </li>
          );
        })}
      </ul>
    </Card>
  );
}
