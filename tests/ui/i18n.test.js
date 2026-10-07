import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { languageNames, messages, resolveLanguage, translate, errorKey, seatStateKey, localizeCheck } from '../../src/AgentSeat.Service/wwwroot/i18n.js';

test('browser region tags, saved choices and unsupported preferences choose a supported language', () => {
  assert.equal(resolveLanguage(['fr-FR', 'ko-KR', 'en-US']), 'ko');
  assert.equal(resolveLanguage(['zh-CN', 'ko-KR']), 'zh');
  assert.equal(resolveLanguage(['en', 'ko-KR']), 'en');
  assert.equal(resolveLanguage(['bad-saved-value', 'zh_Hans_CN']), 'zh');
  assert.equal(resolveLanguage([null, 'fr-FR']), 'en');
});

test('every supported language supplies the same keys and named placeholders', () => {
  const keys = Object.keys(messages.en).sort();
  const placeholders = text => [...text.matchAll(/\{([a-zA-Z][a-zA-Z0-9_]*)\}/g)].map(match => match[1]).sort();
  for (const language of Object.keys(languageNames)) {
    assert.deepEqual(Object.keys(messages[language]).sort(), keys, language);
    for (const key of keys) {
      assert.ok(messages[language][key].trim(), `${language}:${key}`);
      assert.deepEqual(placeholders(messages[language][key]), placeholders(messages.en[key]), `${language}:${key}`);
    }
  }
});

test('static labels and accessibility attributes reference existing translations', async () => {
  const html = await readFile(new URL('../../src/AgentSeat.Service/wwwroot/index.html', import.meta.url), 'utf8');
  const keys = [...html.matchAll(/data-i18n(?:-alt|-placeholder|-aria-label)?="([^"]+)"/g)].map(match => match[1]);
  assert.ok(keys.length > 20);
  for (const key of keys) assert.ok(Object.hasOwn(messages.en, key), key);
});

test('interpolation preserves user data literally and cannot recursively replace it', () => {
  assert.equal(translate('en', 'viewer.namedTitle', { seat: '<b>{time}</b>' }), '<b>{time}</b> · Seat screen');
  assert.equal(translate('zh', 'seats.session', { id: 4 }), '会话 4');
  assert.equal(translate('xx', 'viewer.close'), 'Close');
  assert.equal(translate('ko', 'future.key'), 'future.key');
});

test('host checks localize dynamic account names and ports; unknown checks keep their diagnostics', () => {
  const account = { code: 'account_agent', title: 'Account for AI', status: 'fail', detail: 'Account missing.' };
  const views = [{ seat: { id: 'agent', displayName: 'AI Workspace' } }];
  assert.equal(localizeCheck('ko', account, views).title, 'AI Workspace 계정');
  assert.match(localizeCheck('zh', account, views).detail, /Windows 账户/);
  assert.equal(localizeCheck('zh', { code: 'rdp_port', title: 'RDP TCP port 3390', status: 'pass', detail: 'Listening.' }).title, 'RDP 端口 3390');
  const future = { code: 'future_check', title: 'New check', status: 'warning', detail: 'Provider diagnostic.' };
  assert.deepEqual(localizeCheck('ko', future), { title: future.title, detail: future.detail });
});

test('authorization, partial input failure and status messages follow stable API fields', () => {
  assert.equal(errorKey({ code: 'paused' }), 'error.paused');
  assert.equal(errorKey({ name: 'TimeoutError' }), 'error.timeout');
  assert.equal(errorKey(new TypeError('Failed to fetch')), 'error.network');
  assert.equal(errorKey({ code: 'future_error' }), 'error.unknown');
  assert.equal(seatStateKey({ refused: true, paused: true }), 'seats.refused');
  assert.equal(seatStateKey({ paused: true, sessionAvailable: false }), 'seats.paused');
  assert.equal(seatStateKey({ sessionAvailable: true, helperRunning: false }), 'seats.helperPending');
});
