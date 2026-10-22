// calendar-idempotency.test.js
// Unit tests for the calendar live-instance idempotency guard in
// wwwroot/calendar.js (hasLiveCalendarInstance). A scheduled (cron) calendar
// card re-fires on every matching window and each fire pushes a FRESH To Do
// card (new uid) and starts it — so stopping a running calendar card did NOT
// stop the schedule: the next window spawned a look-alike duplicate that
// auto-started. The guard suppresses a fire while the same calendar card (same
// text + same schedule key) still has a live instance in To Do/Doing, so a
// stopped card never gets duplicated; the schedule resumes once the instance
// leaves the board.
//
// The helper is extracted from the live source (meeting-ticker/board-heal
// pattern); a marker assert fails loudly if the format drifts.
// Dependency-free Node test runner:  node tests/js/calendar-idempotency.test.js
'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');

let passed = 0;
let failed = 0;

function test(name, fn) {
  try {
    fn();
    passed++;
    console.log('  ✓ ' + name);
  } catch (e) {
    failed++;
    console.error('  ✗ ' + name);
    console.error('      ' + (e && e.message));
  }
}

// ── Extract hasLiveCalendarInstance from the live calendar.js ────────────
const calSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/calendar.js'), 'utf8').replace(/\r\n/g, '\n');
const guardMatch = /function hasLiveCalendarInstance\(boardState, cal\) \{[\s\S]*?\n  \}/.exec(calSrc);
assert(guardMatch, 'hasLiveCalendarInstance not found in wwwroot/calendar.js — marker format may have drifted');
const hasLive = eval('(function () { ' + guardMatch[0] + '\n return hasLiveCalendarInstance; })()');

// Board cards carry _cronExpression; the checked calendar card carries
// cronExpression — the guard reads cal.cronExpression and board cards' _cronExpression.
function cronCard(text, cronKey) {
  const key = cronKey || '';
  return { _fromCron: true, text: text, _cronExpression: key, cronExpression: key };
}

test('same cron card with live instance in todo suppresses the fire', () => {
  const state = { todo: [cronCard('Daily post', '0 9 * * 1-5')], doing: [] };
  assert.strictEqual(hasLive(state, cronCard('Daily post', '0 9 * * 1-5')), true);
});

test('same cron card with live instance in doing suppresses the fire', () => {
  const state = { todo: [], doing: [cronCard('Daily post', '0 9 * * 1-5')] };
  assert.strictEqual(hasLive(state, cronCard('Daily post', '0 9 * * 1-5')), true);
});

test('different task on the same schedule does NOT suppress (no false positive)', () => {
  const state = { todo: [cronCard('Standup reminder', '0 9 * * 1-5')], doing: [] };
  assert.strictEqual(hasLive(state, cronCard('Daily post', '0 9 * * 1-5')), false);
});

test('same task on a different schedule does NOT suppress (no false positive)', () => {
  const state = { todo: [cronCard('Daily post', '0 9 * * 1-5')], doing: [] };
  assert.strictEqual(hasLive(state, cronCard('Daily post', '*/30 * * * *')), false);
});

test('no live instance anywhere allows the fire', () => {
  const state = { todo: [], doing: [] };
  assert.strictEqual(hasLive(state, cronCard('Daily post', '0 9 * * 1-5')), false);
});

test('an instance already moved to Done does NOT suppress (schedule resumes)', () => {
  const state = { todo: [], doing: [], done: [cronCard('Daily post', '0 9 * * 1-5')] };
  assert.strictEqual(hasLive(state, cronCard('Daily post', '0 9 * * 1-5')), false);
});

test('one-off card (no schedule key) suppresses on matching text', () => {
  const state = { todo: [cronCard('Send report', '')], doing: [] };
  assert.strictEqual(hasLive(state, cronCard('Send report', '')), true);
});

test('one-off card with different text is allowed', () => {
  const state = { todo: [cronCard('Send report', '')], doing: [] };
  assert.strictEqual(hasLive(state, cronCard('Buy milk', '')), false);
});

test('non-calendar board cards (no _fromCron) never suppress', () => {
  const state = { todo: [{ text: 'Daily post', ready: true }], doing: [] };
  assert.strictEqual(hasLive(state, cronCard('Daily post', '0 9 * * 1-5')), false);
});

test('blank task text never suppresses', () => {
  const state = { todo: [cronCard('x', '0 9 * * 1-5')], doing: [] };
  assert.strictEqual(hasLive(state, { _fromCron: true, text: '', _cronExpression: '0 9 * * 1-5' }), false);
});

test('missing board state never suppresses', () => {
  assert.strictEqual(hasLive(null, cronCard('Daily post', '0 9 * * 1-5')), false);
});

console.log('\ncalendar-idempotency: ' + passed + ' passed, ' + failed + ' failed');
process.exit(failed > 0 ? 1 : 0);
