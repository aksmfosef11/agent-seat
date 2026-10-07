// Optional, read-only checks against an installed agent-seat. Run as its installing owner.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

const seat = process.argv[2] || 'agent';
const cli = process.env.AGENTSEAT_CLI || join(process.env.ProgramFiles, 'agent-seat', 'cli', 'agent-seat.exe');
const base = 'http://127.0.0.1:38399/api/v1';
const token = (await readFile(join(process.env.ProgramData, 'agent-seat', 'agent-token.txt'), 'utf8')).trim();
const headers = { Authorization: `Bearer ${token}` };
assert.equal((await fetch(`${base}/seats/${seat}/agent/status`)).status, 401);
const issued = await fetch(`${base}/seats/${seat}/agent/viewer-ticket`, { method: 'POST', headers });
assert.equal(issued.status, 200);
const { ticket } = await issued.json();
const url = `${base}/agent-viewer?ticket=${encodeURIComponent(ticket)}`;
const redeemed = await fetch(url, { redirect: 'manual' });
assert.equal(redeemed.status, 302);
assert.equal(redeemed.headers.get('location'), `/?viewer=${seat}`);
const setCookie = redeemed.headers.get('set-cookie');
assert.match(setCookie, /httponly/i); assert.match(setCookie, /samesite=strict/i);
assert.match(setCookie, /max-age=1800/i); assert.ok(!setCookie.includes(token));
const cookie = setCookie.split(';')[0];
assert.equal((await fetch(url, { redirect: 'manual' })).status, 401);
assert.equal((await fetch(`${base}/seats/${seat}/agent/status`, { headers: { Cookie: cookie } })).status, 200);
assert.equal((await fetch(`${base}/seats/another-seat/agent/status`, { headers: { Cookie: cookie } })).status, 401);
assert.equal((await fetch(`${base}/seats/${seat}/agent/viewer-ticket`, { method: 'POST', headers: { Cookie: cookie } })).status, 401);
console.log('HTTP viewer authorization: passed (bearer issuance, one-use ticket, seat scope, HttpOnly/Strict, no cookie re-issuance)');

const child = spawn(cli, ['computer', 'mcp', '--seat', seat], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
const pending = new Map();
const lines = createInterface({ input: child.stdout });
lines.on('line', line => { const value = JSON.parse(line); const handler = pending.get(value.id); if (handler) { pending.delete(value.id); handler(value); } });
let id = 0;
async function rpc(method, params) {
  const requestId = ++id;
  const value = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(requestId); reject(new Error('MCP timed out')); }, 30000);
    pending.set(requestId, result => { clearTimeout(timer); resolve(result); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: requestId, method, params }) + '\n');
  });
  assert.equal(value.error, undefined); return value.result;
}
try {
  const init = await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'agent-seat-live-validation', version: '1' } });
  assert.match(init.serverInfo.version, /^0\.[0-9]+\.[0-9]+$/);
  if (process.env.AGENTSEAT_EXPECTED_VERSION) assert.equal(init.serverInfo.version, process.env.AGENTSEAT_EXPECTED_VERSION);
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const list = await rpc('tools/list', {});
  assert.deepEqual(list.tools.map(tool => tool.name).sort(), ['seat_act', 'seat_observe', 'seat_start', 'seat_status']);
  const observed = await rpc('tools/call', { name: 'seat_observe', arguments: { image: 'auto' } });
  assert.ok(!observed.isError);
  const images = observed.content.filter(item => item.type === 'image').length;
  assert.equal(images, 1);
  console.log(`MCP: v${init.serverInfo.version}, ${list.tools.length} tools, first observation ${images} image; passed`);
} finally { child.stdin.end(); child.kill(); lines.close(); }
