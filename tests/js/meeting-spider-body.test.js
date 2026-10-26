// meeting-spider-body.test.js
// Unit tests for wwwroot/meeting.js's de-cartooned spider rendering inside
// drawSpider: the sticker-like rounded-rect body with its hard black outline is
// gone, replaced by a soft head+abdomen egg silhouette, a radial-gradient shadow
// with falloff (no flat dark blob), smaller calmer eyes, and thinner legs.
// Like the other meeting tests, the live source blocks are extracted and evaled
// against a recording canvas stub, so a regression to the old outline breaks this
// suite. The closure-internal blocks are wrapped via `new Function(...params)`.
// Dependency-free Node test runner:  node tests/js/meeting-spider-body.test.js
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

// ── Body block: from the de-cartoon comment through the rim-light fill ───────
const bodyM = /\/\/ Body: a soft head\+abdomen egg silhouette[\s\S]*?ctx\.fillStyle = 'rgba\(255,255,255,0\.16\)';[\s\S]*?ctx\.fill\(\);\n(?=          var look = )/.exec(src);
assert(bodyM, 'body block marker not found — drawSpider formatting may have drifted');

// ── Eye block: from the eye radius through the two pupil fills ───────────────
const eyeM = /var eyeR = s\.glaringAt \? 2\.2 \* scale : 2\.6 \* scale;[\s\S]*?1\.15 \* scale, 0, 6\.283\); ctx\.fill\(\);\n(?=          drawSpiderDetails)/.exec(src);
assert(eyeM, 'eye block marker not found — drawSpider formatting may have drifted');

function recordingCtx() {
  const calls = [];
  const grad = { stops: [], addColorStop: (p, c) => grad.stops.push([p, c]) };
  return {
    calls,
    grad,
    createRadialGradient() { calls.push({ op: 'createRadialGradient' }); return grad; },
    beginPath() { calls.push({ op: 'beginPath' }); },
    ellipse(...a) { calls.push({ op: 'ellipse', a }); },
    arc(...a) { calls.push({ op: 'arc', a }); },
    fill() { calls.push({ op: 'fill' }); },
    stroke() { calls.push({ op: 'stroke' }); },
    fillRect(...a) { calls.push({ op: 'fillRect', a }); },
    set fillStyle(v) { calls.push({ op: 'fillStyle', v }); },
    set strokeStyle(v) { calls.push({ op: 'strokeStyle', v }); },
    set lineWidth(v) { calls.push({ op: 'lineWidth', v }); },
  };
}
const stubBlend = (a, b, k) => 'blend(' + a + ',' + b + ',' + k + ')';
const runBlock = (block, params) => {
  const fn = new Function('ctx', 'blendHex', 'px', 'cy', 'bodyW', 'bodyH', 'bodyColor', block);
  fn.apply(null, params);
};

console.log('de-cartooned spider body tests\n');

// ── Body shape: egg silhouette, no outline ──────────────────────────────────
test('body block draws four ellipse fills and no stroke or rectangle', function () {
  const ctx = recordingCtx();
  runBlock(bodyM[0], [ctx, stubBlend, 100, 100, 26, 20, '#e06c75']);
  const fills = ctx.calls.filter(c => c.op === 'fill');
  const ellipses = ctx.calls.filter(c => c.op === 'ellipse');
  assert.strictEqual(fills.length, 4, 'expected 4 fills (abdomen, head, core shadow, rim light)');
  assert.strictEqual(ellipses.length, 4, 'expected 4 ellipse paths');
  assert.strictEqual(ctx.calls.some(c => c.op === 'stroke'), false, 'body block must not stroke');
  assert.strictEqual(ctx.calls.some(c => c.op === 'fillRect'), false, 'flat highlight rectangle is gone');
});

test('abdomen sits below the head, both anchored at the body center', function () {
  const ctx = recordingCtx();
  runBlock(bodyM[0], [ctx, stubBlend, 100, 100, 26, 20, '#e06c75']);
  const [ab, head, core, rim] = ctx.calls.filter(c => c.op === 'ellipse');
  assert.strictEqual(ab.a[0], 100); assert.strictEqual(ab.a[1], 100 + 20 * 0.16); // abdomen center
  assert.strictEqual(head.a[0], 100); assert.strictEqual(head.a[1], 100 - 20 * 0.2); // head center
  assert.ok(core.a[1] > 100, 'core shadow pools at the lower curve');
  assert.ok(rim.a[1] < 100, 'rim light sits on the upper body');
});

test('body uses one radial gradient: lit top-left, role color, dark edge', function () {
  const ctx = recordingCtx();
  runBlock(bodyM[0], [ctx, stubBlend, 100, 100, 26, 20, '#e06c75']);
  const g = ctx.grad;
  assert.strictEqual(g.stops.length, 3);
  assert.deepStrictEqual(g.stops[0], [0, 'blend(#e06c75,#ffffff,0.3)']);
  assert.deepStrictEqual(g.stops[1], [0.55, '#e06c75']);
  assert.deepStrictEqual(g.stops[2], [1, 'blend(#e06c75,#000000,0.32)']);
});

test('core shadow and rim light are gentle overlays, not harsh bands', function () {
  const ctx = recordingCtx();
  runBlock(bodyM[0], [ctx, stubBlend, 100, 100, 26, 20, '#e06c75']);
  const styles = ctx.calls.filter(c => c.op === 'fillStyle').map(c => c.v);
  assert.ok(styles.some(v => v === 'rgba(0,0,0,0.10)'), 'core shadow alpha is low');
  assert.ok(styles.some(v => v === 'rgba(255,255,255,0.16)'), 'rim light alpha is low');
});

// ── Eyes: smaller whites and pupils for a calmer gaze ───────────────────────
test('eyes: smaller radius, tiny pupil, near-black not pure black', function () {
  const ctx = recordingCtx();
  const fn = new Function('ctx', 's', 'scene', 'px', 'cy', 'bodyW', 'bodyH', 'scale', 'ex', 'ey', 'lookUp', 'drunkPup', eyeM[0]);
  fn.call(null, ctx, { glaringAt: null, state: 'idle' }, { watching: null }, 100, 100, 26, 20, 1, 0, 90, 0, 0);
  const arcs = ctx.calls.filter(c => c.op === 'ellipse').length + ctx.calls.filter(c => c.op === 'arc').length;
  assert.ok(arcs >= 4, 'two whites + two pupils drawn');
  const styles = ctx.calls.filter(c => c.op === 'fillStyle').map(c => c.v);
  assert.ok(styles.includes('rgba(245,248,252,0.95)'), 'eye whites slightly off-white');
  assert.ok(styles.includes('rgba(15,18,24,0.92)'), 'pupils near-black with tiny alpha');
});

test('eyes: default radius 2.6x, glaring 2.2x, pupil 1.15x', function () {
  assert.ok(/var eyeR = s\.glaringAt \? 2\.2 \* scale : 2\.6 \* scale;/.test(eyeM[0]));
  assert.ok(/1\.15 \* scale, 0, 6\.283\); ctx\.fill\(\);\n          ctx\.beginPath\(\); ctx\.arc\(px \+ bodyW \* 0\.18/.test(eyeM[0]));
});

// ── Legs and shadow markers ─────────────────────────────────────────────────
test('legs draw thinner than before (1.7x max)', function () {
  const m = /ctx\.strokeStyle = bodyColor;\n          ctx\.lineWidth = Math\.max\(1\.3, 1\.7 \* scale\);/.exec(src);
  assert.ok(m, 'leg lineWidth must stay at Math.max(1.3, 1.7 * scale)');
});

test('ground shadow is a radial gradient with falloff, not a flat fill', function () {
  const m = /var shG = ctx\.createRadialGradient\(px, py \+ bodyH \* 0\.66, bodyW \* 0\.1, px, py \+ bodyH \* 0\.66, bodyW \* 0\.95\);[\s\S]*?shG\.addColorStop\(1, 'rgba\(0,0,0,0\)'\);/.exec(src);
  assert.ok(m, 'shadow must fade to transparent at the edge');
});

test('old sticker body and hard outline are gone from drawSpider', function () {
  assert.ok(!/rr\(px - bodyW \/ 2, cy - bodyH \/ 2, bodyW, bodyH, 6 \* scale\)/.test(src), 'rounded-rect body removed');
  assert.ok(!/ctx\.strokeStyle = 'rgba\(0,0,0,0\.35\)';\n          ctx\.lineWidth = 1;\n          ctx\.stroke\(\);/.test(src), 'hard black outline removed');
  assert.ok(!/rr\(px - bodyW \/ 2 \+ 3 \* scale, cy - bodyH \/ 2 \+ 2 \* scale, bodyW \* 0\.4, bodyH \* 0\.28, 3 \* scale\)/.test(src), 'white highlight strip removed');
});

console.log('\n' + (failed ? failed + ' FAILED, ' + passed + ' passed' : 'All ' + passed + ' tests passed') + '\n');
process.exit(failed ? 1 : 0);