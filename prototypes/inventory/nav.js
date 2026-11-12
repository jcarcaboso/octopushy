// A small floating switcher shared by every prototype: back to the gallery, previous, next.
(function () {
  const list = [
    ["01-tree.html", "Outline tree"],
    ["02-status-page.html", "Status page"],
    ["03-rack.html", "Rack"],
    ["04-launchpad.html", "Launchpad"],
    ["05-treemap.html", "Resource treemap"],
    ["06-columns.html", "Columns"],
    ["07-topology.html", "Topology"],
    ["08-grid.html", "Data grid"],
    ["09-terminal.html", "Terminal"],
    ["10-timeline.html", "Timeline"],
  ];
  window.PROTOTYPES = list;
  const here = location.pathname.split("/").pop();
  const i = list.findIndex(([f]) => f === here);
  if (i < 0) return;
  const prev = list[(i + list.length - 1) % list.length];
  const next = list[(i + 1) % list.length];
  const el = document.createElement("nav");
  el.setAttribute("aria-label", "Prototypes");
  el.innerHTML = `
    <a href="index.html" title="All prototypes">▦</a>
    <a href="${prev[0]}" title="${prev[1]}">‹</a>
    <span>${String(i + 1).padStart(2, "0")} · ${list[i][1]}</span>
    <a href="${next[0]}" title="${next[1]}">›</a>`;
  el.style.cssText =
    "position:fixed;right:12px;bottom:12px;z-index:9999;display:flex;align-items:center;gap:2px;" +
    "font:500 12px/1 system-ui,sans-serif;background:rgb(20 26 35 / 88%);color:#e6ebf2;border-radius:999px;" +
    "padding:4px;box-shadow:0 4px 16px rgb(0 0 0 / 25%);backdrop-filter:blur(6px)";
  for (const a of el.querySelectorAll("a"))
    a.style.cssText = "color:inherit;text-decoration:none;padding:6px 9px;border-radius:999px;font-size:14px";
  el.querySelector("span").style.cssText = "padding:0 6px;white-space:nowrap";
  addEventListener("DOMContentLoaded", () => document.body.appendChild(el));
})();
