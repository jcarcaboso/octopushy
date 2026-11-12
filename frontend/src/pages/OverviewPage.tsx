import { Bot, CircleCheck, Circle, GitBranch, KeyRound } from "lucide-react";
import { Link } from "react-router";
import { useOverview } from "../api/queries";
import type { Overview } from "../api/types";
import { Card } from "../components/Card";
import { PageHeader } from "../components/PageHeader";
import { ErrorMessage, PageSkeleton } from "../components/States";
import { AgentsTile } from "./overview/AgentsTile";
import { GuestsTile } from "./overview/GuestsTile";
import { NeedsTile } from "./overview/NeedsTile";
import { RunsTile } from "./overview/RunsTile";
import { UptimeTile } from "./overview/UptimeTile";

function GettingStarted({ overview }: { overview: Overview }) {
  const repositoryDone = Boolean(overview.repository.url);
  const agentDone = overview.agentsTotal > 0;
  const steps = [
    {
      done: repositoryDone,
      icon: GitBranch,
      title: "Connect the lab repository",
      body: "Octopushy reads labs, installations, and guests from Git.",
      to: "/repository",
      action: "Repository settings",
    },
    {
      done: agentDone,
      icon: Bot,
      title: "Enroll an agent",
      body: "Agents observe Proxmox and run plans and applies; nothing runs without one.",
      to: "/agents",
      action: "Enroll an agent",
    },
    {
      done: false,
      icon: KeyRound,
      title: "Set the variables the definitions reference",
      body: "Proxmox tokens and state-backend credentials live in the variable store, not in Git.",
      to: "/variables",
      action: "Variables",
    },
  ];
  return (
    <Card title="Get started">
      <ol className="checklist">
        {steps.map((step) => (
          <li key={step.title} className={step.done ? "checklist__item checklist__item--done" : "checklist__item"}>
            {step.done ? (
              <CircleCheck className="checklist__mark" size={20} aria-label="Done" />
            ) : (
              <Circle className="checklist__mark" size={20} aria-label="Not done yet" />
            )}
            <div className="checklist__text">
              <div className="checklist__title">{step.title}</div>
              <div className="muted">{step.body}</div>
            </div>
            <Link to={step.to} className={step.done ? "button button--small" : "button button--small button--primary"}>
              {step.action}
            </Link>
          </li>
        ))}
      </ol>
    </Card>
  );
}

export function OverviewPage() {
  const { data, error, isPending } = useOverview();

  if (isPending) return <PageSkeleton />;
  if (!data) return <ErrorMessage error={error} title="The overview could not be loaded" />;

  const fresh = !data.repository.url || data.agentsTotal === 0;

  return (
    <>
      <PageHeader title="Overview" />
      {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
      <div className="stack stack--large">
        {fresh ? <GettingStarted overview={data} /> : null}
        <div className="bento">
          <GuestsTile counts={data.counts} />
          <NeedsTile overview={data} />
          <RunsTile overview={data} />
          <div className="bento__column">
            <UptimeTile />
            <AgentsTile overview={data} />
          </div>
        </div>
      </div>
    </>
  );
}
