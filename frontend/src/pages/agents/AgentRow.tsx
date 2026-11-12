import { ChevronRight, Ellipsis } from "lucide-react";
import { useEffect, useId, useRef, useState, type MouseEvent } from "react";
import { useDeleteAgent, useUpdateAgent } from "../../api/queries";
import type { Agent } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { RelativeTime } from "../../components/RelativeTime";
import { plural } from "../../lib/format";
import { AgentDetail } from "./AgentDetail";
import { AgentVersion } from "./AgentVersion";
import { ReachMark } from "./ReachMark";
import { reachPills } from "./reach";

function Status({ agent }: { agent: Agent }) {
  const updating = agent.update.state === "updating";
  return (
    <span className="agent-row__status">
      {agent.connected ? "Connected" : agent.lastSeenAt ? <>Last seen <RelativeTime value={agent.lastSeenAt} /></> : "Never connected"}
      {updating ? " · Updating" : agent.draining ? " · Paused" : null}
      {agent.disabled ? " · Blocked" : null}
    </span>
  );
}

function Reaches({ agent }: { agent: Agent }) {
  if (!agent.connected) return <span className="muted small">Not connected</span>;
  if (!agent.reportsReachability) return <span className="muted small">Needs an update to report this</span>;
  const pills = reachPills(agent.reachability ?? []);
  if (pills.length === 0) return <span className="muted small">Nothing checked yet</span>;
  return (
    <ul className="reach-pills" aria-label={`What ${agent.name} reaches`}>
      {pills.map((pill) => (
        <li key={pill.key} className="reach-pill">
          <ReachMark mark={pill.mark} />
          {pill.label}
        </li>
      ))}
    </ul>
  );
}

function Menu({ agent }: { agent: Agent }) {
  const toggle = useUpdateAgent();
  const remove = useDeleteAgent();
  const [open, setOpen] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const menuId = useId();
  const updating = agent.update.state === "updating";
  const running = agent.runningAttempts;

  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      if (!ref.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("pointerdown", outside);
    return () => document.removeEventListener("pointerdown", outside);
  }, [open]);

  const change = (update: { draining?: boolean; disabled?: boolean }) =>
    toggle.mutate({ id: agent.id, ...update }, { onSuccess: () => setOpen(false) });

  return (
    <div
      className="agent-menu"
      ref={ref}
      onKeyDown={(event) => {
        if (event.key !== "Escape" || !open) return;
        setOpen(false);
        ref.current?.querySelector<HTMLButtonElement>(".agent-menu__toggle")?.focus();
      }}
    >
      <button
        type="button"
        className="icon-button agent-menu__toggle"
        aria-label={`Actions for ${agent.name}`}
        aria-expanded={open}
        aria-controls={menuId}
        onClick={() => {
          setOpen(!open);
          toggle.reset();
        }}
      >
        <Ellipsis size={16} aria-hidden="true" />
      </button>
      <div id={menuId} className="agent-menu__list" hidden={!open}>
        <button
          type="button"
          className="agent-menu__item"
          disabled={toggle.isPending || updating}
          onClick={() => change({ draining: !agent.draining })}
        >
          <span>{agent.draining ? "Resume" : "Pause"}</span>
          <span className="agent-menu__hint">
            {updating ? "It is paused while it updates." : agent.draining ? "Takes new work again." : "No new work. What it is running finishes."}
          </span>
        </button>
        <button type="button" className="agent-menu__item" disabled={toggle.isPending} onClick={() => change({ disabled: !agent.disabled })}>
          <span>{agent.disabled ? "Allow" : "Block"}</span>
          <span className="agent-menu__hint">
            {agent.disabled ? "Lets it connect again." : "Disconnects it now and refuses it until you allow it again."}
          </span>
        </button>
        <button
          type="button"
          className="agent-menu__item agent-menu__item--danger"
          onClick={() => {
            setOpen(false);
            setConfirm(true);
          }}
        >
          <span>Remove</span>
          <span className="agent-menu__hint">Its certificate stops being accepted. The machine stays.</span>
        </button>
        {toggle.error ? (
          <span className="field__error agent-menu__error" role="alert">
            {toggle.error.message}
          </span>
        ) : null}
      </div>
      <ConfirmDialog
        open={confirm}
        title={`Remove ${agent.name}?`}
        confirmLabel="Remove the agent"
        danger
        pending={remove.isPending}
        error={remove.error}
        onClose={() => {
          setConfirm(false);
          remove.reset();
        }}
        onConfirm={() => remove.mutate(agent.id, { onSuccess: () => setConfirm(false) })}
      >
        <p>
          The agent is disconnected and its certificate is no longer accepted. This does not delete its VM, remove its Git
          definition or retire the guest.
        </p>
        {running > 0 ? (
          <p>
            It is running {running} {running === 1 ? "attempt" : "attempts"}; pause it and wait for them to finish first.
          </p>
        ) : null}
      </ConfirmDialog>
    </div>
  );
}

/** One agent as a line that opens in place. */
export function AgentRow({ agent }: { agent: Agent }) {
  const [open, setOpen] = useState(false);
  const detailId = useId();
  // A click on the line's empty space opens it too, unless it was on a control or ended a text selection.
  const click = (event: MouseEvent<HTMLDivElement>) => {
    if ((event.target as Element).closest("a, button, input, .agent-menu") || window.getSelection()?.toString()) return;
    setOpen(!open);
  };
  const dot = agent.disabled || !agent.connected ? "conn conn--off" : "conn conn--on";

  return (
    <li className={agent.disabled ? "agent-row agent-row--blocked" : "agent-row"}>
      <div className="agent-row__line" onClick={click}>
        <button
          type="button"
          className="agent-row__toggle"
          aria-expanded={open}
          aria-controls={open ? detailId : undefined}
          onClick={() => setOpen(!open)}
        >
          <ChevronRight className="agent-row__chevron" size={14} aria-hidden="true" />
          <span className={dot} aria-hidden="true" />
          <span className="agent-row__who">
            <span className="agent-row__name">{agent.name}</span> <Status agent={agent} />
          </span>
        </button>
        <div className="agent-row__version">
          <AgentVersion agent={agent} />
        </div>
        <div className="agent-row__reaches">
          <Reaches agent={agent} />
        </div>
        <div className="agent-row__work">
          {agent.work.length > 0 ? <span className="run-chip run-chip--service">{plural(agent.work.length, "run")}</span> : <span className="muted small">Idle</span>}
        </div>
        <Menu agent={agent} />
      </div>
      {open ? <AgentDetail agent={agent} id={detailId} /> : null}
    </li>
  );
}
