// =====================================================================
//  PainelDeck · cena "Navy" · gravação
//  O C# (Recorder.cs) fala com o OBS pelo obs-websocket e manda:
//   - status.rec (1x por segundo): { obs ("closed" | "connecting" | "ok" | "error"), error, fail, failSeq, active, paused,
//       starting, ms, bytes, path, file, scene, dir, freeGb, hotkey, metering,
//       tracks: [{ name, source, muted, found, mixer: [1] }], markers: [ms], stats: { fps, cpu, skipped, total, lagged },
//       last: { name, path, ms, bytes, markers, at }, recent: [{ name, path, bytes, at }] }
//   - Bridge.on("meters") (~15x por segundo, só gravando ou no teste de áudio): { ms, paused, l: [pico, média, pico, média] } em dB
//   - Bridge.call("record", { cmd }) → toggle | start | stop | pause | mark | mute (track) | test | open (path) | history
//  Aqui: o cartão GRAVAÇÃO da coluna da direita e a tela cheia que entra sozinha enquanto grava
//  (linha do tempo que cresce com as ondas das duas faixas + medidores ao vivo).
//  Leve na GPU: os canvas só são redesenhados quando chega um "meters" (nada roda com o painel parado).
//  Config: "gravacao" no deck.json → { "cena", "faixas": [{ "nome", "fonte" }], "obs", "voltar" }.
// =====================================================================
(() => {
  const $ = (s, el = document) => el.querySelector(s);
  const body = document.body, stageEl = $("#recstage");
  const { toast, ripple } = window.Navy;
  const pad = (n) => String(n).padStart(2, "0");
  const setText = (el, v) => { if (el.textContent !== v) el.textContent = v; };
  const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

  const fit = () => stageEl.style.setProperty("--fit", Math.min(innerWidth / 1280, innerHeight / 720));
  addEventListener("resize", () => { fit(); draw(); });
  fit();

  // ---------- formatos ----------
  const clock = (ms) => { const s = Math.floor(ms / 1000); return (s >= 3600 ? `${Math.floor(s / 3600)}:${pad(Math.floor(s / 60) % 60)}` : `${Math.floor(s / 60)}`) + `:${pad(s % 60)}`; };
  const num = (v, d = 1) => v.toFixed(d).replace(".", ",");
  const size = (b) => (b >= 1e9 ? `${num(b / 1073741824, 2)} GB` : b >= 1e6 ? `${Math.round(b / 1048576)} MB` : `${Math.round(b / 1024)} KB`);
  const keys = (hk) => String(hk || "").split("+").filter(Boolean).map((k) => k.charAt(0).toUpperCase() + k.slice(1)).join(" ");
  function ago(iso) {
    const t = Date.parse(iso);
    if (!t) return "";
    const m = Math.round((Date.now() - t) / 60000);
    if (m < 1) return "agora";
    if (m < 60) return `há ${m} min`;
    const h = Math.round(m / 60);
    if (h < 24) return `há ${h} h`;
    const d = Math.round(h / 24);
    return d === 1 ? "ontem" : `há ${d} dias`;
  }

  // ---------- estado ----------
  const BUCKET = 250, FLOOR = -60;
  const COLORS = ["#8fb0ff", "#4f7df0", "#c5d5ff", "#2a55ad"]; // uma cor por faixa, tudo azul; vermelho só no que estoura
  const ICONS = [
    `<svg viewBox="0 0 24 24"><path d="M12 15a3 3 0 0 0 3-3V6a3 3 0 0 0-6 0v6a3 3 0 0 0 3 3Zm5-3a5 5 0 0 1-10 0H5a7 7 0 0 0 6 6.9V22h2v-3.1A7 7 0 0 0 19 12Z"/></svg>`,
    `<svg viewBox="0 0 24 24"><path d="M4 9v6h4l5 4V5L8 9Zm11.5 3a4 4 0 0 0-2-3.5v7a4 4 0 0 0 2-3.5ZM13.5 3.3v2.1a7 7 0 0 1 0 13.2v2.1a9 9 0 0 0 0-17.4Z"/></svg>`,
  ];
  let rec = null;            // último status.rec
  let hist = [[], []];       // nível de pico por balde de 250 ms, por faixa (0..255)
  let live = [];             // medidores: { peak, mag, hold, holdAt }
  let curMs = 0, curAt = 0;  // duração da gravação e quando chegou
  let win = 60;              // segundos que cabem na linha do tempo agora (cresce junto com a gravação)
  let wasActive = false, showing = false, minimized = false, doneUntil = 0, histFor = false;
  let failSeq = null, quiet = 0, stopAsk = 0, trackKey = "", bytesAt = null, rate = 0;

  const cfg = () => window.Navy.deck?.gravacao || {};
  const toByte = (db) => Math.round(Math.max(0, Math.min(1, (db - FLOOR) / -FLOOR)) * 255);

  async function cmd(c, extra, el, e) {
    if (el && e) ripple(el, e);
    try {
      quiet = Date.now() + 2500; // o erro já aparece aqui: não repete quando vier no status
      const r = await Bridge.call("record", { cmd: c, ...extra });
      if (r && r.tracks) apply(r);
      return r;
    } catch (err) {
      toast(`Gravação: ${err.message}`);
      return null;
    }
  }

  // ---------- cartão da coluna da direita ----------
  const card = $("#reccard");
  function renderCard(r) {
    const pill = { closed: "OBS fechado", connecting: "abrindo o OBS", ok: "OBS pronto", error: "OBS sem conexão" }[r.obs] || "OBS";
    $("#rc-pill").className = `rc-pill ${r.active && !r.paused ? "error" : r.obs}`; // gravando = bolinha vermelha
    setText($("#rc-pill-text"), r.active ? (r.paused ? "pausado" : "gravando") : pill);
    $("#rc-pill").title = r.error || "";
    card.classList.toggle("on", !!(r.active || r.starting));
    setText($("#rc-title"), r.active ? clock(r.ms) : r.starting ? "ABRINDO…" : "GRAVAR");
    const hk = keys(r.hotkey);
    const sub = r.active ? "toque pra ver a gravação"
      : r.obs === "closed" ? "o OBS abre sozinho"
      : r.obs === "error" ? "ligue o servidor WebSocket no OBS" : "monitor principal";
    $("#rc-sub").innerHTML = `<span>${sub}</span>${hk ? `<kbd>${esc(hk)}</kbd>` : ""}`;
    setText($("#rc-scene"), r.scene || (cfg().cena === "auto" ? "Tela principal" : "—"));
    const dest = r.dir ? `${esc(r.dir)}${r.freeGb != null ? ` <small>· ${Math.round(r.freeGb)} GB livres</small>` : ""}` : "—";
    if ($("#rc-dest").innerHTML !== dest) $("#rc-dest").innerHTML = dest;
    $("#rc-dest").title = r.dir || "";

    // faixas (só remonta quando muda alguma coisa; a barrinha do nível é mexida pelo "meters")
    const key = JSON.stringify(r.tracks);
    if (key !== trackKey) {
      trackKey = key;
      $("#rc-tracks").innerHTML = r.tracks.map((t, i) => `
        <div class="rc-track${t.muted ? " muted" : ""}" data-i="${i}" title="${t.found ? "Toque pra mutar/desmutar no OBS" : ""}">
          <span class="ico">${ICONS[i] || ICONS[1]}</span>
          <span class="n">${esc(t.name)}<small>${esc(t.found ? t.source : r.obs === "ok" ? "fonte não achada no OBS" : "")}</small></span>
          <span class="tag">${t.muted ? "MUDO" : t.mixer?.length ? `FAIXA ${t.mixer.join("+")}` : ""}</span>
          <span class="bar"><i></i></span>
        </div>`).join("");
      renderHeads(r);
    }

    const list = $("#rc-list");
    const rk = JSON.stringify((r.recent || []).map((f) => f.path)) + Math.floor(Date.now() / 60000);
    if (list.dataset.k !== rk) {
      list.dataset.k = rk;
      list.innerHTML = (r.recent || []).slice(0, 4).map((f, i) => `
        <li data-i="${i}" title="${esc(f.path)}"><svg viewBox="0 0 24 24"><path d="M4 5h11v4l5-3v12l-5-3v4H4Z"/></svg>
          <span class="nm">${esc(f.name)}</span><span class="mt">${size(f.bytes)} · ${ago(f.at)}</span></li>`).join("")
        || `<li class="none">${r.dir ? "Nenhuma gravação nessa pasta ainda." : "Aparecem aqui depois da primeira vez que o painel falar com o OBS."}</li>`;
    }
  }
  $("#rc-btn").addEventListener("pointerdown", (e) => {
    if (rec?.active || rec?.starting) { ripple(e.currentTarget, e); minimized = false; sync(); return; }
    cmd("start", null, e.currentTarget, e);
  });
  $("#rc-test").addEventListener("pointerdown", async (e) => { if (await cmd("test", null, e.currentTarget, e)) toast("Níveis ao vivo por 20 segundos", "ok"); });
  $("#rc-folder").addEventListener("pointerdown", (e) => cmd("open", null, e.currentTarget, e));
  $("#rc-tracks").addEventListener("pointerdown", (e) => {
    const row = e.target.closest(".rc-track");
    if (row && rec?.tracks[row.dataset.i]?.found) cmd("mute", { track: Number(row.dataset.i) });
  });
  $("#rc-list").addEventListener("pointerdown", (e) => {
    const li = e.target.closest("li[data-i]");
    if (li) cmd("open", { path: rec.recent[li.dataset.i].path }, li, e);
  });
  $("#chip-rec").addEventListener("pointerdown", () => { minimized = false; sync(); });

  // ---------- tela cheia ----------
  function renderHeads(r) {
    $("#r-heads").innerHTML = r.tracks.map((t, i) => `
      <button class="r-head${t.muted ? " muted" : ""}" data-i="${i}" style="--c:${COLORS[i % COLORS.length]}" title="Toque pra mutar/desmutar no OBS">
        <span class="n"><i></i>${esc(t.name)}</span>
        <span class="src">${esc(t.found ? t.source : "sem fonte")}</span>
        <span class="tag">${t.muted ? "MUDO" : t.mixer?.length ? `FAIXA ${t.mixer.join("+")}` : "ÁUDIO"}</span>
      </button>`).join("");
    $("#r-vufoot").innerHTML = r.tracks.map((t, i) => `
      <button class="r-vub${t.muted ? " muted" : ""}" data-i="${i}" title="Toque pra mutar/desmutar no OBS">
        <span class="n">${esc(t.name)}</span><span class="db" id="r-db-${i}">${t.muted ? "MUDO" : "-- dB"}</span>
      </button>`).join("");
    while (hist.length < r.tracks.length) hist.push([]);
  }
  for (const sel of ["#r-heads", "#r-vufoot"])
    $(sel).addEventListener("pointerdown", (e) => {
      const b = e.target.closest("[data-i]");
      if (b) cmd("mute", { track: Number(b.dataset.i) }, b, e);
    });

  function renderScene(r, s) {
    const done = !r.active && !r.starting;
    stageEl.classList.toggle("paused", r.active && r.paused);
    stageEl.classList.toggle("starting", !r.active && r.starting);
    stageEl.classList.toggle("done", done);
    setText($("#r-state"), done ? "SALVO" : r.starting && !r.active ? (r.obs === "ok" ? "COMEÇANDO" : "ABRINDO O OBS") : r.paused ? "PAUSADO" : "GRAVANDO");
    setText($("#r-scene-name"), r.scene || "—");
    $("#r-game").hidden = !s.game;
    if (s.game) setText($("#r-game-name"), s.game.name);
    const now = new Date();
    setText($("#r-hh"), pad(now.getHours()));
    setText($("#r-mm"), pad(now.getMinutes()));
    setText($("#r-file"), r.file || (done ? r.last?.name : "") || "o OBS escolhe o nome do arquivo");

    // tamanho + taxa (média das últimas leituras)
    const bytes = done ? r.last?.bytes || 0 : r.bytes;
    if (r.active && !r.paused && bytesAt && r.ms > bytesAt.ms) {
      const inst = ((r.bytes - bytesAt.bytes) * 8) / ((r.ms - bytesAt.ms) / 1000);
      if (inst >= 0) rate = rate ? rate * 0.7 + inst * 0.3 : inst;
    }
    if (r.active) bytesAt = { ms: r.ms, bytes: r.bytes };
    $("#r-size").innerHTML = bytes ? size(bytes).replace(/ (\w+)$/, "<small>$1</small>") : "--";
    setText($("#r-rate"), r.active && rate ? `${num(rate / 1e6, 1)} Mb/s` : " ");
    $("#r-free").innerHTML = r.freeGb != null ? `${Math.round(r.freeGb)}<small>GB</small>` : "--";
    const left = r.freeGb != null && rate > 1e5 ? (r.freeGb * 1073741824 * 8) / rate : null; // segundos que ainda cabem
    setText($("#r-fits"), left == null ? (r.dir || " ") : left > 360000 ? "cabe mais de 100 h" : `cabe mais ~${left >= 3600 ? `${Math.floor(left / 3600)}h${pad(Math.floor(left / 60) % 60)}` : `${Math.floor(left / 60)} min`}`);
    $("#r-st-free").classList.toggle("bad", left != null && left < 600);
    const st = r.stats;
    $("#r-fps").innerHTML = st ? `${Math.round(st.fps)}<small>fps</small>` : "--";
    const lost = st ? st.skipped + st.lagged : 0;
    setText($("#r-drop"), st ? (lost ? `${lost} quadros perdidos` : "nenhum quadro perdido") : " ");
    $("#r-st-video").classList.toggle("bad", !!st && st.total > 0 && lost / st.total > 0.01);
    $("#r-pc").innerHTML = `${Math.round(s.cpu ?? 0)}<small>% CPU</small>`;
    const t = (v) => (v == null ? "--" : Math.round(v));
    $("#r-pc-s").innerHTML = `GPU <b>${s.gpu == null ? "--" : Math.round(s.gpu)}%</b> · ${t(s.sys?.cpuTemp)}° / ${t(s.gpuTemp)}°`;

    setText($("#r-pause-text"), r.paused ? "Retomar" : "Pausar");
    setText($("#r-mark-n"), r.markers?.length ? String(r.markers.length) : "");
    const asking = Date.now() < stopAsk;
    $("#r-stop").classList.toggle("ask", asking);
    setText($("#r-stop-text"), asking ? "Toque de novo pra parar" : "Parar e salvar");
    setText($("#r-stop-key"), asking ? "" : keys(r.hotkey));

    if (done && r.last) {
      setText($("#r-done-name"), r.last.name || "Gravação salva");
      $("#r-done-meta").innerHTML = `<b>${clock(r.last.ms)}</b> de vídeo · <b>${size(r.last.bytes || 0)}</b>${r.last.markers ? ` · <b>${r.last.markers}</b> ${r.last.markers === 1 ? "marca" : "marcas"}` : ""}`;
      setText($("#r-done-count"), `${Math.max(0, Math.ceil((doneUntil - Date.now()) / 1000))}s`);
    }
  }

  $("#r-min").addEventListener("pointerdown", () => { minimized = true; sync(); });
  $("#r-pause").addEventListener("pointerdown", (e) => cmd("pause", null, e.currentTarget, e));
  $("#r-mark").addEventListener("pointerdown", async (e) => {
    const b = e.currentTarget;
    b.classList.remove("pop"); void b.offsetWidth; b.classList.add("pop");
    await cmd("mark", null, b, e);
    draw();
  });
  $("#r-stop").addEventListener("pointerdown", (e) => {
    // dois toques: um esbarrão na tela não pode acabar com a gravação
    if (Date.now() > stopAsk) { stopAsk = Date.now() + 3500; ripple(e.currentTarget, e); sync(); setTimeout(sync, 3600); return; }
    stopAsk = 0;
    cmd("stop", null, e.currentTarget, e);
  });
  $("#r-done-open").addEventListener("pointerdown", (e) => cmd("open", { path: rec?.last?.path }, e.currentTarget, e));
  $("#r-done-back").addEventListener("pointerdown", () => { doneUntil = 0; sync(); });

  // ---------- histórico (a linha do tempo sobrevive a um recarregar do painel) ----------
  async function loadHistory() {
    try {
      const h = await Bridge.call("record", { cmd: "history" });
      if (!h || !h.tracks) return;
      h.tracks.forEach((b64, t) => {
        const bin = atob(b64), a = hist[t] || (hist[t] = []);
        for (let i = 0; i < bin.length; i++) a[i] = Math.max(a[i] || 0, bin.charCodeAt(i));
      });
      draw();
    } catch { /* app antigo: a linha do tempo começa de agora */ }
  }

  // ---------- status ----------
  function apply(r, s = Bridge.status || {}) {
    rec = r;
    const on = !!r.active;
    if (on && !wasActive) { // começou (ou o painel abriu com a gravação rolando)
      hist = r.tracks.map(() => []);
      win = 60; rate = 0; bytesAt = null; stopAsk = 0; doneUntil = 0;
      curMs = r.ms; curAt = performance.now();
      loadHistory();
    }
    if (!on && wasActive) { // parou: mostra o resumo por uns segundos e volta pro painel
      const sec = Number(cfg().voltar ?? 12);
      doneUntil = Date.now() + (isNaN(sec) ? 12 : Math.max(0, sec)) * 1000;
      minimized = false;
      live.forEach((v) => { v.peak = v.mag = v.hold = -100; });
    }
    wasActive = on;
    if (on) { curMs = r.ms; curAt = performance.now(); }
    while (live.length < r.tracks.length) live.push({ peak: -100, mag: -100, hold: -100, holdAt: 0 });

    if (failSeq === null) failSeq = r.failSeq;
    else if (r.failSeq !== failSeq) { failSeq = r.failSeq; if (r.fail && Date.now() > quiet) toast(`Gravação: ${r.fail}`); }

    renderCard(r);
    showing = on || r.starting || Date.now() < doneUntil;
    if (!showing) minimized = false;
    const full = showing && !minimized;
    if (full !== body.classList.contains("recording")) {
      body.classList.toggle("recording", full);
      if (full) requestAnimationFrame(draw);
    }
    const chip = $("#chip-rec");
    chip.hidden = !(showing && minimized && on);
    if (!chip.hidden) setText($("#chip-rec-time"), clock(r.ms));
    if (full) { renderScene(r, s); if (!on || r.paused) draw(); }
  }
  const sync = () => { if (rec) apply(rec); };

  // ---------- níveis ao vivo ----------
  Bridge.on("meters", (m) => {
    const now = performance.now();
    for (let t = 0; t < live.length; t++) {
      const peak = m.l[t * 2] ?? -100, mag = m.l[t * 2 + 1] ?? -100, v = live[t];
      v.peak = Math.max(peak, v.peak - 1.8); // sobe na hora, desce devagar (uns 27 dB por segundo)
      v.mag = Math.max(mag, v.mag - 1.8);
      if (peak >= v.hold) { v.hold = peak; v.holdAt = now; }
      else if (now - v.holdAt > 1300) v.hold -= 1.2;
      if (m.ms >= 0 && !m.paused && hist[t]) {
        const a = hist[t], i = Math.floor(m.ms / BUCKET);
        for (let k = a.length; k < i; k++) a[k] = a[k] || 0;
        a[i] = Math.max(a[i] || 0, toByte(peak));
      }
    }
    if (m.ms >= 0) { curMs = m.ms; curAt = now; }
    if (body.classList.contains("recording")) {
      draw();
      tickTimer();
      for (let t = 0; t < live.length; t++) {
        const el = document.getElementById(`r-db-${t}`);
        if (el) setText(el, rec?.tracks[t]?.muted ? "MUDO" : live[t].peak <= FLOOR ? "silêncio" : `${Math.round(live[t].peak)} dB`);
      }
    } else if (!card.hidden) { // teste de áudio no cartão
      document.querySelectorAll("#rc-tracks .bar i").forEach((bar, t) => {
        if (!live[t]) return;
        bar.style.width = `${Math.max(0, Math.min(1, (live[t].peak - FLOOR) / -FLOOR)) * 100}%`;
        bar.classList.toggle("clip", live[t].peak > -3);
      });
    }
  });

  function tickTimer() {
    const ms = curMs, s = Math.floor(ms / 1000);
    setText($("#r-t-h"), String(Math.floor(s / 3600)));
    setText($("#r-t-m"), pad(Math.floor(s / 60) % 60));
    setText($("#r-t-s"), pad(s % 60));
    setText($("#r-t-d"), `.${Math.floor((ms % 1000) / 100)}`);
  }

  // ---------- desenho ----------
  function prep(cv) {
    const w = cv.clientWidth, h = cv.clientHeight;
    if (!w || !h) return null;
    const k = (cv.getBoundingClientRect().width / w) * (devicePixelRatio || 1); // já conta a escala do palco
    const W = Math.round(w * k), H = Math.round(h * k);
    if (cv.width !== W || cv.height !== H) { cv.width = W; cv.height = H; }
    const ctx = cv.getContext("2d");
    ctx.setTransform(k, 0, 0, k, 0, 0);
    ctx.clearRect(0, 0, w, h);
    return { ctx, w, h };
  }
  const MONO = `"Cascadia Mono", "JetBrains Mono", Consolas, monospace`;
  const WINDOWS = [60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200];
  const TICKS = [5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
  const tickLabel = (sec) => (sec >= 3600 ? `${Math.floor(sec / 3600)}h${pad(Math.floor(sec / 60) % 60)}` : sec >= 60 ? `${Math.floor(sec / 60)}:${pad(sec % 60)}` : `0:${pad(sec)}`);
  function rr(ctx, x, y, w, h, r) { ctx.beginPath(); ctx.roundRect(x, y, w, h, r); }

  function draw() {
    if (!body.classList.contains("recording") || !rec) return;
    drawTimeline();
    drawVu();
  }

  function drawTimeline() {
    const p = prep($("#r-tl-canvas"));
    if (!p) return;
    const { ctx, w, h } = p;
    const n = Math.max(1, rec.tracks.length);
    const done = !rec.active && !rec.starting;
    const ms = done ? rec.last?.ms || curMs : curMs, sec = ms / 1000;

    // a janela cresce em degraus (1, 2, 5, 10 min...) quando a gravação chega perto da borda; a troca é animada
    const target = WINDOWS.find((x) => sec <= x * 0.9) || WINDOWS[WINDOWS.length - 1];
    if (Math.abs(target - win) < target * 0.005) win = target; else win += (target - win) * 0.22;
    setText($("#r-scale"), `janela de ${target >= 3600 ? `${target / 3600} h` : `${target / 60} min`}`);

    const RUL = 28, GAP = 8, PADR = 10, tw = w - PADR;
    const X = (t) => (t / win) * tw;
    const laneH = (h - RUL - GAP * (n - 1)) / n;
    const xNow = Math.min(tw, X(sec));

    // régua
    const step = TICKS.find((t) => X(t) >= 64) || 3600;
    ctx.font = `10px ${MONO}`; ctx.textBaseline = "middle";
    for (let t = 0; t <= win; t += step) {
      const x = Math.round(X(t)) + 0.5;
      ctx.strokeStyle = "rgba(143,176,255,.10)"; ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(x, RUL - 6); ctx.lineTo(x, h); ctx.stroke();
      ctx.fillStyle = t <= sec ? "#7f93c4" : "#4a5d8f";
      ctx.textAlign = t === 0 ? "left" : "center";
      if (x < tw - 18 && Math.abs(x - xNow) > 46) ctx.fillText(tickLabel(t), t === 0 ? 2 : x, 9);
    }

    // faixas
    const PITCH = 3, BARW = 2;
    for (let tr = 0; tr < n; tr++) {
      const y0 = RUL + tr * (laneH + GAP), cy = y0 + laneH / 2, amp = laneH / 2 - 7;
      const color = COLORS[tr % COLORS.length], muted = rec.tracks[tr]?.muted;
      ctx.fillStyle = "rgba(5,13,34,.45)"; rr(ctx, 0, y0, w, laneH, 12); ctx.fill();
      ctx.strokeStyle = "rgba(143,176,255,.12)"; ctx.lineWidth = 1; rr(ctx, 0.5, y0 + 0.5, w - 1, laneH - 1, 12); ctx.stroke();
      // parte já gravada
      ctx.save();
      rr(ctx, 0, y0, w, laneH, 12); ctx.clip();
      const g = ctx.createLinearGradient(0, 0, xNow, 0);
      g.addColorStop(0, "rgba(143,176,255,.03)"); g.addColorStop(1, "rgba(143,176,255,.10)");
      ctx.fillStyle = g; ctx.fillRect(0, y0, xNow, laneH);
      ctx.strokeStyle = "rgba(143,176,255,.16)"; ctx.setLineDash([2, 5]);
      ctx.beginPath(); ctx.moveTo(xNow, cy + 0.5); ctx.lineTo(w, cy + 0.5); ctx.stroke(); ctx.setLineDash([]);

      const a = hist[tr] || [], bars = Math.floor(xNow / PITCH), perBar = (win / tw) * PITCH * (1000 / BUCKET);
      for (let pass = 0; pass < 2; pass++) { // 0 = normal, 1 = estourando (vermelho)
        ctx.fillStyle = pass ? "#e0233f" : color;
        ctx.globalAlpha = muted && !pass ? 0.45 : 1;
        ctx.beginPath();
        for (let i = 0; i <= bars; i++) {
          const b0 = Math.floor(i * perBar), b1 = Math.max(b0 + 1, Math.floor((i + 1) * perBar));
          let v = 0;
          for (let b = b0; b < b1 && b < a.length; b++) if (a[b] > v) v = a[b];
          const hot = v >= 243; // acima de -3 dB
          if (hot !== !!pass) continue;
          const hh = Math.max(1, Math.pow(v / 255, 1.7) * amp);
          ctx.rect(i * PITCH, cy - hh, BARW, hh * 2);
        }
        ctx.fill();
      }
      ctx.globalAlpha = 1;
      ctx.restore();
    }

    // marcas
    ctx.font = `700 9px ${MONO}`; ctx.textAlign = "center";
    (rec.markers || []).forEach((m, i) => {
      const x = Math.round(X(m / 1000)) + 0.5;
      ctx.strokeStyle = "rgba(224,35,63,.55)"; ctx.setLineDash([3, 4]); ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(x, RUL); ctx.lineTo(x, h); ctx.stroke(); ctx.setLineDash([]);
      ctx.fillStyle = "#c8102e"; rr(ctx, x - 7, RUL - 15, 14, 13, 4); ctx.fill();
      ctx.fillStyle = "#fff"; ctx.fillText(String(i + 1), x, RUL - 8);
    });

    // agulha
    const live1 = rec.active && !rec.paused;
    const col = live1 ? "#e0233f" : "#8fb0ff";
    ctx.strokeStyle = col; ctx.lineWidth = 2; ctx.shadowColor = col; ctx.shadowBlur = live1 ? 12 : 0;
    ctx.beginPath(); ctx.moveTo(xNow, RUL - 4); ctx.lineTo(xNow, h); ctx.stroke();
    ctx.shadowBlur = 0;
    ctx.font = `700 10px ${MONO}`;
    const label = clock(ms), lw = ctx.measureText(label).width + 14;
    const lx = Math.max(0, Math.min(w - lw, xNow - lw / 2));
    ctx.fillStyle = live1 ? "#c8102e" : "#1c3c80"; rr(ctx, lx, 0, lw, 18, 5); ctx.fill();
    ctx.fillStyle = "#fff"; ctx.textAlign = "center"; ctx.fillText(label, lx + lw / 2, 9.5);
  }

  function drawVu() {
    const p = prep($("#r-vu-canvas"));
    if (!p) return;
    const { ctx, w, h } = p;
    const n = Math.max(1, rec.tracks.length);
    const SCALE = 34, top = 8, bot = 8, H = h - top - bot;
    const colW = (w - SCALE) / n;
    const Y = (db) => top + (1 - Math.max(0, Math.min(1, (db - FLOOR) / -FLOOR))) * H;

    // escala de dB no meio
    ctx.font = `9.5px ${MONO}`; ctx.textAlign = "center"; ctx.textBaseline = "middle";
    const sx = n > 1 ? colW + SCALE / 2 : w - SCALE / 2;
    for (const db of [0, -6, -12, -20, -30, -40, -50, -60]) {
      ctx.fillStyle = db >= -3 ? "#e0233f" : "#4a5d8f";
      ctx.fillText(db === 0 ? "0" : String(db), sx, Y(db));
    }

    const SEG = 44, gap = 2, segH = (H - gap * (SEG - 1)) / SEG;
    for (let t = 0; t < n; t++) {
      const v = live[t] || { peak: -100, mag: -100, hold: -100 }, muted = rec.tracks[t]?.muted;
      const x0 = t === 0 ? 0 : colW * t + SCALE, bw = Math.min(colW - 8, 78), bx = x0 + (colW - bw) / 2;
      const half = (bw - 4) / 2;
      for (let s = 0; s < SEG; s++) {
        const db = FLOOR + ((s + 0.5) / SEG) * -FLOOR, y = top + H - (s + 1) * segH - s * gap;
        const hot = db > -3, warm = db > -12;
        const onMag = !muted && v.mag >= db, onPeak = !muted && v.peak >= db;
        // esquerda = média (o "volume" de verdade), direita = pico
        for (const [k, on] of [[0, onMag], [1, onPeak]]) {
          ctx.fillStyle = on ? (hot ? "#e0233f" : warm ? "#c5d5ff" : COLORS[t % COLORS.length]) : hot ? "rgba(200,16,46,.16)" : "rgba(143,176,255,.09)";
          rr(ctx, bx + k * (half + 4), y, half, segH, 1.5); ctx.fill();
        }
      }
      if (!muted && v.hold > FLOOR) { // pico segurado
        ctx.fillStyle = v.hold > -3 ? "#ff5a6e" : "#e8eeff";
        ctx.fillRect(bx, Y(v.hold) - 1, bw, 2);
      }
      if (muted) {
        ctx.fillStyle = "rgba(200,16,46,.9)"; rr(ctx, bx, top + H / 2 - 12, bw, 24, 6); ctx.fill();
        ctx.fillStyle = "#fff"; ctx.font = `700 10px ${MONO}`; ctx.fillText("MUDO", bx + bw / 2, top + H / 2 + 0.5); ctx.font = `9.5px ${MONO}`;
      }
    }
  }

  // por último: o onStatus já chama na hora com o status que tiver, e aí tudo acima precisa existir
  Bridge.onStatus((s) => { if (s.rec) apply(s.rec, s); });
  setInterval(() => { if (rec && !rec.active && doneUntil) sync(); }, 1000); // contagem do resumo
})();
