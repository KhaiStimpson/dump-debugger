// Drives the real DumpDebugger.Host over the Chrome DevTools Protocol and takes screenshots.
//
//   node scripts/shot.mjs <plan.json>
//
// plan.json: { "dump": "../fixtures/x.dmp", "size": [1480, 920], "steps": [
//   { "wait": "selector" } | { "hash": "#/threads" } | { "click": "selector" } |
//   { "eval": "js expression" } | { "key": "Control+k" } | { "sleep": 500 } | { "shot": "name" } ] }
//
// Screenshots land in web/shots/<name>.png. The host is launched with --cdp and killed at the end.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..');
const repo = resolve(webRoot, '..');
const plan = JSON.parse(readFileSync(resolve(process.argv[2] ?? 'shots.json'), 'utf8'));
const port = plan.port ?? 9333;

function findHostExe() {
  const base = join(repo, 'src', 'DumpDebugger.Host', 'bin');
  const found = [];
  const walk = (d) => {
    for (const n of readdirSync(d)) {
      const p = join(d, n);
      if (statSync(p).isDirectory()) walk(p);
      else if (n === 'DumpDebugger.Host.exe') found.push(p);
    }
  };
  if (existsSync(base)) walk(base);
  found.sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs);
  if (!found.length) throw new Error('DumpDebugger.Host.exe not found; build the host first.');
  return found[0];
}

const exe = plan.exe ? resolve(plan.exe) : findHostExe();
const args = ['--cdp', String(port)];
if (plan.dump) args.unshift(resolve(webRoot, plan.dump));
console.log(`launching ${exe} ${args.join(' ')}`);
const host = spawn(exe, args, { stdio: 'ignore', env: { ...process.env, ...(plan.env ?? {}) } });

const deadline = Date.now() + 30_000;
let browser;
while (!browser) {
  try {
    browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  } catch (e) {
    if (Date.now() > deadline) throw e;
    await new Promise((r) => setTimeout(r, 300));
  }
}

let page;
while (!page) {
  page = browser
    .contexts()
    .flatMap((c) => c.pages())
    .find((p) => !p.url().startsWith('devtools'));
  if (!page) await new Promise((r) => setTimeout(r, 200));
}

mkdirSync(join(webRoot, 'shots'), { recursive: true });
try {
  for (const step of plan.steps) {
    if (step.wait) await page.waitForSelector(step.wait, { timeout: step.timeout ?? 30_000 });
    if (step.hash) await page.evaluate((h) => (location.hash = h), step.hash);
    if (step.click) await page.click(step.click, { timeout: step.timeout ?? 10_000 });
    if (step.eval) console.log('eval →', await page.evaluate(step.eval));
    if (step.key) await page.keyboard.press(step.key);
    if (step.type) await page.keyboard.type(step.type);
    if (step.sleep) await new Promise((r) => setTimeout(r, step.sleep));
    if (step.shot) {
      await new Promise((r) => setTimeout(r, 250));
      const out = join(webRoot, 'shots', `${step.shot}.png`);
      await page.screenshot({ path: out });
      console.log(`shot → ${out}`);
    }
  }
} finally {
  await browser.close().catch(() => {});
  host.kill();
}
