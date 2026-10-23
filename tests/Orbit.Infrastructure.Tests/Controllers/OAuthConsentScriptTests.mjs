import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const page = readFileSync(new URL('../../../src/Orbit.Api/OAuth/OAuthConsentPage.cs', import.meta.url), 'utf8');
const source = page.split('public const string SubmitScript = """\n')[1].split('\n""";')[0];
const markup = page.match(/<form id="consent-form"[^>]*>[\s\S]*?<\/form>/)[0];

for (const access of ['read_only', 'requested', 'cancel']) {
  test(`allows one submission and preserves the ${access} submitter`, () => {
    const attributes = new Map();
    const buttons = [...markup.matchAll(/<button\b([^>]*)>/g)].map((match) => ({
      disabled: false,
      attributes: new Map([...match[1].matchAll(/([\w-]+)="([^"]*)"/g)].map(([, name, value]) => [name, value])),
      setAttribute(name, value) { this.attributes.set(name, value); }
    }));
    let submit;
    const form = {
      setAttribute(name, value) { attributes.set(name, value); },
      querySelectorAll(selector) {
        if (selector === 'button') return buttons;
        assert.equal(selector, 'button[name="access"]');
        return buttons.filter((button) => button.attributes.get('name') === 'access');
      },
      addEventListener(name, callback) {
        assert.equal(name, 'submit');
        submit = callback;
      },
      submit() { assert.fail('submission must stay native to preserve the submitter formaction'); }
    };
    vm.runInNewContext(source, {
      document: { getElementById(id) { assert.equal(id, 'consent-form'); return form; } }
    });
    const submitter = buttons.find((button) => button.attributes.get('value') === access
      || (access === 'cancel' && button.attributes.get('formaction') === '/oauth/consent/cancel'));
    assert.ok(submitter);
    let prevented = 0;
    const event = { submitter, preventDefault() { prevented++; } };

    submit(event);

    assert.equal(prevented, 0);
    if (access === 'cancel') {
      assert.equal(submitter.attributes.get('formaction'), '/oauth/consent/cancel');
      assert.equal(submitter.attributes.has('name'), false);
    } else {
      assert.equal(submitter.attributes.get('name'), 'access');
      assert.equal(submitter.attributes.get('value'), access);
    }
    assert.equal(attributes.get('aria-busy'), 'true');
    for (const button of buttons) {
      assert.equal(button.disabled, false);
      assert.equal(button.attributes.get('aria-disabled'), 'true');
      assert.equal(button.attributes.get('aria-busy'), 'true');
    }

    submit(event);
    submit({ preventDefault() { prevented++; } });
    assert.equal(prevented, 2);
  });
}
