// calendar-scheduled-popup.test.js
// Unit tests for the calendar day-header ⏱ chip → Scheduled Events popup in
// wwwroot/calendar.js: clicking the chip opens the day's next-fire list, and
// each row's Fire now / Edit actions work. Also covers the shared fire path
// (buildCronFireCard + vm.fireCalCard) that "Run once now", "Requeue now" and
// the popup's Fire button all route through, so the pushed To Do card always
// carries the _fromCron flags, queued-when-busy behavior and audit entry.
//
// The handlers are extracted from the live source (same pattern as
// calendar-cron-chips.test.js); marker asserts fail loudly if the format
// drifts. Dependency-free Node test runner:  node tests/js/calendar-scheduled-popup.test.js
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

// ── Extract the popup handlers + shared fire path from the live calendar.js ─
const calSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/calendar.js'), 'utf8').replace(/\r\n/g, '\n');
function grab(re, label) {
  const m = re.exec(calSrc);
  assert(m, label + ' not found in wwwroot/calendar.js — marker format may have drifted');
  return m[0];
}

const fireBuilder = grab(/      function buildCronFireCard\(calCard\) \{\n[\s\S]*?\n      \}/, 'buildCronFireCard');
const fireCalCard = grab(/      vm\.fireCalCard = function \(calCard, summary\) \{\n[\s\S]*?\n      \};/, 'vm.fireCalCard');
const openScheduled = grab(/      vm\.calOpenScheduled = function \(day, \$event\) \{\n[\s\S]*?\n      \};/, 'calOpenScheduled');
const closeScheduled = grab(/      vm\.calCloseScheduled = function \(event\) \{\n[\s\S]*?\n      \};/, 'calCloseScheduled');
const fireScheduled = grab(/      vm\.calFireScheduled = function \(entry, \$event\) \{\n[\s\S]*?\n      \};/, 'calFireScheduled');
const editScheduled = grab(/      vm\.calEditScheduled = function \(entry, \$event\) \{\n[\s\S]*?\n      \};/, 'calEditScheduled');

// Dependencies the extracted code closes over.
const uidFn = grab(/  function uid\(\) \{ return Math\.random\(\)\.toString\(36\)\.slice\(2, 9\); \}/, 'uid');
const cronRunLogEnsure = grab(/  function cronRunLogEnsure\(vm\) \{\n[\s\S]*?\n  \}/, 'cronRunLogEnsure');
const cronRunLogKey = grab(/  function cronRunLogKey\(calCard\) \{\n[\s\S]*?\n  \}/, 'cronRunLogKey');
const cronRunLogAdd = grab(/  function cronRunLogAdd\(vm, key, entry\) \{\n[\s\S]*?\n  \}/, 'cronRunLogAdd');
const localDateStr = grab(/      function localDateStr\(date\) \{\n[\s\S]*?\n      \}/, 'localDateStr');
const pad2 = grab(/      function pad2\(n\) \{ return String\(n\)\.padStart\(2, '0'\); \}/, 'pad2');
const timeStr = grab(/      function timeStr\(d\) \{ return d \? pad2\(d\.getHours\(\)\) \+ ':' \+ pad2\(d\.getMinutes\(\)\) : ''; \}/, 'timeStr');
const dateToLocal = grab(/      function dateToLocal\(date\) \{\n[\s\S]*?\n      \}/, 'dateToLocal');

// Eval everything in one scope (like the cron-chips test): the handlers are
// assigned onto the closure's `vm`, so freshVm() rebinding vm/_vm gives each
// test a clean controller + board state exactly as init() would.
const harness = eval('(function () {\n' +
  'var vm = {}; var _vm = {}; var _scope = null;\n' +
  'var scheduleUpdates = 0;\n' +
  'function scheduleUpdate() { scheduleUpdates++; }\n' +
  uidFn + '\n' +
  cronRunLogEnsure + '\n' + cronRunLogKey + '\n' + cronRunLogAdd + '\n' +
  localDateStr + '\n' + pad2 + '\n' + timeStr + '\n' + dateToLocal + '\n' +
  fireBuilder + '\n' + fireCalCard + '\n' +
  openScheduled + '\n' + closeScheduled + '\n' + fireScheduled + '\n' + editScheduled + '\n' +
  'return {\n' +  'freshVm: function (opts) {\n' +
  // Handlers live on the closure's vm object (assigned at eval time); reset
  // its per-test state instead of replacing it. calEditCard closes over the
  // fresh `calls` so it is rebound per test, like production's init().\n' +
  '    var calls = { saveCards: 0, toast: [], executed: null, edited: null };\n' +
  '    _vm = { state: { todo: [] }, streamingActive: false, selectedProject: "proj-x",\n' +
  '      saveCards: function () { calls.saveCards++; },\n' +
  '      showSideToast: function (t) { calls.toast.push(t); },\n' +
  '      executeAgent: function (c) { calls.executed = c; } };\n' +
  '    vm.calScheduledDay = null;\n' +
  '    vm.calEditCard = function (c) { calls.edited = c; };\n' +
  '    scheduleUpdates = 0;\n' +
  '    if (opts && opts.busy) _vm.streamingActive = true;\n' +
  '    return { vm: vm, _vm: _vm, calls: calls };\n' +
  '  },\n' +
  '  build: function (c) { return buildCronFireCard(c); }\n' +
  '}; })()');

function stopEv() { return { stopPropagation: function () {} }; }

test('Fire now pushes a To Do card with _fromCron flags + audit entry', () => {
  const h = harness.freshVm();
  const card = { id: 'c1', text: 'Morning standup', date: '2026-08-12', time: '09:00', cronExpression: '0 9 * * *', label: 'Daily', priority: 'high', filePath: '/repo' };
  const pushed = h.vm.fireCalCard(card, 'Fired manually (scheduled list) — card pushed to To Do.');
  assert.strictEqual(h._vm.state.todo.length, 1);
  const t = h._vm.state.todo[0];
  assert.strictEqual(t, pushed);
  assert.strictEqual(t._fromCron, true);
  assert.strictEqual(t._cronExpression, '0 9 * * *');
  assert.strictEqual(t._cronSourceId, 'c1');
  assert.strictEqual(t._cronLabel, 'Daily');
  assert.strictEqual(t.priority, 'high');
  assert.strictEqual(t.filePath, '/repo');
  assert.strictEqual(t.text, 'Morning standup');
  assert.strictEqual(t._endpointQueued, undefined, 'idle endpoint must NOT queue the fire');
  assert.ok(t.id, 'pushed card gets a fresh id');
  // Audit entry recorded against the calendar card (key id:c1).
  const log = h._vm.state._cronRunLog;
  assert.strictEqual(log.length, 1);
  assert.strictEqual(log[0].key, 'id:c1');
  assert.strictEqual(log[0].outcome, 'ran');
  assert.strictEqual(log[0].cardId, t.id);
  assert.ok(String(log[0].summary).indexOf('scheduled list') !== -1);
});

test('Busy endpoint parks the fire as _endpointQueued and does not auto-start', () => {
  const h = harness.freshVm({ busy: true });
  const card = { id: 'c2', text: 'Standup', date: '2026-08-12', time: '09:00', cronExpression: '0 9 * * *' };
  h.vm.fireCalCard(card, 's');
  const t = h._vm.state.todo[0];
  assert.strictEqual(t._endpointQueued, true, 'busy endpoint queues the fire');
  assert.strictEqual(h.calls.executed, null, 'no auto-start while streaming');
  assert.strictEqual(h.calls.toast.length, 0);
});

test('buildCronFireCard accepts Date objects (add/edit form model)', () => {
  const h = harness.freshVm();
  const formCard = { id: null, text: 'T', date: new Date(2026, 7, 12), time: new Date(2000, 0, 1, 11, 25), cronExpression: '', priority: 'low' };
  const out = harness.build(formCard);
  assert.strictEqual(out._cronExpression, '2026-08-12 11:25', 'Date objects serialize to the string model locally');
  assert.strictEqual(out._cronSourceId, null);
  assert.strictEqual(out.priority, 'low');
  assert.strictEqual(out.filePath, 'proj-x', 'falls back to the selected project');
});

test('Clicking the ⏱ chip opens the day scheduled list (stopPropagation + state)', () => {
  const h = harness.freshVm();
  const day = { date: '2026-08-12', nextFires: [{ card: { id: 'c1' }, fire: new Date(2026, 7, 12, 9, 0) }] };
  let stopped = false;
  h.vm.calOpenScheduled(day, { stopPropagation: function () { stopped = true; } });
  assert.strictEqual(stopped, true);
  assert.strictEqual(h.vm.calScheduledDay, day);
  h.vm.calCloseScheduled(stopEv());
  assert.strictEqual(h.vm.calScheduledDay, null);
});

test('Popup Fire now fires the entry, closes the list and auto-starts when idle', () => {
  const h = harness.freshVm();
  const card = { id: 'c3', text: 'Ship it', date: '2026-08-12', time: '10:00', cronExpression: '0 10 * * *' };
  const entry = { card: card, fire: new Date(2026, 7, 12, 10, 0) };
  h.vm.calOpenScheduled({ date: '2026-08-12', nextFires: [entry] }, null);
  h.vm.calFireScheduled(entry, stopEv());
  assert.strictEqual(h.vm.calScheduledDay, null, 'popup closes after firing');
  assert.strictEqual(h._vm.state.todo.length, 1);
  assert.strictEqual(h.calls.executed, h._vm.state.todo[0], 'auto-starts the pushed card');
  assert.strictEqual(h.calls.toast.length, 1);
  assert.strictEqual(h._vm.state._cronRunLog[0].key, 'id:c3');
});

test('Popup Edit closes the list and opens the calendar card in the editor', () => {
  const h = harness.freshVm();
  const card = { id: 'c4', text: 'Edit me', date: '2026-08-13', time: '09:00' };
  const entry = { card: card, fire: new Date(2026, 7, 13, 9, 0) };
  h.vm.calOpenScheduled({ date: '2026-08-13', nextFires: [entry] }, null);
  h.vm.calEditScheduled(entry, stopEv());
  assert.strictEqual(h.vm.calScheduledDay, null, 'popup closes before opening the editor');
  assert.strictEqual(h.calls.edited, card, 'editor opens with the entry\'s card');
});

if (failed > 0) {
  console.error('\n' + failed + ' test(s) failed');
  process.exit(1);
}
console.log('\n' + passed + ' tests passed');
