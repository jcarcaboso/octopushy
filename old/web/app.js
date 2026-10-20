"use strict";
const $ = (id) => document.getElementById(id);
let token = "", data = null, selected = null, pending = false, reviewKey = "";
const terminalIssues = new Set(["plan_failed", "invalidated", "outcome_unknown", "applied_unverified"]);
const statusLabels = {planned:"Review plan", no_changes:"No changes", planning:"Planning", applying:"Applying", applied:"Applied", plan_failed:"Plan failed", invalidated:"Plan invalidated", outcome_unknown:"Needs reconciliation", applied_unverified:"Applied · verify state", superseded:"Superseded", reconciled:"Reconciled"};
function el(tag, text, cls) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (cls) node.className = cls;
  return node;
}
function short(sha) { return sha ? sha.slice(0, 8) : "—"; }
function date(time) { return time ? new Date(time * 1000).toLocaleString() : "Never"; }
function badge(status) {
  return el("span", statusLabels[status] || status,
    `badge ${terminalIssues.has(status) ? "bad" : ["applied", "no_changes"].includes(status) ? "ok" : ["planned", "planning", "applying"].includes(status) ? "warn" : ""}`);
}
function message(text, bad = false) {
  $("message").hidden = !text; $("message").textContent = text; $("message").className = bad ? "bad" : "";
}
function blocked() { return pending || data?.runs.some(r => ["planning", "applying", "outcome_unknown"].includes(r.status)); }
async function api(path, body) {
  const response = await fetch(path, {
    method: body === undefined ? "GET" : "POST",
    headers: {Authorization: `Bearer ${token}`, ...(body === undefined ? {} : {"Content-Type":"application/json"})},
    ...(body === undefined ? {} : {body:JSON.stringify(body)})
  });
  const result = await response.json();
  if (!response.ok) throw new Error(result.error || "Request failed");
  return result;
}
function action(label, callback, disabled = blocked(), cls = "") {
  const button = el("button", label, cls); button.type = "button"; button.disabled = disabled;
  button.addEventListener("click", () => perform(callback)); return button;
}
function selectRun(id) {
  selected=id; reviewKey=""; render();
  review().then(()=>$("review").scrollIntoView({behavior:"smooth",block:"start"})).catch(e=>message(e.message,true));
}
async function perform(callback) {
  if (pending) return;
  pending = true; render();
  try { message(""); await callback(); }
  catch (error) { message(error.message, true); }
  finally { pending = false; await refresh(); }
}
async function refresh() {
  if (!token) return;
  try {
    data = await api("/api/status");
    $("login").hidden = true; $("dashboard").hidden = false; $("refresh").hidden = false; $("logout").hidden = false;
    render();
    if (selected) await review();
  } catch (error) { message(error.message, true); }
}
function render() {
  if (!data) return;
  $("warnings").replaceChildren();
  if (!data.tofu_available) $("warnings").append(el("p", "OpenTofu is not installed or not on PATH. Install it, or restart with --tofu /path/to/tofu. Repository setup is still available.", "notice warn"));
  const unknown = data.runs.filter(r => r.status === "outcome_unknown");
  if (unknown.length) $("warnings").append(el("p", "Execution is blocked: a run has an unknown outcome. Inspect its details and reconcile before creating another plan. Never blindly replay an apply.", "notice bad"));
  else if (blocked()) $("warnings").append(el("p", "An operation is in progress. This PoC runs one execution at a time; command output is collected when each command finishes.", "notice"));
  const latest = data.stacks.map(s => data.runs.find(r => r.stack_id === s.id)).filter(Boolean);
  $("metrics").replaceChildren(...[
    [data.stacks.length, "Managed stacks"], [data.stacks.reduce((n,s) => n+s.resources.length,0), "Resources in observed state"],
    [latest.filter(r => r.status === "planned").length, "Plans awaiting review"], [latest.filter(r => terminalIssues.has(r.status)).length, "Stacks needing attention"]
  ].map(([value,label]) => { const box = el("div", undefined, "metric"); box.append(el("strong",value),el("span",label));return box; }));
  const oldConnection = $("init-connection").value;
  $("init-connection").replaceChildren(el("option", "Select a connection"));
  $("init-connection").firstChild.value = "";
  data.connections.forEach(c => { const option = el("option", c.name); option.value = c.name; $("init-connection").append(option); });
  $("init-connection").value = oldConnection;
  document.querySelectorAll(".setup-grid button").forEach(button => button.disabled = blocked());
  $("refresh").disabled = pending;
  if (!data.repositories.length && !data.connections.length) $("setup").open = true;
  $("connections").replaceChildren(...data.connections.map(c => {
    const box = el("div", undefined, "connection"), row = el("div", undefined, "row");
    row.append(el("h3", c.name), el("span", c.credential_available ? "CREDENTIAL SET" : "CREDENTIAL MISSING", `badge ${c.credential_available ? "ok" : "warn"}`));
    box.append(row, el("p", c.endpoint, "endpoint"), el("code", c.token_env), el("p", "API connectivity has not been checked.", "small muted")); return box;
  }));
  if (!data.connections.length) $("connections").append(el("div", "No Proxmox connections registered. Open setup to add one.", "empty"));
  $("repositories").replaceChildren(...data.repositories.map(repo => {
    const box = el("div", undefined, "repo"), row = el("div", undefined, "row"), meta = el("div", undefined, "meta");
    row.append(el("h3", repo.name), action("Sync Git", async () => { await api(`/api/repositories/${repo.id}/sync`,{}); message("Desired revision synced. Create a plan to check the changes."); }, blocked(), "secondary"));
    meta.append(el("span", `Branch · ${repo.branch}`), el("code", `Desired · ${short(repo.revision)}`), el("span", `Last sync · ${date(repo.synced_at)}`));
    box.append(row, el("p",repo.source,"source"),meta); return box;
  }));
  if (!data.repositories.length) $("repositories").append(el("div", "Link a repository with a manifest, or initialize a new one from setup.", "empty"));
  $("stacks").replaceChildren(...data.stacks.map(stack => {
    const repo = data.repositories.find(r=>r.id===stack.repo_id), run = data.runs.find(r=>r.stack_id===stack.id);
    const box = el("article", undefined, "stack"), head = el("div", undefined, "stack-head"), name = el("div");
    name.append(el("span", `${repo.name} / ${stack.config.connection || "no connection"}`, "small muted"), el("h3",stack.name)); head.append(name,badge(run?.status || "Not planned"));
    const revisions = el("div",undefined,"revisions");
    [["DESIRED",repo.revision],["CHECKED",stack.checked_revision],["APPLIED",stack.applied_revision]].forEach(([label,value]) => {const d=el("div");d.append(el("small",label),el("code",short(value)));revisions.append(d);});
    box.append(head, el("code", `${stack.config.path} · ${stack.config.backend}`), revisions);
    if (run && run.revision !== repo.revision) box.append(el("p","New Git revision — plan again to compare with state.","small warning"));
    const inventory = el("div",undefined,"inventory");
    if (!stack.resources.length) inventory.append(el("p",stack.observed_at ? "No resources in the last observed state." : "State has not been observed yet."));
    stack.resources.forEach(r => inventory.append(el("div", r.vm
      ? `${r.vm.name || r.name} · VM ${r.vm.vm_id ?? "?"} · ${r.vm.node_name || "unknown node"}`
      : `${r.module ? r.module+"." : ""}${r.type}.${r.name}`, "resource")));
    inventory.append(el("p",`Last state observation · ${date(stack.observed_at)}`,"small muted"));
    const controls=el("div",undefined,"row");
    controls.append(action("Create fresh plan",async()=>{ const result = await api(`/api/stacks/${stack.id}/plan`,{});selected=result.id;reviewKey="";},blocked() || !data.tofu_available));
    if (run) {
      const inspect=el("button",terminalIssues.has(run.status)?"Inspect issue":run.status==="planned"?"Review plan":"View latest run","secondary");
      inspect.type="button";inspect.addEventListener("click",()=>selectRun(run.id));controls.append(inspect);
    }
    box.append(inventory,controls);
    return box;
  }));
  if (!data.stacks.length) $("stacks").append(el("div", "Stacks are discovered from the linked repository’s .octopushy.json file.", "empty"));
  $("runs").replaceChildren(...data.runs.map(run => {
    const stack = data.stacks.find(s=>s.id===run.stack_id), repo = data.repositories.find(r=>r.id===stack.repo_id);
    const button = el("button",undefined,`run-button ${selected===run.id?"selected":""}`); button.type="button";
    const row=el("div",undefined,"row");row.append(el("strong",`${repo.name} / ${stack.name}`),badge(run.status));
    button.append(row,el("p",`${short(run.revision)} · ${date(run.created_at)}`));
    button.addEventListener("click",()=>selectRun(run.id));
    return button;
  }));
  if (!data.runs.length) $("runs").append(el("div","No executions yet.","empty"));
}
async function review() {
  const id = selected, run = data.runs.find(r=>r.id===id);
  if (!run) return;
  const stack = data.stacks.find(s=>s.id===run.stack_id);
  const key = JSON.stringify([run,blocked()]);
  if (reviewKey !== key) {
    reviewKey=key;
    const panel=$("review");panel.replaceChildren();
    const row=el("div",undefined,"row");row.append(el("h3",stack.name),badge(run.status));
    panel.append(row,el("p",`Run ${run.id} · Commit ${run.revision}`,"small muted"));
    if (run.error) panel.append(el("p",run.error,"notice bad"));
    for (const change of run.summary.changes || []) {
      const item=el("div",undefined,"change");item.append(el("strong",change.actions.join(" → ")),el("code",change.address));panel.append(item);
    }
    if (run.summary.outputs_changed) panel.append(el("p","Output values also change. Raw values are not shown here.","small warning"));
    const details=el("details");details.open=true;
    details.append(el("summary","Command output / readable plan"));
    const log=el("pre","Loading command output…");log.id="run-log";details.append(log);panel.append(details);
    if (run.status === "planned") {
      panel.append(el("p","Review the complete plan above. Approval applies this exact saved plan, not a new plan. Plans expire after one hour; live infrastructure may still change between plan and apply.","small warning"));
      const check=el("label",undefined,"check"), input=el("input");input.type="checkbox";
      check.append(input,document.createTextNode("I reviewed this saved plan and approve its changes."));panel.append(check);
      const destructive=(run.summary.changes||[]).some(c=>c.actions.includes("delete"));
      let confirm=null;
      if (destructive) {
        const label=el("label",`Type ${stack.name} to confirm deletion or replacement`);confirm=el("input");confirm.autocomplete="off";label.append(confirm);panel.append(label);
      }
      const apply=action("Apply reviewed plan",async()=>{await api(`/api/runs/${run.id}/apply`,{plan_hash:run.plan_hash,confirm_destroy:confirm?.value || ""});reviewKey="";},true,destructive?"danger":"");
      const validate=()=>{apply.disabled=blocked()||!input.checked||(destructive&&confirm.value!==stack.name);};
      input.addEventListener("change",validate);confirm?.addEventListener("input",validate);panel.append(apply);
    } else if (run.status === "outcome_unknown") {
      panel.append(el("p","Stop the controller, confirm no old executor is running, and inspect OpenTofu state plus any Proxmox tasks. Only then acknowledge reconciliation locally:","small warning"),el("pre",`python3 octopushy.py --data-dir YOUR_DATA_DIR resolve-run ${run.id} --acknowledge-stopped-and-state-inspected`));
    } else if (["plan_failed","invalidated","reconciled","superseded","applied_unverified"].includes(run.status)) {
      panel.append(el("p","Fix the reported issue, sync Git if needed, then create a fresh plan on the stack. Old applies are never replayed.","small muted"));
    } else if (run.status === "no_changes") {
      panel.append(el("p","The checked configuration needs no changes. This is not a new successful apply, and it is not a live-health check.","small muted"));
    }
  }
  const result=await api(`/api/runs/${id}/log`);
  if (selected===id && $("run-log")) $("run-log").textContent=
    (result.truncated ? "WARNING: only the last 1 MB is shown. Inspect the complete private log.txt in this run’s data directory before approval.\n\n" : "") +
    (result.log || "No command output yet. Output appears after each command finishes.");
}
$("login-form").addEventListener("submit",async event=>{event.preventDefault();token=$("access-token").value.trim();await refresh();if(data){$("access-token").value="";message("");}});
$("logout").addEventListener("click",()=>location.reload());
$("refresh").addEventListener("click",()=>refresh());
function form(id,path,convert) {
  $(id).addEventListener("submit",event=>{
    event.preventDefault();const fields=Object.fromEntries(new FormData(event.target));
    perform(async()=>{await api(path,convert?convert(fields):fields);event.target.reset();message("Saved. No infrastructure changes have been applied.");});
  });
}
form("connection-form","/api/connections");
form("repo-form","/api/repositories",fields=>({...fields,trusted:true}));
form("init-form","/api/repositories/initialize");
setInterval(()=>{if(token&&!pending&&data?.runs.some(r=>["planning","applying"].includes(r.status))) refresh();},2000);
