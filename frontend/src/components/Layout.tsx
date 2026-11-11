import {
  Bot,
  CirclePlay,
  FilePen,
  GitBranch,
  GitCommitHorizontal,
  KeyRound,
  LayoutDashboard,
  LogOut,
  Rocket,
  Server,
  TriangleAlert,
  User,
} from "lucide-react";
import { NavLink, Outlet, useNavigate } from "react-router";
import { useAuthState, useLogout, useRepository } from "../api/queries";
import { shortCommit } from "../lib/format";

const groups = [
  {
    id: "nav-operate",
    label: "Operate",
    items: [
      { to: "/", label: "Overview", icon: LayoutDashboard, end: true },
      { to: "/inventory", label: "Inventory", icon: Server },
      { to: "/deploy", label: "Deploy", icon: Rocket },
      { to: "/runs", label: "Runs", icon: CirclePlay },
    ],
  },
  {
    id: "nav-configure",
    label: "Configure",
    items: [
      { to: "/variables", label: "Variables", icon: KeyRound },
      { to: "/repository", label: "Repository", icon: GitBranch },
      { to: "/editor", label: "Editor", icon: FilePen },
    ],
  },
  {
    id: "nav-access",
    label: "Access",
    items: [
      { to: "/agents", label: "Agents", icon: Bot },
      { to: "/operator-access", label: "CLI access", icon: User },
    ],
  },
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
        <nav aria-label="Main" className="nav-groups">
          {groups.map((group) => (
            <div key={group.id} className="nav__group" role="group" aria-labelledby={group.id}>
              <div id={group.id} className="nav__heading">
                {group.label}
              </div>
              <ul className="nav">
                {group.items.map(({ to, label, icon: Icon, end }) => (
                  <li key={to}>
                    <NavLink to={to} end={end} className={({ isActive }) => (isActive ? "nav__link nav__link--active" : "nav__link")}>
                      <Icon size={18} aria-hidden="true" />
                      <span>{label}</span>
                    </NavLink>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </nav>
      </aside>
      <div className="shell__main">
        <header className="topbar">
          <RevisionIndicator />
          <div className="topbar__user">
            <span className="topbar__name">{auth?.username}</span>
            <button
              type="button"
              className="button button--small topbar__signout"
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
