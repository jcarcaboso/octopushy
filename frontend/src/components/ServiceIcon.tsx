import { useState } from "react";
import type { ServiceSummary } from "../api/types";

/** The icon Octopushy serves for a service, else the first letter of its name. */
export function ServiceIcon({ service, large = false }: { service: ServiceSummary; large?: boolean }) {
  const src = service.icon?.url ?? null;
  // Keyed by the source, so a new icon gets its own try.
  const [failed, setFailed] = useState<string | null>(null);
  const className = large ? "service-icon service-icon--large" : "service-icon";
  if (!src || failed === src) {
    return (
      <span className={`${className} service-icon--letter`} aria-hidden="true">
        {service.name.slice(0, 1).toUpperCase()}
      </span>
    );
  }
  return <img className={className} src={src} alt="" loading="lazy" onError={() => setFailed(src)} />;
}
