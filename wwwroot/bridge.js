// Ponte painel ⇄ PC. Dentro do app usa o WebView2; aberto num navegador comum
// entra em "modo preview" e só simula (bom pra mexer no visual).
window.Bridge = (() => {
  const here = document.currentScript.src; // as cenas (scenes/*) carregam com ../../bridge.js
  const wv = window.chrome && window.chrome.webview;
  const pending = new Map();
  const listeners = new Set();
  const deckListeners = new Set();
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
    const fakeUpdate = { state: q.has("update") ? "available" : "current", installed: "37a6078", latest: "9f3c2d1", title: "Modo jogo, tempo de jogo e atualização" };
    const fake = () => emit({
      cpu: 6 + Math.random() * 22, ram: 54 + Math.random() * 2,
      ramUsedGb: 17.3, ramTotalGb: 31.9, micMuted: mic, obs: false,
      gpu: 8 + Math.random() * 30, vram: 21, vramUsedGb: 2.5, vramTotalGb: 11.9, gpuTemp: 47,
      game: fakeGameStatus(), update: { ...fakeUpdate },
    });
    window.__previewUpdate = fakeUpdate;
    fake();
    setInterval(fake, 1000);
    loadPreviewDeck();
    var previewRun = (action) => {
      console.log("[preview] ação:", action);
      if (action.type === "mic") { mic = !mic; fake(); }
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

  return { run, call, onStatus, onDeck, get status() { return last; }, get deck() { return deck; }, preview: !wv };
})();
