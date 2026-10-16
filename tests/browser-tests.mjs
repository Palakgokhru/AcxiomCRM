// Real-browser checks for AcxiomCRM using headless Microsoft Edge over the Chrome DevTools Protocol.
// No npm packages: Node 22's built-in WebSocket + fetch. Covers what HTTP tests cannot:
//   - Scenario 3: inline client-side validation appears and blocks submission (no request sent)
//   - Scenario 14: Chart.js charts actually render with the server's scoped data
// Usage: node tests/browser-tests.mjs http://localhost:5099
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const BASE = process.argv[2] ?? "http://localhost:5058";
const OUT = dirname(fileURLToPath(import.meta.url));
const EDGE = [
  "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe",
].find(existsSync);
if (!EDGE) { console.log("NOT RUN  browser tests: Microsoft Edge not found"); process.exit(2); }

const sleep = ms => new Promise(r => setTimeout(r, ms));
let pass = 0, fail = 0;
const check = (name, ok, info = "") => { ok ? pass++ : fail++; console.log(`${ok ? "PASS" : "FAIL"}  ${name}${ok ? "" : `  [${info}]`}`); };

const port = 9300 + Math.floor(Math.random() * 500);
const edge = spawn(EDGE, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${mkdtempSync(join(tmpdir(), "acx-edge-"))}`,
  "--no-first-run", "--disable-extensions", "--window-size=1280,1600", "about:blank"], { stdio: "ignore" });

let ws, nextId = 1;
const pending = new Map(), listeners = [];
async function connect() {
  for (let i = 0; i < 40; i++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const page = targets.find(t => t.type === "page");
      if (page) {
        ws = new WebSocket(page.webSocketDebuggerUrl);
        await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
        ws.onmessage = e => {
          const m = JSON.parse(e.data);
          if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
          else listeners.forEach(l => l(m));
        };
        return;
      }
    } catch { /* not ready yet */ }
    await sleep(250);
  }
  throw new Error("could not connect to Edge DevTools");
}
const send = (method, params = {}) => new Promise(res => { const id = nextId++; pending.set(id, res); ws.send(JSON.stringify({ id, method, params })); });
const evaluate = async expr => (await send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true })).result?.result?.value;
async function navigate(url) {
  const loaded = new Promise(res => { const l = m => { if (m.method === "Page.loadEventFired") { listeners.splice(listeners.indexOf(l), 1); res(); } }; listeners.push(l); });
  await send("Page.navigate", { url });
  await loaded; await sleep(400);
}
async function login(email, password) {
  await navigate(`${BASE}/Identity/Account/Login`);
  const loaded = new Promise(res => { const l = m => { if (m.method === "Page.loadEventFired") { listeners.splice(listeners.indexOf(l), 1); res(); } }; listeners.push(l); });
  await evaluate(`document.querySelector('#Input_Email').value=${JSON.stringify(email)};
                  document.querySelector('#Input_Password').value=${JSON.stringify(password)};
                  document.querySelector('form').submit(); true`);
  await loaded; await sleep(400);
  return evaluate("location.pathname");
}

try {
  await connect();
  await send("Page.enable");
  await send("Network.enable");
  let posts = 0;
  listeners.push(m => { if (m.method === "Network.requestWillBeSent" && m.params.request.method === "POST") posts++; });

  const landed = await login("admin@acxiomcrm.local", "Admin#12345");
  check("B1 Browser login lands on Dashboard", landed === "/", landed);

  // ---- Scenario 3: client-side validation ----
  await navigate(`${BASE}/Customers/Create`);
  const before = posts;
  const v = await evaluate(`(async () => {
      window.__noReload = true;
      document.querySelector('#CustomerName').value = 'Browser Test';
      document.querySelector('#Email').value = 'not-an-email';
      document.querySelector('#Phone').value = 'abc';
      document.querySelector('form[action="/Customers/Create"] button[type=submit]').click();
      await new Promise(r => setTimeout(r, 800));
      const msg = f => (document.querySelector('[data-valmsg-for="' + f + '"]')?.textContent || '').trim();
      return { stillHere: window.__noReload === true && location.pathname === '/Customers/Create',
               email: msg('Email'), phone: msg('Phone'),
               emailInvalid: document.querySelector('#Email').classList.contains('input-validation-error') };
    })()`);
  check("B2 Invalid email shows inline client-side error", !!v?.email && v.emailInvalid, JSON.stringify(v));
  check("B3 Invalid phone shows inline client-side error", !!v?.phone, JSON.stringify(v));
  check("B4 Invalid submission blocked in the browser (no reload, no POST sent)", v?.stillHere && posts === before, `posts=${posts - before}`);
  const req = await evaluate(`(async () => {
      document.querySelector('#Email').value = ''; document.querySelector('#Phone').value = '';
      document.querySelector('form[action="/Customers/Create"] button[type=submit]').click();
      await new Promise(r => setTimeout(r, 500));
      return (document.querySelector('[data-valmsg-for="Email"]')?.textContent || '').trim();
    })()`);
  check("B5 Empty required field shows inline 'required' error", /required/i.test(req ?? ""), req);
  writeFileSync(join(OUT, "screenshot-client-validation.png"), Buffer.from((await send("Page.captureScreenshot", { format: "png" })).result.data, "base64"));

  // ---- Scenario 14: dashboard charts ----
  await navigate(`${BASE}/`);
  await sleep(2500);   // CDN load + chart animation (1s) before measuring
  const d = await evaluate(`(() => {
      if (typeof Chart === 'undefined') return { chartJs: false };
      const charts = window.dashboardCharts || [];
      const painted = charts.map(c => { const cv = c.canvas, ctx = cv.getContext('2d');
          const px = ctx.getImageData(0, 0, cv.width, cv.height).data; let n = 0;
          for (let i = 3; i < px.length; i += 4) if (px[i] > 0) n++; return n; });
      const dd = window.dashboardData;
      return { chartJs: true, count: charts.length, painted,
               leadMatches: JSON.stringify(charts[0]?.data.datasets[0].data) === JSON.stringify(dd.leadCounts),
               stageMatches: JSON.stringify(charts[1]?.data.datasets[0].data) === JSON.stringify(dd.stageCounts),
               months: charts[2]?.data.labels.length, totalOpps: dd.stageCounts.reduce((a, b) => a + b, 0),
               cardOpps: +document.querySelector('[data-card="Total Opportunities"]').textContent.replace(/,/g, '') };
    })()`);
  check("B6 Chart.js loaded and 3 dashboard charts created", d?.chartJs && d.count === 3, JSON.stringify(d));
  check("B7 Each chart canvas has rendered pixels", d?.painted?.length === 3 && d.painted.every(n => n > 500), JSON.stringify(d?.painted));
  check("B8 Chart data equals the server's scoped figures (stage total = Total Opportunities card)",
    d?.leadMatches && d.stageMatches && d.totalOpps === d.cardOpps && d.months >= 1, JSON.stringify(d));
  await evaluate("(window.dashboardCharts||[]).forEach(c => { c.resize(); c.update('none'); }); true"); await sleep(300);
  writeFileSync(join(OUT, "screenshot-dashboard-admin.png"), Buffer.from((await send("Page.captureScreenshot", { format: "png" })).result.data, "base64"));

  // Role scoping in the charts: the SalesExecutive's stage chart total must equal their own card.
  await send("Network.clearBrowserCookies");
  await login("sales@acxiomcrm.local", "Sales#12345");
  await navigate(`${BASE}/`); await sleep(2500);
  const s = await evaluate(`({ total: window.dashboardData.stageCounts.reduce((a, b) => a + b, 0),
                               card: +document.querySelector('[data-card="Total Opportunities"]').textContent.replace(/,/g, ''),
                               charts: (window.dashboardCharts || []).length })`);
  check("B9 SalesExecutive charts use their own scoped data (chart total = their card, < admin total)",
    s?.charts === 3 && s.total === s.card && s.total <= d.totalOpps, `${JSON.stringify(s)} admin=${d?.totalOpps}`);
  await evaluate("(window.dashboardCharts||[]).forEach(c => { c.resize(); c.update('none'); }); true"); await sleep(300);
  writeFileSync(join(OUT, "screenshot-dashboard-sales.png"), Buffer.from((await send("Page.captureScreenshot", { format: "png" })).result.data, "base64"));
} catch (e) {
  fail++; console.log("FAIL  browser test run aborted: " + e.message);
} finally {
  try { ws?.close(); } catch { }
  edge.kill();
}
console.log(`\nBROWSER RESULT: ${pass} passed, ${fail} failed (screenshots in tests/)`);
process.exit(fail ? 1 : 0);
