// Desenha o painel a partir do deck (deck.json). Cada página é um palco livre: os itens têm x/y/w/h em % da tela.
// No modo edição, os itens podem ser arrastados/redimensionados (veja editor.js).
window.App = (() => {
  let cfg = null;      // deck que está na tela (o do deck.json, ou o rascunho do editor)
  let current = 0;     // página aberta
  let editing = false;
  const grid = document.getElementById("grid");
  const tabs = document.getElementById("tabs");
  const toastEl = document.getElementById("toast");
  let mounted = [];
  let unsub = [];

  if (Bridge.preview) {
    const tag = document.createElement("div");
    tag.className = "preview-tag";
    tag.textContent = "modo preview";
    document.body.appendChild(tag);
  }

  function toast(msg, kind = "err") {
    toastEl.textContent = msg;
    toastEl.dataset.kind = kind;
    toastEl.classList.add("show");
    clearTimeout(toast.t);
    toast.t = setTimeout(() => toastEl.classList.remove("show"), kind === "err" ? 5000 : 2500);
  }

  function ripple(el, e) {
    const r = el.getBoundingClientRect();
    const size = Math.max(r.width, r.height) * 2.2;
    const s = document.createElement("span");
    s.className = "ripple";
    s.style.width = s.style.height = size + "px";
    s.style.left = e.clientX - r.left - size / 2 + "px";
    s.style.top = e.clientY - r.top - size / 2 + "px";
    el.appendChild(s);
    setTimeout(() => s.remove(), 600);
  }

  function flash(el, cls) {
    el.classList.remove("ok", "err");
    void el.offsetWidth;
    el.classList.add(cls);
  }

  // ---------- layout livre ----------
  const rd = (v) => Math.round(v * 10) / 10;
  const NEW_ITEM = { x: 40, y: 37.5, w: 20, h: 25 };

  // Páginas antigas (sem "layout": "free") usam a grade cols×rows: converte pra % na mesma posição
  // que o CSS grid (auto-flow dense) colocava. Devolve uma cópia; o arquivo só muda quando o editor salva.
  function toFree(deck) {
    const d = JSON.parse(JSON.stringify(deck));
    const cols = d.grid?.cols || 5, rows = d.grid?.rows || 3;
    for (const p of d.pages || []) {
      const items = p.items || (p.items = []);
      if (p.layout === "free") {
        for (const it of items) for (const k of ["x", "y", "w", "h"]) if (typeof it[k] !== "number") it[k] = NEW_ITEM[k];
        continue;
      }
      const used = [];
      const fits = (r, c, w, h) => {
        for (let i = r; i < r + h; i++) for (let j = c; j < c + w; j++) if (j >= cols || used[i]?.[j]) return false;
        return true;
      };
      let maxRow = rows;
      const cells = items.map((it) => {
        const w = Math.min(cols, it.w || 1), h = it.h || 1;
        let r = 0, c = 0;
        search: for (r = 0; ; r++) for (c = 0; c < cols; c++) if (fits(r, c, w, h)) break search;
        for (let i = r; i < r + h; i++) for (let j = c; j < c + w; j++) (used[i] ||= [])[j] = true;
        maxRow = Math.max(maxRow, r + h);
        return [c, r, w, h];
      });
      items.forEach((it, i) => {
        const [c, r, w, h] = cells[i];
        Object.assign(it, { x: rd((c / cols) * 100), y: rd((r / maxRow) * 100), w: rd((w / cols) * 100), h: rd((h / maxRow) * 100) });
      });
      p.layout = "free";
    }
    return d;
  }

  function place(el, it) {
    el.style.left = `calc(${it.x}% + var(--gap) / 2)`;
    el.style.top = `calc(${it.y}% + var(--gap) / 2)`;
    el.style.width = `calc(${it.w}% - var(--gap))`;
    el.style.height = `calc(${it.h}% - var(--gap))`;
    el.style.zIndex = it.z || "";
  }

  // ações que só existem no painel (não vão pro PC)
  function runLocal(action) {
    if (action.type === "claude") { Editor.askClaude(); return true; }
    if (action.type === "games") { Games.open(); return true; }
    if (action.type === "macros") { Macros.open(); return true; }
    return false;
  }

  function makeTile(item, idx) {
    const el = document.createElement("div");
    el.className = "tile";
    place(el, item);
    el.style.animationDelay = `${idx * 35}ms`;
    if (item.color) el.style.setProperty("--accent", item.color);

    let inst = {};
    if (item.widget) {
      const def = window.Widgets[item.widget];
      el.classList.add("widget");
      if (!def) el.textContent = `widget "${item.widget}" não existe`;
      else {
        try { inst = def.mount(el, item, { run: Bridge.run, toast }) || {}; }
        catch (err) { el.textContent = `widget "${item.widget}": ${err.message}`; }
      }
      mounted.push(inst);
    } else {
      el.classList.add("button");
      el.innerHTML = `<div class="icon"></div><div class="label"></div>`;
      const setFace = (f) => {
        el.querySelector(".icon").textContent = f.icon || "";
        el.querySelector(".label").textContent = f.label || "";
      };
      setFace(item);
      if (item.state) {
        unsub.push(Bridge.onStatus((s) => {
          const on = !!s[item.state];
          el.classList.toggle("on", on);
          setFace(on && item.on ? { ...item, ...item.on } : item);
        }));
      }
    }

    if (editing) {
      Editor.bindTile(el, idx);
      return el;
    }

    const clickable = item.action || item.page !== undefined || inst.click;
    if (clickable) {
      el.classList.add("clickable");
      el.addEventListener("pointerdown", async (e) => {
        ripple(el, e);
        el.classList.add("pressed");
        inst.click?.(e);
        if (item.page !== undefined) return setTimeout(() => show(pageIndex(item.page)), 90);
        if (!item.action || runLocal(item.action)) return;
        try {
          await Bridge.run(item.action);
          flash(el, "ok");
        } catch (err) {
          flash(el, "err");
          toast(`${item.label || "Ação"}: ${err.message}`);
        }
      });
      const release = () => el.classList.remove("pressed");
      el.addEventListener("pointerup", release);
      el.addEventListener("pointerleave", release);
    }
    return el;
  }

  // "page" pode ser número (0, 1...) ou o nome da página
  function pageIndex(p) {
    if (typeof p === "string" && isNaN(p)) {
      const j = cfg.pages.findIndex((pg) => pg.name === p);
      if (j < 0) toast(`Página "${p}" não existe no deck.json`);
      return j < 0 ? current : j;
    }
    return Number(p) || 0;
  }

  function show(i) {
    if (!cfg) return;
    i = Math.max(0, Math.min(cfg.pages.length - 1, Number(i) || 0));
    current = i;
    mounted.forEach((m) => m.destroy?.());
    unsub.forEach((f) => f());
    mounted = [];
    unsub = [];
    grid.innerHTML = "";
    const items = cfg.pages[i].items || [];
    items.forEach((item, idx) => grid.appendChild(makeTile(item, idx)));
    const now = new Date();
    mounted.forEach((m) => m.tick?.(now));
    const st = Bridge.status;
    if (st.cpu !== undefined) mounted.forEach((m) => m.status?.(st));

    tabs.innerHTML = "";
    cfg.pages.forEach((p, j) => {
      const b = document.createElement("button");
      b.textContent = p.name;
      b.className = j === i ? "active" : "";
      b.addEventListener("pointerdown", () => show(j));
      tabs.appendChild(b);
    });
    if (!editing) {
      const e = document.createElement("button");
      e.className = "edit-btn";
      e.title = "Editar painel";
      e.textContent = "✏️";
      e.addEventListener("pointerdown", () => Editor.start());
      const c = document.createElement("button");
      c.className = "edit-btn claude-btn";
      c.title = "Pedir ao Claude um item novo (ou qualquer mudança)";
      c.textContent = "✨";
      c.addEventListener("pointerdown", () => Editor.askClaude());
      tabs.append(e, c);
    }
    tabs.hidden = false; // sempre visível: tem o botão de editar
    history.replaceState(null, "", location.pathname + "#" + i); // caminho explícito: a cena Navy usa <base href>
    if (editing) Editor.refreshBar();
  }

  setInterval(() => { const now = new Date(); mounted.forEach((m) => m.tick?.(now)); }, 1000);
  Bridge.onStatus((s) => mounted.forEach((m) => m.status?.(s)));

  // Deck chegou (ou mudou porque você salvou o deck.json): redesenha mantendo a página aberta.
  // Enquanto edita, ignora: o rascunho do editor é que manda.
  let first = true;
  Bridge.onDeck((deck, error) => {
    if (error) toast(error);
    if (!deck || editing) return;
    cfg = toFree(deck);
    show(first ? location.hash.slice(1) : current); // na 1ª vez mantém a página depois do hot reload
    first = false;
  });

  return {
    toast,
    show,
    place,
    runLocal,
    NEW_ITEM,
    get current() { return current; },
    get deck() { return cfg; },
    // o editor troca o deck da tela pelo rascunho e liga/desliga o modo edição
    setDeck(d, page = current) { cfg = toFree(d); show(page); },
    setEditing(on) { editing = on; document.body.classList.toggle("editing", on); },
  };
})();
