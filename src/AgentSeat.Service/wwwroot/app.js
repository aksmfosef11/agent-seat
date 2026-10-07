import { wireRemoteControl } from './remote-control.js';
const $ = selector => document.querySelector(selector);
const dialog = $('#viewer'), image = $('#remote-screen'), manual = $('#manual-control');
let activeSeat = null, frameBusy = false, inputBusy = false, frameUrl = null, frameGeneration = 0;
let refreshBusy = false, viewerSeat = new URLSearchParams(location.search).get('viewer');
let actionTokenRequired = false, inputError = null;
function node(tag, text, className = '') { const n = document.createElement(tag); n.textContent = text; n.className = className; return n; }
async function request(path, options = {}) {
  const response = await fetch(`/api/v1${path}`, { cache: 'no-store', ...options,
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) } });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try { const body = await response.json(); message = body.error || body.detail || message; } catch { /* not JSON */ }
    throw new Error(message);
  }
  return response;
}
async function agent(path, options = {}) {
  if (!activeSeat) throw new Error('좌석을 선택하세요.');
  const headers = {};
  // computer view grants a seat-scoped HttpOnly cookie; the long-lived token stays out of the URL and JS.
  if (viewerSeat !== activeSeat) {
    const token = sessionStorage.getItem('agent-seat-token');
    if (token) headers.Authorization = `Bearer ${token}`;
  }
  return request(`/seats/${encodeURIComponent(activeSeat)}/agent${path}`, { ...options, headers });
}
function report(message) { $('#viewer-status').textContent = message; }
const remote = wireRemoteControl({ dialog, image, manual,
  async send(action) {
    inputBusy = true;
    try { await agent('/actions', { method: 'POST', body: JSON.stringify({ actions: [action], screenshot: false, settleMs: 0 }) }); }
    finally { inputBusy = false; }
  }, report(message) { inputError = message; report(message); }
});
manual.addEventListener('change', () => { inputError = null; });
async function frame() {
  if (!dialog.open || document.hidden || frameBusy || inputBusy || remote.isInteracting()) return;
  frameBusy = true;
  const generation = frameGeneration;
  try {
    const blob = await (await agent('/screenshot?scale=1&format=jpeg', { signal: AbortSignal.timeout(15000) })).blob();
    if (generation !== frameGeneration || !dialog.open || remote.isInteracting()) return;
    const url = URL.createObjectURL(blob);
    const previous = frameUrl; frameUrl = url; image.src = url;
    if (previous) URL.revokeObjectURL(previous);
    $('#frame-time').textContent = `화면 갱신 ${new Date().toLocaleTimeString()}`;
    if (!inputError) report(manual.checked ? '직접 조작 중 · 화면을 클릭하면 키 입력도 전달됩니다.' : '읽기 전용 · 직접 조작을 켜면 마우스와 키보드를 사용할 수 있습니다.');
  } catch (error) {
    if (generation === frameGeneration) { remote.disarm(); report(`화면 확인 실패: ${error.message} · 세션 시작 또는 computer view를 다시 실행하세요.`); }
  } finally { frameBusy = false; }
}
async function openViewer(seatId) {
  remote.disarm(); inputError = null; frameGeneration++; activeSeat = seatId;
  image.removeAttribute('src'); if (frameUrl) URL.revokeObjectURL(frameUrl); frameUrl = null;
  $('#viewer-title').textContent = `${seatId} · 좌석 화면`;
  $('#frame-time').textContent = '화면을 불러오는 중…';
  if (!dialog.open) dialog.showModal();
  report('화면을 불러오는 중…'); await frame();
}
$('#close-viewer').addEventListener('click', () => dialog.close());
dialog.addEventListener('close', () => {
  frameGeneration++; activeSeat = null; image.removeAttribute('src');
  if (frameUrl) URL.revokeObjectURL(frameUrl); frameUrl = null;
});
$('#send-text').addEventListener('click', () => {
  const text = $('#remote-text').value;
  if (!manual.checked) { report('먼저 직접 조작을 켜세요.'); return; }
  if (text && remote.push({ type: 'type', text })) $('#remote-text').value = '';
});
document.querySelectorAll('[data-keys]').forEach(button => button.addEventListener('click', () => {
  if (!manual.checked) { report('먼저 직접 조작을 켜세요.'); return; }
  remote.push({ type: 'keypress', keys: button.dataset.keys.split('+') }); image.focus();
}));
$('#start-viewer-seat').addEventListener('click', async () => {
  try { report('좌석을 시작하는 중…'); await agent('/start', { method: 'POST' }); await frame(); await refresh(); }
  catch (error) { report(error.message); }
});
async function ownerCommand(seatId, action) {
  const payload = { confirm: true };
  if (actionTokenRequired) {
    const token = sessionStorage.getItem('agent-seat-action-token') || window.prompt('관리자 작업 토큰을 입력하세요.');
    if (!token) return;
    sessionStorage.setItem('agent-seat-action-token', token); payload.actionToken = token;
  }
  try {
    await request(`/seats/${encodeURIComponent(seatId)}/agent-control/${action}`, { method: 'POST', body: JSON.stringify(payload) });
    if (activeSeat === seatId && action !== 'resume') remote.disarm(); await refresh();
  } catch (error) { $('#health').textContent = error.message; }
}
function button(label, handler, className = 'secondary') {
  const b = node('button', label, className); b.type = 'button'; b.addEventListener('click', handler); return b;
}
async function refresh() {
  if (refreshBusy) return;
  refreshBusy = true;
  try {
    const [health, seats, preflight] = await Promise.all(['/health', '/seats', '/preflight'].map(async p => (await request(p)).json()));
    actionTokenRequired = health.actionTokenRequired;
    $('#health').textContent = `${health.hostName} · v${health.version} · 연결됨`;
    const cards = [];
    for (const view of seats) {
      const id = view.seat.id;
      const status = await (await request(`/seats/${encodeURIComponent(id)}/agent-control/status`)).json();
      const card = node('article', '', 'seat-card');
      card.append(node('h3', view.seat.displayName), node('p', `${id} · ${view.seat.userName} · ${status.sessionAvailable ? `세션 ${status.sessionId}` : '세션 꺼짐'}`, 'seat-meta'), node('p', status.paused ? '일시정지됨' : status.detail, 'agent-detail'));
      const actions = node('div', '', 'seat-actions');
      const open = button('화면 보기 · 직접 조작', async () => {
        if (viewerSeat !== id && !sessionStorage.getItem('agent-seat-token')) {
          const token = window.prompt(`computer view --seat ${id}로 열면 토큰 입력 없이 사용할 수 있습니다. 직접 열려면 에이전트 토큰을 입력하세요.`);
          if (!token) return; sessionStorage.setItem('agent-seat-token', token.trim());
        }
        await openViewer(id);
      }, 'primary');
      open.disabled = status.refused;
      actions.append(open, button(status.paused ? '재개' : '일시정지', () => ownerCommand(id, status.paused ? 'resume' : 'pause')), button('입력 중지', () => ownerCommand(id, 'stop'), 'danger'));
      card.append(actions); cards.push(card);
      if (activeSeat === id && (status.paused || status.refused)) remote.disarm();
    }
    $('#seats').replaceChildren(...cards);
    if (!cards.length) $('#seats').append(node('p', '등록된 좌석이 없습니다. Install.ps1으로 전용 계정을 만들어 주세요.', 'empty'));
    $('#checks').replaceChildren(...preflight.checks.map(check => { const n = node('div', '', `preflight-item ${check.status}`); n.append(node('strong', check.title), node('p', check.detail, 'preflight-detail')); return n; }));
    const failures = preflight.checks.filter(c => c.status === 'fail').length;
    $('#readiness').textContent = failures ? `${failures}개 확인 필요` : '준비 완료';
  } catch (error) { $('#health').textContent = `연결 실패 · ${error.message}`; }
  finally { refreshBusy = false; }
}
$('#refresh').addEventListener('click', refresh);
await refresh();
if (viewerSeat && /^[a-z][a-z0-9-]{0,31}$/.test(viewerSeat)) await openViewer(viewerSeat);
setInterval(refresh, 10000); setInterval(frame, 600);
