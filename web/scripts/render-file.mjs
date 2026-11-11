// Renders a standalone HTML file in a fresh headless Chromium (no app, no bridge) and
// screenshots it: proof that an exported report is self-contained.
//   node scripts/render-file.mjs <file.html> <out.png>
import { existsSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright-core';

const [file, out] = process.argv.slice(2);
const cache = join(process.env.LOCALAPPDATA ?? '', 'ms-playwright');
const exe = existsSync(cache)
  ? readdirSync(cache)
      .filter((d) => d.startsWith('chromium-'))
      .sort()
      .reverse()
      .flatMap((d) => [join(cache, d, 'chrome-win64', 'chrome.exe'), join(cache, d, 'chrome-win', 'chrome.exe')])
      .find(existsSync)
  : undefined;
const browser = await chromium.launch({ executablePath: exe, headless: true });
const page = await browser.newPage({ viewport: { width: 1100, height: 1300 } });
await page.goto(pathToFileURL(resolve(file)).href);
await page.screenshot({ path: resolve(out), fullPage: true });
await browser.close();
console.log(`rendered → ${out}`);
