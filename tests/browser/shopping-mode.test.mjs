import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../../src/ISLA/Components/Pages/ShoppingList.razor.js', import.meta.url), 'utf8');
const moduleUrl = 'data:text/javascript;base64,' + Buffer.from(source).toString('base64');
const mode = await import(moduleUrl);

test('mode and selected week survive reloading and exit clears preference', async () => {
    const values = new Map();
    globalThis.localStorage = {
        getItem: key => values.get(key) ?? null,
        setItem: (key, value) => values.set(key, value),
        removeItem: key => values.delete(key)
    };
    assert.equal(mode.readMode(), null);
    assert.equal(mode.saveMode(true, '2026-10-26'), true);
    const reopened = await import(moduleUrl + '#reopened');
    assert.equal(reopened.readMode(), '2026-10-26');
    assert.equal(reopened.saveMode(false, '2026-10-26'), true);
    assert.equal(mode.readMode(), null);
});

test('malformed storage does not prevent the page opening', () => {
    globalThis.localStorage = { getItem: () => '{invalid' };
    assert.equal(mode.readMode(), null);
    globalThis.localStorage = { getItem: () => JSON.stringify({enabled: true, week: 'invalid'}) };
    assert.equal(mode.readMode(), null);
});

test('unavailable storage is handled and reports unsuccessful persistence', () => {
    globalThis.localStorage = {
        getItem() { throw new Error('blocked'); },
        setItem() { throw new Error('blocked'); },
        removeItem() { throw new Error('blocked'); }
    };
    assert.equal(mode.readMode(), null);
    assert.equal(mode.saveMode(true, '2026-10-26'), false);
    assert.equal(mode.saveMode(false, '2026-10-26'), false);
});

test('price drafts survive reopening, preserve blanks, and stay scoped to week and unit', async () => {
    const values = new Map();
    globalThis.localStorage = {
        getItem: key => values.get(key) ?? null,
        setItem: (key, value) => values.set(key, value)
    };
    let handler;
    globalThis.document = {
        addEventListener: (event, callback) => { handler = callback; },
        removeEventListener: (event, callback) => { if (handler === callback) handler = null; }
    };
    globalThis.HTMLInputElement = class {
        constructor(name, unit, value) {
            this.dataset = { week: '2026-10-26', goodName: name, unit };
            this.value = value;
        }
        matches(selector) { return selector === '[data-shopping-price]'; }
    };
    mode.watchPriceInputs();
    handler({ target: new HTMLInputElement('Red pepper', 'item', '2.40') });
    handler({ target: new HTMLInputElement('Red pepper', 'g', '1.25') });
    handler({ target: new HTMLInputElement('Bleach', 'item', '') });
    const reopened = await import(moduleUrl + '#prices-reopened');
    const drafts = reopened.readPriceDrafts('2026-10-26');
    assert.equal(drafts.length, 3);
    assert.equal(drafts.find(x => x.unit === 'g').value, '1.25');
    assert.equal(drafts.find(x => x.name === 'Bleach').value, '');
    assert.deepEqual(reopened.readPriceDrafts('2026-11-02'), []);
    reopened.clearPriceDraft('2026-10-26', 'Red pepper', 'item');
    assert.equal(reopened.readPriceDrafts('2026-10-26').length, 2);
    assert.equal(reopened.readPriceDrafts('2026-10-26').some(x => x.unit === 'g'), true);
    mode.stopWatchingPriceInputs();
    assert.equal(handler, null);
});

test('invalid price draft storage is ignored', () => {
    globalThis.localStorage = { getItem: () => '{bad' };
    assert.deepEqual(mode.readPriceDrafts('2026-10-26'), []);
    globalThis.localStorage = { getItem: () => JSON.stringify([{name: 'Milk', unit: 'ml'}, {name: 'Milk', unit: 'ml', value: '1.50'}]) };
    assert.equal(mode.readPriceDrafts('2026-10-26').length, 1);
});
