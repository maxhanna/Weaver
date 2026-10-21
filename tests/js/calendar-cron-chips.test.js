// calendar-cron-chips.test.js
// Unit tests for the calendar add/edit popup's cron PRESET CHIPS in
// wwwroot/calendar.js (setCronExpression). A user clicking a chip under
// "Daily" / "Weekdays (Mon–Fri)" / "Weekend" / "Specific day" / "Monthly" /
// "Yearly" expects a RECURRING schedule — "9am" must install "0 9 * * *", not
// a one-off fire on the card's date. setCronExpression is what makes that
// true, and together with cronDayMatches (see calendar-cron-days.test.js) a
// daily recurring entry renders a row on EVERY day of the viewed month.
//
// The helpers are extracted from the live source (same pattern as
// calendar-cron-days.test.js); marker asserts fail loudly if the format drifts.
// Dependency-free Node test runner:  node tests/js/calendar-cron-chips.test.js
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

// ── Extract the chip handler + its helpers from the live calendar.js ───────
const calSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/calendar.js'), 'utf8').replace(/\r\n/g, '\n');
const presetMatch = /function cronPresetTime\(expr\) \{\n[\s\S]*?\n      \}/.exec(calSrc);
assert(presetMatch, 'cronPresetTime not found in wwwroot/calendar.js — marker format may have drifted');
const timeMatch = /function timeToDate\(time\) \{\n[\s\S]*?\n      \}/.exec(calSrc);
assert(timeMatch, 'timeToDate not found in wwwroot/calendar.js — marker format may have drifted');
const chipMatch = /vm\.setCronExpression = function \(expr\) \{\n[\s\S]*?\n      \};/.exec(calSrc);
assert(chipMatch, 'setCronExpression not found in wwwroot/calendar.js — marker format may have drifted');

// setCronExpression references `vm` as a closure variable (it's assigned onto
// the controller) and calls module-scope helpers — eval everything in one
// scope and return a wrapper that binds the mock vm for each call.
const applyChip = eval('(function () { var vm;\n' +
  presetMatch[0] + '\n' +
  timeMatch[0] + '\n' +
  chipMatch[0].replace('vm.setCronExpression = function (expr)', 'function setCronExpression(expr)') + '\n' +
  'return function (expr, card) { vm = { calEditCardData: card || freshCard(true) }; setCronExpression(expr); return vm.calEditCardData; }; })()');

// Also pull cronDayMatches + matchField (as calendar-cron-days.test.js does)
// to prove the full story: chip → recurring cron → every day of the month.
const dayMatch = /function cronDayMatches\(expr, dt\) \{\n[\s\S]*?\n  \}/.exec(calSrc);
assert(dayMatch, 'cronDayMatches not found in wwwroot/calendar.js — marker format may have drifted');
const fieldMatch = /function matchField\(field, val\) \{\n[\s\S]*?\n  \}/.exec(calSrc);
assert(fieldMatch, 'matchField not found in wwwroot/calendar.js — marker format may have drifted');
const cronDayMatches = eval('(function () { ' + fieldMatch[0] + '\n' + dayMatch[0] + '\n return cronDayMatches; })()');

function freshCard(hasDate) {
  return {
    id: null,
    date: hasDate ? new Date(2026, 7, 12) : null,
    time: new Date(2000, 0, 1, 11, 25),
    text: 'Morning standup',
    cronExpression: '',
    label: ''
  };
}
function timeStr(d) {
  return d ? String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0') : null;
}

test('Daily "9am" chip installs a recurring daily cron (not a one-off)', () => {
  const d = applyChip('0 9 * * *');
  assert.strictEqual(d.cronExpression, '0 9 * * *');
  assert.strictEqual(timeStr(d.time), '09:00');
});

test('Daily "Midnight" chip installs "0 0 * * *" and syncs the time', () => {
  const d = applyChip('0 0 * * *');
  assert.strictEqual(d.cronExpression, '0 0 * * *');
  assert.strictEqual(timeStr(d.time), '00:00');
});

test('Weekday / specific-day / monthly / yearly chips install their schedules', () => {
  assert.strictEqual(applyChip('0 9 * * 1-5').cronExpression, '0 9 * * 1-5');
  assert.strictEqual(applyChip('0 10 * * 1').cronExpression, '0 10 * * 1');
  assert.strictEqual(applyChip('0 18 15 * *').cronExpression, '0 18 15 * *');
  assert.strictEqual(applyChip('0 0 1 1 *').cronExpression, '0 0 1 1 *');
});

test('interval chips (no plain time) keep the schedule and leave the time alone', () => {
  const d = applyChip('*/30 * * * 1-5');
  assert.strictEqual(d.cronExpression, '*/30 * * * 1-5');
  assert.strictEqual(timeStr(d.time), '11:25'); // untouched
});

test('"No schedule" clears the cron (card becomes a one-off)', () => {
  const d = applyChip('');
  assert.strictEqual(d.cronExpression, '');
});

test('a second chip click replaces the previously installed cron', () => {
  const card = freshCard(true);
  applyChip('0 9 * * *', card);
  applyChip('0 18 * * *', card);
  assert.strictEqual(card.cronExpression, '0 18 * * *');
  assert.strictEqual(timeStr(card.time), '18:00');
});

test('chips work even when the Date field is empty (recurring needs no anchor)', () => {
  const d = applyChip('0 9 * * *', freshCard(false));
  assert.strictEqual(d.cronExpression, '0 9 * * *');
});

test('a daily chip + cronDayMatches = a row on EVERY day of the month', () => {
  // Wire the full user story: click "9am" (Daily) → recurring cron → the
  // calendar's day matcher places the card on all 31 days of August 2026.
  const d = applyChip('0 9 * * *');
  assert.strictEqual(d.cronExpression, '0 9 * * *');
  for (let day = 1; day <= 31; day++) {
    const dt = new Date(2026, 7, day); // August 2026
    assert.strictEqual(cronDayMatches(d.cronExpression, dt), true, 'Aug ' + day + ' should match a daily cron');
  }
  // Sanity: a weekdays-only schedule does NOT land on weekends.
  const wd = applyChip('0 9 * * 1-5');
  assert.strictEqual(cronDayMatches(wd.cronExpression, new Date(2026, 7, 1)), false); // Aug 1 = Saturday
  assert.strictEqual(cronDayMatches(wd.cronExpression, new Date(2026, 7, 3)), true);  // Aug 3 = Monday
});

if (failed > 0) {
  console.error(`\n${failed} test(s) failed, ${passed} passed`);
  process.exit(1);
}
console.log(`\n${passed} passed / 0 failed`);
