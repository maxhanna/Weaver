// calendar-cron-days.test.js
// Unit tests for the day-level cron matcher behind calendar-card PLACEMENT in
// wwwroot/calendar.js (cronDayMatches). A scheduled (cron) calendar card only
// rendered on the exact date it was created for; calBuildDays now ALSO places a
// cron card on every day of the viewed month whose day-level fields
// (day-of-month / month / day-of-week) its schedule fires — so a daily cron
// ("0 9 * * *") appears on every single day of the month, an every-2-days cron
// ("0 9 */2 * *") on every second day, and a weekday cron ("0 9 * * 1") on
// every Monday. The fire TIME is rendered separately (card.time / next-fire
// hint), so the day-level matcher ignores the minute and hour fields.
//
// The helper is extracted from the live source (meeting-ticker/board-heal
// pattern); marker asserts fail loudly if the format drifts.
// Dependency-free Node test runner:  node tests/js/calendar-cron-days.test.js
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

// ── Extract matchField + cronDayMatches from the live calendar.js ──────────
const calSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/calendar.js'), 'utf8').replace(/\r\n/g, '\n');
const fieldMatch = /function matchField\(field, val\) \{[\s\S]*?\n  \}/.exec(calSrc);
assert(fieldMatch, 'matchField not found in wwwroot/calendar.js — marker format may have drifted');
const dayMatch = /function cronDayMatches\(expr, dt\) \{[\s\S]*?\n  \}/.exec(calSrc);
assert(dayMatch, 'cronDayMatches not found in wwwroot/calendar.js — marker format may have drifted');
const cronDayMatches = eval('(function () { ' + fieldMatch[0] + '\n' + dayMatch[0] + '\n return cronDayMatches; })()');

// Aug 1 2026 is a Saturday; Aug 3 is a Monday.
const aug1 = new Date(2026, 7, 1);
const aug2 = new Date(2026, 7, 2);
const aug3 = new Date(2026, 7, 3);   // Monday
const aug4 = new Date(2026, 7, 4);   // Tuesday
const aug15 = new Date(2026, 7, 15); // Saturday
const aug31 = new Date(2026, 7, 31); // Monday
const sep2 = new Date(2026, 8, 2);

test('daily cron matches every day', () => {
  assert.strictEqual(cronDayMatches('0 9 * * *', aug1), true);
  assert.strictEqual(cronDayMatches('0 9 * * *', aug3), true);
  assert.strictEqual(cronDayMatches('0 9 * * *', aug31), true);
  assert.strictEqual(cronDayMatches('0 9 * * *', sep2), true);
});

test('*/N day-of-month matches every Nth day (every-2-days)', () => {
  assert.strictEqual(cronDayMatches('0 9 */2 * *', aug2), true);
  assert.strictEqual(cronDayMatches('0 9 */2 * *', aug4), true);
  assert.strictEqual(cronDayMatches('0 9 */2 * *', aug1), false);
  assert.strictEqual(cronDayMatches('0 9 */2 * *', aug3), false);
});

test('day-of-week field matches only that weekday (Mondays)', () => {
  assert.strictEqual(cronDayMatches('0 9 * * 1', aug3), true);
  assert.strictEqual(cronDayMatches('0 9 * * 1', aug31), true);
  assert.strictEqual(cronDayMatches('0 9 * * 1', aug4), false);
  assert.strictEqual(cronDayMatches('0 9 * * 1', aug15), false); // Saturday
});

test('exact day-of-month and comma lists', () => {
  assert.strictEqual(cronDayMatches('0 9 1,15 * *', aug1), true);
  assert.strictEqual(cronDayMatches('0 9 1,15 * *', aug15), true);
  assert.strictEqual(cronDayMatches('0 9 1,15 * *', aug3), false);
});

test('month field is respected', () => {
  assert.strictEqual(cronDayMatches('0 9 * 8 *', aug15), true);  // August
  assert.strictEqual(cronDayMatches('0 9 * 8 *', sep2), false);  // September
});

test('AND semantics across day fields (mirrors the scheduler)', () => {
  // 15th AND Monday — Aug 15 2026 is a Saturday, so no match.
  assert.strictEqual(cronDayMatches('0 9 15 * 1', aug15), false);
  // 3rd AND Monday — Aug 3 2026 is a Monday, so match.
  assert.strictEqual(cronDayMatches('0 9 3 * 1', aug3), true);
});

test('malformed expressions never match or throw', () => {
  for (const bad of ['', '0 9 * *', '0 9 * * * *', 'not a cron', '*/2 * *', null, undefined, 42]) {
    assert.strictEqual(cronDayMatches(bad, aug1), false);
  }
});

test('the minute/hour fields are ignored for day placement', () => {
  // A 9am cron still marks the day regardless of what time the Date carries.
  const noon = new Date(2026, 7, 3, 12, 30);
  assert.strictEqual(cronDayMatches('0 9 * * 1', noon), true);
});

if (failed > 0) {
  console.error(`\n${failed} test(s) failed, ${passed} passed`);
  process.exit(1);
}
console.log(`\n${passed} passed / 0 failed`);
