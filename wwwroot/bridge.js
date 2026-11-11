// Ponte painel ⇄ PC. Dentro do app usa o WebView2; aberto num navegador comum
// entra em "modo preview" e só simula (bom pra mexer no visual).
window.Bridge = (() => {
  const here = document.currentScript.src; // as cenas (scenes/*) carregam com ../../bridge.js
  const wv = window.chrome && window.chrome.webview;
  const pending = new Map();
  const listeners = new Set();
  const deckListeners = new Set();
  const typed = new Map(); // outros tipos de mensagem do C# (ex.: "meters" = níveis de áudio da gravação)
  const emitTyped = (m) => (typed.get(m.type) || []).forEach((f) => f(m));
  let deck = null, deckError = null;
  const emitDeck = (d, error) => { if (d) deck = d; deckError = error || null; deckListeners.forEach((f) => f(deck, deckError)); };
  let nextId = 1;
  let last = {};

  const emit = (s) => { last = s; listeners.forEach((f) => f(s)); };

  if (wv) {
    wv.addEventListener("message", (e) => {
      const m = e.data;
      if (!m) return;
      if (m.type === "result") {
        const p = pending.get(m.id);
        if (!p) return;
        pending.delete(m.id);
        m.ok ? p.resolve(m.result) : p.reject(new Error(m.error || "erro"));
      } else if (m.type === "status") {
        emit(m);
      } else if (m.type === "deck") {
        emitDeck(m.deck, m.error);
      } else {
        emitTyped(m);
      }
    });
    wv.postMessage({ type: "ready" });
  } else {
    let mic = false;
    // preview com "?jogo" na URL simula um jogo aberto (modo jogo); "?jogo=sem-fps" simula sem o PresentMon
    const q = new URLSearchParams(location.search);
    const fakeGame = q.has("jogo") ? { since: Date.now() - 72 * 60000, noFps: q.get("jogo") === "sem-fps" } : null;
    const fakeGameStatus = () => {
      if (!fakeGame) return null;
      const t = Date.now() / 1000, sec = Math.round((Date.now() - fakeGame.since) / 1000);
      const fps = Math.round(142 + 18 * Math.sin(t / 4) + Math.random() * 8 - (Math.random() < 0.05 ? 40 : 0));
      return {
        name: q.get("nome") || "For Honor", image: "games/for-honor.jpg", exe: "ForHonor.exe", known: true,
        since: new Date(fakeGame.since).toISOString(), sessionSec: sec, todaySec: sec + 1840, idle: false,
        fps: fakeGame.noFps ? null : fps, fpsLow: fakeGame.noFps ? null : 96, fpsAvg: fakeGame.noFps ? null : 144,
        fpsInfo: fakeGame.noFps ? "Pra ver o FPS, rode scripts\\ativar-fps.ps1 uma vez" : null,
      };
    };
    // preview da gravação: "?gravando" (ou "?gravando=754" segundos já gravados), "?gravando=pausa", "?gravando=abrindo";
    // sem nada na URL, o botão GRAVAR do cartão também simula. Os níveis são inventados (fala no mic, música no sistema).
    const R = window.__previewRec = {
      active: q.has("gravando") && q.get("gravando") !== "abrindo", paused: q.get("gravando") === "pausa", starting: q.get("gravando") === "abrindo",
      t0: Date.now() - (Number(q.get("gravando")) || 754) * 1000, pausedAt: 0, markers: q.has("gravando") ? [93000, 371000, 612000] : [],
      muted: [false, false], meterUntil: 0, last: null, closed: q.has("obs-fechado"),
      ms() { return this.active ? (this.paused ? this.pausedAt : Date.now()) - this.t0 : 0; },
      level(tr, sec) { // dB de pico determinístico pro instante "sec"
        const h = (x) => { const v = Math.sin(x * 127.1 + tr * 311.7) * 43758.5453; return v - Math.floor(v); };
        if (tr === 0) {
          const talk = Math.sin(sec * 0.21) + 0.6 * Math.sin(sec * 0.057 + 1) > -0.25 && h(Math.floor(sec / 2.5)) > 0.22;
          return talk ? -30 + 24 * Math.pow(h(sec * 4), 0.6) : -56 + 6 * h(sec * 4);
        }
        const boom = h(Math.floor(sec / 7)) > 0.86 ? 10 : 0;
        return Math.min(-0.5, -27 + 7 * Math.sin(sec * 0.35) + 9 * h(sec * 4) + boom);
      },
    };
    if (R.paused) R.pausedAt = Date.now();
    const fakeRec = () => {
      const ms = R.ms();
      return {
        obs: R.closed ? "closed" : "ok", error: null, fail: null, failSeq: 0, active: R.active, paused: R.paused, starting: R.starting,
        ms, bytes: Math.round(ms * 4850), path: R.active ? "D:\\Vídeos\\2026-11-11 14-20-33.mkv" : null, file: R.active ? "2026-11-11 14-20-33.mkv" : null,
        scene: "PainelDeck · Tela principal", dir: "D:\\Vídeos", freeGb: 84.2, hotkey: "ctrl+alt+g", metering: R.active || Date.now() < R.meterUntil,
        tracks: R.closed ? [{ name: "Microfone", source: "", muted: false, found: false, mixer: [] }, { name: "Sistema", source: "", muted: false, found: false, mixer: [] }]
          : [{ name: "Microfone", source: "Mic/Aux", muted: R.muted[0], found: true, mixer: [1] }, { name: "Sistema", source: "Áudio do desktop", muted: R.muted[1], found: true, mixer: [2] }],
        markers: R.markers, stats: R.active ? { fps: 60, cpu: 3.1, skipped: 0, total: Math.round(ms * 0.06), lagged: 0 } : null, last: R.last,
        recent: [["2026-11-10 21-48-02.mkv", 3.42e9, 16], ["2026-11-09 19-12-40.mkv", 1.18e9, 42], ["2026-11-07 23-05-11.mkv", 6.9e9, 90], ["2026-11-05 20-31-57.mkv", 0.74e9, 140]]
          .map(([name, bytes, h]) => ({ name, path: "D:\\Vídeos\\" + name, bytes, at: new Date(Date.now() - h * 3.6e6).toISOString() })),
      };
    };
    setInterval(() => {
      if (!R.active && Date.now() >= R.meterUntil) return;
      const sec = (R.active ? R.ms() : Date.now()) / 1000, l = [];
      for (let tr = 0; tr < 2; tr++) { const p = R.muted[tr] ? -100 : R.level(tr, Math.floor(sec * 4) / 4) + (Math.random() - 0.5) * 3; l.push(Math.min(0, p), p - 9); }
      emitTyped({ type: "meters", ms: R.active ? R.ms() : -1, paused: R.paused, l });
    }, 66);
    const fakeUpdate = { state: q.has("update") ? "available" : "current", installed: "37a6078", latest: "9f3c2d1", title: "Modo jogo, tempo de jogo e atualização" };
    const fake = () => emit({
      cpu: 6 + Math.random() * 22, ram: 54 + Math.random() * 2,
      ramUsedGb: 17.3, ramTotalGb: 31.9, micMuted: mic, obs: false,
      gpu: 8 + Math.random() * 30, vram: 21, vramUsedGb: 2.5, vramTotalGb: 11.9, gpuTemp: 47,
      game: fakeGameStatus(), update: { ...fakeUpdate }, rec: fakeRec(),
      sys: {
        uptimeSec: Math.round(performance.now() / 1000) + 2 * 86400 + 4 * 3600 + 12 * 60, cpuTemp: 54 + Math.round(Math.random() * 4), cpuTempSrc: "LibreHardwareMonitor",
        net: { down: Math.round(Math.random() < 0.15 ? 2.5e6 * Math.random() : 90000 * Math.random()), up: Math.round(25000 * Math.random()) },
        disk: { read: Math.round(4e6 * Math.random()), write: Math.round(1.5e6 * Math.random()), busy: Math.round(Math.random() * 12) },
        drives: [{ name: "C:", label: "", freeGb: 312.4, totalGb: 931.5 }, { name: "D:", label: "Jogos", freeGb: 84.2, totalGb: 1863 }],
      },
    });
    window.__previewUpdate = fakeUpdate;
    window.__previewEmit = fake;
    fake();
    setInterval(fake, 1000);
    loadPreviewDeck();
    var previewRun = (action) => {
      console.log("[preview] ação:", action);
      if (action.type === "mic") { mic = !mic; fake(); }
      if (action.type === "record") return previewCall("record", { cmd: action.cmd || "toggle" });
      return new Promise((r) => setTimeout(r, 120));
    };
  }

  function run(action) {
    if (!wv) return previewRun(action);
    const id = nextId++;
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      wv.postMessage({ type: "action", id, action });
      setTimeout(() => { if (pending.delete(id)) reject(new Error("O PC não respondeu")); }, 10000);
    });
  }

  // Preview: lê o deck.json direto (precisa abrir por um servidor local, file:// bloqueia fetch)
  async function loadPreviewDeck() {
    try {
      const r = await fetch(new URL("../deck.json", here), { cache: "no-store" });
      if (!r.ok) throw new Error(r.status);
      emitDeck(parseJsonc(await r.text()));
    } catch (e) {
      emitDeck(null, "Preview: não consegui ler o deck.json. Na pasta PainelDeck rode \"python -m http.server\" e abra http://localhost:8000/wwwroot/");
    }
  }

  // JSON com comentários // e /* */ e vírgula sobrando (igual o app aceita)
  function parseJsonc(text) {
    let out = "", i = 0, str = false;
    while (i < text.length) {
      const c = text[i], n = text[i + 1];
      if (str) {
        out += c;
        if (c === "\\") { out += n ?? ""; i += 2; continue; }
        if (c === '"') str = false;
        i++;
      } else if (c === '"') { str = true; out += c; i++; }
      else if (c === "/" && n === "/") { while (i < text.length && text[i] !== "\n") i++; }
      else if (c === "/" && n === "*") { i = text.indexOf("*/", i + 2); i = i < 0 ? text.length : i + 2; }
      else { out += c; i++; }
    }
    return JSON.parse(out.replace(/,(\s*[\]}])/g, "$1"));
  }

  // Chamadas do editor visual (modo edição, salvar, listar scripts, escolher arquivo)
  function call(method, args = {}) {
    if (!wv) return previewCall(method, args);
    const id = nextId++;
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      wv.postMessage({ type: "call", id, method, args });
      const limit = method === "askClaude" ? 11 * 60000 : 120000; // o Claude pode levar uns minutos
      setTimeout(() => { if (pending.delete(id)) reject(new Error("O PC não respondeu")); }, limit);
    });
  }

  // No preview não dá pra gravar no disco: baixa o deck.json pra você substituir na mão
  async function previewCall(method, args) {
    if (method === "saveDeck") {
      const blob = new Blob([JSON.stringify(args.deck, null, 2)], { type: "application/json" });
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      a.download = "deck.json";
      a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 1000);
      emitDeck(args.deck);
      return null;
    }
    if (method === "listScripts") return ["ola.ps1"];
    if (method === "askClaude") {
      await new Promise((r) => setTimeout(r, 1500));
      return `Preview: no app, o Claude Code receberia o pedido "${args.request}" e editaria os arquivos.`;
    }
    if (method === "news") {
      const ago = (min) => new Date(Date.now() - min * 60000).toISOString();
      const items = [
        ["OpenAI apresenta novo modelo com raciocínio mais rápido e barato", "Exemplo", 35],
        ["Anthropic amplia Claude com agentes que usam o computador sozinhos", "Exemplo", 95],
        ["Nvidia bate recorde de receita puxada por chips de inteligência artificial", "Exemplo", 180],
        ["Google libera Gemini em português para mais aparelhos Android", "Exemplo", 260],
        ["Governo debate regras para uso de inteligência artificial em serviços públicos", "Exemplo", 420],
        ["Startups brasileiras de IA captam R$ 1 bilhão no ano", "Exemplo", 760],
      ].map(([title, source, min], i) => ({ title, source, url: "https://news.google.com/", published: ago(min) }));
      return { title: "IA", items, updated: new Date().toISOString(), error: null };
    }
    if (method === "playtime") {
      const names = ["For Honor", "ARC Raiders", "Rematch"];
      const week = Array.from({ length: 7 }, (_, i) => {
        const d = new Date(Date.now() - (6 - i) * 86400000);
        const games = names.map((name, k) => ({ name, sec: Math.round(Math.max(0, Math.sin(i * 1.7 + k * 2.1) + 0.3) * 3600 * (k === 0 ? 1.6 : 0.9)) })).filter((g) => g.sec > 300);
        return { date: d.toISOString().slice(0, 10), label: d.toLocaleDateString("pt-BR", { weekday: "short" }).replace(".", ""), today: i === 6, total: games.reduce((a, g) => a + g.sec, 0), games };
      });
      const totals = {};
      week.forEach((w) => w.games.forEach((g) => (totals[g.name] = (totals[g.name] || 0) + g.sec)));
      const top = Object.entries(totals).map(([name, sec]) => ({ name, sec })).sort((a, b) => b.sec - a.sec);
      return { today: { total: week[6].total, games: week[6].games }, week, weekTotal: week.reduce((a, w) => a + w.total, 0), top, monthTotal: 151200, sessions: [], playing: null };
    }
    if (method === "update") {
      const u = window.__previewUpdate;
      if (!args.check) { u.state = "working"; u.message = "Preview: no app, baixaria e instalaria a versão nova"; setTimeout(() => { u.state = "done"; u.message = "Atualizado pra versão 9f3c2d1"; }, 4000); }
      return u;
    }
    if (method === "record") {
      const R = window.__previewRec, cmd = args.cmd;
      if (cmd === "frame") { // quadro inventado (um "jogo" que muda com o tempo), só pra ver a prévia e as miniaturas
        const t = (args.at ?? R.ms()) / 1000, w = args.width || 640, h = Math.round(w / 2.389), k = w / 640;
        const cv = document.createElement("canvas"); cv.width = w; cv.height = h;
        const c = cv.getContext("2d"); c.scale(k, k);
        const hue = (200 + t * 0.9) % 360, g = c.createLinearGradient(0, 0, 0, 268);
        g.addColorStop(0, `hsl(${hue} 60% 14%)`); g.addColorStop(0.6, `hsl(${(hue + 40) % 360} 55% 30%)`); g.addColorStop(1, `hsl(${(hue + 70) % 360} 50% 12%)`);
        c.fillStyle = g; c.fillRect(0, 0, 640, 268);
        c.fillStyle = `hsl(${(hue + 150) % 360} 80% 62%)`; c.beginPath(); c.arc(320 + 240 * Math.sin(t / 37), 96 + 30 * Math.cos(t / 23), 26, 0, 7); c.fill();
        for (let l = 0; l < 3; l++) {
          c.fillStyle = `hsla(${(hue + 20 * l) % 360} 45% ${20 - l * 5}% / 1)`; c.beginPath(); c.moveTo(0, 268);
          for (let x = 0; x <= 640; x += 16) c.lineTo(x, 150 + l * 30 + 26 * Math.sin(x / (60 + l * 25) + t * (0.05 + l * 0.04) + l * 2));
          c.lineTo(640, 268); c.fill();
        }
        c.fillStyle = "rgba(0,0,0,.45)"; c.fillRect(14, 232, 180, 22); c.fillStyle = "#e0233f"; c.fillRect(17, 235, 174 * (0.55 + 0.4 * Math.sin(t / 9)), 16);
        c.fillStyle = "rgba(255,255,255,.85)"; c.font = "600 13px Segoe UI, sans-serif"; c.fillText("PRÉVIA DE EXEMPLO", 462, 250);
        c.strokeStyle = "rgba(255,255,255,.7)"; c.lineWidth = 1.5; c.beginPath(); c.moveTo(312, 134); c.lineTo(328, 134); c.moveTo(320, 126); c.lineTo(320, 142); c.stroke();
        return { image: cv.toDataURL("image/jpeg", 0.7), error: null };
      }
      if (cmd === "history") {
        const n = Math.floor(R.ms() / 250), enc = (tr) => { let b = ""; for (let i = 0; i < n; i++) b += String.fromCharCode(Math.round(Math.max(0, Math.min(1, (R.level(tr, i / 4) + 60) / 60)) * 255)); return btoa(b); };
        return { bucketMs: 250, ms: R.ms(), tracks: [enc(0), enc(1)], markers: R.markers };
      }
      const stop = () => { R.last = { name: "2026-11-11 14-20-33.mkv", path: "D:\\Vídeos\\2026-11-11 14-20-33.mkv", ms: R.ms(), bytes: Math.round(R.ms() * 4850), markers: R.markers.length, at: new Date().toISOString() }; R.active = R.paused = false; };
      const start = () => { R.active = true; R.paused = false; R.t0 = Date.now(); R.markers = []; R.last = null; };
      if (cmd === "toggle") R.active ? stop() : start();
      if (cmd === "start" && !R.active) start();
      if (cmd === "stop" && R.active) stop();
      if (cmd === "pause" && R.active) { if (R.paused) { R.t0 += Date.now() - R.pausedAt; R.paused = false; } else { R.paused = true; R.pausedAt = Date.now(); } }
      if (cmd === "mark" && R.active) R.markers = [...R.markers, R.ms()];
      if (cmd === "mute") R.muted[args.track] = !R.muted[args.track];
      if (cmd === "test") R.meterUntil = Date.now() + 20000;
      window.__previewEmit();
      return last.rec;
    }
    if (method === "autostart") return !!args.on;
    if (method === "pickFile") return prompt("Caminho do programa (no app abre o seletor de arquivos):", "notepad.exe");
    return null;
  }

  function onDeck(fn) {
    deckListeners.add(fn);
    if (deck || deckError) fn(deck, deckError);
    return () => deckListeners.delete(fn);
  }

  function onStatus(fn) {
    listeners.add(fn);
    if (last.cpu !== undefined) fn(last);
    return () => listeners.delete(fn);
  }

  /** Mensagens do C# fora do status (ex.: Bridge.on("meters", fn) → níveis de áudio ~15x por segundo enquanto grava). */
  function on(type, fn) {
    if (!typed.has(type)) typed.set(type, new Set());
    typed.get(type).add(fn);
    return () => typed.get(type).delete(fn);
  }

  return { run, call, on, onStatus, onDeck, get status() { return last; }, get deck() { return deck; }, preview: !wv };
})();
