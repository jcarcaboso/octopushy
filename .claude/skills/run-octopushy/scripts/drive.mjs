// A signed-in headless browser on the stack from stack.sh. Import `open` from a throwaway script,
// or run this file for a smoke test of every page reachable without an agent. See SKILL.md.
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const cache = join(process.env.XDG_CACHE_HOME ?? join(homedir(), ".cache"), "octopushy-run");
const defaultShots = resolve(here, "../../../../.dev/run/shots");
const defaultBase = "http://127.0.0.1:5175";

/**
 * Signs in to the light stack by default; pass `base`, `user` and `password` for another, such as a demo, and `shots`
 * for where screenshots go.
 */
export async function open({ width = 1400, height = 800, base = defaultBase, user = "run", password = "octopushy-run", shots = defaultShots } = {}) {
  const { chromium } = await import(join(cache, "tool/node_modules/playwright/index.mjs"));
  // The sandbox cannot start on this Fedora host.
  const browser = await chromium.launch({ args: ["--no-sandbox"] });
  const page = await (await browser.newContext({ viewport: { width, height } })).newPage();
  page.setDefaultTimeout(8000);

  const problems = [];
  page.on("console", (m) => m.type() === "error" && problems.push(`console: ${m.text()}`));
  page.on("pageerror", (e) => problems.push(`pageerror: ${e.message}`));
  page.on("response", (r) => r.status() >= 400 && problems.push(`http ${r.status()} ${r.request().method()} ${r.url()}`));

  await page.goto(`${base}/login`);
  await page.getByLabel("User name").fill(user);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL((url) => !url.pathname.startsWith("/login"));

  let failed = 0;
  return {
    page,
    base,
    /** Runs one named check; a failure is reported and the next check still runs. */
    async check(name, fn) {
      try {
        await fn();
        console.log(`PASS  ${name}`);
      } catch (e) {
        failed++;
        console.log(`FAIL  ${name}\n      ${String(e.message).split("\n").slice(0, 3).join(" | ")}`);
      }
    },
    /** Saves a screenshot and returns its path, so it can be read and looked at. */
    async shot(name) {
      const path = join(shots, `${name}.png`);
      await page.screenshot({ path, fullPage: true });
      console.log(`SHOT  ${path}`);
      return path;
    },
    /** Closes the browser; the exit code is non-zero after a failed check or any page problem. */
    async finish() {
      await browser.close();
      for (const problem of problems) console.log(`PROBLEM  ${problem}`);
      process.exitCode = failed > 0 || problems.length > 0 ? 1 : 0;
    },
  };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const { page, base, check, shot, finish } = await open();
  const pages = [
    ["/", "Overview"],
    ["/inventory", "Inventory"],
    ["/runs", "Runs"],
    ["/agents", "Agents"],
    ["/variables", "Variables"],
    ["/repository", "Repository"],
    ["/editor", "Editor"],
  ];
  for (const [path, title] of pages) {
    await check(`${path} shows ${title}`, async () => {
      await page.goto(`${base}${path}`);
      await page.getByRole("heading", { name: title, level: 1 }).waitFor();
      await shot(title.toLowerCase());
    });
  }
  await check("secret values never reach the Variables page", async () => {
    await page.goto(`${base}/variables`);
    await page.getByRole("region", { name: "Variable store entries" }).waitFor();
    if ((await page.content()).includes("MARKER-secret")) throw new Error("a secret value is in the page");
  });
  await finish();
}
