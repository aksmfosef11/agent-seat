import { wireRemoteControl } from './remote-control.js';
import { languageNames, resolveLanguage, translate, errorKey, seatStateKey, localizeCheck } from './i18n.js';
const $ = selector => document.querySelector(selector);
const dialog = $('#viewer'), image = $('#remote-screen'), manual = $('#manual-control');
const languageStorageKey = 'agent-seat-language';
let savedLanguage;
try { savedLanguage = localStorage.getItem(languageStorageKey); } catch { /* Storage may be unavailable. */ }
let language = resolveLanguage([new URLSearchParams(location.search).get('lang'), savedLanguage, ...(navigator.languages || [navigator.language])]);
const t = (key, values) => translate(language, key, values);
let activeSeat = null, frameBusy = false, inputBusy = false, frameUrl = null, frameGeneration = 0;
let refreshBusy = false, viewerSeat = new URLSearchParams(location.search).get('viewer');
let actionTokenRequired = false, inputError = null, lastFrameTime = null, model = null, refreshError = null;
let viewerMessage = { key: 'viewer.readOnly', values: {}, error: null };
function node(tag, text, className = '') { const n = document.createElement(tag); n.textContent = text; n.className = className; return n; }
async function request(path, options = {}) {
  const response = await fetch(`/api/v1${path}`, { cache: 'no-store', ...options,
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) } });
  if (!response.ok) {
    let body = {};
    try { body = await response.json(); } catch { /* Preserve the HTTP diagnostic. */ }
    throw Object.assign(new Error(body.error || body.detail || `${response.status} ${response.statusText}`),
      { code: body.errorCode || (response.status === 401 ? 'unauthorized' : undefined), status: response.status });
  }
  return response;
}
async function agent(path, options = {}) {
  if (!activeSeat) throw Object.assign(new Error('No seat selected.'), { code: 'selectSeat' });
  const headers = {};
  // computer view grants a seat-scoped HttpOnly cookie; the long-lived token stays out of the URL and JS.
  if (viewerSeat !== activeSeat) {
    const token = sessionStorage.getItem('agent-seat-token');
    if (token) headers.Authorization = `Bearer ${token}`;
  }
  return request(`/seats/${encodeURIComponent(activeSeat)}/agent${path}`, { ...options, headers });
}
function renderViewerText() {
  $('#viewer-title').textContent = activeSeat ? t('viewer.namedTitle', { seat: activeSeat }) : t('viewer.title');
  $('#viewer-status').textContent = t(viewerMessage.key, { ...viewerMessage.values,
    ...(viewerMessage.error ? { error: t(errorKey(viewerMessage.error)) } : {}) });
  $('#viewer-diagnostic').hidden = !viewerMessage.error;
  $('#viewer-diagnostic-text').textContent = viewerMessage.error?.message || '';
  $('#frame-time').textContent = lastFrameTime ? t('viewer.updated', {
    time: new Intl.DateTimeFormat(language, { hour: 'numeric', minute: '2-digit', second: '2-digit' }).format(lastFrameTime),
  }) : t(dialog.open ? 'viewer.loading' : 'viewer.waiting');
}
function report(key, values = {}, error = null) { viewerMessage = { key, values, error }; renderViewerText(); }
const remote = wireRemoteControl({ dialog, image, manual,
  async send(action) {
    inputBusy = true;
    try { await agent('/actions', { method: 'POST', body: JSON.stringify({ actions: [action], screenshot: false, settleMs: 0 }) }); }
    finally { inputBusy = false; }
  }, report(error) { inputError = error; report(errorKey(error), {}, error); }
});
manual.addEventListener('change', () => { inputError = null; report(manual.checked ? 'viewer.controlling' : 'viewer.readOnly'); });
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
    lastFrameTime = new Date();
    if (!inputError) report(manual.checked ? 'viewer.controlling' : 'viewer.readOnly'); else renderViewerText();
  } catch (error) {
    if (generation === frameGeneration) { remote.disarm(); report('viewer.failed', {}, error); }
  } finally { frameBusy = false; }
}
async function openViewer(seatId) {
  remote.disarm(); inputError = null; lastFrameTime = null; frameGeneration++; activeSeat = seatId;
  image.removeAttribute('src'); if (frameUrl) URL.revokeObjectURL(frameUrl); frameUrl = null;
  if (!dialog.open) dialog.showModal();
  report('viewer.loading'); await frame();
}
$('#close-viewer').addEventListener('click', () => dialog.close());
dialog.addEventListener('close', () => {
  frameGeneration++; activeSeat = null; lastFrameTime = null; image.removeAttribute('src');
  if (frameUrl) URL.revokeObjectURL(frameUrl); frameUrl = null;
});
$('#send-text').addEventListener('click', () => {
  const text = $('#remote-text').value;
  if (!manual.checked) { report('viewer.enableManual'); return; }
  if (text && remote.push({ type: 'type', text })) $('#remote-text').value = '';
});
document.querySelectorAll('[data-keys]').forEach(button => button.addEventListener('click', () => {
  if (!manual.checked) { report('viewer.enableManual'); return; }
  remote.push({ type: 'keypress', keys: button.dataset.keys.split('+') }); image.focus();
}));
$('#start-viewer-seat').addEventListener('click', async () => {
  try { report('viewer.starting'); await agent('/start', { method: 'POST' }); await frame(); await refresh(); }
  catch (error) { report(errorKey(error), {}, error); }
});
async function ownerCommand(seatId, action) {
  const payload = { confirm: true };
  if (actionTokenRequired) {
    const token = sessionStorage.getItem('agent-seat-action-token') || window.prompt(t('prompt.actionToken'));
    if (!token) return;
    sessionStorage.setItem('agent-seat-action-token', token); payload.actionToken = token;
  }
  try {
    await request(`/seats/${encodeURIComponent(seatId)}/agent-control/${action}`, { method: 'POST', body: JSON.stringify(payload) });
    if (activeSeat === seatId && action !== 'resume') remote.disarm(); await refresh();
  } catch (error) { refreshError = error; renderDashboard(); }
}
function button(label, handler, className = 'secondary') {
  const b = node('button', label, className); b.type = 'button'; b.addEventListener('click', handler); return b;
}
function renderDashboard() {
  $('#health').textContent = refreshError ? t('health.failed', { error: t(errorKey(refreshError)) }) : model
    ? t('health.connected', { host: model.health.hostName, version: model.health.version }) : t('health.checking');
  $('#connection-diagnostic').hidden = !refreshError;
  $('#connection-diagnostic p').textContent = refreshError?.message || '';
  if (!model) return;
  const cards = model.views.map(view => {
    const id = view.seat.id, status = model.statuses[id];
    const card = node('article', '', 'seat-card');
    card.append(node('h3', view.seat.displayName), node('p', `${id} · ${view.seat.userName} · ${status.sessionAvailable ? t('seats.session', { id: status.sessionId }) : t('seats.offline')}`, 'seat-meta'), node('p', t(seatStateKey(status)), 'agent-detail'));
    const actions = node('div', '', 'seat-actions');
    const open = button(t('seats.view'), async () => {
      if (viewerSeat !== id && !sessionStorage.getItem('agent-seat-token')) {
        const token = window.prompt(t('prompt.agentToken', { seat: id }));
        if (!token) return; sessionStorage.setItem('agent-seat-token', token.trim());
      }
      await openViewer(id);
    }, 'primary');
    open.disabled = status.refused;
    actions.append(open, button(t(status.paused ? 'seats.resume' : 'seats.pause'), () => ownerCommand(id, status.paused ? 'resume' : 'pause')), button(t('seats.stop'), () => ownerCommand(id, 'stop'), 'danger'));
    card.append(actions); return card;
  });
  $('#seats').replaceChildren(...cards);
  if (!cards.length) $('#seats').append(node('p', t('seats.empty'), 'empty'));
  $('#checks').replaceChildren(...model.preflight.checks.map(check => {
    const localized = localizeCheck(language, check, model.views), n = node('div', '', `preflight-item ${check.status}`);
    n.append(node('strong', localized.title), node('p', localized.detail, 'preflight-detail'));
    if (check.status !== 'pass') {
      const diagnostic = node('details', '');
      diagnostic.append(node('summary', t('checks.diagnostic')), node('p', [check.detail, check.remediation].filter(Boolean).join(' '), 'preflight-detail'));
      n.append(diagnostic);
    }
    return n;
  }));
  const failures = model.preflight.checks.filter(c => c.status === 'fail').length;
  $('#readiness').textContent = failures ? t('checks.failures', { count: new Intl.NumberFormat(language).format(failures) }) : t('checks.ready');
}
function applyLanguage() {
  document.documentElement.lang = language === 'zh' ? 'zh-Hans' : language;
  document.querySelectorAll('[data-i18n]').forEach(element => { element.textContent = t(element.dataset.i18n); });
  for (const attr of ['alt', 'placeholder', 'aria-label']) {
    document.querySelectorAll(`[data-i18n-${attr}]`).forEach(element => { element.setAttribute(attr, t(element.getAttribute(`data-i18n-${attr}`))); });
  }
  document.querySelectorAll('[data-language-select]').forEach(select => { select.value = language; });
  renderDashboard(); renderViewerText();
}
document.querySelectorAll('[data-language-select]').forEach(select => {
  select.replaceChildren(...Object.entries(languageNames).map(([value, label]) => { const option = node('option', label); option.value = value; return option; }));
  select.addEventListener('change', () => {
    language = resolveLanguage(select.value);
    try { localStorage.setItem(languageStorageKey, language); } catch { /* Language still works without persistence. */ }
    // The query chooses the initial language only; an explicit selection remains authoritative on reload.
    const url = new URL(location.href); url.searchParams.delete('lang'); history.replaceState(null, '', url);
    applyLanguage();
  });
});
async function refresh() {
  if (refreshBusy) return;
  refreshBusy = true;
  try {
    const [health, views, preflight] = await Promise.all(['/health', '/seats', '/preflight'].map(async p => (await request(p)).json()));
    actionTokenRequired = health.actionTokenRequired;
    const statuses = {};
    await Promise.all(views.map(async view => { statuses[view.seat.id] = await (await request(`/seats/${encodeURIComponent(view.seat.id)}/agent-control/status`)).json(); }));
    model = { health, views, preflight, statuses }; refreshError = null;
    if (activeSeat && statuses[activeSeat] && (statuses[activeSeat].paused || statuses[activeSeat].refused)) remote.disarm();
    renderDashboard();
  } catch (error) { refreshError = error; renderDashboard(); }
  finally { refreshBusy = false; }
}
$('#refresh').addEventListener('click', refresh);
applyLanguage(); await refresh();
if (viewerSeat && /^[a-z][a-z0-9-]{0,31}$/.test(viewerSeat)) await openViewer(viewerSeat);
setInterval(refresh, 10000); setInterval(frame, 600);
