// =====================================================================
//  PainelDeck · cena "Navy"
//  Lê o status do PC (Bridge.onStatus) e o deck (Bridge.onDeck):
//   - férias: pega o widget "ferias" do deck (inicio/volta) ou deck.navy.ferias
//   - jogos: seção "games" do deck (capas grandes; "roda" abre a roda de jogos)
//   - coluna da direita: sessões do Claude Code (sessions.js) ou, com "navy": { "coluna": "noticias" },
//     notícias por Bridge.call("news") (o C# busca os feeds de "news" no deck.json)
//   - telemetria: CPU/GPU (com temperatura), RAM, VRAM + dados do PC (status.sys: tempo ligado, rede, disco, espaço)
//   - modo jogo: status.game (o C# acha o jogo aberto) → FPS/sessão/temperaturas no lugar das férias
//     e o selo JOGANDO na capa do jogo
//   - atualização: status.update (chip no topo; 2 toques → Bridge.call("update"))
//   - leve na GPU: nada se mexe em CSS sem parar; o mar e as bolinhas piscando são movidos aqui,
//     poucas vezes por segundo ("navy": { "animacao": 10 } no deck.json = quadros por segundo; 0 = parado)
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
  const temp = (t, warmAt, src, hint) => t == null
    ? `<span class="t">--°C</span>${hint ? `<span class="aprox">SEM SENSOR</span>` : ""}`
    : `<b class="t${t >= warmAt ? " warm" : ""}">${Math.round(t)}°C</b>${src === "acpi" ? `<span class="aprox">APROX</span>` : ""}`;
  const METRICS = {
    cpu: { name: "CPU", sub: (s) => temp(s.sys?.cpuTemp, 85, s.sys?.cpuTempSrc, s.sys?.cpuTempHint) },
    gpu: { name: "GPU", sub: (s) => temp(s.gpuTemp, 80) },
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
    gauges[m] = { el, def, last: "" };
    if (m === "cpu") el.title = "Temperatura da CPU: vem do ajudante de sensores (rode scripts\\instalar-sensores.ps1 uma vez)";
  });

  function updateGauges(s) {
    for (const [m, g] of Object.entries(gauges)) {
      const raw = s[m];
      if (raw == null) {
        g.el.classList.add("na");
        g.last = "";
        $(".pct", g.el).innerHTML = "—";
        $(".sub", g.el).textContent = "sem leitura";
        continue;
      }
      g.el.classList.remove("na");
      const v = Math.round(Math.max(0, Math.min(100, Number(raw))));
      const sub = g.def.sub(s) || "&nbsp;";
      const key = v + "|" + sub;
      if (key === g.last) continue; // nada mudou: não mexe no DOM (sem repintar)
      g.last = key;
      g.el.classList.toggle("hot", v >= 85);
      if (m === "cpu") g.el.dataset.hint = s.sys?.cpuTempHint || "";
      $(".val", g.el).style.strokeDasharray = `${(ARC * v) / 100} 400`;
      $(".pct", g.el).innerHTML = `${v}<small>%</small>`;
      $(".sub", g.el).innerHTML = sub;
    }
  }

  // ---------- dados do PC (status.sys) ----------
  const setText = (sel, v) => { const el = $(sel); if (el.textContent !== v) el.textContent = v; };
  const setHtml = (sel, v) => { const el = $(sel); if (el.innerHTML !== v) el.innerHTML = v; };
  function upFmt(sec) {
    const d = Math.floor(sec / 86400), h = Math.floor(sec / 3600) % 24, m = Math.floor(sec / 60) % 60;
    if (d > 0) return `${d}<small>d</small> ${pad(h)}<small>h</small> ${pad(m)}<small>m</small>`;
    return `${h}<small>h</small> ${pad(m)}<small>m</small>`;
  }
  // bytes/s → "12,4 Mb/s" (rede em bits, igual o speedtest)
  function bits(bps) {
    const b = (bps || 0) * 8;
    if (b >= 1e9) return `${(b / 1e9).toFixed(2).replace(".", ",")} Gb/s`;
    if (b >= 1e6) return `${(b / 1e6).toFixed(b >= 1e8 ? 0 : 1).replace(".", ",")} Mb/s`;
    if (b >= 1e3) return `${Math.round(b / 1e3)} kb/s`;
    return `${Math.round(b)} b/s`;
  }
  function bytes(bps) {
    const b = bps || 0;
    if (b >= 1e9) return `${(b / 1e9).toFixed(1).replace(".", ",")} GB/s`;
    if (b >= 1e6) return `${(b / 1e6).toFixed(b >= 1e8 ? 0 : 1).replace(".", ",")} MB/s`;
    if (b >= 1e3) return `${Math.round(b / 1e3)} KB/s`;
    return `0 KB/s`;
  }
  const netHist = [];
  let bootShown = "";
  function updateSys(s) {
    const x = s.sys;
    if (!x) { setText("#st-up-v", "--"); setText("#st-up-s", "reinicie o app pra ver"); return; }
    // tempo ligado
    setHtml("#st-up-v", upFmt(x.uptimeSec || 0));
    const boot = new Date(Date.now() - (x.uptimeSec || 0) * 1000);
    const bootTxt = `desde ${boot.toLocaleDateString("pt-BR", { weekday: "short" }).replace(".", "")} ${pad(boot.getHours())}:${pad(boot.getMinutes())}`;
    if (bootTxt !== bootShown) { bootShown = bootTxt; setText("#st-up-s", bootTxt); }
    // rede
    if (x.net) {
      setText("#st-net-down", bits(x.net.down));
      setText("#st-net-up", bits(x.net.up));
      netHist.push(x.net.down);
      if (netHist.length > 60) netHist.shift();
      drawNet();
    }
    // disco
    if (x.disk) {
      setHtml("#st-disk-v", `${x.disk.busy != null ? x.disk.busy : "--"}<small>%</small>`);
      setHtml("#st-disk-s", `L <b>${bytes(x.disk.read)}</b> · E <b>${bytes(x.disk.write)}</b>`);
      const st = $("#st-disk");
      let bar = $(".busy", st);
      if (!bar) { bar = document.createElement("i"); bar.className = "busy"; st.appendChild(bar); }
      bar.style.width = (x.disk.busy || 0) + "%";
      bar.classList.toggle("hot", (x.disk.busy || 0) >= 90);
    }
    // espaço livre
    const html = (x.drives || []).slice(0, 2).map((d) => {
      const usedPct = d.totalGb ? Math.round((1 - d.freeGb / d.totalGb) * 100) : 0;
      const low = d.totalGb && d.freeGb / d.totalGb < 0.1;
      const free = d.freeGb >= 1000 ? `${(d.freeGb / 1024).toFixed(1).replace(".", ",")} TB` : `${Math.round(d.freeGb)} GB`;
      return `<div class="drive${low ? " low" : ""}"><span class="n">${d.name}</span><span class="f"><b>${free}</b> livres</span><span class="bar"><i style="width:${usedPct}%"></i></span></div>`;
    }).join("");
    setHtml("#st-drives-list", html || `<span class="s">--</span>`);
  }
  function drawNet() {
    const svg = $("#st-net .spark"), W = 120, H = 30;
    if (netHist.length < 2) return;
    const max = Math.max(125000, ...netHist) * 1.15; // mínimo 1 Mb/s de escala
    const pts = netHist.map((v, i) => [(i / 59) * W + (60 - netHist.length) * (W / 59), H - (v / max) * H]);
    const d = pts.map(([x, y], i) => `${i ? "L" : "M"}${x.toFixed(1)} ${y.toFixed(1)}`).join(" ");
    $(".line", svg).setAttribute("d", d);
    $(".area", svg).setAttribute("d", `${d} L${W} ${H} L${pts[0][0].toFixed(1)} ${H} Z`);
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

  // CPU sem temperatura: tocar no relógio explica o que falta
  $('.gauge[data-metric="cpu"]').addEventListener("pointerdown", (e) => {
    const h = e.currentTarget.dataset.hint;
    if (h) toast(`Temperatura da CPU: ${h}`);
  });
  $("#btn-claude").addEventListener("pointerdown", () => run({ type: "claude" }));
  $("#btn-rest").addEventListener("pointerdown", () => run({ type: "rest" }));
  $("#games-all").addEventListener("pointerdown", () => run({ type: "games" }));

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
    list.slice(0, 4).forEach((g, k) => {
      const b = document.createElement("button");
      b.className = "game";
      b.dataset.name = g.name || "";
      b.style.setProperty("--k", k);
      b.title = `Abrir ${g.name || "jogo"}`;
      b.innerHTML = `<div class="bg"></div><div class="ph"></div><img alt=""><div class="shade"></div><span class="badge"><i class="rec"></i>JOGANDO</span>
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
    markPlaying();
  }
  function markPlaying() { document.querySelectorAll(".game").forEach((b) => b.classList.toggle("on", !!gameName && b.dataset.name === gameName)); }

  // ---------- notícias de IA ----------
  const PER_PAGE = 9; // coluna alta: cabem 9 manchetes de uma vez
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
      ex("Com mais de 9, a lista troca de página sozinha a cada 25 segundos (e para quando o mouse está em cima)", 4),
      ex("Os feeds ficam em \"news\" no deck.json: dá pra trocar o tema ou pôr outros sites", 6),
      ex("Atualiza sozinho a cada 20 minutos", 9),
      ex("Itens com menos de 3 horas ganham a bolinha vermelha", 12),
    ] };
  }
  setInterval(() => {
    stamp();
    if (!newsHover && pages() > 1) { page = (page + 1) % pages(); renderPage(true); }
  }, 25000);
  setInterval(() => { if (newsOn()) loadNews(false); }, 10 * 60000);
  // a coluna da direita é das sessões do Claude, a não ser que o deck peça as notícias
  const newsOn = () => /^not/i.test(String(deckNow?.navy?.coluna || ""));
  let newsLoaded = false;
  function applyColumn() {
    const on = newsOn();
    body.classList.toggle("col-news", on);
    if (on && !newsLoaded) { newsLoaded = true; loadNews(false); }
  }

  // ---------- modo jogo ----------
  const dur = (sec) => {
    const m = Math.floor((sec || 0) / 60);
    if (m < 60) return `${m}min`;
    return `${Math.floor(m / 60)}h${pad(m % 60)}`;
  };
  const clock = (sec) => { sec = Math.floor(sec || 0); return `${Math.floor(sec / 3600)}:${pad(Math.floor(sec / 60) % 60)}:${pad(sec % 60)}`; };
  let deckNow = null, gameName = null, fpsHist = [], lastStatus = {};
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
      markPlaying();
      if (g) {
        $("#g-name").textContent = g.name;
        const art = g.image || gameCfg(g.name)?.image;
        $("#g-art").style.backgroundImage = art ? `url("${art}")` : "";
        swapIn($("#gamehero"));
        toast(`Modo jogo: ${g.name}`, "ok");
      } else {
        swapIn($("#hero"));
      }
    }
    if (!g) return;
    $("#chip-play-time").textContent = dur(g.sessionSec);
    $("#g-session").textContent = clock(g.sessionSec);
    const t = (sel, v, warmAt) => { const el = $(sel); el.textContent = v == null ? "--°" : `${Math.round(v)}°`; el.classList.toggle("warm", v != null && v >= warmAt); };
    t("#g-cpu", lastStatus.sys?.cpuTemp, 85);
    t("#g-gpu", lastStatus.gpuTemp, 80);
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
    const f = findFerias(deck);
    ferias = f ? { inicio: D.parse(f.inicio), volta: D.parse(f.volta) } : null;
    $("#hero").style.display = ferias ? "" : "none";
    renderGames(deck);
    tickHero(new Date());
    applyColumn();
  }

  Bridge.onDeck((deck, error) => { if (error && !/Preview/.test(error)) toast(error); applyDeck(deck); });
  Bridge.onStatus((s) => {
    // no preview, inventa GPU/VRAM se o bridge não mandar
    if (Bridge.preview && s.gpu === undefined) {
      const t = Date.now() / 1000;
      s = { ...s, gpu: 38 + 22 * Math.sin(t / 3) + Math.random() * 6, vram: 21 + Math.random() * 2, vramUsedGb: 2.5, vramTotalGb: 11.9, gpuTemp: 47 + Math.round(Math.random() * 3), gpuName: "NVIDIA GeForce RTX 5070" };
    }
    lastStatus = s;
    updateGauges(s);
    updateSys(s);
    setGame(s.game || null);
    setUpdate(s.update);
  });

  window.Navy = { toast, ripple }; // usado pelo sessions.js (coluna das sessões do Claude)

  const loop = () => {
    const now = new Date();
    tickClock(now);
    tickHero(now);
    body.classList.toggle("blink", now.getSeconds() % 2 === 1); // ":" do relógio e bolinhas "ao vivo"/"jogando"
  };
  loop();
  setInterval(loop, 1000);

  // ---------- mar das férias: poucos quadros por segundo em vez de animação CSS a 60 ----------
  // Ondas com período de 50 unidades num SVG de 400: andar 200 e voltar pro 0 não dá pra perceber.
  const waves = [...document.querySelectorAll(".hero .sea path")].map((el, i) => ({ el, speed: [5, -7, 9][i] })); // unidades por segundo
  const t0 = performance.now();
  function fx() {
    const fpsCfg = Number(deckNow?.navy?.animacao ?? 10);
    const fpsNow = Math.max(0, Math.min(30, isNaN(fpsCfg) ? 10 : fpsCfg));
    if (fpsNow > 0 && !document.hidden && !gameName && ferias) {
      const t = (performance.now() - t0) / 1000;
      for (const w of waves) {
        const x = -(((t * Math.abs(w.speed)) % 200 + 200) % 200);
        w.el.setAttribute("transform", `translate(${(w.speed > 0 ? x : -200 - x).toFixed(1)} 0)`);
      }
    }
    setTimeout(fx, fpsNow > 0 ? 1000 / fpsNow : 2000);
  }
  fx();
})();
