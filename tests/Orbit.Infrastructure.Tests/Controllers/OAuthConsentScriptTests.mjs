import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../../../src/Orbit.Api/OAuth/OAuthConsentPage.cs', import.meta.url), 'utf8')
  .split('public const string SubmitScript = """\n')[1].split('\n""";')[0];

for (const access of ['read_only', 'requested']) {
  test(`allows one submission and preserves the ${access} submitter`, () => {
    const attributes = new Map();
    const buttons = ['read_only', 'requested'].map((value) => ({
      name: 'access', value, disabled: false, attributes: new Map(),
      setAttribute(name, value) { this.attributes.set(name, value); }
    }));
    let submit;
    const form = {
      setAttribute(name, value) { attributes.set(name, value); },
      querySelectorAll(selector) {
        assert.equal(selector, 'button[name="access"]');
        return buttons;
      },
      addEventListener(name, callback) {
        assert.equal(name, 'submit');
        submit = callback;
      }
    };
    vm.runInNewContext(source, {
      document: { getElementById(id) { assert.equal(id, 'consent-form'); return form; } }
    });
    const submitter = buttons.find((button) => button.value === access);
    let prevented = 0;
    const event = { submitter, preventDefault() { prevented++; } };

    submit(event);

    assert.equal(prevented, 0);
    assert.equal(submitter.name, 'access');
    assert.equal(submitter.value, access);
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
