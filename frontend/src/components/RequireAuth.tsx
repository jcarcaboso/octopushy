import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router";
import { useAuthState } from "../api/queries";
import { ErrorMessage, Spinner } from "./States";

/** Shows its children only to a signed-in user; otherwise sends them to setup or sign-in. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { data, error, isPending, refetch } = useAuthState();
  const location = useLocation();

  if (isPending) {
    return (
      <div className="center-screen">
        <Spinner label="Checking the session" />
      </div>
    );
  }
  if (error || !data) {
    return (
      <div className="center-screen">
        <div className="auth-card stack">
          <ErrorMessage error={error} title="Octopushy cannot be reached" />
          <button type="button" className="button" onClick={() => void refetch()}>
            Try again
          </button>
        </div>
      </div>
    );
  }
  if (data.setupRequired) return <Navigate to="/setup" replace />;
  if (!data.authenticated) {
    const next = location.pathname + location.search;
    return <Navigate to={next === "/" ? "/login" : `/login?next=${encodeURIComponent(next)}`} replace />;
  }
  return <>{children}</>;
}
