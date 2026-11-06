#!/usr/bin/env node
// Disposable synthetic stack only; never CI or real data. Requires installed headless Chrome and Node WebSocket.
// UI_SMOKE_DISPOSABLE=1 UI_SMOKE_URL=http://localhost:<port> UI_SMOKE_OPERATOR_EMAIL=<synthetic> \
// UI_SMOKE_OPERATOR_PASSWORD=<synthetic> node tools/ui_smoke.mjs
// Creates one synthetic company through the API, then drives imports/run/confirm/summary through the browser UI.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const base = new URL(process.env.UI_SMOKE_URL || 'http://localhost:8080');
assert.equal(process.env.UI_SMOKE_DISPOSABLE, '1', 'Explicit disposable-stack opt-in required');
assert.ok(['localhost', '127.0.0.1'].includes(base.hostname), 'Loopback disposable stack required');
const operatorEmail = process.env.UI_SMOKE_OPERATOR_EMAIL;
const operatorPassword = process.env.UI_SMOKE_OPERATOR_PASSWORD;
assert.ok(operatorEmail && operatorPassword, 'Synthetic operator credentials must be supplied through the environment');
const output = path.resolve('docs/build-log/images');
const temporary = await mkdtemp(path.join(tmpdir(), 'bnk-ui-smoke-'));
let chrome;
let socket;
const exceptions = [];
const framework = [];
try {
    async function api(resource, body, token) {
        const response = await fetch(new URL(resource, base), {
            method: 'POST', headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }, body: JSON.stringify(body)
        });
        assert.ok(response.ok, `Synthetic setup failed: ${resource} (${response.status})`);
        return response.json();
    }
    const operator = await api('/auth/tokens', { email: operatorEmail, password: operatorPassword });
    const email = `ui-smoke-${randomUUID()}@example.invalid`;
    const password = 'Synthetic-web-smoke-123!';
    const company = await api('/companies', { rfc: 'AAA010101AAA', name: 'Synthetic UI company', adminEmail: email, adminPassword: password }, operator.accessToken);
    let xml = await readFile('samples/synthetic-income-ppd-retention.xml', 'utf8');
    xml = xml.replace('SubTotal="1690.00"', 'SubTotal="10000.00"').replace('Total="1939.27"', 'Total="11600.00"')
        .replace('Folio="488"', 'Folio="0001"').replace('Fecha="2026-09-11T17:33:14"', 'Fecha="2026-10-02T12:00:00"')
        .replace('Rfc="BBB020202BBB"', 'Rfc="XEXX010101000"').replace('TotalImpuestosRetenidos="21.13" TotalImpuestosTrasladados="270.40"', 'TotalImpuestosRetenidos="0.00" TotalImpuestosTrasladados="1600.00"')
        .replace('00000000-0000-4000-8000-000000000002', randomUUID());
    const invoice = path.join(temporary, 'synthetic-invoice.xml');
    const bank = path.join(temporary, 'synthetic.csv');
    await writeFile(invoice, xml);
    await writeFile(bank, 'fecha,concepto,abono,cargo,saldo\n2026-10-10,SPEI REC 0034 PAGO FACT 0001,5800.00,0,5800.00\n2026-10-20,SPEI REC 0071 XEXX COMERCIAL,5800.00,0,11600.00\n');
    const profile = path.join(temporary, 'chrome');
    chrome = spawn('/usr/bin/google-chrome', ['--headless=new', '--no-sandbox', '--disable-dev-shm-usage', '--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--disable-background-timer-throttling', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { detached: true, stdio: 'ignore' });
    async function wait(check, label, timeout = 60000) {
        const until = Date.now() + timeout;
        while (Date.now() < until) { try { if (await check()) return; } catch { } await delay(100); }
        throw new Error(`Timed out: ${label}; browser errors: ${exceptions.join('; ')}`);
    }
    let port;
    await wait(async () => { port = (await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; return port; }, 'Chrome debugging port');
    const target = await (await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: 'PUT' })).json();
    socket = new WebSocket(target.webSocketDebuggerUrl);
    await once(socket, 'open');
    let sequence = 0;
    const pending = new Map();
    socket.addEventListener('message', event => {
        const message = JSON.parse(event.data);
        if (message.id) { const item = pending.get(message.id); pending.delete(message.id); if (message.error) item?.reject(new Error(message.error.message)); else item?.resolve(message.result); }
        if (message.method === 'Runtime.exceptionThrown') exceptions.push(message.params.exceptionDetails.text);
        if (message.method === 'Network.responseReceived' && message.params.response.url.includes('/_framework/')) framework.push(message.params.response);
    });
    function cdp(method, params = {}) {
        return new Promise((resolve, reject) => { const id = ++sequence; const timer = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)); }, 30000); pending.set(id, { resolve: result => { clearTimeout(timer); resolve(result); }, reject: error => { clearTimeout(timer); reject(error); } }); socket.send(JSON.stringify({ id, method, params })); });
    }
    async function evaluate(expression) {
        const result = await cdp('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
        assert.ok(!result.exceptionDetails, 'Browser evaluation failed');
        return result.result.value;
    }
    const dom = expression => wait(() => evaluate(expression), expression);
    const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    async function fill(selector, value) {
        await evaluate(`{ const input=document.querySelector(${JSON.stringify(selector)}); input.value=${JSON.stringify(value)}; input.dispatchEvent(new Event('change',{bubbles:true})); }`);
        await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
    }
    async function chooseFile(filename) {
        const document = await cdp('DOM.getDocument');
        const input = await cdp('DOM.querySelector', { nodeId: document.root.nodeId, selector: '#files' });
        await cdp('DOM.setFileInputFiles', { nodeId: input.nodeId, files: [filename] });
        await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
        await dom('document.querySelector("form button[type=submit]")?.disabled === false');
    }
    async function screenshot(name, directory = output) {
        await mkdir(directory, { recursive: true });
        const captured = await cdp('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        await writeFile(path.join(directory, name), Buffer.from(captured.data, 'base64'));
    }
    await cdp('Page.enable');
    await cdp('Runtime.enable');
    await cdp('Network.enable');
    await cdp('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
    await cdp('Page.navigate', { url: new URL('/ui/login', base).href });
    await dom('!!document.querySelector("input[type=email]")');
    await screenshot('pr13-login.png');
    async function layout(width, height) {
        await cdp('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 640 });
        await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
        assert.ok(await evaluate('document.documentElement.scrollWidth <= innerWidth'), `Horizontal overflow at ${width}px`);
    }
    await layout(390, 844);
    await screenshot('bnk-ui-qa-login-mobile.png', '/tmp');
    await layout(1440, 900);
    await fill('#email', email);
    await fill('#password', password);
    await click('form button[type=submit]');
    await dom(`!!document.querySelector('button[data-company="${company.id}"]')`);
    await click(`button[data-company="${company.id}"]`);
    await dom('document.querySelector("h1")?.textContent === "Company overview"');
    await click('a[href="/ui/imports"]');
    await dom('!!document.querySelector("#files")');
    await chooseFile(invoice);
    await click('form button[type=submit]');
    await dom('document.body.textContent.includes("synthetic-invoice.xml") && document.querySelector("tbody")?.textContent.includes("imported")');
    await fill('#kind', 'bank-statement');
    await dom('!!document.querySelector("#account")');
    await fill('#account', 'Synthetic-Web-Smoke');
    await chooseFile(bank);
    await click('form button[type=submit]');
    await dom('document.querySelector("tbody")?.textContent.includes("synthetic.csv") && document.querySelector("tbody")?.textContent.includes("2 imported")');
    await click('a[href="/ui/overview"]');
    await dom('!!document.querySelector("section.run button")');
    await click('section.run button');
    await dom('document.querySelector("section.run strong")?.textContent === "succeeded"');
    await dom('document.querySelectorAll(".badge.suggested").length === 2');
    const totals = await evaluate(`Array.from(document.querySelectorAll('.resource-grid table')).map(table => ({ count: Number(table.querySelector('tfoot td').textContent), amount: Number(table.querySelector('tfoot .money').textContent.replaceAll(',','')), rows: Array.from(table.querySelectorAll('tbody tr')).map(row=>({count:Number(row.querySelector('td').textContent),amount:Number(row.querySelector('.money').textContent.replaceAll(',',''))})) }))`);
    assert.deepEqual(totals.map(item => item.count), [1, 2]);
    for (const item of totals) { assert.equal(item.amount, 11600); assert.equal(item.rows.reduce((sum,row)=>sum+row.count,0), item.count); assert.equal(item.rows.reduce((sum,row)=>sum+row.amount,0), item.amount); }
    await screenshot('pr13-overview.png');
    await layout(768, 1024);
    await layout(390, 844);
    await screenshot('bnk-ui-qa-overview-mobile.png', '/tmp');
    await layout(1440, 900);
    await click('a[href="/ui/review"]');
    await dom('document.querySelectorAll("button[data-decision=confirm]").length === 2');
    await screenshot('pr13-review-queue.png');
    await layout(390, 844);
    await screenshot('bnk-ui-qa-review-mobile.png', '/tmp');
    await layout(1440, 900);
    await click('button[data-decision=confirm]');
    await dom('document.querySelectorAll("button[data-decision=confirm]").length === 1');
    await click('article a[href^="/ui/invoices/"]');
    await dom('document.querySelector("[data-summary=pending]")?.textContent === "5,800.00"');
    assert.ok(await evaluate('Array.from(document.querySelectorAll("dd")).some(item=>item.textContent==="50%")'));
    assert.equal(await evaluate('document.querySelectorAll("tbody tr").length'), 2);
    assert.ok(await evaluate('localStorage.length === 0 && sessionStorage.length === 0'));
    await layout(390, 844);
    await layout(1440, 900);
    await cdp('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
    assert.ok(await evaluate('matchMedia("(prefers-color-scheme: dark)").matches'));
    await screenshot('bnk-ui-qa-summary-dark.png', '/tmp');
    await cdp('Emulation.setEmulatedMedia', { features: [] });
    await cdp('Page.reload', { ignoreCache: true });
    await dom(`!!document.querySelector('button[data-company="${company.id}"]')`);
    assert.ok(await evaluate('!document.querySelector("input[type=password]")'), 'Reload must silently renew using the refresh cookie');
    const cookies = await cdp('Network.getCookies', { urls: [new URL('/auth/refresh', base).href] });
    assert.ok(cookies.cookies.some(cookie=>cookie.name==='refresh_token' && cookie.httpOnly && cookie.sameSite==='Strict' && cookie.path==='/auth'));
    assert.equal(exceptions.length, 0, 'Browser must have no unhandled exceptions');
    const shell = await fetch(new URL('/ui/review', base), { headers: { Accept: 'text/html' } });
    assert.equal(shell.headers.get('cache-control'), 'no-cache');
    assert.equal(shell.headers.get('x-content-type-options'), 'nosniff');
    assert.equal(shell.headers.get('referrer-policy'), 'no-referrer');
    assert.ok(shell.headers.get('content-security-policy').includes("wasm-unsafe-eval"));
    assert.ok(!(await shell.text()).includes('Synthetic UI company'));
    const denied = await fetch(new URL(`/companies/${company.id}/overview`, base));
    assert.equal(denied.status, 401);
    assert.notEqual(denied.headers.get('content-type'), 'text/html');
    const immutable = framework.filter(item => /\.[a-z0-9]{10}\.(wasm|js)$/.test(new URL(item.url).pathname));
    assert.ok(immutable.length > 0, 'Expected fingerprinted framework assets');
    assert.ok(immutable.every(item => String(item.headers['Cache-Control'] || item.headers['cache-control']).includes('immutable')));
    console.log(JSON.stringify({ succeeded: true, viewport: '1440x900', summary: { paidPercentage: 50, pending: 5800, linkedMovements: 2 }, screenshots: ['pr13-login.png', 'pr13-overview.png', 'pr13-review-queue.png'], layoutWidths: [1440, 768, 390], darkModeChecked: true, fingerprintedAssets: immutable.length, unhandledBrowserExceptions: exceptions.length }));
} finally {
    socket?.close();
    if (chrome?.pid) {
        const exited = once(chrome, 'exit').catch(() => {});
        try { process.kill(-chrome.pid, 'SIGTERM'); } catch { }
        await Promise.race([exited, delay(3000)]);
        try { process.kill(-chrome.pid, 'SIGKILL'); } catch { }
    }
    await rm(temporary, { recursive: true, force: true });
}
