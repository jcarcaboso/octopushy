import { Link } from "react-router";
import type { InstallationSummary } from "../api/types";
import { Callout } from "./Callout";

/** Why the installation's last observe run did not produce an observation: it failed, or it is blocked. */
export function ObserveError({ installation }: { installation: InstallationSummary }) {
  const { observeError: error, observeState: state, observeRunId: runId } = installation;
  if (!error && !state) return null;
  const blocked = state === "blocked";
  return (
    <Callout
      kind={blocked ? "warning" : "danger"}
      title={blocked ? "Observing is blocked" : "The last observation failed"}
      actions={
        <>
          {runId ? (
            <Link to={`/runs/${runId}`} className="button button--small">
              Open the run
            </Link>
          ) : null}
          {error && /missing secret/i.test(error) ? (
            <Link to="/variables" className="button button--small">
              Open Variables
            </Link>
          ) : null}
        </>
      }
    >
      {error}
    </Callout>
  );
}
