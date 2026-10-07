export function screenPoint(rect, width, height, clientX, clientY, clamp = false) {
  if (!(rect.width > 0 && rect.height > 0 && width > 0 && height > 0)) return null;
  const x = (clientX - rect.left) * width / rect.width;
  const y = (clientY - rect.top) * height / rect.height;
  if (!clamp && (x < 0 || y < 0 || x >= width || y >= height)) return null;
  return { x: Math.max(0, Math.min(width - 1, Math.floor(x))), y: Math.max(0, Math.min(height - 1, Math.floor(y))) };
}

const keyNames = { ArrowLeft: 'LEFT', ArrowRight: 'RIGHT', ArrowUp: 'UP', ArrowDown: 'DOWN',
  Escape: 'ESC', Enter: 'ENTER', Backspace: 'BACKSPACE', Delete: 'DELETE', Tab: 'TAB',
  Home: 'HOME', End: 'END', PageUp: 'PGUP', PageDown: 'PGDN', Insert: 'INSERT', ' ': 'SPACE' };

export function keyboardAction(event) {
  if (event.isComposing || event.keyCode === 229 || event.metaKey ||
      ['Control', 'Alt', 'Shift', 'Meta', 'Dead', 'Process', 'Unidentified'].includes(event.key)) return null;
  if (event.key.length === 1 && !event.ctrlKey && !event.altKey) return { type: 'type', text: event.key };
  const key = keyNames[event.key] || (/^F([1-9]|1[0-2])$/.test(event.key) ? event.key :
    /^[a-z0-9]$/i.test(event.key) ? event.key.toUpperCase() : null);
  if (!key) return null;
  const keys = [];
  if (event.ctrlKey) keys.push('CTRL');
  if (event.altKey) keys.push('ALT');
  if (event.shiftKey) keys.push('SHIFT');
  keys.push(key);
  return { type: 'keypress', keys };
}

export function wheelAction(event, point) {
  if (!point) return null;
  const factor = event.deltaMode === 1 ? 40 : event.deltaMode === 2 ? 800 : 1;
  const bound = value => Math.max(-2400, Math.min(2400, Math.round(value * factor)));
  const scroll_x = bound(event.deltaX), scroll_y = bound(event.deltaY);
  return scroll_x || scroll_y ? { type: 'scroll', ...point, scroll_x, scroll_y } : null;
}

export function pointerAction(path, button, doubleClick = false) {
  if (!path.length) return null;
  const start = path[0], end = path.at(-1);
  if (button === 'left' && path.some(p => Math.hypot(p.x - start.x, p.y - start.y) > 4))
    return { type: 'drag', path: path.map(p => ({ x: p.x, y: p.y })) };
  return { type: doubleClick && button === 'left' ? 'double_click' : 'click', ...end, button };
}

// Bounded, serialized input. A failed batch is never retried because it may have partially executed.
export class InputQueue {
  constructor(send, onError, limit = 64) { this.send = send; this.onError = onError; this.limit = limit; this.generation = 0; this.pending = []; this.running = false; }
  cancel() { this.generation++; this.pending = []; }
  push(action) {
    if (!action) return false;
    if (this.pending.length >= this.limit) { this.cancel(); this.onError(Object.assign(new Error('Input queue full.'), { code: 'input_queue_full' })); return false; }
    this.pending.push({ action, generation: this.generation });
    void this.drain();
    return true;
  }
  async drain() {
    if (this.running) return;
    this.running = true;
    try {
      while (this.pending.length) {
        const item = this.pending.shift();
        if (item.generation !== this.generation) continue;
        try { await this.send(item.action); }
        catch (error) { if (item.generation === this.generation) { this.cancel(); this.onError(error); break; } }
      }
    } finally { this.running = false; }
  }
}

export function wireRemoteControl({ dialog, image, manual, send, report }) {
  let path = null, button = 'left', pendingClick = null, clickTimer = null;
  const queue = new InputQueue(send, error => { disarm(); report(error); });
  const point = (event, clamp) => screenPoint(image.getBoundingClientRect(), image.naturalWidth, image.naturalHeight, event.clientX, event.clientY, clamp);
  const enabled = () => dialog.open && manual.checked;
  const armed = () => enabled() && image.complete && image.naturalWidth > 0;
  function flushClick() { if (pendingClick) queue.push(pendingClick.action); pendingClick = null; clearTimeout(clickTimer); }
  function disarm() { manual.checked = false; path = null; pendingClick = null; clearTimeout(clickTimer); queue.cancel(); image.classList.remove('armed'); }
  manual.addEventListener('change', () => {
    if (!manual.checked) disarm();
    image.classList.toggle('armed', manual.checked);
    if (manual.checked) image.focus();
  });
  image.addEventListener('pointerdown', event => {
    if (!armed() || event.button > 2 || !point(event, false)) return;
    event.preventDefault(); image.focus(); image.setPointerCapture(event.pointerId);
    button = ['left', 'middle', 'right'][event.button]; path = [point(event, false)];
  });
  image.addEventListener('pointermove', event => {
    if (!path || !armed()) return;
    const p = point(event, true);
    if (path.length < 49) path.push(p); else path[path.length - 1] = p;
  });
  image.addEventListener('pointerup', event => {
    if (!path || !armed()) { path = null; return; }
    path.push(point(event, true));
    let action = pointerAction(path, button); path = null;
    if (action.type === 'click' && button === 'left') {
      if (pendingClick && performance.now() - pendingClick.time < 350 &&
          Math.hypot(action.x - pendingClick.action.x, action.y - pendingClick.action.y) < 8) {
        pendingClick = null; clearTimeout(clickTimer); action.type = 'double_click'; queue.push(action);
      } else { flushClick(); pendingClick = { action, time: performance.now() }; clickTimer = setTimeout(flushClick, 350); }
    } else { flushClick(); queue.push(action); }
  });
  image.addEventListener('pointercancel', () => { path = null; });
  image.addEventListener('contextmenu', event => { if (enabled()) event.preventDefault(); });
  image.addEventListener('dragstart', event => event.preventDefault());
  image.addEventListener('wheel', event => {
    if (!armed()) return;
    event.preventDefault(); flushClick(); queue.push(wheelAction(event, point(event, false)));
  }, { passive: false });
  image.addEventListener('keydown', event => {
    if (!enabled()) return;
    const action = keyboardAction(event);
    if (action) { event.preventDefault(); event.stopPropagation(); flushClick(); queue.push(action); }
  });
  dialog.addEventListener('cancel', event => {
    if (armed() && document.activeElement === image) event.preventDefault();
  });
  dialog.addEventListener('close', disarm);
  return { disarm, isInteracting: () => Boolean(path || pendingClick), push(action) { if (enabled()) { flushClick(); return queue.push(action); } return false; } };
}
