import test from 'node:test';
import assert from 'node:assert/strict';
import { screenPoint, keyboardAction, wheelAction, pointerAction, InputQueue } from '../../src/AgentSeat.Service/wwwroot/remote-control.js';

test('scaled frame coordinates use the real image rectangle and clamp a captured drag', () => {
  const rect = { left: 100, top: 30, width: 640, height: 400 };
  assert.deepEqual(screenPoint(rect, 1280, 800, 420, 230), { x: 640, y: 400 });
  assert.equal(screenPoint(rect, 1280, 800, 99, 30), null);
  assert.equal(screenPoint(rect, 1280, 800, 740, 430), null);
  assert.deepEqual(screenPoint(rect, 1280, 800, 900, -10, true), { x: 1279, y: 0 });
  assert.equal(screenPoint({ width: 0 }, 1280, 800, 0, 0), null);
});
test('Unicode text, shortcuts and navigation keys; IME and modifier events are ignored', () => {
  assert.deepEqual(keyboardAction({ key: '한' }), { type: 'type', text: '한' });
  assert.deepEqual(keyboardAction({ key: 'S', ctrlKey: true, shiftKey: true }), { type: 'keypress', keys: ['CTRL', 'SHIFT', 'S'] });
  assert.deepEqual(keyboardAction({ key: 'ArrowDown' }), { type: 'keypress', keys: ['DOWN'] });
  for (const event of [{ key: 'Control' }, { key: 'a', isComposing: true }, { key: 'a', keyCode: 229 }, { key: 'a', metaKey: true }]) assert.equal(keyboardAction(event), null);
});
test('wheel modes preserve direction, support horizontal scroll and bound oversized offsets', () => {
  assert.deepEqual(wheelAction({ deltaX: 0, deltaY: 3, deltaMode: 1 }, { x: 4, y: 5 }), { type: 'scroll', x: 4, y: 5, scroll_x: 0, scroll_y: 120 });
  assert.equal(wheelAction({ deltaX: 0, deltaY: 0 }, { x: 1, y: 1 }), null);
  assert.equal(wheelAction({ deltaX: -99999, deltaY: 0 }, { x: 1, y: 1 }).scroll_x, -2400);
});
test('drag paths survive returning to their start; right clicks never become drags', () => {
  const path = [{ x: 10, y: 10 }, { x: 30, y: 40 }, { x: 10, y: 10 }];
  assert.equal(pointerAction(path, 'left').type, 'drag');
  assert.equal(pointerAction(path, 'right').type, 'click');
  assert.equal(pointerAction([path[0]], 'left', true).type, 'double_click');
});
test('input is serialized, cancellation drops unsent actions, partial failures are not retried', async () => {
  let release, calls = [], errors = [];
  const queue = new InputQueue(async action => { calls.push(action); if (action === 1) await new Promise(resolve => { release = resolve; }); if (action === 3) throw new Error('paused'); }, e => errors.push(e.message));
  queue.push(1); queue.push(2); queue.cancel(); queue.push(3); queue.push(4);
  release(); await new Promise(resolve => setTimeout(resolve, 0));
  assert.deepEqual(calls, [1, 3]); assert.deepEqual(errors, ['paused']);
});
test('queue overflow stops and clears pending input', async () => {
  let release, errors = [], calls = [];
  const queue = new InputQueue(async action => { calls.push(action); await new Promise(resolve => { release = resolve; }); }, e => errors.push(e.message), 1);
  queue.push(1); queue.push(2); queue.push(3); release();
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.deepEqual(calls, [1]); assert.equal(errors.length, 1);
});
