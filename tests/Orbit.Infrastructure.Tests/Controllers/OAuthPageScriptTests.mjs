import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../../../src/Orbit.Api/OAuth/OAuthPageScript.cs', import.meta.url), 'utf8')
  .split('public const string Js = """\n')[1].split('\n""";')[0];

function page({ enabled = true, response = { ok: true, json: async () => ({}) } } = {}) {
  const elements = new Map();
  function element(id) {
    if (!elements.has(id)) {
      const classes = new Set();
      const attributes = new Map();
      elements.set(id, {
        value: '', textContent: '', disabled: false,
        listeners: {},
        classList: {
          toggle(name, force) {
            if (force) classes.add(name);
            else classes.delete(name);
          },
          contains: (name) => classes.has(name)
        },
        addEventListener(name, callback) { this.listeners[name] = callback; },
        setAttribute: (name, value) => attributes.set(name, value),
        removeAttribute: (name) => attributes.delete(name),
        toggleAttribute(name, force) {
          if (force) attributes.set(name, '');
          else attributes.delete(name);
        },
        focus() { document.activeElement = this; }
      });
    }
    return elements.get(id);
  }
  const request = { botProtectionEnabled: enabled, turnstileSiteKey: enabled ? 'public-key' : null, language: 'pt-BR' };
  element('oauth-request').textContent = JSON.stringify(request);
  element('oauth-copy').textContent = JSON.stringify({
    sendFailed: 'Send failed', codeHint: 'Enter code', resendCountdown: '{0}', sentToLine: '{0}'
  });
  element('email').value = 'person@example.com';
  const document = { getElementById: element, querySelectorAll: () => [], activeElement: null };
  const calls = [];
  const resets = [];
  let callbacks;
  let tick;
  const window = {
    location: {},
    turnstile: {
      render(selector, options) {
        assert.equal(selector, '#turnstile-widget');
        callbacks = options;
        return 'widget-id';
      },
      reset(id) { resets.push(id); }
    }
  };
  const context = vm.createContext({
    document, window,
    setInterval(callback) { tick = callback; return 1; },
    clearInterval() { tick = null; },
    async fetch(path, options) {
      calls.push({ path, payload: JSON.parse(options.body) });
      if (response instanceof Error) throw response;
      return await response;
    }
  });
  vm.runInContext(source, context);
  return {
    context, element, calls, resets,
    load() { context.onOAuthTurnstileLoad(); return callbacks; },
    advanceResendWindow() { for (let second = 0; second < 60; second++) tick(); }
  };
}

test('enabled blocks clicks and Enter until the widget issues a token', async () => {
  const p = page();
  assert.equal(p.element('send-code').disabled, true);
  await p.element('send-code').listeners.click();
  await p.element('email').listeners.keydown({ key: 'Enter' });
  assert.equal(p.calls.length, 0);
  const widget = p.load();
  assert.equal(widget.sitekey, 'public-key');
  assert.equal(widget.language, 'pt-br');
  assert.equal(widget.size, 'flexible');
  widget.callback('first-token');
  assert.equal(p.element('send-code').disabled, false);
  await p.element('send-code').listeners.click();
  assert.deepEqual(p.calls, [{
    path: '/oauth/send-code',
    payload: { email: 'person@example.com', language: 'pt-BR', turnstileToken: 'first-token' }
  }]);
  assert.deepEqual(p.resets, ['widget-id']);
  assert.equal(p.element('step-email').classList.contains('hidden'), true);
  assert.equal(p.element('step-code').classList.contains('hidden'), false);
  assert.equal(p.element('resend').disabled, true);
});

test('resend sends a fresh token after the actual cooldown and cannot reuse it', async () => {
  const p = page();
  const widget = p.load();
  widget.callback('first-token');
  await p.element('send-code').listeners.click();
  p.advanceResendWindow();
  assert.equal(p.element('resend').classList.contains('hidden'), false);
  await p.element('resend').listeners.click();
  assert.equal(p.calls.length, 1);
  widget.callback('resend-token');
  assert.equal(p.element('resend').disabled, false);
  await p.element('resend').listeners.click();
  assert.equal(p.calls[1].payload.turnstileToken, 'resend-token');
  assert.deepEqual(p.resets, ['widget-id', 'widget-id']);
  await p.element('resend').listeners.click();
  assert.equal(p.calls.length, 2);
});

for (const callback of ['expired-callback', 'timeout-callback', 'error-callback']) {
  test(`${callback} invalidates the token for buttons and Enter`, async () => {
    const p = page();
    const widget = p.load();
    widget.callback('token');
    widget[callback]();
    assert.equal(p.element('send-code').disabled, true);
    assert.equal(p.element('resend').disabled, true);
    await p.element('email').listeners.keydown({ key: 'Enter' });
    assert.equal(p.calls.length, 0);
    widget.callback('recovered-token');
    await p.element('send-code').listeners.click();
    assert.equal(p.calls[0].payload.turnstileToken, 'recovered-token');
  });
}

test('script load failure leaves email delivery blocked and reports the existing failure copy', async () => {
  const p = page();
  p.context.onOAuthTurnstileError();
  assert.equal(p.element('error').textContent, 'Send failed');
  assert.equal(p.element('send-code').disabled, true);
  await p.element('send-code').listeners.click();
  assert.equal(p.calls.length, 0);
});

for (const response of [{ ok: false, json: async () => ({ error: 'Rejected' }) }, new Error('Network failure')]) {
  test(`failed send resets the consumed token for retry: ${response instanceof Error ? 'network' : 'server'}`, async () => {
    const p = page({ response });
    const widget = p.load();
    widget.callback('used-token');
    await p.element('send-code').listeners.click();
    assert.equal(p.element('step-email').classList.contains('hidden'), false);
    assert.equal(p.element('send-code').disabled, true);
    assert.deepEqual(p.resets, ['widget-id']);
    widget.callback('retry-token');
    await p.element('send-code').listeners.click();
    assert.equal(p.calls[1].payload.turnstileToken, 'retry-token');
  });
}

test('changing email requires a fresh token and sends the changed address', async () => {
  const p = page();
  const widget = p.load();
  widget.callback('first-token');
  await p.element('send-code').listeners.click();
  p.element('change-email').listeners.click();
  p.element('email').value = 'changed@example.com';
  await p.element('email').listeners.keydown({ key: 'Enter' });
  assert.equal(p.calls.length, 1);
  widget.callback('next-token');
  await p.element('email').listeners.keydown({ key: 'Enter' });
  assert.equal(p.calls[1].payload.email, 'changed@example.com');
});

test('pending send prevents concurrent Enter submissions', async () => {
  let finish;
  const response = new Promise((resolve) => { finish = resolve; });
  const p = page({ response });
  p.load().callback('token');
  const sending = p.element('send-code').listeners.click();
  await p.element('email').listeners.keydown({ key: 'Enter' });
  assert.equal(p.calls.length, 1);
  finish({ ok: true, json: async () => ({}) });
  await sending;
});

test('disabled sends and resends without loading Turnstile', async () => {
  const p = page({ enabled: false });
  assert.equal(p.element('send-code').disabled, false);
  await p.element('send-code').listeners.click();
  p.advanceResendWindow();
  await p.element('resend').listeners.click();
  assert.equal(p.calls.length, 2);
  assert.equal(p.calls[0].payload.turnstileToken, null);
  assert.deepEqual(p.resets, []);
});
