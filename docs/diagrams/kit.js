/* MetaCodex diagram kit: layout, icons, connectors, lint.
   Usage: see docs/diagrams/README.md. A diagram file loads this script in <head> and
   calls connect(...) in a script at the end of <body>. */
(function () {
  'use strict';

  var CW = 1600, CHH = 900;       // canvas size
  var SAFE = 16;                  // minimum distance of components to the canvas edge
  var MIN_GAP = 4;                // minimum gap between components
  var MIN_FONT = 13;
  var STUB = 22, RAD = 9, ARROW = 10, BEND = 45, CROSS = 40, OVER = 8;
  var CORNER_SAFE = 18;           // anchors stay this far from the corners of an element

  var qs = new URLSearchParams(location.search);
  var THEME = qs.get('theme') === 'light' ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', THEME);
  if (qs.has('guides')) document.documentElement.setAttribute('data-guides', '1');
  var DEBUG = qs.has('debug');

  var runtimeErrors = [];
  window.addEventListener('error', function (e) { runtimeErrors.push(String(e.message || e)); });

  /* ------------------------------------------------------------------ icons */
  function gearPath() {
    var n = 8, ro = 9.2, ri = 6.9, pts = [], i, a0, a1;
    for (i = 0; i < n; i++) {
      var c = (i / n) * Math.PI * 2, hw = Math.PI / n * 0.36, hn = Math.PI / n * 0.62;
      a0 = c - hn; a1 = c + hn;
      pts.push([ri, c - Math.PI / n * 0.95], [ri, a0], [ro, c - hw], [ro, c + hw], [ri, a1]);
    }
    return 'M' + pts.map(function (p) {
      return (12 + Math.cos(p[1]) * p[0]).toFixed(2) + ' ' + (12 + Math.sin(p[1]) * p[0]).toFixed(2);
    }).join('L') + 'Z';
  }
  var ICONS = {
    cpu: '<rect x="6" y="6" width="12" height="12" rx="2"/><rect x="9.5" y="9.5" width="5" height="5" rx="1"/><path d="M9 3v3M15 3v3M9 18v3M15 18v3M3 9h3M3 15h3M18 9h3M18 15h3"/>',
    process: '<path d="M12 2.8l8 4.6v9.2l-8 4.6-8-4.6V7.4z"/><circle cx="12" cy="12" r="2.4"/>',
    thread: '<path d="M3 7h5.5c3 0 3 5 6.5 5H21M3 17h5.5c3 0 3-5 6.5-5"/><path d="M18.5 9.5L21 12l-2.5 2.5"/>',
    lock: '<rect x="5" y="11" width="14" height="10" rx="2.2"/><path d="M8 11V8a4 4 0 018 0v3"/><path d="M12 15v2.4"/>',
    key: '<circle cx="8" cy="15.5" r="4"/><path d="M11 12.5L20 3.5M16.5 7l3 3M14 9.5l2 2"/>',
    shield: '<path d="M12 3l7.5 2.8v5.7c0 4.6-3.2 7.9-7.5 9.5-4.3-1.6-7.5-4.9-7.5-9.5V5.8z"/><path d="M8.6 12.2l2.5 2.5 4.4-4.9"/>',
    mic: '<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5.5 11a6.5 6.5 0 0013 0M12 17.5V21M9 21h6"/>',
    waveform: '<path d="M4 10v4M7.5 6.5v11M11 3.5v17M14.5 7.5v9M18 9.5v5M21 11v2"/>',
    model: '<circle cx="5.5" cy="6.5" r="2.2"/><circle cx="5.5" cy="17.5" r="2.2"/><circle cx="12" cy="12" r="2.4"/><circle cx="18.5" cy="6.5" r="2.2"/><circle cx="18.5" cy="17.5" r="2.2"/><path d="M7.4 7.7l2.6 2.8M7.4 16.3l2.6-2.8M14 10.6l2.6-2.8M14 13.4l2.6 2.8"/>',
    brain: '<path d="M12 5.2v14.2"/><path d="M12 5.2C11.2 3.4 8.3 3.3 7.3 5 5.2 4.8 4 6.7 4.6 8.4 3.3 9.4 3.4 11.4 4.6 12.2 3.7 13.6 4.5 15.6 6.1 15.8c.2 1.9 2.2 2.9 3.8 2.1.5.9 1.4 1.5 2.1 1.5"/><path d="M12 5.2c.8-1.8 3.7-1.9 4.7-.2 2.1-.2 3.3 1.7 2.7 3.4 1.3 1 1.2 3-.1 3.8.9 1.4.1 3.4-1.5 3.6-.2 1.9-2.2 2.9-3.8 2.1-.5.9-1.4 1.5-2.1 1.5"/>',
    plug: '<path d="M9 3v5M15 3v5M6 8h12v3.2a6 6 0 01-12 0zM12 17.2V21"/>',
    puzzle: '<path d="M9.5 4.5a2.2 2.2 0 014.4 0V6.5H18v4h-2a2.2 2.2 0 100 4.4h2V19H14v-2a2.2 2.2 0 00-4.4 0v2H5.5v-4.1h2a2.2 2.2 0 000-4.4h-2V6.5h4z"/>',
    hand: '<path d="M8.5 12.5V5.2a1.6 1.6 0 013.2 0V11M11.7 10.5V3.8a1.6 1.6 0 013.2 0v6.7M14.9 10.8V5.6a1.6 1.6 0 013.2 0V15c0 3.7-2.6 6-5.6 6-2.2 0-3.7-.9-4.9-2.7l-3-4.6c-.7-1.1.5-2.3 1.7-1.6l2.2 1.5"/>',
    terminal: '<rect x="3" y="4.5" width="18" height="15" rx="2.4"/><path d="M7 9.5l3 2.5-3 2.5M12.5 15h4.5"/>',
    package: '<path d="M12 3l8 4.4v9.2L12 21l-8-4.4V7.4z"/><path d="M4.3 7.6L12 12l7.7-4.4M12 12v9"/>',
    gear: '<path d="' + gearPath() + '"/><circle cx="12" cy="12" r="2.7"/>',
    folder: '<path d="M3 7.5A2 2 0 015 5.5h4.2l2 2.3H19a2 2 0 012 2V17a2 2 0 01-2 2H5a2 2 0 01-2-2z"/>',
    branch: '<circle cx="6.5" cy="5.5" r="2.2"/><circle cx="6.5" cy="18.5" r="2.2"/><circle cx="17.5" cy="8.5" r="2.2"/><path d="M6.5 7.7v8.6M17.5 10.7c0 3.6-3.4 4.2-7 4.8-2.4.4-4 .8-4 1.9"/>',
    headset: '<path d="M3 9.5A2.5 2.5 0 015.5 7h13A2.5 2.5 0 0121 9.5v5a2.5 2.5 0 01-2.5 2.5h-3l-1.4-2.4h-3.2L9.5 17h-3A2.5 2.5 0 014 14.5z"/><path d="M7.2 11.8h.01M16.8 11.8h.01" stroke-width="2.4"/>',
    loop: '<path d="M20 12a8 8 0 01-13.7 5.6M4 12a8 8 0 0113.7-5.6"/><path d="M18 3v4h-4M6 21v-4h4"/>',
    file: '<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v4h4M9 12h6M9 15.5h6"/>',
    code: '<path d="M8.5 7L3.5 12l5 5M15.5 7l5 5-5 5M13.5 5l-3 14"/>',
    globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3.2 3 14.8 0 18M12 3c-3 3.2-3 14.8 0 18"/>',
    database: '<ellipse cx="12" cy="6" rx="7.5" ry="3"/><path d="M4.5 6v12c0 1.7 3.4 3 7.5 3s7.5-1.3 7.5-3V6M4.5 12c0 1.7 3.4 3 7.5 3s7.5-1.3 7.5-3"/>',
    bolt: '<path d="M13 2.5L5 13.5h6l-1 8 8-11h-6z"/>',
    eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="3"/>',
    user: '<circle cx="12" cy="8" r="3.8"/><path d="M4.5 20.5c.8-4 3.8-6 7.5-6s6.7 2 7.5 6"/>',
    speaker: '<path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z"/><path d="M15.5 9a4.2 4.2 0 010 6M18 6.5a8 8 0 010 11"/>',
    check: '<path d="M4.5 12.5l5 5 10-11"/>',
    warn: '<path d="M12 3.5l9.5 16.5h-19z"/><path d="M12 10v4.5M12 17.4h.01" stroke-width="2"/>',
    x: '<path d="M5.5 5.5l13 13M18.5 5.5l-13 13"/>',
    camera: '<path d="M3.5 8A1.5 1.5 0 015 6.5h2.8l1.4-2h5.6l1.4 2H19A1.5 1.5 0 0120.5 8v10A1.5 1.5 0 0119 19.5H5A1.5 1.5 0 013.5 18z"/><circle cx="12" cy="12.5" r="3.6"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5.2l3.5 2"/>',
    link: '<path d="M10 14a4.2 4.2 0 006 0l3.2-3.2a4.2 4.2 0 00-6-6L11.8 6.2M14 10a4.2 4.2 0 00-6 0l-3.2 3.2a4.2 4.2 0 006 6l1.4-1.4"/>',
    cloud: '<path d="M7 18.5a4.5 4.5 0 01-.6-8.96A6 6 0 0117.9 8.7 4.9 4.9 0 0117 18.5z"/>',
    server: '<rect x="3.5" y="4" width="17" height="6.5" rx="1.8"/><rect x="3.5" y="13.5" width="17" height="6.5" rx="1.8"/><path d="M7 7.2h.01M7 16.8h.01" stroke-width="2.4"/>',
    download: '<path d="M12 4v11M7 10.5l5 5 5-5M5 20h14"/>',
    wifi: '<path d="M3 9.5a13 13 0 0118 0M6 13a8.5 8.5 0 0112 0M9 16.5a4 4 0 016 0M12 20h.01" stroke-width="1.8"/>',
    layers: '<path d="M12 3.5l9 4.8-9 4.8-9-4.8zM3.5 12.6L12 17l8.5-4.4M3.5 16.6L12 21l8.5-4.4"/>',
    sparkle: '<path d="M12 3l2 6 6 2-6 2-2 6-2-6-6-2 6-2zM19 17l.7 2 2 .7-2 .7-.7 2-.7-2-2-.7 2-.7z"/>'
  };
  function icon(name) {
    if (!ICONS[name]) { problems.push({ check: 'unknown-icon', msg: 'unknown icon "' + name + '"', el: '' }); return ''; }
    return '<svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#i-' + name + '"/></svg>';
  }
  function injectSprite() {
    var s = '<svg width="0" height="0" style="position:absolute" aria-hidden="true"><defs>';
    Object.keys(ICONS).forEach(function (k) { s += '<symbol id="i-' + k + '" viewBox="0 0 24 24">' + ICONS[k] + '</symbol>'; });
    s += '</defs></svg>';
    document.body.insertAdjacentHTML('afterbegin', s);
  }

  /* ----------------------------------------------------------- helper utils */
  var problems = [];
  var canvas;
  var DEFAULT_TONE_LABELS = { os: 'OS / platform', app: 'App main process', svc: 'Background service', linux: 'Linux userland', ai: 'Interface AI', agent: 'Agent harness', sec: 'Security / vault', native: 'Native apps', ext: 'External / user' };
  var DEFAULT_LINE_LABELS = { data: 'Data', control: 'Control', secret: 'Secret', async: 'Async' };

  function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
  function describe(el) {
    if (!el || !el.tagName) return '';
    if (el.id) return '#' + el.id;
    var t = (el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 28);
    return el.tagName.toLowerCase() + (el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\s+/)[0] : '') + (t ? ' "' + t + '"' : '');
  }
  function addProblem(check, msg, el) {
    problems.push({ check: check, msg: msg, el: describe(el), _el: el });
  }
  function parseList(str) {
    return String(str).split(',').map(function (x) { return x.trim(); }).filter(Boolean).map(function (x) {
      var i = x.indexOf(':');
      return i < 0 ? { key: x, label: null } : { key: x.slice(0, i).trim(), label: x.slice(i + 1).trim() };
    });
  }
  function lineSample(style) {
    return '<svg class="ls link link-' + style + '" viewBox="0 0 44 12"><path class="glow" d="M2 6H34"/><path class="ln" d="M2 6H34"/>' +
      (style === 'async' ? '<path class="arrow" d="M43 6L34 1.5V10.5Z"/>' : '<path class="arrow" d="M43 6L34 1.5V10.5Z"/>') + '</svg>';
  }

  /* -------------------------------------------------------- DOM expansion */
  function expand() {
    // title bar
    var tb = canvas.querySelector('.titlebar');
    if (tb) {
      tb.innerHTML = '<div class="tmark"><svg viewBox="0 0 48 48" fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round"><path d="M24 3.5l17.8 10.25v20.5L24 44.5 6.2 34.25v-20.5z"/><path d="M24 12.5l10 5.75v11.5L24 35.5l-10-5.75v-11.5z" stroke-opacity=".55"/><circle cx="24" cy="24" r="3.2" fill="currentColor" stroke="none"/></svg></div>' +
        '<div class="tt"><div class="ttitle">' + esc(tb.dataset.title || '') + '</div><div class="tcap">' + esc(tb.dataset.caption || '') + '</div></div>' +
        (tb.dataset.tag ? '<div class="ttag">' + esc(tb.dataset.tag) + '</div>' : '');
    }
    // cards
    canvas.querySelectorAll('.card').forEach(function (c) {
      if (c.dataset.title || c.dataset.icon) {
        var h = document.createElement('div'); h.className = 'ch';
        var html = '';
        if (c.dataset.icon) html += '<span class="ci">' + icon(c.dataset.icon) + '</span>';
        html += '<div class="ct"><div class="ctitle">' + esc(c.dataset.title || '') + '</div>' + (c.dataset.sub ? '<div class="csub">' + esc(c.dataset.sub) + '</div>' : '') + '</div>';
        if (c.dataset.status) html += '<span class="cstat" style="--sc:var(--' + (c.dataset.status === 'error' ? 'err' : c.dataset.status) + ')"></span>';
        if (c.dataset.badge) html += '<span class="badge">' + esc(c.dataset.badge) + '</span>';
        h.innerHTML = html;
        c.insertBefore(h, c.firstChild);
      }
    });
    // groups
    canvas.querySelectorAll('.group').forEach(function (g) {
      if (g.dataset.label) {
        g.classList.add('labeled');
        var l = document.createElement('div'); l.className = 'glabel';
        l.innerHTML = (g.dataset.icon ? icon(g.dataset.icon) : '') + '<span>' + esc(g.dataset.label) + '</span>' + (g.dataset.sub ? '<span class="gsub">' + esc(g.dataset.sub) + '</span>' : '');
        g.insertBefore(l, g.firstChild);
      }
    });
    // notes
    canvas.querySelectorAll('.note').forEach(function (n) {
      var inner = n.innerHTML;
      n.innerHTML = (n.dataset.icon ? '<span class="ni">' + icon(n.dataset.icon) + '</span>' : '') + '<div class="nt">' + inner + '</div>';
    });
    // metrics
    canvas.querySelectorAll('.metric').forEach(function (m) {
      m.innerHTML = '<div class="mv"><b>' + esc(m.dataset.value || '') + '</b>' + (m.dataset.unit ? '<u>' + esc(m.dataset.unit) + '</u>' : '') + '</div>' + (m.dataset.caption ? '<div class="mc">' + esc(m.dataset.caption) + '</div>' : '');
    });
    // legend
    canvas.querySelectorAll('.legend').forEach(function (lg) {
      var html = '';
      if (lg.dataset.title) html += '<span class="lt">' + esc(lg.dataset.title) + '</span>';
      if (lg.dataset.tones) parseList(lg.dataset.tones).forEach(function (it) {
        var lab = it.label || DEFAULT_TONE_LABELS[it.key] || it.key;
        html += '<span class="li" data-tone="' + esc(it.key) + '"><span class="sw"></span>' + esc(lab) + '</span>';
      });
      if (lg.dataset.tones && lg.dataset.lines) html += '<span class="vsep"></span>';
      if (lg.dataset.lines) parseList(lg.dataset.lines).forEach(function (it) {
        var lab = it.label || DEFAULT_LINE_LABELS[it.key] || it.key;
        html += '<span class="li">' + lineSample(it.key) + esc(lab) + '</span>';
      });
      lg.insertAdjacentHTML('beforeend', html);
      if (lg.dataset.layout === 'col') lg.classList.add('vertical');
      lg.querySelectorAll('.vsep').forEach(function (v) { if (lg.classList.contains('vertical')) v.style.display = 'none'; });
    });
    // chips from data-chips
    canvas.querySelectorAll('[data-chips]').forEach(function (el) {
      var box = document.createElement('div'); box.className = 'chips';
      parseList(el.dataset.chips).forEach(function (it) {
        var s = document.createElement('span'); s.className = 'chip' + (/[\/.\\_]/.test(it.key) && !it.label ? ' mono' : '');
        if (it.label) s.setAttribute('data-tone', it.key); s.textContent = it.label || it.key; box.appendChild(s);
      });
      el.appendChild(box);
    });
    // inline icons: <i data-icon="cpu"></i>
    canvas.querySelectorAll('i[data-icon]').forEach(function (i) { i.outerHTML = icon(i.dataset.icon); });
    // positions, sizes and flow layouts
    canvas.querySelectorAll('[data-x],[data-y]').forEach(function (e) {
      e.style.position = 'absolute';
      if (e.dataset.x != null) e.style.left = e.dataset.x + 'px';
      if (e.dataset.y != null) e.style.top = e.dataset.y + 'px';
    });
    canvas.querySelectorAll('[data-w]').forEach(function (e) { e.style.width = e.dataset.w + 'px'; });
    canvas.querySelectorAll('[data-h]').forEach(function (e) { e.style.height = e.dataset.h + 'px'; });
    canvas.querySelectorAll('[data-layout]').forEach(function (e) {
      var l = e.dataset.layout, g = e.dataset.gap;
      e.style.display = l === 'grid' ? 'grid' : 'flex';
      if (l === 'row') e.style.flexDirection = 'row';
      if (l === 'col') e.style.flexDirection = 'column';
      if (l === 'grid') e.style.gridTemplateColumns = 'repeat(' + (e.dataset.cols || 2) + ', minmax(0, 1fr))';
      if (g != null) e.style.gap = g + 'px';
    });
    canvas.querySelectorAll('[data-gap]').forEach(function (e) { e.style.gap = e.dataset.gap + 'px'; });
    canvas.querySelectorAll('[data-align]').forEach(function (e) { e.style.alignItems = e.dataset.align; });
    canvas.querySelectorAll('[data-justify]').forEach(function (e) { e.style.justifyContent = e.dataset.justify; });
    canvas.querySelectorAll('[data-flex]').forEach(function (e) { e.style.flex = e.dataset.flex + ' 1 0'; e.style.minWidth = '0'; });
  }

  /* ---------------------------------------------------------------- geometry */
  function crect() { return canvas.getBoundingClientRect(); }
  function rawRect(el) {
    var r = el.getBoundingClientRect(), c = crect();
    return { l: r.left - c.left, t: r.top - c.top, r: r.right - c.left, b: r.bottom - c.top, w: r.width, h: r.height };
  }
  function snapRect(el) {
    var q = rawRect(el);
    var l = Math.round(q.l), t = Math.round(q.t), r = Math.round(q.r), b = Math.round(q.b);
    return { l: l, t: t, r: r, b: b, w: r - l, h: b - t };
  }
  var NORMAL = { right: [1, 0], bottom: [0, 1], left: [-1, 0], top: [0, -1] };
  function parseAnchor(spec) {
    var m = /^(top|right|bottom|left)(?:@(.+))?$/.exec(String(spec).trim());
    if (!m) return null;
    return { side: m[1], off: m[2] === undefined ? null : m[2].trim() };
  }
  function anchorPoint(el, spec) {
    var a = parseAnchor(spec);
    if (!a) return null;
    var r = snapRect(el), horiz = a.side === 'top' || a.side === 'bottom';
    var len = horiz ? r.w : r.h, pos = len / 2, o = a.off;
    if (o !== null) {
      if (/%$/.test(o)) pos = parseFloat(o) / 100 * len;
      else if (/px$/.test(o)) pos = parseFloat(o);
      else if (/^[+-]/.test(o)) pos = len / 2 + parseFloat(o);
      else { var v = parseFloat(o); pos = v <= 1 ? v * len : v; }
    }
    var lo = Math.min(CORNER_SAFE, len / 2), hi = len - lo;
    pos = Math.round(Math.max(lo, Math.min(hi, pos)));
    var p;
    if (a.side === 'left') p = { x: r.l, y: r.t + pos };
    else if (a.side === 'right') p = { x: r.r, y: r.t + pos };
    else if (a.side === 'top') p = { x: r.l + pos, y: r.t };
    else p = { x: r.l + pos, y: r.b };
    p.n = NORMAL[a.side]; p.side = a.side;
    return p;
  }

  /* ------------------------------------------------------------ router */
  function Heap() { this.a = []; }
  Heap.prototype.push = function (c, s) {
    var a = this.a, i = a.length; a.push([c, s]);
    while (i > 0) { var p = (i - 1) >> 1; if (a[p][0] <= a[i][0]) break; var t = a[p]; a[p] = a[i]; a[i] = t; i = p; }
  };
  Heap.prototype.pop = function () {
    var a = this.a, top = a[0], last = a.pop();
    if (a.length) {
      a[0] = last; var i = 0, n = a.length;
      for (;;) {
        var l = 2 * i + 1, r = l + 1, m = i;
        if (l < n && a[l][0] < a[m][0]) m = l;
        if (r < n && a[r][0] < a[m][0]) m = r;
        if (m === i) break; var t = a[m]; a[m] = a[i]; a[i] = t; i = m;
      }
    }
    return top;
  };
  var DX = [1, 0, -1, 0], DY = [0, 1, 0, -1];

  function gridRoute(A1, dir0, B1, obs, placed, clr) {
    var infl = obs.map(function (o) {
      var c = clr;   // shrink the clearance where a port stub ends close to an obstacle
      [A1, B1].forEach(function (P) {
        var dd = Math.max(o.l - P.x, P.x - o.r, o.t - P.y, P.y - o.b);
        if (dd > 0) c = Math.min(c, dd - 1);
      });
      c = Math.max(0, c); o.c = c;
      return { l: o.l - c, t: o.t - c, r: o.r + c, b: o.b + c };
    });
    var xs = {}, ys = {};
    function ax(v) { xs[Math.round(v)] = 1; } function ay(v) { ys[Math.round(v)] = 1; }
    ax(A1.x); ax(B1.x); ay(A1.y); ay(B1.y); ax((A1.x + B1.x) / 2); ay((A1.y + B1.y) / 2);
    obs.forEach(function (o) { ax(o.l - o.c); ax(o.r + o.c); ay(o.t - o.c); ay(o.b + o.c); });
    // channel mid-lines between neighbouring obstacles
    for (var i = 0; i < obs.length; i++) for (var j = 0; j < obs.length; j++) {
      if (i === j) continue;
      var p = obs[i], q = obs[j];
      if (p.r < q.l && q.l - p.r < 260 && q.l - p.r > 2 * clr && p.t < q.b && q.t < p.b) ax((p.r + q.l) / 2);
      if (p.b < q.t && q.t - p.b < 260 && q.t - p.b > 2 * clr && p.l < q.r && q.l < p.r) ay((p.b + q.t) / 2);
    }
    var X = Object.keys(xs).map(Number).filter(function (v) { return v >= 6 && v <= CW - 6; }).sort(function (a, b) { return a - b; });
    var Y = Object.keys(ys).map(Number).filter(function (v) { return v >= 6 && v <= CHH - 6; }).sort(function (a, b) { return a - b; });
    var nx = X.length, ny = Y.length;
    var sx = X.indexOf(Math.round(A1.x)), sy = Y.indexOf(Math.round(A1.y)), ex = X.indexOf(Math.round(B1.x)), ey = Y.indexOf(Math.round(B1.y));
    if (sx < 0 || sy < 0 || ex < 0 || ey < 0) return null;

    function blockedH(y, x1, x2) { for (var k = 0; k < infl.length; k++) { var o = infl[k]; if (y > o.t && y < o.b && x2 > o.l && x1 < o.r) return true; } return false; }
    function blockedV(x, y1, y2) { for (var k = 0; k < infl.length; k++) { var o = infl[k]; if (x > o.l && x < o.r && y2 > o.t && y1 < o.b) return true; } return false; }
    function penalty(horiz, c, a, b) {
      var pen = 0;
      for (var k = 0; k < placed.length; k++) {
        var s = placed[k];
        if (horiz) {
          if (s.h) { if (Math.abs(s.y - c) < 8) { var ov = Math.min(b, s.x2) - Math.max(a, s.x1); if (ov > 0) pen += OVER * ov; } }
          else if (s.x > a && s.x < b && c > s.y1 && c < s.y2) pen += CROSS;
        } else {
          if (!s.h) { if (Math.abs(s.x - c) < 8) { var ov2 = Math.min(b, s.y2) - Math.max(a, s.y1); if (ov2 > 0) pen += OVER * ov2; } }
          else if (s.y > a && s.y < b && c > s.x1 && c < s.x2) pen += CROSS;
        }
      }
      return pen;
    }
    var N = nx * ny * 4, dist = new Float64Array(N).fill(Infinity), prev = new Int32Array(N).fill(-1);
    var heap = new Heap();
    var s0 = ((sx * ny) + sy) * 4 + dir0;
    dist[s0] = 0; heap.push(0, s0);
    while (heap.a.length) {
      var top = heap.pop(), c = top[0], s = top[1];
      if (c > dist[s]) continue;
      var d = s & 3, node = s >> 2, iy = node % ny, ix = (node - iy) / ny;
      for (var nd = 0; nd < 4; nd++) {
        if (nd === ((d + 2) & 3)) continue;
        var jx = ix + DX[nd], jy = iy + DY[nd];
        if (jx < 0 || jy < 0 || jx >= nx || jy >= ny) continue;
        var len, cost;
        if (nd === 0 || nd === 2) {
          var xa = Math.min(X[ix], X[jx]), xb = Math.max(X[ix], X[jx]);
          if (blockedH(Y[iy], xa, xb)) continue;
          len = xb - xa; cost = c + len + penalty(true, Y[iy], xa, xb);
        } else {
          var ya = Math.min(Y[iy], Y[jy]), yb = Math.max(Y[iy], Y[jy]);
          if (blockedV(X[ix], ya, yb)) continue;
          len = yb - ya; cost = c + len + penalty(false, X[ix], ya, yb);
        }
        if (nd !== d) cost += BEND;
        var ns = ((jx * ny) + jy) * 4 + nd;
        if (cost < dist[ns]) { dist[ns] = cost; prev[ns] = s; heap.push(cost, ns); }
      }
    }
    return { X: X, Y: Y, ny: ny, dist: dist, prev: prev, endNode: ex * ny + ey, start: s0 };
  }
  function extract(res, entryDir) {
    // choose best final state at B1; entryDir is the direction travelled by the final stub (into the element)
    var best = -1, bc = Infinity;
    for (var d = 0; d < 4; d++) {
      var s = res.endNode * 4 + d, c = res.dist[s];
      if (c === Infinity) continue;
      if (d !== entryDir) c += BEND;
      if (c < bc) { bc = c; best = s; }
    }
    if (best < 0) return null;
    var pts = [], s2 = best;
    while (s2 !== -1) {
      var node = s2 >> 2, iy = node % res.ny, ix = (node - iy) / res.ny;
      pts.push({ x: res.X[ix], y: res.Y[iy] });
      s2 = res.prev[s2];
    }
    return pts.reverse();
  }
  function cleanPts(pts) {
    var out = [];
    pts.forEach(function (p) {
      var l = out[out.length - 1];
      if (l && l.x === p.x && l.y === p.y) return;
      out.push({ x: p.x, y: p.y });
    });
    var res = [];
    out.forEach(function (p) {
      while (res.length >= 2) {
        var a = res[res.length - 2], b = res[res.length - 1];
        if ((a.x === b.x && b.x === p.x) || (a.y === b.y && b.y === p.y)) res.pop(); else break;
      }
      res.push(p);
    });
    return res;
  }
  function dirIndex(n) { return n[0] === 1 ? 0 : n[1] === 1 ? 1 : n[0] === -1 ? 2 : 3; }

  function viaPoints(start, end, via, firstDir) {
    // explicit waypoints: [x,y] pass-through points, {x:..} vertical lanes, {y:..} horizontal lanes.
    var pts = [start], cur = { x: start.x, y: start.y }, horizNext = (firstDir === 1 || firstDir === 3);
    function move(x, y, horizFirst) {
      if (cur.x === x && cur.y === y) return;
      if (cur.x === x || cur.y === y) { cur = { x: x, y: y }; pts.push(cur); return; }
      var mid = horizFirst ? { x: x, y: cur.y } : { x: cur.x, y: y };
      pts.push(mid); cur = { x: x, y: y }; pts.push(cur);
    }
    via.forEach(function (v) {
      if (Array.isArray(v)) { move(v[0], v[1], !horizNext); horizNext = !horizNext; }
      else if (v.x !== undefined) { move(v.x, cur.y, true); horizNext = false; }
      else if (v.y !== undefined) { move(cur.x, v.y, false); horizNext = true; }
    });
    move(end.x, end.y, !horizNext);
    return pts;
  }

  function polyD(pts, endCut, startCut) {
    // path with rounded corners; ends optionally shortened (for arrow bases)
    var p = pts.map(function (q) { return { x: q.x, y: q.y }; });
    function cut(i, j, by) { // move p[i] toward p[j] by 'by'
      var dx = p[j].x - p[i].x, dy = p[j].y - p[i].y, L = Math.hypot(dx, dy);
      if (L > 0) { p[i].x += dx / L * by; p[i].y += dy / L * by; }
    }
    if (endCut) cut(p.length - 1, p.length - 2, endCut);
    if (startCut) cut(0, 1, startCut);
    var d = 'M' + p[0].x + ' ' + p[0].y;
    for (var i = 1; i < p.length - 1; i++) {
      var P = p[i - 1], C = p[i], N = p[i + 1];
      var l1 = Math.hypot(C.x - P.x, C.y - P.y), l2 = Math.hypot(N.x - C.x, N.y - C.y), r = Math.min(RAD, l1 / 2, l2 / 2);
      var a = { x: C.x - (C.x - P.x) / l1 * r, y: C.y - (C.y - P.y) / l1 * r };
      var b = { x: C.x + (N.x - C.x) / l2 * r, y: C.y + (N.y - C.y) / l2 * r };
      d += 'L' + a.x + ' ' + a.y + 'Q' + C.x + ' ' + C.y + ' ' + b.x + ' ' + b.y;
    }
    d += 'L' + p[p.length - 1].x + ' ' + p[p.length - 1].y;
    return d;
  }
  function arrowD(tip, from, len, half) {
    var dx = tip.x - from.x, dy = tip.y - from.y, L = Math.hypot(dx, dy) || 1, ux = dx / L, uy = dy / L;
    var bx = tip.x - ux * len, by = tip.y - uy * len, px = -uy * half, py = ux * half;
    return 'M' + tip.x + ' ' + tip.y + 'L' + (bx + px) + ' ' + (by + py) + 'L' + (bx - px) + ' ' + (by - py) + 'Z';
  }

  /* ------------------------------------------------------------- connectors */
  var specs = [];
  function connect(a, sa, b, sb, opts) { specs.push({ a: a, sa: sa, b: b, sb: sb, o: opts || {} }); }
  window.connect = connect;
  var links = [];       // drawn connectors (for lint)
  var pills = [];       // label elements

  var OBST_SEL = '.card,.glabel,.note,.metric,.legend,.titlebar,.badge';

  function obstaclesFor(elA, elB) {
    var list = [];
    canvas.querySelectorAll(OBST_SEL).forEach(function (e) {
      if (e.classList.contains('onpath')) return;
      if (e.contains(elA) || e.contains(elB)) { if (e !== elA && e !== elB) return; }
      list.push(snapRect(e));
    });
    [elA, elB].forEach(function (e) { list.push(snapRect(e)); });
    return list;
  }

  function segsOf(pts) {
    var out = [];
    for (var i = 0; i < pts.length - 1; i++) {
      var p = pts[i], q = pts[i + 1];
      if (p.y === q.y) out.push({ h: true, y: p.y, x1: Math.min(p.x, q.x), x2: Math.max(p.x, q.x) });
      else if (p.x === q.x) out.push({ h: false, x: p.x, y1: Math.min(p.y, q.y), y2: Math.max(p.y, q.y) });
    }
    return out;
  }


  function adaptiveStub(P, own, obs) {
    // free distance from the port to the next obstacle along its normal; short stubs when the gap is tight
    var free = Infinity, nx = P.n[0], ny = P.n[1];
    obs.forEach(function (o) {
      if (o.l === own.l && o.t === own.t && o.r === own.r && o.b === own.b) return;
      var d = Infinity;
      if (ny === 0 && P.y > o.t && P.y < o.b) d = nx > 0 ? o.l - P.x : P.x - o.r;
      if (nx === 0 && P.x > o.l && P.x < o.r) d = ny > 0 ? o.t - P.y : P.y - o.b;
      if (d >= 0 && d < free) free = d;
    });
    return free < 2 * STUB ? Math.max(6, Math.floor(free / 2)) : STUB;
  }
  function autoAlign(elA, sa, elB, sb) {
    // facing sides without explicit offsets: pick one coordinate inside both extents so the line runs straight
    var a = parseAnchor(sa), b = parseAnchor(sb);
    if (!a || !b || a.off !== null || b.off !== null) return null;
    var opp = { left: 'right', right: 'left', top: 'bottom', bottom: 'top' };
    if (opp[a.side] !== b.side) return null;
    var ra = snapRect(elA), rb = snapRect(elB), horiz = (a.side === 'left' || a.side === 'right');
    var lo = horiz ? Math.max(ra.t, rb.t) : Math.max(ra.l, rb.l), hi = horiz ? Math.min(ra.b, rb.b) : Math.min(ra.r, rb.r);
    var sLo = Math.max(lo, (horiz ? Math.max(ra.t, rb.t) : Math.max(ra.l, rb.l)) + CORNER_SAFE), sHi = hi - CORNER_SAFE;
    if (sHi < sLo) return null;
    var cA = horiz ? ra.t + ra.h / 2 : ra.l + ra.w / 2, cB = horiz ? rb.t + rb.h / 2 : rb.l + rb.w / 2;
    var c = Math.round(Math.max(sLo, Math.min(sHi, (cA + cB) / 2)));
    function off(r) { return '@' + (c - (horiz ? r.t : r.l)) + 'px'; }
    return [a.side + off(ra), b.side + off(rb)];
  }

  function buildLinks() {
    var svgNS = 'http://www.w3.org/2000/svg';
    var svg = document.createElementNS(svgNS, 'svg');
    svg.setAttribute('id', 'kit-links'); svg.setAttribute('viewBox', '0 0 ' + CW + ' ' + CHH);
    canvas.appendChild(svg);
    var labels = document.createElement('div'); labels.id = 'kit-labels'; canvas.appendChild(labels);
    var placed = [];

    specs.forEach(function (sp, idx) {
      var o = sp.o, style = o.style || 'data', arrow = o.arrow || 'end';
      var elA = typeof sp.a === 'string' ? document.getElementById(sp.a) : sp.a;
      var elB = typeof sp.b === 'string' ? document.getElementById(sp.b) : sp.b;
      if (!elA || !elB) { problems.push({ check: 'unknown-id', msg: 'connect #' + idx + ': element not found (' + sp.a + ' -> ' + sp.b + ')', el: '' }); return; }
      var sa = sp.sa, sb = sp.sb;
      if (o.straight !== false) { var al = autoAlign(elA, sa, elB, sb); if (al) { sa = al[0]; sb = al[1]; } }
      var A = anchorPoint(elA, sa), B = anchorPoint(elB, sb);
      if (!A || !B) { problems.push({ check: 'bad-anchor', msg: 'connect #' + idx + ': bad side "' + sp.sa + '" / "' + sp.sb + '"', el: '' }); return; }
      var obsAll = obstaclesFor(elA, elB);
      var stubA = o.stub || adaptiveStub(A, snapRect(elA), obsAll), stubB = o.stub || adaptiveStub(B, snapRect(elB), obsAll);
      var A1 = { x: A.x + A.n[0] * stubA, y: A.y + A.n[1] * stubA };
      var B1 = { x: B.x + B.n[0] * stubB, y: B.y + B.n[1] * stubB };
      var d0 = dirIndex(A.n), entry = dirIndex([-B.n[0], -B.n[1]]);
      var mid;
      if (o.via && o.via.length) {
        mid = viaPoints(A1, B1, o.via, d0);
      } else {
        var obs = obsAll, res = null;
        [10, 6, 3].some(function (clr) {
          var r = gridRoute(A1, d0, B1, obs, placed, clr);
          var p = r && extract(r, entry);
          if (p) { res = p; return true; }
          return false;
        });
        if (!res) {
          problems.push({ check: 'route-failed', msg: 'connect #' + idx + ' (' + sp.a + ' -> ' + sp.b + '): no clear route, use via', el: '' });
          res = viaPoints(A1, B1, [], d0);
        }
        mid = res;
      }
      var pts = cleanPts([A].concat(mid, [B]));
      // keep first/last exactly at the anchors
      pts[0] = { x: A.x, y: A.y }; pts[pts.length - 1] = { x: B.x, y: B.y };
      segsOf(pts).forEach(function (s) { placed.push(s); });

      var g = document.createElementNS(svgNS, 'g');
      g.setAttribute('class', 'link link-' + style); g.setAttribute('data-link', idx);
      var endArrow = (arrow === 'end' || arrow === 'both'), startArrow = (arrow === 'both');
      var dLine = polyD(pts, endArrow ? ARROW - 1 : 0, startArrow ? ARROW - 1 : 0);
      var glow = document.createElementNS(svgNS, 'path'); glow.setAttribute('class', 'glow'); glow.setAttribute('d', dLine);
      var ln = document.createElementNS(svgNS, 'path'); ln.setAttribute('class', 'ln'); ln.setAttribute('d', dLine);
      g.appendChild(glow); g.appendChild(ln);
      function arrowAt(tip, from) {
        var ar = document.createElementNS(svgNS, 'path'); ar.setAttribute('class', 'arrow'); ar.setAttribute('d', arrowD(tip, from, ARROW, 5.2)); g.appendChild(ar);
      }
      function portAt(p) { var c = document.createElementNS(svgNS, 'circle'); c.setAttribute('class', 'port'); c.setAttribute('cx', p.x); c.setAttribute('cy', p.y); c.setAttribute('r', 3.5); g.appendChild(c); }
      var n = pts.length;
      if (endArrow) arrowAt(pts[n - 1], pts[n - 2]); else if (style !== 'note') portAt(pts[n - 1]);
      if (startArrow) arrowAt(pts[0], pts[1]); else if (style !== 'note') portAt(pts[0]);
      svg.appendChild(g);

      links.push({ idx: idx, from: elA, to: elB, sa: sa, sb: sb, A: pts[0], B: pts[n - 1], pts: pts, style: style, spec: sp, g: g });
    });

    // labels after all routes exist, so they can avoid other lines
    links.forEach(function (lk) {
      var o = lk.spec.o;
      if (!o.label && !o.badge) return;
      var pill = document.createElement('div');
      pill.className = 'pill' + (o.mono ? ' mono' : '') + (o.label ? '' : ' nolab');
      pill.style.setProperty('--lc', 'var(--l-' + (lk.style === 'note' ? 'control' : lk.style) + ')');
      pill.innerHTML = (o.badge ? '<span class="pb">' + esc(o.badge) + '</span>' : '') + (o.label ? '<span>' + esc(o.label) + '</span>' : '');
      labels.appendChild(pill);
      lk.pill = pill;
    });
    var okPills = [];
    links.forEach(function (lk) {
      if (!lk.pill) return;
      placePill(lk, okPills);
      okPills.push(lk);
    });
    pills = okPills;
  }

  function placePill(lk, doneLinks) {
    var pill = lk.pill, w = pill.offsetWidth, h = pill.offsetHeight, o = lk.spec.o;
    var obs = [];
    canvas.querySelectorAll(OBST_SEL).forEach(function (e) { if (!e.classList.contains('onpath')) obs.push(snapRect(e)); });
    doneLinks.forEach(function (d) { var r = rawRect(d.pill); obs.push({ l: r.l, t: r.t, r: r.r, b: r.b }); });
    var otherSegs = [];
    links.forEach(function (d) { if (d !== lk) segsOf(d.pts).forEach(function (s) { otherSegs.push(s); }); });
    var segs = [];
    for (var i = 0; i < lk.pts.length - 1; i++) {
      var p = lk.pts[i], q = lk.pts[i + 1], L = Math.hypot(q.x - p.x, q.y - p.y);
      segs.push({ i: i, p: p, q: q, L: L, horiz: p.y === q.y });
    }
    // candidate (segment, t) ordered by preference
    var cands = [], ts = [0.5, 0.42, 0.58, 0.33, 0.67, 0.25, 0.75, 0.15, 0.85];
    var want = typeof o.labelAt === 'number' ? o.labelAt : null;
    segs.forEach(function (s) {
      var half = (s.horiz ? w : h) / 2;
      var arrAt0 = lk.spec.o.arrow === 'both', arrAt1 = lk.spec.o.arrow === 'both' || !lk.spec.o.arrow || lk.spec.o.arrow === 'end';
      // room to the ends: a corner, or a port (keep the arrow head and some line visible)
      var m0 = half + (s.i === 0 ? (arrAt0 ? ARROW + 10 : 10) : RAD + 4), m1 = half + (s.i === segs.length - 1 ? (arrAt1 ? ARROW + 10 : 10) : RAD + 4);
      var usable = s.L - m0 - m1;
      if (usable < 0) return;
      ts.forEach(function (t) {
        var d = m0 + usable * t;
        cands.push({ s: s, d: d, score: (want !== null ? Math.abs((segStart(segs, s) + d) / totalLen(segs) - want) * 400 : 0) + Math.abs(t - 0.5) * 40 - Math.min(s.L, 300) * 0.15 + (s.i === 0 || s.i === segs.length - 1 ? 25 : 0) });
      });
    });
    cands.sort(function (a, b) { return a.score - b.score; });
    var chosen = null;
    for (var k = 0; k < cands.length && !chosen; k++) {
      var c = cands[k], s = c.s, ux = (s.q.x - s.p.x) / s.L, uy = (s.q.y - s.p.y) / s.L;
      var cx = s.p.x + ux * c.d, cy = s.p.y + uy * c.d;
      var rect = { l: Math.round(cx - w / 2), t: Math.round(cy - h / 2) }; rect.r = rect.l + w; rect.b = rect.t + h;
      if (rect.l < SAFE || rect.t < SAFE || rect.r > CW - SAFE || rect.b > CHH - SAFE) continue;
      var hit = obs.some(function (q) { return rect.l - MIN_GAP < q.r && rect.r + MIN_GAP > q.l && rect.t - MIN_GAP < q.b && rect.b + MIN_GAP > q.t; });
      if (hit) continue;
      var cross = otherSegs.some(function (q) {
        if (q.h) return q.y > rect.t - 2 && q.y < rect.b + 2 && q.x2 > rect.l - 2 && q.x1 < rect.r + 2;
        return q.x > rect.l - 2 && q.x < rect.r + 2 && q.y2 > rect.t - 2 && q.y1 < rect.b + 2;
      });
      if (cross && k < cands.length - 1) { if (!chosen && !c.soft) { c.soft = true; if (!lk._fallback) lk._fallback = rect; } continue; }
      chosen = rect;
    }
    if (!chosen && lk._fallback) chosen = lk._fallback;
    if (!chosen) {
      var s0 = segs.slice().sort(function (a, b) { return b.L - a.L; })[0];
      chosen = { l: Math.round((s0.p.x + s0.q.x) / 2 - w / 2), t: Math.round((s0.p.y + s0.q.y) / 2 - h / 2) };
      addProblem('label-unplaced', 'connector #' + lk.idx + ': no free place for its label', pill);
    }
    pill.style.left = chosen.l + 'px'; pill.style.top = chosen.t + 'px';
  }
  function segStart(segs, s) { var t = 0; for (var i = 0; i < s.i; i++) t += segs[i].L; return t; }
  function totalLen(segs) { return segs.reduce(function (a, s) { return a + s.L; }, 0) || 1; }

  /* ------------------------------------------------------------------- lint */
  var COMP_SEL = '.card,.group,.glabel,.note,.metric,.legend,.titlebar,.badge,.chip,.pill';
  var HOST_SEL = COMP_SEL;

  function parentComp(el) { var p = el.parentElement; while (p && p !== canvas) { if (p.matches(COMP_SEL)) return p; p = p.parentElement; } return null; }
  function gapBetween(a, b) {
    var dx = Math.max(b.l - a.r, a.l - b.r), dy = Math.max(b.t - a.b, a.t - b.b);
    return Math.max(dx, dy);
  }
  function inside(inner, outer, tol) { return inner.l >= outer.l - tol && inner.t >= outer.t - tol && inner.r <= outer.r + tol && inner.b <= outer.b + tol; }

  function loadBanned() {
    var url = '../banned-words.txt';
    var meta = document.querySelector('meta[name="banned-words-url"]');
    if (meta) url = meta.content;
    return fetch(url).then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.text(); });
  }
  function wordRegex(w) {
    var parts = w.trim().split(/\s+/).map(function (p) { return p.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); });
    return new RegExp('(^|[^A-Za-z0-9_])(' + parts.join('\\s+') + ')(?![A-Za-z0-9_])', 'i');
  }

  function runLint(bannedText, bannedErr) {
    var comps = Array.prototype.slice.call(canvas.querySelectorAll(COMP_SEL));
    var seenIds = {};
    document.querySelectorAll('[id]').forEach(function (e) { if (seenIds[e.id]) addProblem('duplicate-id', 'duplicate id "' + e.id + '"', e); seenIds[e.id] = 1; });

    // (a) text overflow: scroll size, and real text rectangles against their host component
    comps.forEach(function (c) {
      if (c.scrollWidth > c.clientWidth + 1 || c.scrollHeight > c.clientHeight + 1) {
        addProblem('overflow', 'content overflows its box (scroll ' + c.scrollWidth + 'x' + c.scrollHeight + ' > client ' + c.clientWidth + 'x' + c.clientHeight + ')', c);
      }
    });
    var all = Array.prototype.slice.call(canvas.querySelectorAll('*')).filter(function (e) { return !e.closest('svg') || e.tagName.toLowerCase() === 'svg' ? true : false; });
    all.forEach(function (e) {
      if (e.closest('svg') && e.tagName.toLowerCase() !== 'svg') return;
      var hasText = false, rects = [];
      for (var i = 0; i < e.childNodes.length; i++) {
        var n = e.childNodes[i];
        if (n.nodeType === 3 && n.textContent.trim()) {
          hasText = true;
          var rg = document.createRange(); rg.selectNodeContents(n);
          for (var rr of rg.getClientRects()) rects.push(rr);
        }
      }
      if (!hasText || !rects.length) return;
      // (e) small text
      var fs = parseFloat(getComputedStyle(e).fontSize);
      if (fs < MIN_FONT - 0.001) addProblem('small-text', 'font-size ' + fs + 'px < ' + MIN_FONT + 'px', e);
      // text vs. host box
      var host = e.closest(HOST_SEL);
      if (!host) return;
      var cr = crect(), hr = rawRect(host);
      rects.forEach(function (r) {
        var q = { l: r.left - cr.left, t: r.top - cr.top, r: r.right - cr.left, b: r.bottom - cr.top };
        if (q.l < hr.l - 0.5 || q.r > hr.r + 0.5 || q.t < hr.t - 4 || q.b > hr.b + 4) {   // vertical: glyph boxes may exceed line boxes by a few px
          if (!e.__flagged) { e.__flagged = true; addProblem('text-outside', 'text sticks out of its component (text ' + Math.round(q.l) + ',' + Math.round(q.t) + '-' + Math.round(q.r) + ',' + Math.round(q.b) + ' vs box ' + Math.round(hr.l) + ',' + Math.round(hr.t) + '-' + Math.round(hr.r) + ',' + Math.round(hr.b) + ')', host); }
        }
      });
    });
    // children must stay inside their parent component
    comps.forEach(function (c) {
      var p = parentComp(c);
      if (!p || c.classList.contains('pill')) return;
      if (!inside(rawRect(c), rawRect(p), 0.5)) addProblem('escapes-parent', 'component lies (partly) outside its parent ' + describe(p), c);
    });

    // (b) overlap / gap between sibling components
    var groupsOfSiblings = new Map();
    comps.forEach(function (c) {
      if (c.classList.contains('pill')) return;
      var p = parentComp(c) || canvas;
      if (!groupsOfSiblings.has(p)) groupsOfSiblings.set(p, []);
      groupsOfSiblings.get(p).push(c);
    });
    groupsOfSiblings.forEach(function (list) {
      for (var i = 0; i < list.length; i++) for (var j = i + 1; j < list.length; j++) {
        var a = list[i], b = list[j];
        var g = gapBetween(rawRect(a), rawRect(b));
        if (g < MIN_GAP - 0.01) addProblem('overlap', (g < 0 ? 'overlaps ' : 'only ' + g.toFixed(1) + 'px from ') + describe(b) + ' (min gap ' + MIN_GAP + 'px)', a);
      }
    });
    // labels vs components and vs each other
    var pillEls = Array.prototype.slice.call(canvas.querySelectorAll('.pill'));
    pillEls.forEach(function (p, i) {
      var pr = rawRect(p);
      comps.forEach(function (c) {
        if (c === p || c.classList.contains('group') || c.contains(p) || p.contains(c)) return;
        if (c.classList.contains('pill') && pillEls.indexOf(c) < i) return;
        if (c.classList.contains('chip') || c.classList.contains('badge') && c.closest('.card')) { /* chips inside cards are covered by their card */ if (c.closest('.card')) return; }
        var g = gapBetween(pr, rawRect(c));
        if (g < MIN_GAP - 0.01) addProblem('label-overlap', 'connector label ' + (g < 0 ? 'overlaps ' : 'only ' + g.toFixed(1) + 'px from ') + describe(c), p);
      });
      if (pr.l < SAFE - 0.5 || pr.t < SAFE - 0.5 || pr.r > CW - SAFE + 0.5 || pr.b > CHH - SAFE + 0.5) addProblem('outside-canvas', 'label outside the safe area', p);
    });

    // (c) connector endpoints on the live element edge, and routes that cut through components
    links.forEach(function (lk) {
      [['from', lk.from, lk.A, lk.sa], ['to', lk.to, lk.B, lk.sb]].forEach(function (e) {
        var r = rawRect(e[1]), p = e[2], side = parseAnchor(e[3]).side, tol = 0.5 + 1e-6, err;
        var edgeCoord = side === 'left' ? r.l : side === 'right' ? r.r : side === 'top' ? r.t : r.b;
        var onEdge = (side === 'left' || side === 'right') ? Math.abs(p.x - edgeCoord) : Math.abs(p.y - edgeCoord);
        var along = (side === 'left' || side === 'right') ? (p.y >= r.t - tol && p.y <= r.b + tol) : (p.x >= r.l - tol && p.x <= r.r + tol);
        if (onEdge > tol || !along) addProblem('endpoint', 'connector #' + lk.idx + ' ' + e[0] + '-end is ' + onEdge.toFixed(1) + 'px off the ' + side + ' edge of ' + describe(e[1]) + (along ? '' : ' (outside its extent)'), e[1]);
      });
      // route vs obstacles
      var segs = segsOf(lk.pts);
      canvas.querySelectorAll(OBST_SEL).forEach(function (o) {
        if (o.classList.contains('onpath')) return;
        if (o.contains(lk.from) && o !== lk.from || o.contains(lk.to) && o !== lk.to) return;
        var r = rawRect(o), hit = segs.some(function (s) {
          if (s.h) return s.y > r.t + 0.5 && s.y < r.b - 0.5 && s.x2 > r.l + 0.5 && s.x1 < r.r - 0.5;
          return s.x > r.l + 0.5 && s.x < r.r - 0.5 && s.y2 > r.t + 0.5 && s.y1 < r.b - 0.5;
        });
        if (hit) addProblem('connector-crosses', 'connector #' + lk.idx + ' (' + (lk.from.id || '?') + ' to ' + (lk.to.id || '?') + ') runs through ' + describe(o), o);
      });
      lk.pts.forEach(function (p) { if (p.x < 0 || p.y < 0 || p.x > CW || p.y > CHH) addProblem('outside-canvas', 'connector #' + lk.idx + ' leaves the canvas', lk.from); });
    });

    // (d) anything outside the canvas / safe area
    comps.forEach(function (c) {
      var r = rawRect(c);
      if (c.classList.contains('pill')) return;
      if (r.l < SAFE - 0.5 || r.t < SAFE - 0.5 || r.r > CW - SAFE + 0.5 || r.b > CHH - SAFE + 0.5) addProblem('outside-canvas', 'component outside the canvas safe area (' + SAFE + 'px margin): ' + Math.round(r.l) + ',' + Math.round(r.t) + '-' + Math.round(r.r) + ',' + Math.round(r.b), c);
    });
    if (canvas.scrollWidth > CW || canvas.scrollHeight > CHH) addProblem('outside-canvas', 'something extends beyond the 1600x900 canvas (scroll ' + canvas.scrollWidth + 'x' + canvas.scrollHeight + ')', canvas);

    // fonts really loaded
    [['600 20px "Rajdhani"', 'Rajdhani SemiBold'], ['500 15px "Inter"', 'Inter Medium'], ['600 15px "Inter"', 'Inter SemiBold'], ['400 13px "DejaVu Sans Mono"', 'DejaVu Sans Mono']].forEach(function (f) {
      if (!document.fonts.check(f[0])) addProblem('font', f[1] + ' is not loaded', canvas);
    });

    // (f) banned words
    var allow = {};
    var am = document.querySelector('meta[name="lint-allow-words"]');
    if (am) am.content.split(',').forEach(function (w) { w = w.trim().toLowerCase(); if (w) allow[w] = 1; });
    if (bannedErr) addProblem('banned-words', 'cannot read banned-words.txt (' + bannedErr + '); render with render.sh or serve over http', canvas);
    else {
      var words = bannedText.split(/\r?\n/).map(function (s) { return s.replace(/#.*/, '').trim(); }).filter(Boolean);
      words = [].concat.apply([], words.map(function (l) { return l.indexOf(',') >= 0 ? l.split(',') : [l]; })).map(function (s) { return s.trim(); }).filter(Boolean);
      var text = [];
      canvas.querySelectorAll('*').forEach(function (e) {
        if (e.closest('svg') && e.tagName.toLowerCase() !== 'svg' && e.tagName.toLowerCase() !== 'text' && e.tagName.toLowerCase() !== 'title') return;
        ['title', 'aria-label', 'alt'].forEach(function (a) { if (e.getAttribute && e.getAttribute(a)) text.push({ t: e.getAttribute(a), e: e }); });
        for (var i = 0; i < e.childNodes.length; i++) if (e.childNodes[i].nodeType === 3 && e.childNodes[i].textContent.trim()) text.push({ t: e.childNodes[i].textContent, e: e });
      });
      text.push({ t: document.title, e: document.head });
      words.forEach(function (w) {
        if (allow[w.toLowerCase()]) return;
        var re = wordRegex(w);
        text.forEach(function (x) { if (re.test(x.t)) addProblem('banned-word', 'banned word "' + w + '" in "' + x.t.trim().replace(/\s+/g, ' ').slice(0, 50) + '"', x.e); });
      });
    }
    runtimeErrors.forEach(function (m) { addProblem('js-error', m, canvas); });

    // report
    var out = problems.map(function (p) { return { check: p.check, msg: p.msg, el: p.el }; });
    var report = {
      ok: out.length === 0, theme: THEME, problems: out.length,
      stats: { components: comps.length, connectors: links.length, labels: pillEls.length },
      issues: out
    };
    if (DEBUG) problems.forEach(function (p) {
      if (p._el && p._el.classList && p._el !== canvas) p._el.classList.add('lint-bad');
    });
    return report;
  }

  /* ------------------------------------------------------------------- boot */
  function boot() {
    canvas = document.getElementById('canvas');
    if (!canvas) { document.body.insertAdjacentHTML('beforeend', '<pre id="lint">{"ok":false,"issues":[{"check":"js-error","msg":"no #canvas"}]}</pre>'); return; }
    injectSprite();
    var pre = document.getElementById('lint');
    if (!pre) { pre = document.createElement('pre'); pre.id = 'lint'; document.body.appendChild(pre); }
    var fontLoads = ['600 20px "Rajdhani"', '500 15px "Inter"', '600 15px "Inter"', '400 13px "DejaVu Sans Mono"'].map(function (f) { return document.fonts.load(f, 'Ag0'); });
    var bannedP = loadBanned().then(function (t) { return { t: t }; }, function (e) { return { err: String(e && e.message || e) }; });
    Promise.all(fontLoads.concat([document.fonts.ready])).then(function () { return bannedP; }).then(function (b) {
      try {
        expand();
        // wait for layout to settle after expansion
        return new Promise(function (res) { setTimeout(res, 30); }).then(function () {   // timers, not rAF: rAF may never fire in headless virtual time
          buildLinks();
          document.dispatchEvent(new CustomEvent('kit:drawn'));
          return new Promise(function (res) { setTimeout(res, 0); });
        }).then(function () {
          var report = runLint(b.t || '', b.err);
          pre.textContent = JSON.stringify(report, null, 1);
          document.documentElement.setAttribute('data-kit', report.ok ? 'ok' : 'problems');
        });
      } catch (e) {
        pre.textContent = JSON.stringify({ ok: false, problems: 1, issues: [{ check: 'js-error', msg: String(e && e.stack || e) }] });
        document.documentElement.setAttribute('data-kit', 'problems');
      }
    }).catch(function (e) {
      pre.textContent = JSON.stringify({ ok: false, problems: 1, issues: [{ check: 'js-error', msg: String(e && e.stack || e) }] });
      document.documentElement.setAttribute('data-kit', 'problems');
    });
  }
  window.Kit = { connect: connect, icons: Object.keys(ICONS), problems: problems };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();
