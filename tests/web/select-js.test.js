// WEB's chooser, the page's side: SELECT_JS (unity/Assets/Plugins/Android/MetaCodexWebView.java), taken from the Java
// source and run against a small fake DOM: which presses, clicks and keys it takes (and which it leaves to the page), the
// request it sends (options, groups, disabled, hidden, the element's place), and how an answer is written (the page's
// setters, input and change, an old token or id refused). The real engine and the headset are not here: see
// docs/TEST_CHECKLIST.md (WEB) and tests/web/select.html for those.
// Run: tests/web/run-web-select-tests.sh   (Node 18+, no install)
'use strict'
const fs = require('fs')
const path = require('path')
const JAVA = path.join(__dirname, '../../unity/Assets/Plugins/Android/MetaCodexWebView.java')
function selectJs() {
  const s = fs.readFileSync(JAVA, 'utf8')
  const i = s.indexOf('private static final String SELECT_JS =')
  const j = s.indexOf(';\n', i)
  if (i < 0 || j < 0) throw new Error('SELECT_JS not found in ' + JAVA)
  const lits = s.slice(i, j).match(/"((?:[^"\\]|\\.)*)"/g) || []
  return lits.map(l => l.slice(1, -1).replace(/\\(.)/g, (m, c) => c === 'n' ? '\n' : c === 't' ? '\t' : c)).join('')
}
const SRC = selectJs()
let fails = 0;
const ok = (c, m) => { console.log((c ? 'ok   ' : 'FAIL ') + m); if (!c) fails++; };
const listeners = {};
const W = {
  addEventListener(t, f, c) { (listeners[t] = listeners[t] || []).push([f, c]); },
  frames: { length: 0 }, visualViewport: { offsetLeft: 0, offsetTop: 100, width: 1280, height: 800 },
  innerWidth: 1280, innerHeight: 800, getComputedStyle: n => ({ appearance: n.appearance || 'auto' }),
  mcxSelectBridge: { calls: [], open(t, j) { this.calls.push([t, JSON.parse(j)]); } },
};
const doc = { defaultView: W, addEventListener() {}, getElementById() { return null; } };
W.document = doc; W.parent = W; W.frameElement = null;
class El {
  constructor(tag) { this.nodeType = 1; this.tagName = tag; this.attrs = {}; this.parentNode = null; this.isConnected = true; this.ownerDocument = doc; this.focused = 0; this.events = []; this.labels = []; }
  getAttribute(k) { return k in this.attrs ? this.attrs[k] : null; }
  matches(s) { return s === ':disabled' && (!!this.disabled || !!(this.parentNode && this.parentNode.tagName === 'OPTGROUP' && this.parentNode.disabled)); }
  closest(s) { let n = this; while (n) { if (n.tagName && n.tagName.toLowerCase() === s) return n; n = n.parentNode; } return null; }
  focus() { this.focused++; }
  getBoundingClientRect() { return this.rect || { left: 0, top: 0, width: 0, height: 0 }; }
  dispatchEvent(e) { this.events.push(e.type + (e.bubbles ? ':b' : '')); return true; }
}
class HTMLSelectElement extends El {
  constructor() { super('SELECT'); this.options = []; this.multiple = false; this.name = ''; }
  get selectedIndex() { return this.options.findIndex(o => o.selected); }
  set selectedIndex(i) { this.options.forEach((o, k) => o.selected = k === i); }
}
class HTMLInputElement extends El {
  constructor(type) { super('INPUT'); this.type = type; this._v = ''; this.min = ''; this.max = ''; this.step = ''; this.required = false; this.readOnly = false; }
  get value() { return this._v; } set value(v) { this._v = String(v); }
}
class Event { constructor(t, o) { this.type = t; this.bubbles = !!(o && o.bubbles); } }
HTMLInputElement.prototype.showPicker = function () { this.nativePicker = (this.nativePicker || 0) + 1; };
W.HTMLInputElement = HTMLInputElement; W.HTMLSelectElement = HTMLSelectElement;
const nav = { userActivation: { isActive: false } };
const run = tok => new Function('window', 'HTMLSelectElement', 'HTMLInputElement', 'Event', 'navigator', 'return (' + SRC + ')')(W, HTMLSelectElement, HTMLInputElement, Event, nav)(tok);
function opt(text, parent, extra) { const o = new El('OPTION'); o.text = text; o.label = text; o.selected = false; o.disabled = false; o.hidden = false; o.parentNode = parent; return Object.assign(o, extra || {}); }
function ev(type, path, extra) { return Object.assign({ type, isTrusted: true, button: 0, defaultPrevented: false, target: path[0], composedPath: () => path, preventDefault() { this.defaultPrevented = true; } }, extra || {}); }
function fire(type, e) { for (const [f] of listeners[type] || []) f(e); return e; }
const calls = W.mcxSelectBridge.calls;

run('tok1');
ok(listeners.mousedown && listeners.mousedown[0][1] === true && listeners.click && listeners.keydown, 'capture listeners on the window: mousedown, click, keydown');

// a select with optgroups (one disabled), a disabled option, an option outside any group
const s = new HTMLSelectElement();
const gA = new El('OPTGROUP'); gA.label = 'Italy'; gA.parentNode = s;
const gB = new El('OPTGROUP'); gB.label = 'France'; gB.disabled = true; gB.parentNode = s;
s.options = [opt('Choose…', s, { selected: true }), opt('  Rome\n', gA), opt('Milan', gA, { disabled: true }), opt('Paris', gB), opt('Other', s, { hidden: true })];
s.attrs['aria-label'] = 'City'; s.rect = { left: 100, top: 300, width: 200, height: 32 };
let e = fire('mousedown', ev('mousedown', [s]));
let c = calls[calls.length - 1];
ok(e.defaultPrevented && calls.length === 1 && c[0] === 'tok1' && s.focused === 1, 'mousedown on a select: native popup prevented, focused, one request with the token');
const d = c[1];
ok(d.kind === 'select' && d.label === 'City' && d.multiple === false && d.options.length === 5 && d.groups.join() === 'Italy,France', 'payload: kind, label, options, groups ' + JSON.stringify(d.groups));
ok(d.options[1].t === 'Rome' && d.options[1].g === 0 && d.options[2].d && d.options[3].d && d.options[3].g === 1 && d.options[4].g === -1 && d.options[4].h && d.options[0].s,
  'options: text trimmed, group index, disabled (own and by its group), hidden, selected');
ok(Math.abs(d.box[0] - 100 / 1280) < 1e-9 && Math.abs(d.box[1] - 200 / 800) < 1e-9 && Math.abs(d.box[2] - 200 / 1280) < 1e-9 && Math.abs(d.box[3] - 32 / 800) < 1e-9, 'box: fractions of the visual viewport ' + d.box.map(x => x.toFixed(3)));
fire('click', ev('click', [s]));
ok(calls.length === 1, 'the click after it opens nothing more');
ok(W.__mcxSelAnswer('tok1', d.id, [2]) === false && s.selectedIndex === 0, 'a disabled option is refused');
fire('mousedown', ev('mousedown', [s])); const id2 = calls[calls.length - 1][1].id;
ok(W.__mcxSelAnswer('bad', id2, [1]) === false && s.selectedIndex === 0, 'a wrong token writes nothing');
ok(W.__mcxSelAnswer('tok1', id2 - 1, [1]) === false, 'an old id writes nothing');
s.events = [];
ok(W.__mcxSelAnswer('tok1', id2, [1]) === true && s.selectedIndex === 1 && s.events.join() === 'input:b,change:b', 'answer: selectedIndex 1, input and change bubble ' + s.events);
ok(W.__mcxSelAnswer('tok1', id2, [1]) === false, 'answered once only');
// the same option again: no events
fire('mousedown', ev('mousedown', [s])); s.events = [];
ok(W.__mcxSelAnswer('tok1', calls[calls.length - 1][1].id, [1]) === true && s.events.length === 0, 'the same option again: no change events');
// the options changed meanwhile
fire('mousedown', ev('mousedown', [s])); const id3 = calls[calls.length - 1][1].id; s.options.push(opt('New', s));
ok(W.__mcxSelAnswer('tok1', id3, [0]) === false && s.selectedIndex === 1, 'options changed since: nothing written');
s.options.pop();

// multiple
const m = new HTMLSelectElement(); m.multiple = true;
m.options = [opt('a', m), opt('b', m, { selected: true }), opt('c', m, { selected: true, disabled: true }), opt('d', m)];
fire('mousedown', ev('mousedown', [m]));
const dm = calls[calls.length - 1][1];
ok(dm.multiple === true && dm.options.filter(o => o.s).length === 2, 'multiple: flagged, two selected');
ok(W.__mcxSelAnswer('tok1', dm.id, [0, 3]) === true && m.options.map(o => o.selected ? 1 : 0).join('') === '1011' && m.events.join() === 'input:b,change:b',
  'multiple answer [0,3]: a and d on, b off, disabled c kept ' + m.options.map(o => o.selected ? 1 : 0).join(''));

// keys
const n0 = calls.length;
fire('keydown', ev('keydown', [s], { key: 'a' }));
ok(calls.length === n0, 'a letter key on a select opens nothing (type-ahead stays the page\'s)');
e = fire('keydown', ev('keydown', [s], { key: 'Enter' }));
ok(calls.length === n0 + 1 && e.defaultPrevented, 'Enter on a select opens the chooser');
e = fire('keydown', ev('keydown', [s], { key: 'ArrowDown', altKey: true }));
ok(e.defaultPrevented, 'Alt+Down too');

// untrusted, right button, empty, disabled, base-select
const n1 = calls.length;
fire('mousedown', ev('mousedown', [s], { isTrusted: false }));
fire('mousedown', ev('mousedown', [s], { button: 2 }));
const empty = new HTMLSelectElement();
e = fire('mousedown', ev('mousedown', [empty]));
const dis = new HTMLSelectElement(); dis.options = [opt('x', dis)]; dis.disabled = true;
fire('mousedown', ev('mousedown', [dis]));
const base = new HTMLSelectElement(); base.options = [opt('x', base)]; base.appearance = 'base-select';
const eb = fire('mousedown', ev('mousedown', [base]));
ok(calls.length === n1 && !e.defaultPrevented && !eb.defaultPrevented, 'untrusted, right button, no options, disabled, base-select: left to the page');

// open shadow root (the select is first in the composed path) and a press on an option
const host = new El('DIV');
fire('mousedown', ev('mousedown', [s, host]));
const o2 = s.options[3];
fire('mousedown', ev('mousedown', [o2, s]));
ok(calls.length === n1 + 2, 'inside an open shadow root, and a press on an option, both reach the select');

// a date field: picker opens on click (and keyboard activation), not on mousedown
const dt = new HTMLInputElement('date'); dt._v = '2026-11-17'; dt.min = '2026-11-01'; dt.required = true; dt.rect = { left: 0, top: 100, width: 100, height: 20 };
const n2 = calls.length;
e = fire('mousedown', ev('mousedown', [dt]));
ok(calls.length === n2 && !e.defaultPrevented, 'mousedown on a date field: left alone (focus as usual)');
e = fire('click', ev('click', [dt]));
const dd = calls[calls.length - 1][1];
ok(e.defaultPrevented && dd.kind === 'date' && dd.value === '2026-11-17' && dd.min === '2026-11-01' && dd.required === true && dd.options === undefined, 'click on a date field: prevented, request with value, min, required');
dt.events = [];
ok(W.__mcxSelAnswer('tok1', dd.id, '2026-11-20') === true && dt.value === '2026-11-20' && dt.events.join() === 'input:b,change:b', 'date answer written through the setter, events ' + dt.events);
const tx = new HTMLInputElement('text');
const n3 = calls.length; fire('click', ev('click', [tx]));
const ro = new HTMLInputElement('time'); ro.readOnly = true; fire('click', ev('click', [ro]));
ok(calls.length === n3, 'a text field and a read-only time field: nothing');
const col = new HTMLInputElement('color'); col._v = '#000000'; fire('click', ev('click', [col]));
ok(calls[calls.length - 1][1].kind === 'color', 'a colour field: a request of kind color');

// a list box drawn in the page (multiple or size > 1 on engines that lay its options out): left to the page; the same
// multiple select drawn as a menu list (its options have no boxes): the chooser
const lb = new HTMLSelectElement(); lb.multiple = true; lb.options = [opt('p', lb), opt('q', lb)]; lb.options.forEach(o => o.rect = { left: 0, top: 0, width: 80, height: 20 });
const n4 = calls.length; e = fire('mousedown', ev('mousedown', [lb]));
ok(calls.length === n4 && !e.defaultPrevented, 'an in-page list box: left to the page');
const sz = new HTMLSelectElement(); sz.size = 4; sz.options = [opt('p', sz), opt('q', sz)];
e = fire('mousedown', ev('mousedown', [sz]));
ok(calls.length === n4 + 1 && e.defaultPrevented, 'size 4 drawn as a menu list (an older engine): the chooser');

// a page's own button: showPicker() and an untrusted click open the chooser during a user gesture, never without one
const pk = new HTMLInputElement('time'); pk._v = '10:00';
const n5 = calls.length; pk.showPicker();
const noGesture = calls.length === n5 && pk.nativePicker === 1;
fire('click', ev('click', [pk], { isTrusted: false }));
const noGesture2 = calls.length === n5;
nav.userActivation.isActive = true;
pk.showPicker();
const viaPicker = calls.length === n5 + 1 && calls[calls.length - 1][1].kind === 'time' && pk.nativePicker === 1;
e = fire('click', ev('click', [pk], { isTrusted: false }));
const viaClick = calls.length === n5 + 2 && e.defaultPrevented;
const n6 = calls.length; fire('click', ev('click', [s], { isTrusted: false }));
nav.userActivation.isActive = false;
ok(noGesture && noGesture2 && viaPicker && viaClick && calls.length === n6, 'showPicker() and an untrusted click: the chooser only during a user gesture (else the engine\'s own); an untrusted click on a select: nothing');

// run again in the same document (load finished, or a new token): only the token changes
const nl = listeners.mousedown.length;
run('tok2');
fire('mousedown', ev('mousedown', [s]));
const r2 = calls[calls.length - 1];
ok(listeners.mousedown.length === nl && r2[0] === 'tok2', 'injected again: no second listener, the new token is used');
ok(W.__mcxSelAnswer('tok1', r2[1].id, [0]) === false && W.__mcxSelAnswer('tok2', r2[1].id, [0]) === true && s.selectedIndex === 0, 'the old token is refused, the new one works');

console.log(fails ? fails + ' FAILED' : 'all ok');
process.exit(fails ? 1 : 0);
