import {
  Bot,
  CirclePlay,
  FilePen,
  GitBranch,
  GitCommitHorizontal,
  KeyRound,
  LayoutDashboard,
  LogOut,
  Server,
  TriangleAlert,
  User,
} from "lucide-react";
import { NavLink, Outlet, useNavigate } from "react-router";
import { useAuthState, useLogout, useRepository } from "../api/queries";
import { shortCommit } from "../lib/format";

const nav = [
  { to: "/", label: "Overview", icon: LayoutDashboard, end: true },
  { to: "/inventory", label: "Inventory", icon: Server },
  { to: "/runs", label: "Runs", icon: CirclePlay },
  { to: "/agents", label: "Agents", icon: Bot },
  { to: "/variables", label: "Variables", icon: KeyRound },
  { to: "/repository", label: "Repository", icon: GitBranch },
  { to: "/editor", label: "Editor", icon: FilePen },
  { to: "/operator-access", label: "CLI access", icon: User },
];

function RevisionIndicator() {
  const { data } = useRepository();
  if (!data) return <span className="topbar__revision muted">…</span>;
  if (!data.url) {
    return (
      <NavLink to="/repository" className="topbar__revision muted">
        No lab repository connected
      </NavLink>
    );
  }
  const revision = data.revision;
  return (
    <span className="topbar__status">
      <NavLink
        to="/repository"
        className="topbar__revision"
        title={revision ? `Current revision ${revision.commit}${revision.subject ? `: ${revision.subject}` : ""}` : "No revision loaded yet"}
      >
        <GitCommitHorizontal size={16} aria-hidden="true" />
        {revision ? (
          <>
            <code className="mono">{shortCommit(revision.commit)}</code>
            <span className="topbar__subject">{revision.subject ?? "(no subject)"}</span>
          </>
        ) : (
          <span className="muted">No revision loaded yet</span>
        )}
      </NavLink>
      {data.lastFetchError ? (
        <NavLink to="/repository" className="badge badge--danger" title={data.lastFetchError}>
          <TriangleAlert size={13} aria-hidden="true" className="badge__icon" />
          Last fetch failed
        </NavLink>
      ) : null}
    </span>
  );
}

export function Layout() {
  const { data: auth } = useAuthState();
  const logout = useLogout();
  const navigate = useNavigate();

  return (
    <div className="shell">
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <aside className="sidebar">
        <div className="brand">
          <img src="/favicon.svg" alt="" width={26} height={26} />
          <span>octopushy</span>
        </div>
        <nav aria-label="Main">
          <ul className="nav">
            {nav.map(({ to, label, icon: Icon, end }) => (
              <li key={to}>
                <NavLink to={to} end={end} className={({ isActive }) => (isActive ? "nav__link nav__link--active" : "nav__link")}>
                  <Icon size={17} aria-hidden="true" />
                  <span>{label}</span>
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
      </aside>
      <div className="shell__main">
        <header className="topbar">
          <RevisionIndicator />
          <div className="topbar__user">
            <span className="topbar__name">
              <User size={15} aria-hidden="true" />
              {auth?.username}
            </span>
            <button
              type="button"
              className="button button--ghost button--small"
              onClick={() => logout.mutate(undefined, { onSettled: () => navigate("/login", { replace: true }) })}
              disabled={logout.isPending}
            >
              <LogOut size={15} aria-hidden="true" />
              Sign out
            </button>
          </div>
        </header>
        <main id="main" className="content" tabIndex={-1}>
          <Outlet />
        </main>
      </div>
    </div>
  );
}
