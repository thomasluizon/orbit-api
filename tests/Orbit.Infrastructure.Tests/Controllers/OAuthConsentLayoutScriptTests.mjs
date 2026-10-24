import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const page = readFileSync(new URL('../../../src/Orbit.Api/OAuth/OAuthConsentPage.cs', import.meta.url), 'utf8');
const source = page.split('public const string ActionLayoutScript = """\n')[1].split('\n""";')[0];
const markup = page.match(/<div class="action-row">[\s\S]*?<\/div>/)[0];

function mount(widths, available) {
  let layout;
  let resized;
  let fontsLoaded;
  const buttons = [...markup.matchAll(/<button\b[^>]*>/g)].map((_, index) => ({
    getBoundingClientRect() {
      assert.equal(layout, 'row');
      return { width: widths[index] };
    }
  }));
  assert.equal(buttons.length, widths.length);
  const row = {
    setAttribute(name, value) { assert.equal(name, 'data-layout'); layout = value; },
    querySelectorAll(selector) { assert.equal(selector, 'button'); return buttons; },
    getBoundingClientRect() { return { width: available }; }
  };
  const form = {
    querySelector(selector) { assert.equal(selector, '.action-row'); return row; }
  };
  const ready = new Promise((resolve) => { fontsLoaded = resolve; });
  vm.runInNewContext(source, {
    document: {
      getElementById(id) { assert.equal(id, 'consent-form'); return form; },
      fonts: { ready }
    },
    getComputedStyle(element) { assert.equal(element, row); return { columnGap: '12px' }; },
    ResizeObserver: class {
      constructor(callback) { resized = callback; }
      observe(element) { assert.equal(element, form); }
    }
  });
  return {
    get layout() { return layout; },
    resize(width) { available = width; resized(); },
    async loadFonts(nextWidths) { widths = nextWidths; fontsLoaded(); await ready; }
  };
}

for (const [language, widths, expected] of [
  ['en', [81, 90, 125], 'row'],
  ['pt-BR', [95, 122, 133], 'stack']
]) {
  test(`${language} uses one row only when all buttons and both gaps fit`, () => {
    const view = mount(widths, 356);
    assert.equal(view.layout, expected);
    const required = widths.reduce((sum, width) => sum + width, 24);
    view.resize(required);
    assert.equal(view.layout, 'row');
    view.resize(required - 0.25);
    assert.equal(view.layout, 'stack');
    view.resize(required + 0.25);
    assert.equal(view.layout, 'row');
    view.resize(288);
    assert.equal(view.layout, 'stack');
    view.resize(356);
    assert.equal(view.layout, expected);
  });
}

test('rechecks the intrinsic button widths when fonts finish loading', async () => {
  const view = mount([81, 90, 125], 356);
  assert.equal(view.layout, 'row');
  await view.loadFonts([95, 122, 133]);
  assert.equal(view.layout, 'stack');
  view.resize(400);
  assert.equal(view.layout, 'row');
});
