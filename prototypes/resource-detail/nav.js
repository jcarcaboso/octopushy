// The floating switcher shared by every prototype: back to the gallery, previous, next, and which
// resource the page shows (node, machine or service). The choice follows you between prototypes.
(function () {
  const list = [
    ["01-spec-sheet.html", "Spec sheet"],
    ["02-tabs.html", "Tabs"],
    ["03-drift.html", "Declared and observed"],
    ["04-timeline.html", "Timeline"],
    ["05-actions.html", "Action deck"],
    ["06-blueprint.html", "Blueprint"],
    ["07-source-map.html", "Source map"],
    ["08-lines.html", "Lines"],
    ["09-bento.html", "Bento"],
    ["10-status-page.html", "Status page"],
    ["11-terminal.html", "Terminal"],
    ["12-lifecycle.html", "Lifecycle"],
    ["13-three-panes.html", "Three panes"],
    ["14-readme.html", "Readme"],
    ["15-checklist.html", "Checklist"],
    ["16-graph.html", "Graph"],
  ];
  window.PROTOTYPES = list;
  const here = location.pathname.split("/").pop();
  const i = list.findIndex(([f]) => f === here);
  if (i < 0) return;
  const kind = new URLSearchParams(location.search).get("kind") || "machine";
  // In the gallery's thumbnails the switcher would only cover the design.
  if (new URLSearchParams(location.search).has("thumb")) return;
  const prev = list[(i + list.length - 1) % list.length];
  const next = list[(i + 1) % list.length];
  const el = document.createElement("nav");
  el.setAttribute("aria-label", "Prototypes");
  el.innerHTML = `
    <a href="index.html" title="All prototypes">▦</a>
    <a href="${prev[0]}?kind=${kind}" title="${prev[1]}">‹</a>
    <span>${String(i + 1).padStart(2, "0")} · ${list[i][1]}</span>
    <a href="${next[0]}?kind=${kind}" title="${next[1]}">›</a>
    <i></i>
    ${["node", "machine", "service"].map((k) => `<a href="${here}?kind=${k}" data-kind="${k}"${k === kind ? ' aria-current="page"' : ""}>${k[0].toUpperCase() + k.slice(1)}</a>`).join("")}`;
  el.style.cssText =
    "position:fixed;right:12px;bottom:12px;z-index:9999;display:flex;align-items:center;gap:2px;" +
    "font:500 12px/1 system-ui,sans-serif;background:rgb(20 26 35 / 92%);color:#e6ebf2;border-radius:999px;" +
    "padding:4px;box-shadow:0 4px 16px rgb(0 0 0 / 35%);border:1px solid rgb(255 255 255 / 10%);backdrop-filter:blur(6px)";
  for (const a of el.querySelectorAll("a")) {
    const current = a.getAttribute("aria-current");
    a.style.cssText = `color:inherit;text-decoration:none;padding:6px 9px;border-radius:999px;font-size:${a.dataset.kind ? 12 : 14}px;${current ? "background:#2f5bd3;color:#fff" : ""}`;
  }
  el.querySelector("span").style.cssText = "padding:0 6px;white-space:nowrap";
  el.querySelector("i").style.cssText = "width:1px;height:16px;background:rgb(255 255 255 / 18%);margin:0 4px";
  addEventListener("DOMContentLoaded", () => document.body.appendChild(el));
})();
