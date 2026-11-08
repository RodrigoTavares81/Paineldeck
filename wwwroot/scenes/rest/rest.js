// =====================================================================
//  PainelDeck · Modo descanso
//  Cada monitor abre esta página numa janela própria. Todas desenham o MESMO "mundo",
//  medido em MILÍMETROS (tamanho físico real de cada tela), e todas usam o mesmo relógio.
//  Por isso o mar, as estrelas e o cometa continuam certinhos de um monitor pro outro,
//  mesmo com resoluções e tamanhos diferentes (ultrawide 3440x1440 ao lado de um 1920x1080).
//
//  Parâmetros (query string ou mensagem {type:"rest-init"} do app):
//    layout = { monitors: [ { id, px:{x,y,w,h}, mm:{w,h}, panel:true|false } ], gapMm }
//    mon    = id do monitor desta janela
//    start  = Date.now() de quando o modo começou (igual pra todas as janelas)
// =====================================================================
window.RestScene = (() => {
  const canvas = document.getElementById("c");
  const ctx = canvas.getContext("2d");
  let W = null, me = null, start = Date.now(), exitAt = null, onExited = null;

  // ---------- mundo em mm ----------
  // Põe os monitores lado a lado na ordem do Windows (esquerda → direita), com a borda física (gapMm)
  // entre eles, centralizados na vertical. Funciona igual em todas as janelas.
  function buildWorld(layout) {
    const gap = layout.gapMm ?? 18;
    const mons = [...layout.monitors].sort((a, b) => a.px.x - b.px.x);
    const maxH = Math.max(...mons.map((m) => m.mm.h));
    let x = 0;
    for (const m of mons) {
      m.wx = x;
      m.wy = (maxH - m.mm.h) / 2;
      m.k = m.px.w / m.mm.w; // px por mm desta tela
      x += m.mm.w + gap;
    }
    const rest = mons.filter((m) => !m.panel);
    const panel = mons.find((m) => m.panel);
    const big = rest.reduce((a, b) => (a.mm.w * a.mm.h >= b.mm.w * b.mm.h ? a : b), rest[0]);
    return { mons, rest, panel, big, w: x - gap, h: maxH,
      // a "cena" ocupa só os monitores em descanso (o painel continua sendo painel)
      left: Math.min(...rest.map((m) => m.wx)), right: Math.max(...rest.map((m) => m.wx + m.mm.w)) };
  }

  // ---------- números "aleatórios" iguais em todas as janelas ----------
  function rng(seed) {
    return () => { seed |= 0; seed = (seed + 0x6d2b79f5) | 0; let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
  }
  let stars = [];
  function makeStars() {
    const r = rng(1337);
    const n = Math.round((W.right - W.left) * W.h * 0.0016);
    stars = Array.from({ length: n }, () => ({
      x: W.left + r() * (W.right - W.left), y: r() * W.h * 0.64,
      s: 0.25 + r() * r() * 0.9, p: r() * Math.PI * 2, f: 0.4 + r() * 1.6, d: 0.2 + r() * 0.8,
    }));
  }

  // ---------- helpers de desenho (tudo em mm) ----------
  const X = (mm) => (mm - me.wx) * me.k;
  const Y = (mm) => (mm - me.wy) * me.k;
  const S = (mm) => mm * me.k;
  const NAVY = { top: "#030817", mid: "#08142f", low: "#0e214c" };
  const RED = "#c8102e";
  const ICE = "143,176,255";
  const horizon = () => W.h * 0.66;
  const sunX = () => W.right - Math.min(260, (W.right - W.left) * 0.2); // perto do painel: a luz "vem" dele

  function drawSky(t) {
    const g = ctx.createLinearGradient(0, Y(0), 0, Y(horizon()));
    g.addColorStop(0, NAVY.top); g.addColorStop(0.6, NAVY.mid); g.addColorStop(1, "#16306b");
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    // nebulosas que andam devagar (posição no mundo)
    const blobs = [
      { x: W.left + (W.right - W.left) * 0.22, y: W.h * 0.22, r: 230, c: "18,53,127", a: 0.55, sp: 0.013 },
      { x: W.left + (W.right - W.left) * 0.62, y: W.h * 0.12, r: 300, c: "15,42,107", a: 0.5, sp: -0.009 },
      { x: sunX(), y: horizon() - 20, r: 260, c: "200,16,46", a: 0.22, sp: 0 },
    ];
    for (const b of blobs) {
      const bx = b.x + Math.sin(t * b.sp * 6) * 40, by = b.y + Math.cos(t * b.sp * 4) * 14;
      const rg = ctx.createRadialGradient(X(bx), Y(by), 0, X(bx), Y(by), S(b.r));
      rg.addColorStop(0, `rgba(${b.c},${b.a})`); rg.addColorStop(1, `rgba(${b.c},0)`);
      ctx.fillStyle = rg;
      ctx.fillRect(0, 0, canvas.width, canvas.height);
    }
  }

  function drawStars(t) {
    for (const s of stars) {
      const x = s.x + t * 0.6 * s.d; // deriva bem lenta, com paralaxe
      if (x < me.wx - 2 || x > me.wx + me.mm.w + 2) continue;
      const a = 0.35 + 0.65 * (0.5 + 0.5 * Math.sin(t * s.f + s.p));
      ctx.fillStyle = `rgba(220,232,255,${a * (0.4 + s.d * 0.6)})`;
      const sz = Math.max(1, S(s.s));
      ctx.fillRect(X(x) - sz / 2, Y(s.y) - sz / 2, sz, sz);
    }
  }

  function drawSun(t) {
    const cx = sunX(), cy = horizon() + 6, r = 46 + Math.sin(t * 0.5) * 1.2;
    const glow = ctx.createRadialGradient(X(cx), Y(cy), S(r * 0.6), X(cx), Y(cy), S(r * 3.2));
    glow.addColorStop(0, "rgba(200,16,46,0.35)"); glow.addColorStop(1, "rgba(200,16,46,0)");
    ctx.fillStyle = glow;
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    const body = ctx.createRadialGradient(X(cx), Y(cy - r * 0.3), S(2), X(cx), Y(cy), S(r));
    body.addColorStop(0, "#ff5a6e"); body.addColorStop(0.55, RED); body.addColorStop(1, "#7d0a1c");
    ctx.fillStyle = body;
    ctx.beginPath(); ctx.arc(X(cx), Y(cy), S(r), Math.PI, 0); ctx.fill();
  }

  function wave(x, t, k) { return Math.sin(x * 0.018 * k + t * 0.6) * 2.4 + Math.sin(x * 0.007 * k - t * 0.35) * 3.2 + Math.sin(x * 0.041 + t * 1.1) * 0.8; }
  function drawSea(t) {
    const hz = horizon();
    const g = ctx.createLinearGradient(0, Y(hz), 0, Y(W.h));
    g.addColorStop(0, "#0b1d47"); g.addColorStop(1, "#030817");
    ctx.fillStyle = g;
    ctx.fillRect(0, Y(hz), canvas.width, canvas.height);
    // reflexo do sol (listras que tremem)
    const sx = sunX();
    for (let i = 0; i < 26; i++) {
      const y = hz + 4 + i * 3.4, w = (34 - i * 0.9) * (0.75 + 0.25 * Math.sin(t * 2 + i * 1.3));
      if (w <= 0) break;
      ctx.fillStyle = `rgba(224,35,63,${0.5 * (1 - i / 26)})`;
      ctx.fillRect(X(sx - w / 2 + Math.sin(t + i) * 3), Y(y), S(w), Math.max(1, S(1.1)));
    }
    // ondas: 3 camadas contínuas no mundo inteiro
    const layers = [{ dy: 2, k: 1, c: "rgba(14,33,76,0.85)" }, { dy: 18, k: 1.4, c: "rgba(9,22,54,0.9)" }, { dy: 40, k: 1.9, c: "rgba(5,13,34,0.96)" }];
    const x0 = me.wx, x1 = me.wx + me.mm.w, step = 4;
    for (const L of layers) {
      ctx.fillStyle = L.c;
      ctx.beginPath();
      ctx.moveTo(X(x0), Y(W.h + 5));
      for (let x = x0; x <= x1 + step; x += step) ctx.lineTo(X(x), Y(hz + L.dy + wave(x, t * L.k, L.k)));
      ctx.lineTo(X(x1 + step), Y(W.h + 5));
      ctx.fill();
      // crista de luz
      ctx.strokeStyle = `rgba(${ICE},0.12)`; ctx.lineWidth = Math.max(1, S(0.5));
      ctx.beginPath();
      for (let x = x0; x <= x1 + step; x += step) { const y = Y(hz + L.dy + wave(x, t * L.k, L.k)); x === x0 ? ctx.moveTo(X(x), y) : ctx.lineTo(X(x), y); }
      ctx.stroke();
    }
  }

  // cometa vermelho: sai do lado do painel e atravessa todos os monitores (velocidade física constante)
  function drawComet(t) {
    const period = 16, speed = 260; // s, mm/s
    const span = W.right - W.left + 400;
    const k = Math.floor(t / period), lt = t - k * period;
    const r = rng(900 + k);
    const y0 = W.h * (0.08 + r() * 0.22), y1 = W.h * (0.18 + r() * 0.3);
    const dur = span / speed;
    if (lt > dur) return;
    const pos = (u) => { // u: 0..1, curva suave da direita (painel) pra esquerda
      const x = W.right + 200 - u * span;
      const y = y0 + (y1 - y0) * u + Math.sin(u * Math.PI) * -W.h * 0.06;
      return [x, y];
    };
    const u = lt / dur;
    // cauda: segmentos que afinam e somem (comprimento físico fixo: ~190 mm)
    const N = 48, tail = 190 / span;
    ctx.lineCap = "round";
    for (let i = N; i > 0; i--) {
      const ua = u - (i / N) * tail, ub = u - ((i - 1) / N) * tail;
      if (ub < 0) continue;
      const [xa, ya] = pos(Math.max(0, ua)), [xb, yb] = pos(ub);
      if (Math.max(xa, xb) < me.wx - 40 || Math.min(xa, xb) > me.wx + me.mm.w + 40) continue;
      const a = 1 - i / N;
      ctx.strokeStyle = `rgba(${a > 0.85 ? "255,170,182" : "224,35,63"},${0.75 * a * a})`;
      ctx.lineWidth = S(0.6 + 3.4 * a);
      ctx.beginPath(); ctx.moveTo(X(xa), Y(ya)); ctx.lineTo(X(xb), Y(yb)); ctx.stroke();
    }
    { const [x, y] = pos(u);
      if (x > me.wx - 20 && x < me.wx + me.mm.w + 20) { ctx.fillStyle = "#fff"; ctx.beginPath(); ctx.arc(X(x), Y(y), S(2.6), 0, Math.PI * 2); ctx.fill(); } }
    const [hx, hy] = pos(u);
    if (hx > me.wx - 60 && hx < me.wx + me.mm.w + 60) {
      const g = ctx.createRadialGradient(X(hx), Y(hy), 0, X(hx), Y(hy), S(22));
      g.addColorStop(0, "rgba(255,120,140,0.6)"); g.addColorStop(1, "rgba(200,16,46,0)");
      ctx.fillStyle = g; ctx.fillRect(X(hx) - S(22), Y(hy) - S(22), S(44), S(44));
    }
  }

  // relógio: só no monitor maior, tamanho físico fixo (fica igual em qualquer resolução)
  const pad = (n) => String(n).padStart(2, "0");
  function drawClock(now) {
    if (me !== W.big) return;
    const cx = me.wx + me.mm.w / 2, cy = me.wy + me.mm.h * 0.3;
    ctx.textAlign = "center"; ctx.textBaseline = "middle";
    ctx.fillStyle = "rgba(232,238,255,0.92)";
    ctx.font = `200 ${S(46)}px "Segoe UI Variable Display", "Segoe UI", Inter, sans-serif`;
    ctx.fillText(`${pad(now.getHours())}:${pad(now.getMinutes())}`, X(cx), Y(cy));
    ctx.font = `500 ${S(6.5)}px "Cascadia Mono", "JetBrains Mono", Consolas, monospace`;
    ctx.letterSpacing = `${S(1.6)}px`;
    ctx.fillStyle = `rgba(${ICE},0.8)`;
    ctx.fillText(now.toLocaleDateString("pt-BR", { weekday: "long", day: "numeric", month: "long" }).toUpperCase(), X(cx), Y(cy + 36));
    if (window.REST_SUBTITLE) {
      ctx.fillStyle = "rgba(224,35,63,0.95)";
      ctx.fillText(window.REST_SUBTITLE.toUpperCase(), X(cx), Y(cy + 50));
    }
    ctx.letterSpacing = "0px";
  }

  // entrada/saída: uma "frente de luz" que sai do painel e varre os monitores (e volta pro painel na saída)
  const SWEEP_MM_S = 1100;
  function sweepFront(t) {
    const from = W.panel ? W.panel.wx : W.right + 100;
    if (exitAt == null) return from - t * SWEEP_MM_S; // indo pra esquerda
    const te = (Date.now() - exitAt) / 1000;
    return W.left - 60 + te * SWEEP_MM_S * 1.4;        // voltando pro painel
  }
  function drawSweep(t) {
    const fx = sweepFront(t);
    const covered = exitAt == null ? (x) => x < fx : (x) => x < fx; // à esquerda da frente: ainda/de novo escuro
    const lx = X(fx);
    if (lx > 0) { // parte "apagada"
      ctx.fillStyle = "#000";
      ctx.fillRect(0, 0, Math.min(canvas.width, lx), canvas.height);
    }
    if (lx > -S(80) && lx < canvas.width + S(80)) {
      const g = ctx.createLinearGradient(lx - S(40), 0, lx + S(40), 0);
      g.addColorStop(0, "rgba(200,16,46,0)"); g.addColorStop(0.45, "rgba(224,35,63,0.55)");
      g.addColorStop(0.5, "rgba(255,230,235,0.95)"); g.addColorStop(0.55, `rgba(${ICE},0.5)`); g.addColorStop(1, `rgba(${ICE},0)`);
      ctx.fillStyle = g;
      ctx.fillRect(lx - S(40), 0, S(80), canvas.height);
    }
    return covered;
  }

  // ---------- loop ----------
  function frame() {
    const now = new Date(), t = (Date.now() - start) / 1000;
    drawSky(t); drawStars(t); drawSun(t); drawSea(t); drawComet(t); drawClock(now);
    drawSweep(t);
    if (exitAt != null && sweepFront(t) > W.right + 120) { onExited?.(); onExited = null; return; }
    requestAnimationFrame(frame);
  }

  function resize() {
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.round(innerWidth * dpr);
    canvas.height = Math.round(innerHeight * dpr);
    if (me) me.k = canvas.width / me.mm.w; // px reais do canvas por mm
  }

  function init(layout, monId, startEpoch) {
    W = buildWorld(layout);
    me = W.mons.find((m) => String(m.id) === String(monId)) || W.rest[0];
    start = startEpoch || Date.now();
    makeStars();
    resize();
    addEventListener("resize", resize);
    requestAnimationFrame(frame);
  }

  // saída com animação (o app chama quando você mexe o mouse ou aperta uma tecla)
  function exit(cb) { if (exitAt == null) { exitAt = Date.now(); onExited = cb; } }

  // app → página
  window.chrome?.webview?.addEventListener("message", (e) => {
    const m = e.data;
    if (m?.type === "rest-init") { window.REST_SUBTITLE = m.subtitle; init(m.layout, m.mon, m.start); }
    if (m?.type === "rest-exit") exit(() => window.chrome.webview.postMessage({ type: "rest-exited" }));
  });
  // preview / iframe: parâmetros na URL
  const q = new URLSearchParams(location.search);
  if (q.get("layout")) { window.REST_SUBTITLE = q.get("sub") || ""; init(JSON.parse(q.get("layout")), q.get("mon"), Number(q.get("start")) || Date.now()); }
  else window.chrome?.webview?.postMessage({ type: "rest-ready" });

  return { init, exit };
})();
