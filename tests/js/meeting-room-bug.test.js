// meeting-room-bug.test.js
// Unit tests for wwwroot/meeting.js's "room bug" scene: when an edit enters its
// deterministic resolution pipeline (edit-resolve SSE / failing edit steps) a
// literal bug is let loose in the meeting room and the other spiders stomp it
// while the editor keeps working; landing the edit squashes it for good.
// Like the other meeting tests, the live source blocks are extracted and evaled
// against stubs, so regressions (bug no longer spawns, no longer wanders, no
// longer gets stomped) break this suite.
// Dependency-free Node test runner:  node tests/js/meeting-room-bug.test.js
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

const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/meeting.js'), 'utf8').replace(/\r\n/g, '\n');

function extract(name) {
  const m = new RegExp('function ' + name + '\\(([^)]*)\\) \\{\\n([\\s\\S]*?)\\n        \\}').exec(src);
  assert(m, name + ' block marker not found — meeting.js formatting may have drifted');
  const params = m[1].split(',').map(s => s.trim()).filter(Boolean);
  return { params: params, body: m[2] };
}

const wrap = (name, extraParams) => {
  const e = extract(name);
  return new Function(...e.params.concat(extraParams || []), e.body);
};

const bugSizeForFailStreak = wrap('bugSizeForFailStreak');
const bugSpawnState = wrap('bugSpawnState');
const roomBugWander = wrap('roomBugWander');
const bugTakeStomp = wrap('bugTakeStomp');
const bugSceneBlocked = wrap('bugSceneBlocked');
const updateRoomBug = wrap('updateRoomBug');
const spawnRoomBug = wrap('spawnRoomBug');
const squishRoomBug = wrap('squishRoomBug');

function crawler(overrides) {
  const b = {
    x: 0.5, y: 0.6, vx: 0.1, vy: 0,
    crawlT: 0, turnT: 5, hp: 3, stomps: 0, phase: 'crawl',
    squishT: 0, dodgeT: 0, squashCd: 0, stomper: null, size: 1, attempt: 0
  };
  return Object.assign(b, overrides || {});
}

const cleanScene = () => ({
  gossip: null, watching: null, standoff: null, coolerTrip: null, glare: null,
  editorMeltdown: null, shoutMatch: null, verifierVictory: null, gripeSession: null,
  writer: null, queue: []
});

const withStub = (key, value, fn) => {
  const prev = globalThis[key];
  globalThis[key] = value;
  try { fn(); } finally { globalThis[key] = prev; }
};

const withScene = (scene, fn) => withStub('scene', scene, fn);

console.log('meeting room bug tests\n');

// ── Bug size vs. edit-failure streak ────────────────────────────────────────
test('bug size grows with the edit-failure streak, capped at +8', function () {
  assert.strictEqual(bugSizeForFailStreak(0), 1);
  assert.strictEqual(bugSizeForFailStreak(2), 5);
  assert.strictEqual(bugSizeForFailStreak(4), 9, 'cap of 1 + 8');
  assert.strictEqual(bugSizeForFailStreak(10), 9, 'streak 10 still capped');
  assert.strictEqual(bugSizeForFailStreak(undefined), 1, 'no streak defaults to base');
});

// ── Spawn state ─────────────────────────────────────────────────────────────
test('bugSpawnState creates a crawling 3-hp bug in the floor band', function () {
  withStub('Math', Object.assign(Object.create(Math), { random: () => 0.5 }), () => {
    const b = bugSpawnState(2);
    assert.strictEqual(b.phase, 'crawl');
    assert.strictEqual(b.hp, 3);
    assert.strictEqual(b.squashCd, 1.2);
    assert.strictEqual(b.attempt, 2);
    assert.strictEqual(b.x, 0.5, 'x = 0.3 + 0.5*0.4');
    assert.strictEqual(b.y, 0.675, 'y = 0.55 + 0.5*0.25');
  });
});

// ── Wandering ───────────────────────────────────────────────────────────────
test('roomBugWander moves the bug by its velocity', function () {
  const b = crawler({ vx: 0.1, vy: 0, turnT: 5 });
  roomBugWander(b, 1, 1, 0.5, () => 0.5);
  assert.strictEqual(b.x, 0.55);
  assert.strictEqual(b.y, 0.6);
});

test('roomBugWander bounces the bug back inside the floor bounds', function () {
  const b = crawler({ x: 0.05, vx: -0.05, y: 0.9, vy: 0.05, turnT: 5 });
  roomBugWander(b, 1, 1, 0.5, () => 0.5);
  assert.ok(b.x >= 0.06, 'clamped to minX, got ' + b.x);
  assert.ok(b.vx > 0, 'velocity flipped outward');
  assert.ok(b.y <= 0.88, 'clamped to maxY, got ' + b.y);
  assert.ok(b.vy < 0, 'velocity flipped upward');
});

test('roomBugWander picks a new heading when its turn timer expires', function () {
  const b = crawler({ turnT: 0 });
  roomBugWander(b, 1, 1, 0.1, () => 0.5);
  assert.strictEqual(b.vx, 0, '(0.5-0.5)*0.14');
  assert.strictEqual(b.vy, 0);
  assert.ok(Math.abs(b.turnT - 1.9) < 1e-9, '0.8 + 0.5*2.2 within float tolerance, got ' + b.turnT);
});

test('roomBugWander ignores a squished bug', function () {
  const b = crawler({ phase: 'squished', x: 0.5, vx: 0.1 });
  roomBugWander(b, 1, 1, 1, () => 0.5);
  assert.strictEqual(b.x, 0.5, 'squished bug does not move');
});

// ── Stomping ────────────────────────────────────────────────────────────────
test('bugTakeStomp decrements hp, dodges, and counts the stomp', function () {
  const b = crawler({ x: 0.5, y: 0.6, hp: 3 });
  bugTakeStomp(b, () => 0);
  assert.strictEqual(b.hp, 2);
  assert.strictEqual(b.stomps, 1);
  assert.strictEqual(b.dodgeT, 0.5);
  assert.ok(b.x < 0.5, 'bug dodges sideways');
});

test('bugTakeStomp squishes the bug on the third stomp', function () {
  const b = crawler({ hp: 1 });
  bugTakeStomp(b, () => 0.5);
  assert.strictEqual(b.hp, 0);
  assert.strictEqual(b.phase, 'squished');
  assert.strictEqual(b.squishT, 0.9);
});

test('bugTakeStomp does nothing to a squished bug', function () {
  const b = crawler({ phase: 'squished', hp: 2 });
  bugTakeStomp(b, () => 0.5);
  assert.strictEqual(b.hp, 2);
});

test('updateRoomBug sends an idle non-editor spider to stomp the bug', function () {
  const b = crawler({ squashCd: 0, x: 0.5, y: 0.6 });
  const spiders = [
    { role: 'editor', state: 'idle' },
    { role: 'reviewer', state: 'idle' },
    { role: 'planner', state: 'walk' }
  ];
  const scene = Object.assign(cleanScene(), { bug: b, spiders: spiders });
  withStub('Math', Object.assign(Object.create(Math), { random: () => 0.5 }), () => {
    withStub('roomBugWander', (x) => x, () => {
      withStub('bugSceneBlocked', () => false, () => {
        withScene(scene, () => {
          updateRoomBug(0.1);
        });
      });
    });
  });
  assert.strictEqual(b.stomper, spiders[1], 'stomper picked from idle non-editors');
  assert.strictEqual(spiders[1].state, 'walk');
  assert.deepStrictEqual(spiders[1].target, { x: 0.5, y: 0.615 });
  assert.ok(b.squashCd > 0, 'squash cooldown armed');
});

test('updateRoomBug holds off while another scene owns the room', function () {
  const b = crawler({ squashCd: 0 });
  const scene = Object.assign(cleanScene(), {
    bug: b,
    gossip: {},
    spiders: [{ role: 'reviewer', state: 'idle' }]
  });
  withScene(scene, () => {
    withStub('roomBugWander', () => null, () => {
      withStub('bugSceneBlocked', () => true, () => {
        updateRoomBug(0.1);
      });
    });
  });
  assert.strictEqual(b.stomper, null, 'no stomper dispatched during gossip');
  assert.strictEqual(scene.spiders[0].state, 'idle');
});

// ── Spawn / squish lifecycle with stubbed surroundings ──────────────────────
test('spawnRoomBug spawns once with streak-scaled hp and reports the bug', function () {
  const calls = { speech: 0, ticker: 0, gossip: 0, record: 0, apply: 0 };
  const scene = Object.assign(cleanScene(), {
    meetingOn: true, bug: null, _editFailStreak: 2,
    spiders: [{ role: 'reviewer', state: 'idle' }]
  });
  const stubs = {
    BUG_SPAWN_LINES: ['A bug is loose!'],
    bugSpawnState: bugSpawnState,
    bugSizeForFailStreak: bugSizeForFailStreak,
    spiderFor: () => null,
    randomSpider: () => ({ icon: '🕷', name: 'Spot' }),
    setSpeech: () => { calls.speech++; },
    pick: a => a[0],
    pushTicker: () => { calls.ticker++; },
    logGossipEntry: () => { calls.gossip++; },
    recordEvent: () => { calls.record++; },
    $scope: { $applyAsync: () => { calls.apply++; } }
  };
  const prev = {};
  Object.keys(stubs).forEach(k => { prev[k] = globalThis[k]; globalThis[k] = stubs[k]; });
  try {
    withScene(scene, () => {
      spawnRoomBug(3, false);
      assert.ok(scene.bug, 'bug spawned');
      assert.strictEqual(scene.bug.hp, 4, 'hp 3 + floor(2/2) from streak');
      assert.strictEqual(scene.bug.size, 5, 'size from streak 2');
      assert.strictEqual(scene.bug.attempt, 3);
      spawnRoomBug(4, false);
      assert.strictEqual(scene.bug.attempt, 3, 'double spawn guarded');
    });
    assert.strictEqual(calls.ticker, 1);
    assert.strictEqual(calls.gossip, 1);
    assert.strictEqual(calls.record, 1);
  } finally {
    Object.keys(stubs).forEach(k => { globalThis[k] = prev[k]; });
  }
});

test('spawnRoomBug is silent during replay', function () {
  const calls = { ticker: 0, gossip: 0, record: 0 };
  const scene = Object.assign(cleanScene(), { meetingOn: true, bug: null, spiders: [] });
  const prev = {};
  const stubs = {
    bugSpawnState: bugSpawnState,
    bugSizeForFailStreak: bugSizeForFailStreak,
    spiderFor: () => null,
    randomSpider: () => ({ icon: '🕷', name: 'Spot' }),
    setSpeech: () => {},
    pick: a => a[0],
    pushTicker: () => { calls.ticker++; },
    logGossipEntry: () => { calls.gossip++; },
    recordEvent: () => { calls.record++; },
    $scope: { $applyAsync: () => {} }
  };
  Object.keys(stubs).forEach(k => { prev[k] = globalThis[k]; globalThis[k] = stubs[k]; });
  try {
    withScene(scene, () => {
      spawnRoomBug(1, true);
      assert.ok(scene.bug);
    });
    assert.strictEqual(calls.ticker, 0);
    assert.strictEqual(calls.gossip, 0);
    assert.strictEqual(calls.record, 0);
  } finally {
    Object.keys(stubs).forEach(k => { globalThis[k] = prev[k]; });
  }
});

test('squishRoomBug bursts sparkles and announces the kill', function () {
  const calls = { speech: 0, ticker: 0, gossip: 0, record: 0, apply: 0 };
  const scene = Object.assign(cleanScene(), { bug: crawler(), sparkles: [] });
  const stomper = { icon: '🕷', name: 'Squash' };
  const prev = {};
  const stubs = {
    BUG_SQUISH_LINES: ['Squashed!'],
    setSpeech: () => { calls.speech++; },
    pick: a => a[0],
    pushTicker: () => { calls.ticker++; },
    logGossipEntry: () => { calls.gossip++; },
    recordEvent: () => { calls.record++; },
    $scope: { $applyAsync: () => { calls.apply++; } }
  };
  Object.keys(stubs).forEach(k => { prev[k] = globalThis[k]; globalThis[k] = stubs[k]; });
  try {
    withScene(scene, () => {
      squishRoomBug(true, stomper, false);
      assert.strictEqual(scene.bug.phase, 'squished');
      assert.strictEqual(scene.bug.squishT, 0.01, 'instant squish');
      assert.strictEqual(scene.bug.stomper, null);
    });
    assert.strictEqual(scene.sparkles.length, 12);
    assert.strictEqual(calls.speech, 1);
    assert.strictEqual(calls.ticker, 1);
    assert.strictEqual(calls.gossip, 1);
    assert.strictEqual(calls.record, 1);
    assert.strictEqual(calls.apply, 1);
  } finally {
    Object.keys(stubs).forEach(k => { globalThis[k] = prev[k]; });
  }
});

test('squishRoomBug with no bug is a no-op', function () {
  const scene = Object.assign(cleanScene(), { bug: null });
  withScene(scene, () => {
    squishRoomBug(true, null, false);
  });
  assert.ok(true, 'no throw');
});

console.log('\n' + passed + ' passed, ' + failed + ' failed');
process.exit(failed ? 1 : 0);