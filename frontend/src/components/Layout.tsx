import { Bot, ChevronsUpDown, CirclePlay, FilePen, GitBranch, Globe, KeyRound, LayoutDashboard, LogOut, Rocket, Server, User } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { NavLink, Outlet, useNavigate } from "react-router";
import { useAuthState, useLogout, useRepository } from "../api/queries";

const groups = [
  {
    id: "nav-operate",
    label: "Operate",
    items: [
      { to: "/", label: "Overview", icon: LayoutDashboard, end: true },
      { to: "/inventory", label: "Inventory", icon: Server },
      // A service's own page lives under /services/:id; only this list lights the item.
      { to: "/services", label: "Services", icon: Globe, end: true },
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

/** The signed-in user, at the foot of the sidebar; signing out is one step behind the name. */
function Account() {
  const { data: auth } = useAuthState();
  const logout = useLogout();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      if (!ref.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("pointerdown", outside);
    return () => document.removeEventListener("pointerdown", outside);
  }, [open]);

  return (
    <div
      className="account"
      ref={ref}
      onKeyDown={(event) => {
        if (event.key !== "Escape" || !open) return;
        setOpen(false);
        ref.current?.querySelector<HTMLButtonElement>(".account__toggle")?.focus();
      }}
    >
      <button type="button" className="account__toggle" aria-expanded={open} aria-controls="account-menu" onClick={() => setOpen(!open)}>
        <span className="account__avatar" aria-hidden="true">
          {auth?.username?.slice(0, 1).toUpperCase()}
        </span>
        <span className="account__name">{auth?.username}</span>
        <ChevronsUpDown size={14} aria-hidden="true" />
      </button>
      <div id="account-menu" className="account__menu" hidden={!open}>
        <button
          type="button"
          className="account__item"
          onClick={() => logout.mutate(undefined, { onSettled: () => navigate("/login", { replace: true }) })}
          disabled={logout.isPending}
        >
          <LogOut size={15} aria-hidden="true" />
          <span>Sign out</span>
        </button>
      </div>
    </div>
  );
}

export function Layout() {
  const { data: repository } = useRepository();
  // The Repository item carries what the top bar used to say on every page: the lab repository is missing or its fetch failed.
  const repositoryAlert = !repository ? null : !repository.url ? "Not connected" : repository.lastFetchError ? "Last fetch failed" : null;

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
                      <span>{label}</span>{" "}
                      {to === "/repository" && repositoryAlert ? (
                        <span className={repository?.url ? "nav__alert nav__alert--danger" : "nav__alert"} title={repositoryAlert}>
                          <span className="visually-hidden">{repositoryAlert}</span>
                        </span>
                      ) : null}
                    </NavLink>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </nav>
        <Account />
      </aside>
      <div className="shell__main">
        <main id="main" className="content" tabIndex={-1}>
          <Outlet />
        </main>
      </div>
    </div>
  );
}
