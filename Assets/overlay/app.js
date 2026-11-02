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
  $('run').onclick = function () { call('snitch.sampling', state && state.active ? 'stop' : 'start'); };
  $('reset').onclick = function () { call('snitch.sampling', 'reset'); };
  $('dash').onclick = function () { call('snitch.dashboard'); };
  $('phone').onclick = function () { call('snitch.phone', state && state.phone && state.phone.on ? '0' : '1'); };

  // ---- standings ----
  function buildRow(r) {
    var row = el('div', 'row');
    var mark = el('div', 'nomark');
    var bib = el('div', 'bib');
    var name = el('div', 'name');
    var bar = el('div', 'bar');
    var fill = el('div', 'fill');
    bar.appendChild(fill);
    var m = el('div', 'ms');
    var g = el('div', 'gap');
    row.appendChild(mark);
    row.appendChild(bib);
    row.appendChild(name);
    row.appendChild(bar);
    row.appendChild(m);
    row.appendChild(g);
    return { el: row, mark: mark, bib: bib, name: name, fill: fill, ms: m, gap: g, y: 0 };
  }

  function updateRow(o, r, i, scale) {
    var isLeader = i === 0 && !r.dnf;
    setCls(o.mark, isLeader ? 'mark' : 'nomark');
    setCls(o.el, r.dnf ? 'row dnf' : 'row');
    setText(o.bib, String(r.bib));
    o.bib.style.backgroundColor = r.c;
    var full = r.g + (r.n && r.n !== r.g ? ' ' + r.n : '');
    setText(o.name, full.length > NAME_MAX ? full.substring(0, NAME_MAX - 3) + '...' : full);
    var w = r.dnf ? 0 : Math.max(0.5, Math.min(100, (r.ms / scale) * 100));
    o.fill.style.width = w + '%';
    o.fill.style.backgroundColor = r.c;
    setText(o.ms, r.dnf ? '' : ms(r.ms) + ' ms');
    if (r.dnf) setText(o.gap, 'DNF ' + ago(r.silent));
    else if (isLeader) setText(o.gap, 'Leader');
    else setText(o.gap, '+' + r.gap.toFixed(2) + ' ms');
    o.gap.style.color = r.dnf ? '#D7262E' : isLeader ? '#14213D' : '#4A5568';
    o.gap.style.fontWeight = isLeader || r.dnf ? '700' : '400';
  }

  function renderRows(st) {
    var host = $('rows');
    var riders = st.riders || [];
    var scale = st.scale > 0 ? st.scale : 1;

    if (riders.length === 0) {
      if (!host.getAttribute('data-empty')) {
        host.replaceChildren();
        host.appendChild(el('div', 'empty', st.active ? 'No sections reported yet.' : 'Sampling is stopped. Press Start.'));
        host.setAttribute('data-empty', '1');
        rowEls = {};
        lastOrder = [];
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

  // ---- panels (expanded) ----
  function buildPanel(p) {
    var box = el('div', 'panel');
    var t = el('div', 'ptitle');
    var bib = el('div', 'pbib');
    t.appendChild(bib);
    t.appendChild(el('div', '', p.title || p.id));
    box.appendChild(t);
    var text = el('div', 'ptext');
    var acts = el('div', 'acts');
    var togs = el('div', 'togs');
    var slids = el('div', 'slids');
    var log = el('div', 'log');
    box.appendChild(text);
    box.appendChild(acts);
    box.appendChild(togs);
    box.appendChild(slids);
    box.appendChild(log);
    return { el: box, bib: bib, text: text, acts: acts, togs: togs, slids: slids, log: log,
             sig: '', tsig: '', ssig: '', sliders: {}, toggles: {} };
  }

  function updatePanel(o, p) {
    setText(o.bib, String(p.bib));
    o.bib.style.backgroundColor = p.c;

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
          var box = el('div', 'togbox');
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

  function renderPanels(st) {
    var host = $('panels');
    var list = st.panels || [];
    var seen = {};
    for (var i = 0; i < list.length; i++) {
      var p = list[i];
      seen[p.id] = true;
      var o = panelEls[p.id];
      if (!o) { o = buildPanel(p); panelEls[p.id] = o; host.appendChild(o.el); }
      updatePanel(o, p);
    }
    for (var k in panelEls) {
      if (!seen[k]) { host.removeChild(panelEls[k].el); delete panelEls[k]; }
    }
    if (list.length === 0 && !host.getAttribute('data-none')) {
      host.setAttribute('data-none', '1');
      host.appendChild(el('div', 'ptext', 'No mod has registered a panel.'));
    } else if (list.length > 0 && host.getAttribute('data-none')) {
      host.removeAttribute('data-none');
      host.replaceChildren();
      panelEls = {};
      renderPanels(st);
      return;
    }

    var states = $('states');
    var sig = '';
    for (var s = 0; s < st.states.length; s++) sig += st.states[s].t + st.states[s].n + st.states[s].b + ';';
    if (states.getAttribute('data-sig') !== sig) {
      states.setAttribute('data-sig', sig);
      states.replaceChildren();
      for (var q = 0; q < st.states.length; q++) {
        states.appendChild(el('div', 'st', st.states[q].t + ' ' + st.states[q].n + ': ' + st.states[q].b));
      }
    }
  }

  // ---- whole state ----
  function render(st) {
    state = st;
    var expanded = st.mode === 'expanded';

    var fpsTxt = st.samples > 0
      ? Math.round(st.fps) + ' fps  ' + ms(st.ms) + ' ms  p95 ' + ms(st.p95) + ' ms'
      : 'Waiting for samples';
    setText($('sum'), fpsTxt);

    for (var i = 0; i < winBtns.length; i++) {
      var on = Number(winBtns[i].getAttribute('data-w')) === st.win;
      setCls(winBtns[i], on ? 'segbtn on' : 'segbtn');
    }
    setText($('toggle'), expanded ? 'Compact' : 'Expand');
    setShow($('ctl'), expanded);
    setText($('run'), st.active ? 'Stop' : 'Start');
    setCls($('phone'), st.phone && st.phone.on ? 'btn on' : 'btn');
    setText($('hint'), st.bad > 0 ? st.bad + ' spike frames in unattributed work' : 'Key ' + st.key);
    var showPair = expanded && st.phone && st.phone.on && st.phone.qr;
    setShow($('pair'), showPair);
    if (showPair) setText($('pairu'), st.phone.url);

    renderRows(st);
    setText($('morerows'), st.more > 0 ? '+' + st.more + ' more' : '');
    setShow($('morerows'), st.more > 0);
    setShow($('more'), expanded);
    if (expanded) renderPanels(st);
    else dragging = false;
  }

  s1.on('state', function (payload) {
    var st;
    try { st = typeof payload === 'string' ? JSON.parse(payload) : payload; }
    catch (e) { call('page.log', 'bad state payload: ' + e); return; }
    try { if (st) render(st); }
    catch (e2) { call('page.log', 'render failed: ' + e2); }
  });

  call('page.ready');
})();
