import { Link } from "react-router";
import { PageHeader } from "../components/PageHeader";
import { EmptyState } from "../components/States";

export function NotFoundPage() {
  return (
    <>
      <PageHeader title="Page not found" />
      <EmptyState title="There is nothing at this address" actions={<Link to="/" className="button">Go to the overview</Link>} />
    </>
  );
}
