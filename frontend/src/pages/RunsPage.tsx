import { Link, useSearchParams } from "react-router";
import { useRuns } from "../api/queries";
import { Card } from "../components/Card";
import { PageHeader } from "../components/PageHeader";
import { RunTable } from "../components/RunTable";
import { ErrorMessage, PageSkeleton, Spinner } from "../components/States";
import { runStatuses, statusStyle } from "../components/status";

export function RunsPage() {
  const [params] = useSearchParams();
  const status = params.get("status");
  const { data, error, isPending, isFetching, isPlaceholderData } = useRuns(status);

  return (
    <>
      <PageHeader title="Runs" meta="Every provision and retire run, newest first." />
      <nav className="segmented" aria-label="Filter runs by status">
        <Link to="/runs" className="segmented__item" aria-current={status === null ? "page" : undefined}>
          All
        </Link>
        {runStatuses.map((s) => (
          <Link key={s} to={`/runs?status=${s}`} className="segmented__item" aria-current={status === s ? "page" : undefined}>
            {statusStyle(s).label}
          </Link>
        ))}
        {isFetching && isPlaceholderData ? <Spinner label="Loading runs" /> : null}
      </nav>
      {isPending ? (
        <PageSkeleton rows={1} />
      ) : !data ? (
        <ErrorMessage error={error} title="The runs could not be loaded" />
      ) : (
        <Card flush>
          <RunTable
            runs={data}
            label="Runs"
            emptyText={status ? `No runs are ${statusStyle(status).label.toLowerCase()}.` : "No runs yet. Runs start when a revision changes a guest, or by hand with Plan now."}
          />
        </Card>
      )}
      {data && error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
    </>
  );
}
