// =====================================================================
//  PainelDeck · Navy · coluna "CLAUDE · SESSÕES"
//  Mostra as sessões do Claude Code deste PC (o C# lê %USERPROFILE%\.claude\projects) e deixa mandar
//  mensagem pra qualquer uma por ÁUDIO (🎤: grava → vira texto → você confere → envia) ou digitando.
//  O envio roda `claude --resume <id> -p` em segundo plano no PC: nenhuma janela abre, o jogo não perde o foco.
//   - Bridge.call("claudeSessions") → { items: [{ id, title, project, branch, updated, state, last, lastRole, recent, sending, error }], error }
//     state: "working" (trabalhando) | "waiting" (respondeu, sua vez) | "idle" (parada no meio)
//   - Bridge.call("claudeSend", { id, text })
//   - "+ Nova": Bridge.call("claudeNew", { cwd, text }) → { id } abre uma sessão nova na pasta escolhida
//     (pastas = "folders" da resposta de claudeSessions: as do deck.json "claude.folders" + as das sessões recentes)
//   - Bridge.call("voice", { cmd: "start" | "state" | "stop" | "cancel" }) → stop devolve { text }
//  Leve na GPU: nada de animação infinita; a bolinha "trabalhando" pisca pelo body.blink (1x por segundo).
// =====================================================================
(() => {
  const $ = (s, el = document) => el.querySelector(s);
  const toast = (...a) => window.Navy?.toast(...a);
  const ripple = (...a) => window.Navy?.ripple(...a);
  const list = $("#ses-list"), detail = $("#ses-detail"), compose = $("#sd-compose"), chat = $("#sd-chat");
  const card = $("#sessions");

  let items = [], openId = null, lastErr = null, folders = [];
  const NEW = "__nova__";
  let newCwd = (() => { try { return localStorage.getItem("navy.claude.pasta") || ""; } catch { return ""; } })();
  let mode = "idle"; // compose: idle | rec | wait | review | type | sent
  let draftText = "", recTimer = null, recStart = 0;

  const MIC = `<svg viewBox="0 0 24 24"><path d="M12 15a3.5 3.5 0 0 0 3.5-3.5v-6a3.5 3.5 0 0 0-7 0v6A3.5 3.5 0 0 0 12 15Zm6-3.5h-1.8a4.2 4.2 0 0 1-8.4 0H6a6 6 0 0 0 5.1 5.9V21h1.8v-3.6a6 6 0 0 0 5.1-5.9Z"/></svg>`;
  const KEY = `<svg viewBox="0 0 24 24"><path d="M20 6H4a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2ZM7 9h2v2H7Zm0 4h10v2H7Zm4-4h2v2h-2Zm4 0h2v2h-2Z"/></svg>`;
  const STATE = { working: "trabalhando", waiting: "sua vez", idle: "parada" };

  const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  function ago(iso) {
    const m = Math.round((Date.now() - Date.parse(iso)) / 60000);
    if (!(m >= 0)) return "";
    if (m < 1) return "agora";
    if (m < 60) return `${m} min`;
    const h = Math.round(m / 60);
    if (h < 24) return `${h} h`;
    const d = Math.round(h / 24);
    return d === 1 ? "ontem" : `${d} dias`;
  }
  const oneLine = (s) => String(s || "").replace(/\s+/g, " ").trim();
  const folderName = (p) => String(p || "").split(/[\\/]/).filter(Boolean).pop() || p;
  const current = () => openId === NEW
    ? { id: NEW, title: "Nova sessão", project: folderName(newCwd), state: "idle", cwd: newCwd }
    : items.find((x) => x.id === openId) || null;

  // ---------- lista ----------
  function renderList() {
    const w = items.filter((x) => x.state === "working").length;
    $("#ses-count").innerHTML = !items.length ? "" : w ? `<b>${w} ${w === 1 ? "ativa" : "ativas"}</b>` : `${items.length}`;
    if (!items.length) {
      list.innerHTML = `<li class="msg"><b></b><span></span></li>`;
      $(".msg b", list).textContent = lastErr ? "Sessões desligadas" : "Nenhuma sessão recente";
      $(".msg span", list).textContent = lastErr || "Quando você usar o Claude Code no PC, as conversas aparecem aqui.";
      return;
    }
    // reaproveita as linhas que já existem (não pisca a lista a cada atualização)
    const old = new Map([...list.querySelectorAll(".ses")].map((li) => [li.dataset.id, li]));
    list.querySelectorAll(".msg").forEach((m) => m.remove());
    items.forEach((it, k) => {
      let li = old.get(it.id);
      if (!li) {
        li = document.createElement("li");
        li.className = "ses";
        li.dataset.id = it.id;
        li.style.setProperty("--k", k);
        li.innerHTML = `<div class="txt"><div class="meta"><i class="dot"></i><span class="st"></span><span class="proj"></span><span class="ago"></span></div>
          <div class="ttl"></div><div class="last"></div></div><button class="mic" title="Falar com esta sessão">${MIC}</button>`;
        li.addEventListener("pointerdown", (e) => {
          if (e.target.closest(".mic")) return;
          ripple(li, e);
          openSession(it.id);
        });
        $(".mic", li).addEventListener("pointerdown", (e) => { e.stopPropagation(); ripple($(".mic", li), e); openSession(li.dataset.id, true); });
      }
      old.delete(it.id);
      li.className = `ses ${it.state}${it.error ? " err" : ""}`;
      $(".st", li).textContent = it.sending ? "mandando" : STATE[it.state] || "";
      $(".proj", li).textContent = it.project || "";
      $(".ago", li).textContent = ago(it.updated);
      $(".ttl", li).textContent = it.title;
      $(".last", li).textContent = it.error ? `⚠ ${it.error}` : (it.lastRole === "user" ? "você: " : "") + oneLine(it.last);
      li.title = `${it.title}\n${it.cwd || ""}${it.branch ? ` (${it.branch})` : ""}`;
      if (list.children[k] !== li) list.insertBefore(li, list.children[k] || null);
    });
    old.forEach((li) => li.remove());
  }

  // ---------- detalhe ----------
  function openSession(id, record) {
    if (openId !== id) { cancelVoice(); setMode("idle"); }
    openId = id;
    card.classList.add("open");
    detail.hidden = false;
    renderDetail(true);
    if (record) startRec();
  }
  function closeSession() {
    cancelVoice();
    leaveTyping();
    openId = null;
    card.classList.remove("open", "new");
    detail.hidden = true;
    setMode("idle");
  }
  $("#sd-back").addEventListener("pointerdown", closeSession);

  let chatKey = "";
  function renderDetail(scroll) {
    if (openId === NEW) return renderNew();
    card.classList.remove("new");
    const it = current();
    if (!it) { if (openId) closeSession(); return; }
    $("#sd-title").textContent = it.title;
    $("#sd-meta").innerHTML = `<i class="dot ${it.state}"></i><span>${esc(it.sending ? "mandando" : STATE[it.state] || "")}</span><span>· ${esc(it.project || "")}${it.branch ? ` · ${esc(it.branch)}` : ""}</span><span>· ${esc(ago(it.updated))}</span>`;
    const recent = it.recent?.length ? it.recent : it.last ? [{ role: it.lastRole || "assistant", text: it.last }] : [];
    const key = JSON.stringify([recent, it.sending, it.error, it.state]);
    if (key !== chatKey) {
      chatKey = key;
      chat.innerHTML = recent.map((m) => `<div class="b ${m.role === "user" ? "me" : "ai"}">${esc(m.text)}</div>`).join("")
        + (it.sending && !recent.some((m) => m.role === "user" && m.text.startsWith(it.sending.slice(0, 40))) ? `<div class="b me pending">${esc(it.sending)}</div>` : "")
        + (it.state === "working" ? `<div class="b ai typing"><i></i><i></i><i></i></div>` : "")
        + (it.error ? `<div class="b warn">${esc(it.error)}</div>` : "");
      scroll = true;
    }
    if (scroll) chat.scrollTop = chat.scrollHeight;
    if (mode === "sent" && it.state !== "working") setMode("idle");
    else if (mode === "idle") renderCompose();
  }

  // ---------- sessão nova: escolhe a pasta e fala ----------
  $("#ses-new").addEventListener("pointerdown", (e) => { ripple(e.currentTarget, e); openSession(NEW); });
  function renderNew() {
    card.classList.add("new");
    if (!folders.some((f) => f.path === newCwd)) newCwd = folders[0]?.path || "";
    $("#sd-title").textContent = "Nova sessão";
    $("#sd-meta").innerHTML = `<span>${newCwd ? esc(newCwd) : "escolha a pasta"}</span>`;
    const key = JSON.stringify(["new", folders, newCwd]);
    if (key === chatKey) return;
    chatKey = key;
    chat.innerHTML = `<div class="nw-hint">Em qual pasta? O Claude Code abre uma sessão nova lá, em segundo plano, e ela aparece na lista.</div>
      <div class="nw-folders">${folders.map((f) => `<button class="fold${f.path === newCwd ? " on" : ""}" data-path="${esc(f.path)}" title="${esc(f.path)}"><b>${esc(f.name)}</b><span>${esc(f.path)}</span></button>`).join("")
        || `<div class="nw-hint">Nenhuma pasta ainda. Ponha em "claude": { "folders": [...] } no deck.json.</div>`}</div>`;
    chat.querySelectorAll(".fold").forEach((b) => b.addEventListener("pointerdown", () => {
      newCwd = b.dataset.path;
      try { localStorage.setItem("navy.claude.pasta", newCwd); } catch { /* ok */ }
      renderNew();
      if (mode === "type") $("textarea", compose)?.focus();
    }));
    const on = $(".fold.on", chat);
    if (on) on.scrollIntoView({ block: "nearest" });
    if (mode === "idle") renderCompose();
  }

  // ---------- compor (voz / digitar) ----------
  function setMode(m) { mode = m; renderCompose(); }
  function renderCompose() {
    const it = current();
    const busy = it?.state === "working";
    compose.className = `sd-compose ${mode}`;
    if (mode === "idle") {
      compose.innerHTML = `<button class="big talk"${busy ? " disabled" : ""}>${MIC}<span>${busy ? "Trabalhando… espera terminar" : "Falar"}</span></button>
        <button class="small type" title="Digitar"${busy ? " disabled" : ""}>${KEY}</button>`;
      $(".talk", compose).addEventListener("pointerdown", (e) => { if (!busy) { ripple(e.currentTarget, e); startRec(); } });
      $(".type", compose).addEventListener("pointerdown", () => { if (!busy) startTyping(); });
    } else if (mode === "rec") {
      compose.innerHTML = `<div class="rec-row"><i class="rec-dot"></i><span class="rec-time">0:00</span><span class="meter"><i></i></span></div>
        <div class="row"><button class="big stop">■ <span>Parar</span></button><button class="small x" title="Cancelar">✕</button></div>`;
      $(".stop", compose).addEventListener("pointerdown", stopRec);
      $(".x", compose).addEventListener("pointerdown", () => { cancelVoice(); setMode("idle"); });
    } else if (mode === "wait") {
      compose.innerHTML = `<div class="wait">Transcrevendo o áudio…</div>`;
    } else if (mode === "review") {
      compose.innerHTML = `<div class="draft"></div>
        <div class="row"><button class="big send">Enviar</button><button class="small again" title="Gravar de novo">${MIC}</button><button class="small x" title="Descartar">✕</button></div>`;
      $(".draft", compose).textContent = draftText;
      $(".send", compose).addEventListener("pointerdown", () => send(draftText));
      $(".again", compose).addEventListener("pointerdown", startRec);
      $(".x", compose).addEventListener("pointerdown", () => setMode("idle"));
    } else if (mode === "type") {
      compose.innerHTML = `<textarea rows="3" placeholder="Escreva pra ${esc(it?.project || "a sessão")}… (Ctrl+Enter envia)"></textarea>
        <div class="row"><button class="big send">Enviar</button><button class="small x" title="Cancelar">✕</button></div>`;
      const ta = $("textarea", compose);
      ta.value = draftText;
      ta.addEventListener("keydown", (e) => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); send(ta.value); } if (e.key === "Escape") { leaveTyping(); setMode("idle"); } });
      $(".send", compose).addEventListener("pointerdown", () => send(ta.value));
      $(".x", compose).addEventListener("pointerdown", () => { leaveTyping(); setMode("idle"); });
      setTimeout(() => ta.focus(), 60);
    } else if (mode === "sent") {
      compose.innerHTML = `<div class="wait ok">Mandei! O Claude está trabalhando no PC.</div>`;
    }
  }

  async function startRec() {
    if (mode === "rec") return;
    leaveTyping();
    try {
      await Bridge.call("voice", { cmd: "start" });
    } catch (err) {
      setMode("idle");
      return toast(/desconhecid/i.test(err.message) ? "Reinicie o PainelDeck pra ligar o áudio." : `Áudio: ${err.message}`);
    }
    recStart = Date.now();
    setMode("rec");
    clearInterval(recTimer);
    recTimer = setInterval(async () => {
      if (mode !== "rec") return clearInterval(recTimer);
      const sec = Math.floor((Date.now() - recStart) / 1000);
      const t = $(".rec-time", compose);
      if (t) t.textContent = `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, "0")}`;
      $(".rec-dot", compose)?.classList.toggle("off", sec % 2 === 1);
      try {
        const s = await Bridge.call("voice", { cmd: "state" });
        const bar = $(".meter i", compose);
        if (bar && s) bar.style.width = `${Math.min(100, Math.round(Math.sqrt(s.level || 0) * 130))}%`;
        if (s && !s.recording && mode === "rec") setMode("idle");
      } catch { /* ok */ }
    }, 200);
  }
  async function stopRec() {
    if (mode !== "rec") return;
    clearInterval(recTimer);
    setMode("wait");
    try {
      const r = await Bridge.call("voice", { cmd: "stop" });
      draftText = (r?.text || "").trim();
      if (!draftText) { toast("Não entendi nada no áudio. Tenta de novo?"); return setMode("idle"); }
      setMode("review");
    } catch (err) {
      toast(`Áudio: ${err.message}`);
      setMode("idle");
    }
  }
  function cancelVoice() {
    clearInterval(recTimer);
    if (mode === "rec") Bridge.call("voice", { cmd: "cancel" }).catch(() => {});
  }

  // digitar precisa de foco no painel: liga o "modo edição" do app só enquanto a caixa está aberta
  let typing = false;
  async function startTyping() {
    draftText = "";
    try { await Bridge.call("editMode", { on: true }); typing = true; } catch { /* preview */ }
    setMode("type");
  }
  function leaveTyping() {
    if (!typing) return;
    typing = false;
    Bridge.call("editMode", { on: false }).catch(() => {});
  }

  async function send(text) {
    text = String(text || "").trim();
    const it = current();
    if (!text || !it) return;
    leaveTyping();
    if (it.id === NEW) return sendNew(text);
    try {
      await Bridge.call("claudeSend", { id: it.id, text });
      draftText = "";
      it.state = "working"; it.sending = text; it.error = null;
      setMode("sent");
      renderList(); renderDetail(true);
      toast(`Mandei pra ${it.project || "a sessão"} ✓`, "ok");
      poll(1500);
    } catch (err) {
      toast(/desconhecid/i.test(err.message) ? "Reinicie o PainelDeck pra ligar o envio." : `Não mandei: ${err.message}`);
      draftText = text;
      setMode(mode === "type" ? "type" : "review");
    }
  }

  async function sendNew(text) {
    if (!newCwd) return toast("Escolha a pasta primeiro.");
    try {
      const r = await Bridge.call("claudeNew", { cwd: newCwd, text });
      draftText = "";
      toast(`Abri uma sessão nova em ${folderName(newCwd)} ✓`, "ok");
      await load();
      if (r?.id) openSession(r.id); else closeSession();
      setMode("sent");
      poll(1500);
    } catch (err) {
      toast(/desconhecid/i.test(err.message) ? "Reinicie o PainelDeck pra abrir sessão nova." : `Não abri: ${err.message}`);
      draftText = text;
      setMode(mode === "type" ? "type" : "review");
    }
  }

  // ---------- atualização ----------
  let pollT = null;
  async function load() {
    try {
      let r = await Bridge.call("claudeSessions");
      if (!r && Bridge.preview) r = PREVIEW();
      if (!r) throw new Error("sem resposta");
      items = r.items || [];
      folders = r.folders || folders;
      lastErr = r.error || null;
    } catch (err) {
      if (!items.length) lastErr = /desconhecid|unknown/i.test(err.message) ? "Reinicie o PainelDeck pra ligar as sessões." : `Não consegui ler: ${err.message}`;
    }
    renderList();
    if (openId) renderDetail(false);
  }
  function poll(ms) {
    clearTimeout(pollT);
    pollT = setTimeout(async () => {
      if (!document.hidden && !document.body.classList.contains("col-news")) await load();
      // tem sessão trabalhando (ou aberta): olha mais vezes
      poll(items.some((x) => x.state === "working") ? 3000 : openId ? 6000 : 15000);
    }, ms);
  }
  load();
  poll(15000);

  // ---------- preview (navegador comum, sem o app) ----------
  let pv = null;
  function PREVIEW() {
    const t = (min) => new Date(Date.now() - min * 60000).toISOString();
    pv ??= [
      { id: "a1", title: "Painel: trocar notícias por sessões do Claude", project: "paineldeck", branch: "claude/sessoes", updated: t(0.2), state: "working", lastRole: "assistant",
        last: "Lendo navy.js pra achar a coluna de notícias…",
        recent: [{ role: "user", text: "troca as notícias pelas sessões do Claude e deixa eu mandar áudio" }, { role: "assistant", text: "Bora! Vou ler os arquivos da cena Navy primeiro." }, { role: "assistant", text: "Lendo navy.js pra achar a coluna de notícias…" }] },
      { id: "b2", title: "Corrigir login do app no Safari", project: "dreamlabs-web", branch: "fix/safari-login", updated: t(12), state: "waiting", lastRole: "assistant",
        last: "Pronto: o cookie estava sem SameSite=None. Subi o fix na branch e os testes passaram. Quer que eu abra o PR?",
        recent: [{ role: "user", text: "o login não funciona no safari, vê aí" }, { role: "assistant", text: "Pronto: o cookie estava sem SameSite=None. Subi o fix na branch e os testes passaram. Quer que eu abra o PR?" }] },
      { id: "c3", title: "Script de backup das fotos pro HD externo", project: "scripts", updated: t(95), state: "waiting", lastRole: "assistant", last: "Fiz o backup.ps1: copia só o que mudou e mantém 30 dias de histórico." },
      { id: "d4", title: "Migrar API pro .NET 9", project: "api-core", branch: "main", updated: t(60 * 5), state: "idle", lastRole: "user", last: "continua de onde parou" },
      { id: "e5", title: "Ideias de widget de clima", project: "PainelDeck", updated: t(60 * 26), state: "waiting", lastRole: "assistant", last: "Dá pra usar o open-meteo sem chave: temperatura, chuva e ícone animado." },
    ];
    return { items: pv, error: null, folders: [
      { path: "C:\\Users\\rodri\\PainelDeck", name: "PainelDeck" }, { path: "C:\\Users\\rodri\\Projetos\\dreamlabs-web", name: "dreamlabs-web" },
      { path: "C:\\Users\\rodri\\scripts", name: "scripts" }, { path: "C:\\Users\\rodri", name: "rodri" }] };
  }
  if (Bridge.preview) {
    const real = Bridge.call;
    let rec = false;
    Bridge.call = async (method, args = {}) => {
      if (method === "claudeSessions") return PREVIEW();
      if (method === "voice") {
        if (args.cmd === "start") rec = true;
        if (args.cmd === "cancel") rec = false;
        if (args.cmd === "state") return { recording: rec, level: rec ? Math.random() * 0.4 : 0 };
        if (args.cmd === "stop") { rec = false; await new Promise((r) => setTimeout(r, 900)); return { text: "Pode abrir o PR, e depois roda os testes de novo no Safari." }; }
        return { recording: rec };
      }
      if (method === "claudeNew") {
        const id = "n" + Date.now();
        pv.unshift({ id, title: args.text, project: folderName(args.cwd), updated: new Date().toISOString(), state: "working", sending: args.text, lastRole: "user", last: args.text, recent: [{ role: "user", text: args.text }] });
        setTimeout(() => { const it = pv.find((x) => x.id === id); it.state = "waiting"; it.sending = null; it.recent.push({ role: "assistant", text: "Respondendo pela sessão nova (preview)." }); it.last = "Respondendo pela sessão nova (preview)."; it.lastRole = "assistant"; }, 4000);
        return { started: true, id };
      }
      if (method === "claudeSend") {
        const it = pv.find((x) => x.id === args.id);
        it.state = "working"; it.sending = args.text; it.updated = new Date().toISOString();
        setTimeout(() => { it.sending = null; it.state = "waiting"; it.recent = [...(it.recent || []), { role: "user", text: args.text }, { role: "assistant", text: "Feito! (preview)" }]; it.last = "Feito! (preview)"; it.lastRole = "assistant"; }, 4000);
        return { started: true };
      }
      return real(method, args);
    };
  }
})();
