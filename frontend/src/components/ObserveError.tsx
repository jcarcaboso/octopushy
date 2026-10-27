import { Link } from "react-router";
import type { InstallationSummary } from "../api/types";
import { Callout } from "./Callout";
import { isCredentialBlock } from "../lib/observe";
import { RelativeTime } from "./RelativeTime";

/** Why the installation's latest background observation did not succeed. */
export function ObserveError({ installation }: { installation: InstallationSummary }) {
  const { observeError: error, observeErrorAt: at } = installation;
  if (!error) return null;
  const blocked = isCredentialBlock(error);
  return (
    <Callout
      kind={blocked ? "warning" : "danger"}
      title={
        <>
          {blocked ? "Observing is blocked" : "The last observation failed"}
          {at ? (
            <span className="muted small">
              {" "}
              · <RelativeTime value={at} />
            </span>
          ) : null}
        </>
      }
      actions={
        blocked ? (
          <Link to="/variables" className="button button--small">
            Open Variables
          </Link>
        ) : null
      }
    >
      {error}
    </Callout>
  );
}
