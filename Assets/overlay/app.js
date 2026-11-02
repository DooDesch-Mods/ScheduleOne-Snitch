(function () {
  var $ = function (id) { return document.getElementById(id); };
  var ROW_H = 34;
  var NAME_MAX = 30;    // character budget of the name column, so a row never wraps
  var rowEls = {};      // rider key -> { el, ... }
  var lastOrder = [];
  var panelEls = {};    // panel id -> element
  var state = null;
  var phoneShown = false;
  var dragging = false;

  function call(name, arg) {
    try { return s1.call(name, arg === undefined ? '' : String(arg)); }
    catch (e) { return 'error: ' + e; }
  }

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
  }

  function setText(e, t) { if (e.textContent !== t) e.textContent = t; }
  function setCls(e, c) { if (e.className !== c) e.className = c; }
  function setShow(e, on) { var v = on ? '' : 'none'; if (e.style.display !== v) e.style.display = v; }

  function ms(v) { return v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2); }

  // Digit colour on a team colour: white or navy, whichever contrasts more (the dashboard's inkOn).
  var NAVY = '#14213D';
  function lin(c) {
    var s = c / 255;
    return s <= 0.04045 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  }
  function luminance(hex) {
    var n = parseInt(hex.slice(1), 16);
    return 0.2126 * lin((n >> 16) & 255) + 0.7152 * lin((n >> 8) & 255) + 0.0722 * lin(n & 255);
  }
  function inkOn(color) {
    var l = luminance(color);
    var white = 1.05 / (l + 0.05);
    var navy = (l + 0.05) / (luminance(NAVY) + 0.05);
    return white >= navy ? '#FFFFFF' : NAVY;
  }

  function ago(sec) {
    if (sec < 60) return sec + ' s';
    var m = Math.floor(sec / 60);
    return m + ' min ' + (sec - m * 60) + ' s';
  }

  // ---- header controls ----
  var winBtns = document.querySelectorAll('.segbtn');
  for (var i = 0; i < winBtns.length; i++) {
    (function (b) { b.onclick = function () { call('ui.window', b.getAttribute('data-w')); }; })(winBtns[i]);
  }
  $('toggle').onclick = function () {
    call('ui.mode', state && state.mode === 'expanded' ? 'compact' : 'expanded');
  };
  // The Standings window is the hub of the others, so its close button steps back to the compact card.
  $('closecard').onclick = function () { call('ui.mode', 'compact'); };
  $('run').onclick = function () { call('snitch.sampling', state && state.active ? 'stop' : 'start'); };
  $('reset').onclick = function () { call('snitch.sampling', 'reset'); };
  $('dash').onclick = function () { call('snitch.dashboard'); };
  $('phone').onclick = function () { call('snitch.phone', state && state.phone && (state.phone.on || state.phone.want) ? '0' : '1'); };

  // ---- standings ----
  // The bib is two boxes: the outer one is the white ring with a hairline in the team colour, the inner one the
  // team-coloured tile that carries the number.
  function buildBib(cls) {
    var outer = el('div', cls);
    var inner = el('div', cls + 'i');
    outer.appendChild(inner);
    return { outer: outer, inner: inner };
  }

  function paintBib(b, number, color) {
    setText(b.inner, String(number));
    b.outer.style.borderColor = color;
    b.inner.style.backgroundColor = color;
    b.inner.style.color = inkOn(color);
  }

  function buildRow(r) {
    var row = el('div', 'row');
    var mark = el('div', 'nomark');
    var bib = buildBib('bib');
    var name = el('div', 'name');
    var bar = el('div', 'bar');
    var fill = el('div', 'fill');
    bar.appendChild(fill);
    var m = el('div', 'ms');
    var g = el('div', 'gap');
    var gt = el('div', 'gt');
    g.appendChild(gt);
    row.appendChild(mark);
    row.appendChild(bib.outer);
    row.appendChild(name);
    row.appendChild(bar);
    row.appendChild(m);
    row.appendChild(g);
    return { el: row, mark: mark, bib: bib, name: name, fill: fill, ms: m, gt: gt, y: 0 };
  }

  function updateRow(o, r, i, scale) {
    var isLeader = i === 0 && !r.dnf;
    setCls(o.mark, isLeader ? 'mark' : 'nomark');
    setCls(o.el, r.dnf ? 'row dnf' : isLeader ? 'row lead' : 'row');
    paintBib(o.bib, r.bib, r.c);
    var full = r.g + (r.n && r.n !== r.g ? ' ' + r.n : '');
    setText(o.name, full.length > NAME_MAX ? full.substring(0, NAME_MAX - 3) + '...' : full);
    var w = r.dnf ? 0 : Math.max(0.5, Math.min(100, (r.ms / scale) * 100));
    // The fill is always full width and scaled: a transform is repainted, a changed width lays the page out again.
    var sx = String(Math.round(w) / 100);
    if (o.sx !== sx) { o.sx = sx; o.fill.style.transform = 'scaleX(' + sx + ')'; }
    if (o.fc !== r.c) { o.fc = r.c; o.fill.style.backgroundColor = r.c; }
    setText(o.ms, r.dnf ? '' : ms(r.ms) + ' ms');
    if (r.dnf) setText(o.gt, 'DNF ' + ago(r.silent));
    else if (isLeader) setText(o.gt, 'LEADER');
    else setText(o.gt, '+' + r.gap.toFixed(2) + ' ms');
    setCls(o.gt, r.dnf ? 'gt red' : isLeader ? 'gt chip' : 'gt');
  }

  function renderRows(st) {
    var host = $('rows');
    var riders = st.riders || [];
    var scale = st.scale > 0 ? st.scale : 1;
    host.setAttribute('data-scale', String(scale));

    if (riders.length === 0) {
      // Start/Stop lives in the expanded view, so compact points at the key that opens it.
      var msg = st.active ? 'No sections reported yet.'
        : st.mode === 'expanded' ? 'Sampling is stopped. Press Start.'
        : 'Sampling stopped - ' + st.key + ' to open';
      if (!host.getAttribute('data-empty')) {
        host.replaceChildren();
        host.appendChild(el('div', 'empty', msg));
        host.setAttribute('data-empty', '1');
        rowEls = {};
        lastOrder = [];
      } else if (host.firstChild) {
        setText(host.firstChild, msg);
      }
      return;
    }
    if (host.getAttribute('data-empty')) { host.replaceChildren(); host.removeAttribute('data-empty'); }

    var order = [];
    var seen = {};
    for (var i = 0; i < riders.length; i++) {
      var r = riders[i];
      order.push(r.k);
      seen[r.k] = true;
      var o = rowEls[r.k];
      if (!o) { o = buildRow(r); rowEls[r.k] = o; host.appendChild(o.el); }
      updateRow(o, r, i, scale);
    }
    for (var k in rowEls) {
      if (!seen[k]) { host.removeChild(rowEls[k].el); delete rowEls[k]; }
    }

    // Reorder the DOM only when the ranking changed, and slide the rows that moved.
    var same = order.length === lastOrder.length;
    for (var j = 0; same && j < order.length; j++) if (order[j] !== lastOrder[j]) same = false;
    if (same) return;

    var oldPos = {};
    for (var a = 0; a < lastOrder.length; a++) oldPos[lastOrder[a]] = a;
    for (var b = 0; b < order.length; b++) host.appendChild(rowEls[order[b]].el);
    lastOrder = order;

    // Only a row that was already on the table and changed rank slides; a new row appears in place.
    var moved = [];
    for (var c = 0; c < order.length; c++) {
      var was = oldPos[order[c]];
      if (was === undefined || was === c) continue;
      var e = rowEls[order[c]].el;
      e.style.transition = 'none';
      e.style.translate = '0px ' + ((was - c) * ROW_H) + 'px';
      moved.push(e);
    }
    if (moved.length) {
      var go = function () {
        for (var m = 0; m < moved.length; m++) {
          moved[m].style.transition = 'translate 180ms ease-out';
          moved[m].style.translate = '0px 0px';
        }
      };
      if (typeof setTimeout === 'function') setTimeout(go, 16); else go();
    }
  }

  // ---- window manager ----
  // Every window sits in #desk at an absolute position. The layout is kept in screen fractions (so it survives a
  // resolution change) and stored by the host as one string: "id=x,y,w,h,v;..." with v 1 open, 0 closed. The entry
  // "c" is the compact card (x its left edge, y its bottom edge). Windows are laid out from it on every change of
  // mode or viewport and written back once a drag or resize ends, never on every move.
  var MIN_STANDINGS = [580, 280];
  var MIN_OTHER = [240, 120];
  var CARD_W = 760;
  var layout = {};      // id -> { x, y, w, h, v }
  var wins = {};        // id -> { id, el, bar, body, grip, r }
  var modIds = [];      // panel window ids in registration order, the default grid slots
  var titles = {};      // window id -> title, for the Windows strip
  var zTop = 10;
  var seq = 0;          // numbers the windows, so scripted checks can address them by id
  var drag = null;
  var flushHeld = function () {};
  var lastVw = 0;
  var lastVh = 0;
  var curMode = '';

  function safeId(id) { return String(id).replace(/[=;,]/g, '_'); }

  function viewport() {
    var w = typeof innerWidth === 'number' ? innerWidth : 0;
    var h = typeof innerHeight === 'number' ? innerHeight : 0;
    if (w <= 0 || h <= 0) {
      var r = $('desk').getBoundingClientRect();
      w = r.width;
      h = r.height;
    }
    return { w: w, h: h };
  }

  function loadLayout(raw) {
    layout = {};
    var parts = String(raw || '').split(';');
    for (var i = 0; i < parts.length; i++) {
      var eq = parts[i].indexOf('=');
      if (eq <= 0) continue;
      var f = parts[i].substring(eq + 1).split(',');
      if (f.length !== 5) continue;
      layout[parts[i].substring(0, eq)] = {
        x: Number(f[0]), y: Number(f[1]), w: Number(f[2]), h: Number(f[3]), v: f[4] === '0' ? 0 : 1
      };
    }
  }

  function saveLayout() {
    var out = [];
    for (var id in layout) {
      var e = layout[id];
      out.push(id + '=' + e.x.toFixed(4) + ',' + e.y.toFixed(4) + ',' + e.w.toFixed(4) + ',' + e.h.toFixed(4) + ',' + e.v);
    }
    var r = call('layout.set', out.join(';'));
    if (r !== 'ok') call('page.log', 'layout write answered ' + r);
  }

  function isOpen(id) { var e = layout[id]; return !e || e.v !== 0; }

  function minOf(id) { return id === 'standings' ? MIN_STANDINGS : MIN_OTHER; }

  // The first-run layout, in 1920x1080 css px (the page is authored against the canvas reference): Standings and
  // States stacked on the left, the mod windows in a 3 x 3 grid to their right, a small cascade after nine.
  function defaultRect(id, vw, vh) {
    var s = Math.min(1, vh / 1080, vw / 1920);
    var r;
    if (id === 'standings') r = { x: 24, y: 24, w: 760, h: 560 };
    else if (id === 'states') r = { x: 24, y: 600, w: 760, h: 200 };
    else {
      var i = Math.max(0, modIds.indexOf(id));
      var lap = Math.floor(i / 9) * 28;
      r = { x: 808 + (i % 3) * 362 + lap, y: 24 + (Math.floor(i / 3) % 3) * 348 + lap, w: 350, h: 336 };
    }
    return { x: r.x * s, y: r.y * s, w: r.w * s, h: r.h * s };
  }

  function clampRect(r, id, vw, vh, fixedSize) {
    var m = fixedSize ? [0, 0] : minOf(id);
    var w = Math.min(Math.max(r.w, m[0]), vw);
    var h = Math.min(Math.max(r.h, m[1]), vh);
    return { x: Math.min(Math.max(r.x, 0), vw - w), y: Math.min(Math.max(r.y, 0), vh - h), w: w, h: h };
  }

  function rectFor(id, vw, vh) {
    var e = layout[id];
    var r = e ? { x: e.x * vw, y: e.y * vh, w: e.w * vw, h: e.h * vh } : defaultRect(id, vw, vh);
    return clampRect(r, id, vw, vh, false);
  }

  // Every window is placed by its left and bottom edge, so the compact card (height from its content) and the
  // windows (fixed height) use one rule.
  function paint(id, r, vh, autoHeight) {
    var w = wins[id];
    var st = w.el.style;
    w.r = r;
    st.left = r.x + 'px';
    st.bottom = (vh - r.y - r.h) + 'px';
    st.width = r.w + 'px';
    st.height = autoHeight ? 'auto' : r.h + 'px';
  }

  function cardCompact() { return curMode !== 'expanded'; }

  function placeCard() {
    var v = viewport();
    if (v.w <= 0) return;
    if (!cardCompact()) { paint('standings', rectFor('standings', v.w, v.h), v.h, false); return; }
    var c = layout.c;
    var rect = $('card').getBoundingClientRect();
    var h = rect.height > 0 ? rect.height : 170;
    var left = c ? c.x * v.w : 24;
    var bottom = c ? c.y * v.h : 124;
    var r = clampRect({ x: left, y: v.h - bottom - h, w: CARD_W, h: h }, 'standings', v.w, v.h, true);
    paint('standings', r, v.h, true);
  }

  function placeAll() {
    var v = viewport();
    if (v.w <= 0) return;
    lastVw = v.w;
    lastVh = v.h;
    placeCard();
    for (var id in wins) {
      if (id === 'standings') continue;
      paint(id, rectFor(id, v.w, v.h), v.h, false);
    }
  }

  function showWindows() {
    for (var id in wins) {
      if (id === 'standings') continue;
      setShow(wins[id].el, curMode === 'expanded' && isOpen(id));
    }
  }

  // z-index is not a paint-only property in Sideload: writing it lays the page out again and replaces every uGUI
  // object. A write while the mouse is held destroys the pressed button, and uGUI then raises no click on release.
  // So a window already on top is left alone, and a plain press raises its window only after the release.
  function front(id) {
    var w = wins[id];
    if (!w) return;
    if (w.el.style.zIndex === String(zTop)) return;
    w.el.style.zIndex = String(++zTop);
  }

  var pendingFront = null;

  function frontAfterRelease() {
    if (pendingFront === null) return;
    var id = pendingFront;
    pendingFront = null;
    setTimeout(function () { front(id); }, 0);
  }

  function hitButton(t) {
    while (t) {
      if (String(t.tagName).toUpperCase() === 'BUTTON') return true;
      t = t.parentNode;
    }
    return false;
  }

  function startDrag(kind, w, ev, src) {
    if (ev.buttons !== undefined && ev.buttons !== 0 && !(ev.buttons & 1)) return;
    if (kind === 'move' && hitButton(ev.target)) return;
    if (drag) endDrag();
    // A drag carries no click, so its window comes to the front at once.
    pendingFront = null;
    front(w.id);
    var compact = w.id === 'standings' && cardCompact();
    var r = $('card').getBoundingClientRect();
    var cur = compact ? { x: r.left, y: r.top, w: r.width, h: r.height } : w.r;
    if (!cur) return;
    drag = { w: w, kind: kind, src: src, pid: ev.pointerId, x0: ev.clientX, y0: ev.clientY, r: cur, v: viewport(),
             compact: compact, last: null };
    if (kind === 'size') showGhost(cur);
    try { src.setPointerCapture(ev.pointerId); }
    catch (e) { call('page.log', 'pointer capture failed: ' + e); }
  }

  // While a pointer is held only paint-only properties change: a move shifts the window with `transform`, a resize
  // scales the ghost outline. Sideload repaints those boxes without laying the page out again. Left, bottom, width
  // and height are written once, on release, together with the layout.
  function moveDrag(ev, src) {
    if (!drag || drag.src !== src) return;
    if (ev.buttons !== undefined && !(ev.buttons & 1)) { endDrag(); return; }
    var dx = ev.clientX - drag.x0;
    var dy = ev.clientY - drag.y0;
    var r0 = drag.r;
    var vw = drag.v.w;
    var vh = drag.v.h;
    var r;
    if (drag.kind === 'move') r = { x: r0.x + dx, y: r0.y + dy, w: r0.w, h: r0.h };
    else r = { x: r0.x, y: r0.y, w: Math.min(r0.w + dx, vw - r0.x), h: Math.min(r0.h + dy, vh - r0.y) };
    r = clampRect(r, drag.w.id, vw, vh, drag.compact);
    drag.last = r;
    if (drag.kind === 'move') {
      drag.w.el.style.transform = 'translate(' + (r.x - r0.x) + 'px, ' + (r.y - r0.y) + 'px)';
    } else {
      $('ghost').style.transform = 'scale(' + (r.w / r0.w) + ', ' + (r.h / r0.h) + ')';
    }
  }

  function showGhost(r) {
    var g = $('ghost').style;
    g.left = r.x + 'px';
    g.top = r.y + 'px';
    g.width = r.w + 'px';
    g.height = r.h + 'px';
    g.transform = 'none';
    g.zIndex = String(zTop + 1);
    g.display = 'block';
  }

  function endDrag() {
    if (!drag) return;
    var d = drag;
    drag = null;
    if (d.src.hasPointerCapture && d.src.hasPointerCapture(d.pid)) d.src.releasePointerCapture(d.pid);
    if (d.kind === 'move') d.w.el.style.transform = 'none';
    else $('ghost').style.display = 'none';
    if (d.last) {
      var vw = d.v.w;
      var vh = d.v.h;
      var r = d.last;
      paint(d.w.id, r, vh, d.compact);
      if (d.compact) {
        layout.c = { x: r.x / vw, y: (vh - r.y - r.h) / vh, w: 0, h: 0, v: 1 };
      } else {
        var open = isOpen(d.w.id) ? 1 : 0;
        layout[d.w.id] = { x: r.x / vw, y: r.y / vh, w: r.w / vw, h: r.h / vh, v: open };
      }
      saveLayout();
    }
    flushHeld();
  }

  function attach(w) {
    w.el.addEventListener('pointerdown', function () { pendingFront = w.id; });
    w.el.addEventListener('pointerup', frontAfterRelease);
    w.bar.addEventListener('pointerdown', function (ev) { startDrag('move', w, ev, w.bar); });
    w.bar.addEventListener('pointermove', function (ev) { moveDrag(ev, w.bar); });
    w.bar.addEventListener('pointerup', function () { endDrag(); });
    w.bar.addEventListener('lostpointercapture', function () { endDrag(); });
    w.grip.addEventListener('pointerdown', function (ev) { startDrag('size', w, ev, w.grip); });
    w.grip.addEventListener('pointermove', function (ev) { moveDrag(ev, w.grip); });
    w.grip.addEventListener('pointerup', function () { endDrag(); });
    w.grip.addEventListener('lostpointercapture', function () { endDrag(); });
  }

  function setOpen(id, open) {
    var w = wins[id];
    if (!w) return;
    var v = viewport();
    var e = layout[id];
    if (!e) {
      var r = rectFor(id, v.w, v.h);
      e = layout[id] = { x: r.x / v.w, y: r.y / v.h, w: r.w / v.w, h: r.h / v.h, v: 1 };
    }
    e.v = open ? 1 : 0;
    setShow(w.el, open && curMode === 'expanded');
    if (open) { paint(id, rectFor(id, v.w, v.h), v.h, false); front(id); }
    syncChips();
    saveLayout();
  }

  function resetLayout() {
    layout = {};
    zTop = 10;
    placeAll();
    showWindows();
    syncChips();
  }

  var card = { id: 'standings', el: $('card'), bar: $('head'), body: $('wbody'), grip: $('grip'), n: 0, r: null };
  wins.standings = card;
  attach(card);
  $('resetlayout').onclick = function () { call('layout.reset'); resetLayout(); };
  s1.on('layout', function () { resetLayout(); });

  var rawLayout = String(call('layout.get'));
  if (rawLayout.indexOf('error') === 0) { call('page.log', 'could not read the layout: ' + rawLayout); rawLayout = ''; }
  loadLayout(rawLayout);

  function syncChips() {
    var host = $('chips');
    var ids = ['states'].concat(modIds);
    var sig = '';
    for (var i = 0; i < ids.length; i++) sig += ids[i] + (isOpen(ids[i]) ? '1' : '0') + (titles[ids[i]] || '') + ';';
    if (host.getAttribute('data-sig') === sig) return;
    host.setAttribute('data-sig', sig);
    host.replaceChildren();
    for (var k = 0; k < ids.length; k++) {
      (function (id) {
        var open = isOpen(id);
        var b = el('button', open ? 'btn chip on' : 'btn chip', (open ? '' : '+ ') + (titles[id] || id));
        if (wins[id]) b.id = 'chip-' + wins[id].n;
        b.onclick = function () { setOpen(id, !isOpen(id)); };
        host.appendChild(b);
      })(ids[k]);
    }
  }

  function createWin(id, title, cls, withBib) {
    var box = el('div', cls);
    box.setAttribute('data-win', id);
    var n = ++seq;
    box.id = 'win-' + n;
    var bar = el('div', 'wbar');
    bar.id = 'bar-' + n;
    var bib = null;
    if (withBib) { bib = el('div', 'pbib'); bar.appendChild(bib); }
    var t = el('div', 'wtitle', title);
    bar.appendChild(t);
    var x = el('button', 'wclose');
    x.setAttribute('title', 'Close');
    x.id = 'close-' + n;
    x.appendChild(el('div', 'xa'));
    x.appendChild(el('div', 'xb'));
    bar.appendChild(x);
    var body = el('div', 'wbody');
    var grip = el('div', 'grip');
    grip.id = 'grip-' + n;
    grip.appendChild(el('div', 'gl'));
    box.appendChild(bar);
    box.appendChild(body);
    box.appendChild(grip);
    $('desk').appendChild(box);
    var w = { id: id, el: box, bar: bar, body: body, grip: grip, bib: bib, title: t, n: n, r: null };
    wins[id] = w;
    x.onclick = function () { setOpen(id, false); };
    attach(w);
    front(id);
    var v = viewport();
    if (v.w > 0) paint(id, rectFor(id, v.w, v.h), v.h, false);
    setShow(box, curMode === 'expanded' && isOpen(id));
    return w;
  }

  // ---- panels (expanded) ----
  function buildPanel(p, id) {
    var w = createWin(id, p.title || p.id, 'win mwin', true);
    var pad = el('div', 'pbody');
    var text = el('div', 'ptext');
    var acts = el('div', 'acts');
    var togs = el('div', 'togs');
    var slids = el('div', 'slids');
    var log = el('div', 'log');
    pad.appendChild(text);
    pad.appendChild(acts);
    pad.appendChild(togs);
    pad.appendChild(slids);
    pad.appendChild(log);
    w.body.appendChild(pad);
    w.body.appendChild(el('div', 'foot'));
    return { win: w, bib: w.bib, text: text, acts: acts, togs: togs, slids: slids, log: log,
             sig: '', tsig: '', ssig: '', sliders: {}, toggles: {} };
  }

  function updatePanel(o, p) {
    setText(o.bib, String(p.bib));
    o.bib.style.backgroundColor = p.c;
    o.bib.style.color = inkOn(p.c);
    setText(o.win.title, p.title || p.id);

    var t = (p.text || []).join('\n');
    if (o.textVal !== t) {
      o.textVal = t;
      o.text.replaceChildren();
      for (var i = 0; i < p.text.length; i++) o.text.appendChild(el('div', '', p.text[i]));
    }
    setShow(o.text, p.text.length > 0);

    var sig = '';
    var k;
    for (k = 0; k < p.actions.length; k++) sig += p.actions[k].id + '|' + p.actions[k].l + ';';
    if (o.sig !== sig) {
      o.sig = sig;
      o.acts.replaceChildren();
      for (k = 0; k < p.actions.length; k++) {
        (function (a) {
          var b = el('button', 'btn', a.l);
          b.onclick = function () { call('panel.act', a.id); };
          o.acts.appendChild(b);
        })(p.actions[k]);
      }
    }
    setShow(o.acts, p.actions.length > 0);

    var tsig = '';
    for (k = 0; k < p.toggles.length; k++) tsig += p.toggles[k].id + '|' + p.toggles[k].l + ';';
    if (o.tsig !== tsig) {
      o.tsig = tsig;
      o.togs.replaceChildren();
      o.toggles = {};
      for (k = 0; k < p.toggles.length; k++) {
        (function (tg) {
          var row = el('div', 'tog');
          var box = el('div', 'togbox', 'Off');
          row.appendChild(box);
          row.appendChild(el('div', '', tg.l));
          row.onclick = function () {
            var cur = o.toggles[tg.id];
            call('panel.toggle', tg.id + '\t' + (cur && cur.on ? '0' : '1'));
          };
          o.toggles[tg.id] = { box: box, on: false };
          o.togs.appendChild(row);
        })(p.toggles[k]);
      }
    }
    for (k = 0; k < p.toggles.length; k++) {
      var tt = o.toggles[p.toggles[k].id];
      tt.on = p.toggles[k].on;
      setCls(tt.box, tt.on ? 'togbox on' : 'togbox');
      setText(tt.box, tt.on ? 'On' : 'Off');
    }

    var ssig = '';
    for (k = 0; k < p.sliders.length; k++) {
      var s0 = p.sliders[k];
      ssig += s0.id + '|' + s0.l + '|' + s0.min + '|' + s0.max + '|' + s0.step + ';';
    }
    if (o.ssig !== ssig) {
      o.ssig = ssig;
      o.slids.replaceChildren();
      o.sliders = {};
      for (k = 0; k < p.sliders.length; k++) {
        (function (sl) {
          var wrap = el('div', 'slid');
          var lab = el('div', 'slab');
          lab.appendChild(el('div', '', sl.l));
          var val = el('div', 'sval');
          lab.appendChild(val);
          var inp = document.createElement('input');
          inp.type = 'range';
          inp.min = sl.min;
          inp.max = sl.max;
          inp.step = sl.step;
          inp.value = sl.v;
          inp.addEventListener('input', function (ev) {
            dragging = true;
            var v = ev && ev.value !== undefined ? ev.value : inp.value;
            setText(val, v + (sl.u ? ' ' + sl.u : ''));
            call('panel.slider', sl.id + '\t' + v);
          });
          wrap.appendChild(lab);
          wrap.appendChild(inp);
          o.sliders[sl.id] = { inp: inp, val: val, u: sl.u };
          o.slids.appendChild(wrap);
        })(p.sliders[k]);
      }
    }
    for (k = 0; k < p.sliders.length; k++) {
      var cs = o.sliders[p.sliders[k].id];
      setText(cs.val, p.sliders[k].v + (cs.u ? ' ' + cs.u : ''));
      if (!dragging) cs.inp.value = p.sliders[k].v;
    }

    var lsig = '';
    for (k = 0; k < p.log.length; k++) lsig += p.log[k].t + p.log[k].m + ';';
    if (o.lsig !== lsig) {
      o.lsig = lsig;
      o.log.replaceChildren();
      for (k = 0; k < p.log.length; k++) {
        var L = p.log[k];
        o.log.appendChild(el('div', L.l >= 2 ? 'll e' : L.l === 1 ? 'll w' : 'll', L.t + ' ' + L.m));
      }
    }
    setShow(o.log, p.log.length > 0);
  }

  var statesEl = null;

  function renderPanels(st) {
    var list = st.panels || [];
    var seen = {};
    modIds = [];
    for (var j = 0; j < list.length; j++) modIds.push(safeId(list[j].id));

    // The States window carries the Mods-state lines (NPC, Trash, Quests).
    if (!wins.states) {
      var sw = createWin('states', 'States', 'win swin', false);
      statesEl = el('div', 'pbody');
      sw.body.appendChild(statesEl);
      sw.body.appendChild(el('div', 'foot'));
    }
    titles.states = 'States';

    for (var i = 0; i < list.length; i++) {
      var p = list[i];
      var id = safeId(p.id);
      seen[id] = true;
      titles[id] = p.title || p.id;
      var o = panelEls[id];
      if (!o) { o = buildPanel(p, id); panelEls[id] = o; }
      updatePanel(o, p);
    }
    for (var k in panelEls) {
      if (!seen[k]) {
        $('desk').removeChild(wins[k].el);
        delete wins[k];
        delete panelEls[k];
        delete titles[k];
      }
    }

    var sig = '';
    for (var s = 0; s < st.states.length; s++) sig += st.states[s].t + st.states[s].n + st.states[s].b + ';';
    if (statesEl.getAttribute('data-sig') !== sig) {
      statesEl.setAttribute('data-sig', sig);
      statesEl.replaceChildren();
      for (var q = 0; q < st.states.length; q++) {
        statesEl.appendChild(el('div', 'st', st.states[q].t + ' ' + st.states[q].n + ': ' + st.states[q].b));
      }
      if (st.states.length === 0) statesEl.appendChild(el('div', 'st', 'No state providers reported yet.'));
    }
    syncChips();
  }

  // The Standings window is the compact card in compact mode and a window in expanded mode.
  function applyMode(expanded) {
    var m = expanded ? 'expanded' : 'compact';
    if (curMode === m) return;
    curMode = m;
    $('card').className = expanded ? 'win standings' : 'win standings compact';
    $('wbody').className = expanded ? 'wbody' : 'wbody fit';
    setShow($('ctl'), expanded);
    setShow($('strip'), expanded);
    setShow($('grip'), expanded);
    setShow($('closecard'), expanded);
    // Compact keeps the cursor locked, so it shows nothing to click: the controls live in the expanded view.
    setShow($('winbar'), expanded);
    setShow($('toggle'), false);
    setShow($('chint'), !expanded);
    placeCard();
    showWindows();
    if (expanded) front('standings');
  }

  // ---- whole state ----
  function render(st) {
    state = st;
    var expanded = st.mode === 'expanded';
    applyMode(expanded);
    var v = viewport();
    if (!drag && v.w > 0 && (v.w !== lastVw || v.h !== lastVh)) placeAll();

    var fpsTxt = st.samples > 0
      ? Math.round(st.fps) + ' fps  ' + ms(st.ms) + ' ms  p95 ' + ms(st.p95) + ' ms'
      : 'Waiting for samples';
    setText($('sum'), st.active ? fpsTxt : st.samples > 0 ? 'Stopped  ' + ms(st.ms) + ' ms' : 'Stopped');

    for (var i = 0; i < winBtns.length; i++) {
      var on = Number(winBtns[i].getAttribute('data-w')) === st.win;
      setCls(winBtns[i], on ? 'segbtn on' : 'segbtn');
    }
    setText($('run'), st.active ? 'Stop' : 'Start');
    var phoneOn = !!(st.phone && st.phone.on);
    setCls($('phone'), phoneOn ? 'btn on' : 'btn');
    setText($('phone'), phoneOn ? 'Phone on' : 'Phone');
    setText($('hint'), st.bad > 0 ? st.bad + ' spike frames in unattributed work' : 'Key ' + st.key);
    setText($('chint'), st.key + ' to open · hold Alt to move');
    var note = expanded && st.note ? st.note : '';
    setText($('note'), note);
    setShow($('note'), note.length > 0);
    var showPair = expanded && st.phone && st.phone.on && st.phone.qr;
    setShow($('pair'), showPair);
    if (showPair) setText($('pairu'), st.phone.url);

    renderRows(st);
    setText($('morerows'), st.more > 0 ? '+' + st.more + ' more' : '');
    setShow($('morerows'), st.more > 0);
    if (expanded) renderPanels(st);
    else dragging = false;
  }

  function renderSafe(st) {
    try { if (st) render(st); }
    catch (e) { call('page.log', 'render failed: ' + e); }
  }

  // A state that arrives while a window is held waits for the release: every changed number would lay the page out
  // again, which is the cost the drag avoids.
  var heldState = null;
  flushHeld = function () {
    if (!heldState) return;
    var st = heldState;
    heldState = null;
    renderSafe(st);
  };

  s1.on('state', function (payload) {
    var st;
    try { st = typeof payload === 'string' ? JSON.parse(payload) : payload; }
    catch (e) { call('page.log', 'bad state payload: ' + e); return; }
    if (drag) { heldState = st; return; }
    renderSafe(st);
  });

  call('page.ready');
})();
