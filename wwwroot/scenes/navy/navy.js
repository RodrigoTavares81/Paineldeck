// =====================================================================
//  PainelDeck · cena "Navy"
//  Lê o status do PC (Bridge.onStatus) e o deck (Bridge.onDeck):
//   - férias: pega o widget "ferias" do deck (inicio/volta) ou deck.navy.ferias
//   - jogos: seção "games" do deck (capas grandes; "roda" abre a roda de jogos)
//   - notícias: Bridge.call("news") (o C# busca os feeds de "news" no deck.json)
//   - dock: deck.navy.dock (lista de botões) ou os botões da 1ª página (menos mic/mídia, que têm lugar próprio)
//   - modo jogo: status.game (o C# acha o jogo aberto) → FPS/sessão no lugar das férias, "macros" do jogo
//     no lugar da prateleira e o tempo de jogo da semana (Bridge.call("playtime")) no lugar das notícias
//   - atualização: status.update (chip no topo; 2 toques → Bridge.call("update"))
// =====================================================================
(() => {
  const $ = (s, el = document) => el.querySelector(s);
  const stage = $("#stage");

  // ---------- escala: desenhado em 1280x720, cabe em qualquer tela ----------
  const fit = () => stage.style.setProperty("--fit", Math.min(innerWidth / 1280, innerHeight / 720));
  addEventListener("resize", fit);
  fit();

  // ---------- aviso ----------
  const toastEl = $("#toast");
  function toast(msg, kind = "err") {
    toastEl.textContent = msg;
    toastEl.className = "show " + (kind === "ok" ? "ok" : "");
    clearTimeout(toast.t);
    toast.t = setTimeout(() => (toastEl.className = ""), kind === "ok" ? 2200 : 4500);
  }

  // ---------- relógio ----------
  const pad = (n) => String(n).padStart(2, "0");
  function tickClock(now) {
    $("#hh").textContent = pad(now.getHours());
    $("#mm").textContent = pad(now.getMinutes());
    $("#ss").textContent = pad(now.getSeconds());
    $("#date").textContent = now.toLocaleDateString("pt-BR", { weekday: "long", day: "numeric", month: "long" });
  }

  // ---------- férias ----------
  const D = {
    parse(s) { const [y, m, d] = String(s).split("-").map(Number); return new Date(y, m - 1, d); },
    sod(d) { return new Date(d.getFullYear(), d.getMonth(), d.getDate()); },
    days(a, b) { return Math.round((D.sod(b) - D.sod(a)) / 86400000); },
    short(d) { return d.toLocaleDateString("pt-BR", { day: "2-digit", month: "short" }).replace(".", ""); },
  };
  let ferias = null;
  let lastNum = null;
  function tickHero(now) {
    if (!ferias) return;
    const { inicio, volta } = ferias;
    const ate = D.days(now, inicio), ateVolta = D.days(now, volta);
    let kicker, num, unit, caption, timer;
    if (ate > 0) {
      const ms = inicio - now;
      kicker = "CONTAGEM REGRESSIVA";
      num = ate; unit = ate === 1 ? "dia" : "dias"; caption = "pras férias";
      timer = `faltam ${Math.floor(ms / 3.6e6)}h ${pad(Math.floor(ms / 6e4) % 60)}m ${pad(Math.floor(ms / 1000) % 60)}s`;
    } else if (ateVolta > 0) {
      kicker = ate === 0 ? "COMEÇOU!" : "MODO FÉRIAS";
      num = ateVolta; unit = ateVolta === 1 ? "dia" : "dias"; caption = ateVolta === 1 ? "amanhã volta" : "até a volta";
      timer = "aproveita";
    } else {
      kicker = "ACABOU"; num = "—"; unit = ""; caption = "de volta ao trampo"; timer = "edite as datas no deck.json";
    }
    $("#hero-kicker").textContent = kicker;
    if (num !== lastNum) {
      const el = $("#hero-num");
      el.textContent = num;
      el.classList.remove("pop"); void el.offsetWidth; el.classList.add("pop");
      lastNum = num;
    }
    $("#hero-unit").textContent = unit;
    $("#hero-caption").textContent = caption;
    $("#hero-timer").textContent = timer;

    // linha do tempo: de 14 dias antes do início até a volta
    const start = new Date(inicio.getTime() - 14 * 86400000);
    const span = volta - start;
    const pos = (d) => Math.max(0, Math.min(1, (d - start) / span)) * 100;
    $("#hero-fill").style.width = pos(now) + "%";
    $("#hero-now").style.left = pos(now) + "%";
    $("#hero-tag").style.left = `clamp(24px, ${pos(now)}%, calc(100% - 24px))`;
    $("#hero-tick").style.left = pos(inicio) + "%";
    $("#hero-m1").style.left = `clamp(40px, ${pos(inicio)}%, calc(100% - 110px))`;
    $("#hero-d1").textContent = D.short(inicio);
    $("#hero-d2").textContent = D.short(volta);
  }

  // ---------- telemetria ----------
  const hot = (t) => (t >= 80 ? ` class="warm"` : "");
  const METRICS = {
    cpu: { name: "CPU", sub: (s, g) => `pico 1 min <b>${Math.round(Math.max(...g.hist))}%</b>` },
    gpu: { name: "GPU", sub: (s) => (s.gpuTemp != null ? `<b${hot(s.gpuTemp)}>${s.gpuTemp}°C</b>` : "--°C") },
    ram: { name: "RAM", sub: (s) => (s.ramUsedGb != null ? `<b>${s.ramUsedGb}</b> / ${s.ramTotalGb} GB` : "") },
    vram: { name: "VRAM", sub: (s) => (s.vramUsedGb != null ? `<b>${s.vramUsedGb}</b> / ${s.vramTotalGb} GB` : "") },
  };
  const ARC = 2 * Math.PI * 52 * 0.75; // 270°
  const gauges = {};
  document.querySelectorAll(".gauge").forEach((el) => {
    const m = el.dataset.metric, def = METRICS[m];
    let ticks = "";
    for (let i = 0; i <= 20; i++) {
      const a = ((135 + (270 * i) / 20) * Math.PI) / 180;
      const r1 = i % 5 === 0 ? 37 : 40, r2 = 44;
      ticks += `<line x1="${64 + r1 * Math.cos(a)}" y1="${64 + r1 * Math.sin(a)}" x2="${64 + r2 * Math.cos(a)}" y2="${64 + r2 * Math.sin(a)}"/>`;
    }
    el.innerHTML = `
      <div class="arc">
        <svg viewBox="0 0 128 128"><g stroke="rgba(143,176,255,.22)" stroke-width="1.5">${ticks}</g></svg>
        <svg viewBox="0 0 128 128" class="rot"><circle class="track" cx="64" cy="64" r="52"/><circle class="val" cx="64" cy="64" r="52"/></svg>
        <div class="center"><div class="pct">--<small>%</small></div></div>
        <div class="name">${def.name}</div>
      </div>
      <div class="sub">&nbsp;</div>`;
    gauges[m] = { el, def, hist: [] };
  });

  function updateGauges(s) {
    for (const [m, g] of Object.entries(gauges)) {
      const raw = s[m];
      if (raw == null) {
        g.el.classList.add("na");
        $(".pct", g.el).innerHTML = "—";
        $(".sub", g.el).textContent = "sem leitura";
        continue;
      }
      g.el.classList.remove("na");
      const v = Math.max(0, Math.min(100, Number(raw)));
      g.hist.push(v);
      if (g.hist.length > 60) g.hist.shift(); // 1 leitura por segundo = último minuto
      g.el.classList.toggle("hot", v >= 85);
      $(".val", g.el).style.strokeDasharray = `${(ARC * v) / 100} 400`;
      $(".pct", g.el).innerHTML = `${Math.round(v)}<small>%</small>`;
      $(".sub", g.el).innerHTML = g.def.sub(s, g) || "&nbsp;";
    }
  }

  // ---------- microfone ----------
  const mic = $("#mic"), micbox = $("#micbox");
  function updateMic(s) {
    const muted = !!s.micMuted;
    micbox.classList.toggle("muted", muted);
    $("#mic-state").textContent = muted ? "MUTADO" : "LIGADO";
    $("#mic-hint").textContent = muted ? "toque pra ligar" : "toque pra mutar";
  }

  // ---------- ações ----------
  function ripple(el, e) {
    const r = el.getBoundingClientRect(), k = r.width / el.offsetWidth || 1;
    const size = Math.max(el.offsetWidth, el.offsetHeight) * 2;
    const s = document.createElement("span");
    s.className = "ripple";
    s.style.width = s.style.height = size + "px";
    s.style.left = (e.clientX - r.left) / k - size / 2 + "px";
    s.style.top = (e.clientY - r.top) / k - size / 2 + "px";
    el.appendChild(s);
    setTimeout(() => s.remove(), 650);
  }
  function flash(el, cls) { el.classList.remove("ok", "err"); void el.offsetWidth; el.classList.add(cls); }

  // ações que só existem no JS (roda de jogos, gaveta de macros, pedir ao Claude, modo descanso)
  function runSpecial(action) {
    const t = action.type;
    const hooks = {
      games: () => Games.open(),
      macros: () => Macros.open(),
      claude: () => Editor.askClaude(),
      rest: () => Bridge.call("rest", { on: true }).catch((err) => toast(`Descansar: ${err.message}`)),
    };
    if (!(t in hooks)) return false;
    hooks[t]();
    return true;
  }
  async function run(action, el, e, label) {
    if (el && e) ripple(el, e);
    if (runSpecial(action)) return;
    try {
      await Bridge.run(action);
      if (el) flash(el, "ok");
    } catch (err) {
      if (el) flash(el, "err");
      toast(`${label || "Ação"}: ${err.message}`);
    }
  }

  mic.addEventListener("pointerdown", () => run({ type: "mic", mode: "toggle" }, null, null, "Mic"));
  $("#btn-rest").addEventListener("pointerdown", () => run({ type: "rest" }));
  $("#games-all").addEventListener("pointerdown", () => run({ type: "games" }));

  // ---------- dock ----------
  const OWN_PLACE = (it) => it.widget || it.page !== undefined || !it.action || it.action.type === "mic" || it.action.type === "media";
  function dockItems(deck) {
    if (deck.navy?.dock) return deck.navy.dock;
    const items = (deck.pages || []).flatMap((p) => p.items || []).filter((it) => !OWN_PLACE(it));
    const seen = new Set();
    return items.filter((it) => { const k = JSON.stringify(it.action); if (seen.has(k)) return false; seen.add(k); return true; });
  }
  const SPECIALS = [
    { icon: "🔁", label: "Macros", action: { type: "macros" } },
    { icon: "✨", label: "Claude", action: { type: "claude" }, cls: "claude" },
  ];
  function renderDock(deck) {
    const dock = $("#dock");
    dock.innerHTML = "";
    const normal = dockItems(deck).filter((it) => !["games", "macros", "claude"].includes(it.action?.type)).slice(0, 8);
    const make = (it, k, extra = "") => {
      const b = document.createElement("button");
      b.className = "key " + extra;
      b.style.setProperty("--k", k);
      if (it.color) b.style.setProperty("--accent", it.color);
      b.innerHTML = `<span class="ico"></span><span class="lbl"></span>`;
      $(".ico", b).textContent = it.icon || "•";
      $(".lbl", b).textContent = it.label || "";
      b.addEventListener("pointerdown", (e) => run(it.action, b, e, it.label));
      return b;
    };
    normal.forEach((it, k) => dock.appendChild(make(it, k)));
    const sep = document.createElement("div");
    sep.className = "sep";
    dock.appendChild(sep);
    SPECIALS.forEach((it, k) => dock.appendChild(make(it, normal.length + k, "special " + (it.cls || ""))));
  }

  // ---------- jogos ----------
  const PLAY = `<svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>`;
  function renderGames(deck) {
    const shelf = $("#shelf");
    const list = (deck.games || []).filter((g) => g && g.action);
    shelf.innerHTML = "";
    if (!list.length) {
      shelf.innerHTML = `<div class="empty"><b>Nenhum jogo ainda</b>No ✏️, use 🎮 Jogos pra adicionar</div>`;
      return;
    }
    list.slice(0, 5).forEach((g, k) => {
      const b = document.createElement("button");
      b.className = "game";
      b.style.setProperty("--k", k);
      b.title = `Abrir ${g.name || "jogo"}`;
      b.innerHTML = `<div class="bg"></div><div class="ph"></div><img alt=""><div class="shade"></div>
        <div class="foot"><span class="gname"></span><span class="play">${PLAY}</span></div>`;
      $(".gname", b).textContent = g.name || "";
      $(".ph", b).textContent = (g.name || "?").trim().charAt(0).toUpperCase();
      const img = $("img", b);
      if (g.image) {
        img.onload = () => {
          $(".ph", b).remove();
          $(".bg", b).style.backgroundImage = `url("${img.src}")`;
          // capa em pé (2:3) preenche; logo quadrado ou deitado aparece inteiro sobre o fundo desfocado
          b.classList.toggle("fit", img.naturalWidth / img.naturalHeight > 0.8);
        };
        img.onerror = () => img.remove();
        img.src = g.image;
      } else img.remove();
      b.addEventListener("pointerdown", async (e) => {
        ripple(b, e);
        b.classList.remove("go", "err"); void b.offsetWidth;
        try {
          await Bridge.run(g.action);
          b.classList.add("go");
          toast(`Abrindo ${g.name}…`, "ok");
        } catch (err) {
          b.classList.add("err");
          toast(`${g.name}: ${err.message}`);
        }
      });
      shelf.appendChild(b);
    });
  }

  // ---------- notícias de IA ----------
  const PER_PAGE = 4;
  let news = { items: [] }, page = 0, newsHover = false;
  const feed = $("#feed");
  $(".news").addEventListener("pointerenter", () => (newsHover = true));
  $(".news").addEventListener("pointerleave", () => (newsHover = false));
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
  function newsMsg(title, text) {
    feed.innerHTML = `<li class="msg"><b></b><span></span></li>`;
    $(".msg b", feed).textContent = title;
    $(".msg span", feed).textContent = text;
    $("#pager").innerHTML = "";
  }
  function pages() { return Math.max(1, Math.ceil(news.items.length / PER_PAGE)); }
  function renderPage(animate) {
    const items = news.items.slice(page * PER_PAGE, page * PER_PAGE + PER_PAGE);
    const draw = () => {
      feed.classList.remove("out");
      feed.innerHTML = "";
      items.forEach((it, k) => {
        const li = document.createElement("li");
        li.className = "item";
        li.style.setProperty("--k", k);
        const fresh = Date.now() - Date.parse(it.published) < 3 * 3.6e6;
        li.innerHTML = `<div class="meta">${fresh ? `<i class="new"></i>` : ""}<span class="src"></span><span class="ago"></span></div><div class="ttl"></div>`;
        $(".src", li).textContent = it.source || "";
        $(".ago", li).textContent = ago(it.published);
        $(".ttl", li).textContent = it.title || "";
        li.title = it.title || "";
        li.addEventListener("pointerdown", (e) => { ripple(li, e); if (it.url) run({ type: "url", url: it.url }, null, null, "Notícia"); });
        feed.appendChild(li);
      });
      $("#pager").innerHTML = pages() > 1 ? Array.from({ length: pages() }, (_, i) => `<i class="${i === page ? "on" : ""}"></i>`).join("") : "";
    };
    if (animate && feed.children.length) { feed.classList.add("out"); setTimeout(draw, 380); }
    else draw();
  }
  function stamp() { $("#news-upd").textContent = news.updated ? ago(news.updated) : ""; }
  async function loadNews(force) {
    try {
      let r = await Bridge.call("news", force ? { force: true } : {});
      if (!r && Bridge.preview) r = PREVIEW_NEWS();
      if (!r) throw new Error("sem resposta");
      news = { ...r, items: r.items || [] };
      if (r.title) $("#news-label").textContent = `${r.title.toUpperCase()} · NOTÍCIAS`;
      if (!news.items.length) newsMsg("Sem notícias agora", r.error ? `Não consegui buscar: ${r.error}` : "Tento de novo daqui a pouco.");
      else { page = Math.min(page, pages() - 1); renderPage(false); }
      stamp();
    } catch (err) {
      if (news.items.length) return; // mantém o que já tinha
      newsMsg("Notícias desligadas", /desconhecid|unknown|not found|método/i.test(err.message)
        ? "Reinicie o PainelDeck pra ligar as notícias."
        : `Não consegui buscar: ${err.message}`);
    }
  }
  // só no preview do navegador (sem o app): manchetes de exemplo
  function PREVIEW_NEWS() {
    const h = (x) => new Date(Date.now() - x * 3.6e6).toISOString();
    const ex = (title, x) => ({ title, source: "Exemplo", published: h(x), url: "" });
    return { title: "IA", updated: h(0.1), items: [
      ex("Aqui aparecem as manchetes de IA do Google Notícias quando o app estiver rodando", 0.6),
      ex("Cada manchete abre no navegador com um clique", 2),
      ex("A lista troca de página sozinha a cada 15 segundos (e para quando o mouse está em cima)", 4),
      ex("Os feeds ficam em \"news\" no deck.json: dá pra trocar o tema ou pôr outros sites", 6),
      ex("Atualiza sozinho a cada 20 minutos", 9),
      ex("Itens com menos de 3 horas ganham a bolinha vermelha", 12),
    ] };
  }
  setInterval(() => {
    stamp();
    if (!newsHover && pages() > 1) { page = (page + 1) % pages(); renderPage(true); }
  }, 15000);
  setInterval(() => loadNews(false), 10 * 60000);

  // ---------- modo jogo ----------
  const dur = (sec) => {
    const m = Math.floor((sec || 0) / 60);
    if (m < 60) return `${m}min`;
    return `${Math.floor(m / 60)}h${pad(m % 60)}`;
  };
  const clock = (sec) => { sec = Math.floor(sec || 0); return `${Math.floor(sec / 3600)}:${pad(Math.floor(sec / 60) % 60)}:${pad(sec % 60)}`; };
  let deckNow = null, gameName = null, fpsHist = [];
  const body = document.body;

  function swapIn(...els) { els.forEach((el, k) => { el.style.setProperty("--i", k); el.classList.remove("in"); void el.offsetWidth; el.classList.add("in"); }); }
  function gameCfg(name) { return (deckNow?.games || []).find((g) => g && g.name === name) || null; }

  function setGame(g) {
    const name = g ? g.name : null;
    if (name !== gameName) {
      gameName = name;
      fpsHist = [];
      body.classList.toggle("gaming", !!g);
      $("#chip-play").hidden = !g;
      if (g) {
        $("#g-name").textContent = g.name;
        const art = g.image || gameCfg(g.name)?.image;
        $("#g-art").style.backgroundImage = art ? `url("${art}")` : "";
        renderGameMacros(g);
        swapIn($("#gamehero"), $("#gmacros"), $("#playtime"));
        toast(`Modo jogo: ${g.name}`, "ok");
      } else {
        swapIn($("#hero"), $(".games"), $(".news"));
      }
      loadPlaytime();
      updateWeekChip();
    }
    if (!g) return;
    $("#chip-play-time").textContent = dur(g.sessionSec);
    $("#g-session").textContent = clock(g.sessionSec);
    $("#g-today").textContent = dur(g.todaySec);
    $("#gamehero").classList.toggle("idle", !!g.idle);
    const has = g.fps != null;
    $("#gamehero").classList.toggle("nofps", !has);
    $("#g-fps").classList.toggle("none", !has);
    $("#g-fps").classList.toggle("low", has && g.fps < 45);
    // sem FPS (PresentMon não instalado): o número grande vira o tempo da sessão
    $("#g-fps-num").textContent = has ? g.fps : `${Math.floor(g.sessionSec / 3600)}:${pad(Math.floor(g.sessionSec / 60) % 60)}`;
    $("#g-fps .unit").textContent = has ? "FPS" : "SESSÃO";
    const sub = $("#g-fps-sub");
    if (has) sub.innerHTML = [g.fpsLow != null ? `1% low <b>${g.fpsLow}</b>` : "", g.fpsAvg != null ? `média <b>${g.fpsAvg}</b>` : ""].filter(Boolean).join(" · ") || "&nbsp;";
    else { sub.innerHTML = `<span class="hint"></span>`; $(".hint", sub).textContent = g.fpsInfo || "sem leitura de FPS"; }
    if (has) { fpsHist.push(g.fps); if (fpsHist.length > 60) fpsHist.shift(); }
    drawSpark();
  }

  function drawSpark() {
    const svg = $("#g-spark"), W = 300, H = 70;
    if (fpsHist.length < 2) { $(".line", svg).setAttribute("d", ""); $(".area", svg).setAttribute("d", ""); $(".head", svg).setAttribute("cx", -10); return; }
    const max = Math.max(60, ...fpsHist) * 1.08, min = 0;
    // até ter 1 minuto de leituras, a linha estica pra largura toda
    const pts = fpsHist.map((v, i) => [(i / (fpsHist.length - 1)) * W, H - ((v - min) / (max - min)) * H]);
    const off = 0;
    const d = pts.map(([x, y], i) => `${i ? "L" : "M"}${(x + off).toFixed(1)} ${y.toFixed(1)}`).join(" ");
    $(".line", svg).setAttribute("d", d);
    $(".area", svg).setAttribute("d", `${d} L${W} ${H} L${(pts[0][0] + off).toFixed(1)} ${H} Z`);
    const [hx, hy] = pts[pts.length - 1];
    $(".head", svg).setAttribute("cx", hx + off);
    $(".head", svg).setAttribute("cy", hy);
  }

  // macros do jogo: "macros" do jogo no deck ("games": [{ ..., "macros": [{ icon, label, action }] }]);
  // sem isso, as macros com nome da seção "macros"
  function renderGameMacros(g) {
    const grid = $("#gm-grid");
    const cfg = gameCfg(g.name);
    $("#gm-label").textContent = `MACROS · ${g.name.toUpperCase()}`;
    let items = (cfg?.macros || []).filter((m) => m && m.action);
    const own = items.length > 0;
    if (!own) items = Object.keys(deckNow?.macros || {}).map((k) => ({ icon: "🔁", label: k, action: { type: "macro", ref: k } }));
    grid.innerHTML = "";
    grid.classList.toggle("few", items.length > 0 && items.length < 4);
    grid.style.setProperty("--n", Math.max(1, items.length));
    if (!items.length) {
      grid.innerHTML = `<div class="gm-empty"><b>Nenhuma macro pra esse jogo</b>
        <span>No deck.json, ponha <code>"macros": [{ "icon": "💬", "label": "gg", "action": {...} }]</code> no jogo
        (ou peça pro ✨ Claude).</span></div>`;
      return;
    }
    items.slice(0, 8).forEach((m, k) => {
      const b = document.createElement("button");
      b.className = "gm-key";
      b.style.setProperty("--k", k);
      if (m.color) b.style.setProperty("--accent", m.color);
      b.innerHTML = `<span class="ico"></span><span class="lbl"></span>`;
      $(".ico", b).textContent = m.icon || "•";
      $(".lbl", b).textContent = m.label || "";
      b.addEventListener("pointerdown", (e) => run(m.action, b, e, m.label));
      grid.appendChild(b);
    });
  }
  $("#gm-all").addEventListener("pointerdown", () => run({ type: "macros" }));

  // tempo de jogo da semana
  let play = null;
  async function loadPlaytime() {
    try {
      play = await Bridge.call("playtime");
      if (!play) return;
      renderPlaytime();
      updateWeekChip();
    } catch { play = null; $("#chip-week").hidden = true; } // app antigo, sem essa chamada
  }
  function renderPlaytime() {
    const week = play.week || [];
    const max = Math.max(1800, ...week.map((w) => w.total));
    $("#pt-total").innerHTML = `7 dias · <b>${dur(play.weekTotal)}</b>`;
    $("#pt-bars").innerHTML = week.map((w, k) => `
      <div class="pt-bar${w.today ? " today" : ""}" style="--k:${k}" title="${w.date}">
        <span class="v">${w.total >= 60 ? dur(w.total) : ""}</span>
        <i class="col${w.total < 60 ? " zero" : ""}" style="height:${Math.max(3, (w.total / max) * 100)}%"></i>
        <span class="d">${w.today ? "hoje" : w.label}</span>
      </div>`).join("");
    const top = (play.top || []).slice(0, 3);
    const tmax = Math.max(1, ...top.map((t) => t.sec));
    const ol = $("#pt-top");
    ol.innerHTML = "";
    if (!top.length) { ol.innerHTML = `<li><span class="none">Nenhum jogo nos últimos 7 dias</span></li>`; return; }
    top.forEach((t, k) => {
      const li = document.createElement("li");
      li.className = t.name === gameName ? "on" : "";
      li.innerHTML = `<span class="n"></span><span class="t">${dur(t.sec)}</span><span class="bar"><i style="width:${(t.sec / tmax) * 100}%;animation-delay:${200 + k * 90}ms"></i></span>`;
      $(".n", li).textContent = t.name;
      ol.appendChild(li);
    });
  }
  function updateWeekChip() {
    const chip = $("#chip-week");
    chip.hidden = !play || !!gameName;
    if (play) $("#chip-week-time").textContent = dur(play.today?.total || 0);
  }
  let ptTimer = 0;
  $("#chip-week").addEventListener("pointerdown", () => {
    const on = !body.classList.contains("show-playtime");
    body.classList.toggle("show-playtime", on);
    swapIn(on ? $("#playtime") : $(".news"));
    clearTimeout(ptTimer);
    if (on) { loadPlaytime(); ptTimer = setTimeout(() => { body.classList.remove("show-playtime"); swapIn($(".news")); }, 30000); }
  });
  setInterval(() => { if (gameName || body.classList.contains("show-playtime") || !play) loadPlaytime(); else updateWeekChip(); }, 60000);

  // ---------- atualização ----------
  let upd = null, askUntil = 0, doneSeen = false;
  const chipUp = $("#chip-update");
  function setUpdate(u) {
    upd = u || null;
    const st = upd?.state;
    const show = st === "available" || st === "working" || st === "error" || (st === "done" && !doneSeen);
    chipUp.hidden = !show;
    if (!show) return;
    const asking = st === "available" && Date.now() < askUntil;
    chipUp.className = `chip action update ${st}${asking ? " ask" : ""}`;
    const text = st === "available" ? (asking ? "Toque de novo pra atualizar" : `Atualizar · ${upd.latest || "nova versão"}`)
      : st === "working" ? (upd.message || "Atualizando…")
      : st === "done" ? (upd.message || "Atualizado") : (upd.message || "Erro ao atualizar");
    $("#chip-update-text").textContent = text;
    chipUp.title = st === "available" && upd.title ? `Novidade: ${upd.title}` : text;
  }
  chipUp.addEventListener("pointerdown", async () => {
    const st = upd?.state;
    if (st === "done") { doneSeen = true; setUpdate(upd); return; }
    if (st === "error") { Bridge.call("update", { check: true }).catch(() => {}); return; }
    if (st !== "available") return;
    if (Date.now() > askUntil) { askUntil = Date.now() + 4000; setUpdate(upd); setTimeout(() => setUpdate(upd), 4100); return; }
    askUntil = 0;
    try { setUpdate(await Bridge.call("update")); }
    catch (err) { toast(`Atualizar: ${err.message}`); }
  });

  // ---------- dados ----------
  function findFerias(deck) {
    if (deck.navy?.ferias) return deck.navy.ferias;
    for (const p of deck.pages || []) for (const it of p.items || []) if (it.widget === "ferias") return it;
    return null;
  }
  function applyDeck(deck) {
    if (!deck) return;
    deckNow = deck;
    if (gameName) renderGameMacros({ name: gameName });
    const f = findFerias(deck);
    ferias = f ? { inicio: D.parse(f.inicio), volta: D.parse(f.volta) } : null;
    $("#hero").style.display = ferias ? "" : "none";
    renderDock(deck);
    renderGames(deck);
    tickHero(new Date());
  }

  Bridge.onDeck((deck, error) => { if (error && !/Preview/.test(error)) toast(error); applyDeck(deck); });
  Bridge.onStatus((s) => {
    // no preview, inventa GPU/VRAM se o bridge não mandar
    if (Bridge.preview && s.gpu === undefined) {
      const t = Date.now() / 1000;
      s = { ...s, gpu: 38 + 22 * Math.sin(t / 3) + Math.random() * 6, vram: 21 + Math.random() * 2, vramUsedGb: 2.5, vramTotalGb: 11.9, gpuTemp: 47 + Math.round(Math.random() * 3), gpuName: "NVIDIA GeForce RTX 5070" };
    }
    updateGauges(s);
    updateMic(s);
    setGame(s.game || null);
    setUpdate(s.update);
  });

  loadNews(false);
  loadPlaytime();

  const loop = () => { const now = new Date(); tickClock(now); tickHero(now); };
  loop();
  setInterval(loop, 1000);
})();
