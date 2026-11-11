// =====================================================================
//  Editor visual do PainelDeck
//  ✏️ (canto das abas) liga o modo edição. Tudo é feito num rascunho;
//  "Salvar" grava no deck.json (com backup em deck.backup.json), "Cancelar" descarta.
// =====================================================================
window.Editor = (() => {
  let draft = null;
  let dirty = false;
  let scripts = [];

  const clone = (o) => JSON.parse(JSON.stringify(o));
  // cada mudança vira um ponto de desfazer (Ctrl+Z): guarda o rascunho como estava antes dela
  let undoStack = [], lastSnap = null;
  const markDirty = () => {
    const now = JSON.stringify(draft);
    if (lastSnap && now !== lastSnap) { undoStack.push(lastSnap); if (undoStack.length > 80) undoStack.shift(); }
    lastSnap = now;
    dirty = true;
    refreshBar();
  };
  function undo() {
    const s = undoStack.pop();
    if (!s) return App.toast("Nada pra desfazer.", "ok");
    draft = JSON.parse(s);
    lastSnap = s;
    sel = -1;
    dirty = true;
    App.setDeck(draft, Math.min(App.current, draft.pages.length - 1));
  }

  // ---------- catálogos ----------
  const ACTION_TYPES = {
    keys: "⌨️ Atalho de teclado",
    type: "📝 Digitar texto",
    media: "🎵 Mídia",
    macro: "🔁 Macro (sequência)",
    run: "🚀 Abrir programa",
    url: "🔗 Abrir link",
    script: "📜 Rodar script",
    mic: "🎙️ Microfone",
    obs: "🎬 OBS",
    record: "⏺️ Gravar a tela (OBS)",
    claude: "✨ Pedir ao Claude (abre a caixa)",
    games: "🎮 Roda de jogos",
    macros: "🔁 Gaveta de macros",
  };
  const ACTION_DEFAULTS = {
    keys: { keys: "" }, type: { text: "" }, media: { key: "playpause" }, macro: { steps: [] },
    run: { path: "" }, url: { url: "https://" }, script: { file: "" }, mic: { mode: "toggle" },
    obs: { request: "ToggleRecord" },
    record: {}, claude: {}, games: {}, macros: {},
  };
  const MEDIA_KEYS = { playpause: "Play / Pause", next: "Próxima", prev: "Anterior", stop: "Parar", volup: "Volume +", voldown: "Volume −", volmute: "Mudo (som)" };
  const MIC_MODES = { toggle: "Alternar", mute: "Mutar", unmute: "Desmutar" };
  const OBS_REQUESTS = [
    "SetCurrentProgramScene", "ToggleRecord", "StartRecord", "StopRecord", "ToggleRecordPause",
    "ToggleStream", "StartStream", "StopStream", "SaveReplayBuffer", "ToggleReplayBuffer",
    "ToggleInputMute", "SetInputMute", "SetInputVolume", "SetSceneItemEnabled", "ToggleVirtualCam",
  ];
  const WIDGET_FIELDS = {
    clock: [],
    stat: [
      { k: "metric", label: "Mostrar", options: { cpu: "CPU", ram: "RAM", vram: "VRAM (placa de vídeo)", gpu: "Uso da GPU" } },
      { k: "label", label: "Título" },
    ],
    ferias: [
      { k: "inicio", label: "Início das férias", type: "date" },
      { k: "volta", label: "Dia da volta", type: "date" },
    ],
    countdown: [
      { k: "title", label: "Texto" },
      { k: "date", label: "Data alvo", type: "date" },
      { k: "emoji", label: "Emoji" },
    ],
  };
  const WIDGET_NAMES = { clock: "🕒 Relógio", stat: "📊 CPU / RAM / GPU", ferias: "🌴 Férias", countdown: "⏳ Contagem regressiva" };
  const STATES = { "": "Nunca", micMuted: "Quando o mic estiver mutado", obs: "Quando o OBS estiver conectado" };
  const EMOJIS = "🎙️ 🔇 🎧 🔊 🔉 ⏯️ ⏭️ ⏮️ 🎵 🎬 ⏺️ 📡 💾 🎮 📷 ☕ 🤝 ✂️ 💻 🌐 📁 🧪 🚀 ⚡ 🔥 ⭐ 💬 📝 🔒 🏠 ⚙️ 🎯 🕹️ 🖥️ 📸 🗑️".split(" ");

  // ---------- helpers de formulário ----------
  function h(tag, attrs = {}, ...kids) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs)) {
      if (v === undefined || v === null || v === false) continue;
      if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
      else if (k === "class") el.className = v;
      else if (k === "value") el.value = v;
      else if (k === "checked") el.checked = !!v;
      else el.setAttribute(k, v === true ? "" : v);
    }
    for (const kid of kids.flat()) if (kid !== null && kid !== undefined && kid !== false) el.append(kid);
    return el;
  }
  const row = (label, ...input) => h("label", { class: "row" }, h("span", {}, label), h("div", { class: "ctl" }, ...input));
  const hint = (text) => h("div", { class: "hint" }, text);
  function select(options, value, onChange) {
    const s = h("select", { onchange: (e) => onChange(e.target.value) });
    for (const [v, t] of Object.entries(options)) s.append(h("option", { value: v }, t));
    s.value = value ?? "";
    return s;
  }
  const text = (value, onInput, attrs = {}) =>
    h("input", { type: "text", value: value ?? "", oninput: (e) => onInput(e.target.value), ...attrs });

  // ---------- diálogo ----------
  const dlg = h("dialog", { id: "editor-dlg" });
  document.body.append(dlg);
  let recording = null; // função que cancela a gravação de atalho em andamento
  dlg.addEventListener("cancel", (e) => { if (recording) e.preventDefault(); });

  function openDialog(title, body, buttons) {
    dlg.innerHTML = "";
    dlg.append(
      h("h2", {}, title),
      h("div", { class: "dlg-body" }, body),
      h("div", { class: "dlg-buttons" }, buttons),
    );
    if (!dlg.open) dlg.showModal();
  }
  const closeDialog = () => { recording?.(); dlg.close(); };

  // ---------- gravar atalho ----------
  const CODE_NAMES = {
    Enter: "enter", NumpadEnter: "enter", Escape: "esc", Tab: "tab", Space: "space", Backspace: "backspace",
    Delete: "delete", Insert: "insert", Home: "home", End: "end", PageUp: "pageup", PageDown: "pagedown",
    ArrowUp: "up", ArrowDown: "down", ArrowLeft: "left", ArrowRight: "right", PrintScreen: "printscreen",
    Pause: "pause", CapsLock: "capslock", Equal: "plus", Minus: "minus", Comma: "comma", Period: "period",
    MediaPlayPause: "playpause", MediaTrackNext: "next", MediaTrackPrevious: "prev", MediaStop: "stop",
    AudioVolumeUp: "volup", AudioVolumeDown: "voldown", AudioVolumeMute: "volmute",
  };
  function keyName(code) {
    let m;
    if ((m = code.match(/^Key([A-Z])$/))) return m[1].toLowerCase();
    if ((m = code.match(/^Digit(\d)$/))) return m[1];
    if ((m = code.match(/^Numpad(\d)$/))) return "num" + m[1];
    if ((m = code.match(/^F(\d{1,2})$/))) return "f" + m[1];
    return CODE_NAMES[code] || null;
  }
  function recordInto(input, btn, onDone) {
    recording?.();
    const old = btn.textContent;
    btn.textContent = "Aperte o atalho… (Esc cancela)";
    btn.classList.add("recording");
    const onKey = (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (["Control", "Shift", "Alt", "Meta", "OS"].includes(e.key)) return; // espera a tecla "de verdade"
      const mods = [e.ctrlKey && "ctrl", e.shiftKey && "shift", e.altKey && "alt", e.metaKey && "win"].filter(Boolean);
      if (e.code === "Escape" && mods.length === 0) return stop();
      const k = keyName(e.code);
      if (!k) { App.toast(`A tecla "${e.code}" ainda não é suportada. Digite o atalho na mão.`); return stop(); }
      input.value = [...mods, k].join("+");
      onDone(input.value);
      stop();
    };
    function stop() {
      window.removeEventListener("keydown", onKey, true);
      btn.textContent = old;
      btn.classList.remove("recording");
      recording = null;
    }
    window.addEventListener("keydown", onKey, true);
    recording = stop;
  }

  // ---------- editor de ação (usado nos botões e nos passos de macro) ----------
  function actionEditor(a, onChange, depth = 0) {
    const box = h("div", { class: "action" });
    const changed = () => { onChange(); };
    function render() {
      box.innerHTML = "";
      box.append(row("Ação", select(ACTION_TYPES, a.type, (t) => {
        for (const k of Object.keys(a)) delete a[k];
        Object.assign(a, { type: t }, clone(ACTION_DEFAULTS[t]));
        changed();
        render();
      })));

      switch (a.type) {
        case "keys": {
          const inp = text(a.keys, (v) => { a.keys = v.trim(); changed(); }, { placeholder: "ctrl+shift+m" });
          const btn = h("button", { type: "button", class: "small", onclick: () => recordInto(inp, btn, (v) => { a.keys = v; changed(); }) }, "⏺ Gravar");
          box.append(row("Teclas", inp, btn),
            hint("Clique em Gravar e aperte o atalho. Atalhos com a tecla Windows o próprio Windows pega antes: digite na mão (ex.: win+shift+s)."));
          break;
        }
        case "type":
          box.append(row("Texto", h("textarea", { rows: 3, oninput: (e) => { a.text = e.target.value; changed(); } }, a.text || "")),
            hint("Cada quebra de linha vira um Enter."));
          break;
        case "media":
          box.append(row("Tecla", select(MEDIA_KEYS, a.key, (v) => { a.key = v; changed(); })));
          break;
        case "macro":
          box.append(macroChooser(a, changed, depth));
          break;
        case "run": {
          const inp = text(a.path, (v) => { a.path = v; changed(); }, { placeholder: "notepad.exe ou C:\\...\\app.exe" });
          const browse = h("button", { type: "button", class: "small", onclick: async () => {
            const f = await Bridge.call("pickFile").catch((e) => App.toast(e.message));
            if (f) { inp.value = a.path = f; changed(); }
          } }, "📂 Procurar…");
          box.append(row("Programa", inp, browse),
            row("Argumentos", text(a.args, (v) => { a.args = v || undefined; changed(); })),
            row("Pasta inicial", text(a.cwd, (v) => { a.cwd = v || undefined; changed(); }, { placeholder: "(opcional)" })));
          break;
        }
        case "url":
          box.append(row("Link", text(a.url, (v) => { a.url = v; changed(); }, { placeholder: "https://... ou spotify: ou steam://..." })));
          break;
        case "script": {
          const opts = { "": "— escolha —" };
          for (const f of scripts) opts[f] = f;
          if (a.file && !opts[a.file]) opts[a.file] = a.file + " (não encontrado)";
          box.append(row("Script", select(opts, a.file, (v) => { a.file = v; changed(); })),
            row("Argumentos", text(a.args, (v) => { a.args = v || undefined; changed(); })),
            row("", h("label", { class: "check" },
              h("input", { type: "checkbox", checked: a.hidden !== false, onchange: (e) => { if (e.target.checked) delete a.hidden; else a.hidden = false; changed(); } }),
              " Rodar escondido (sem janela)")),
            hint("Coloque .ps1, .bat, .cmd, .py ou .js na pasta scripts/ do PainelDeck pra aparecerem aqui."));
          break;
        }
        case "mic":
          box.append(row("Modo", select(MIC_MODES, a.mode || "toggle", (v) => { a.mode = v; changed(); })));
          break;
        case "record":
          box.append(hint("Começa a gravar o monitor principal no OBS; apertar de novo para e salva. Na cena Navy o painel vira a tela de gravação. Dica: \"state\": \"recording\" deixa o botão aceso enquanto grava."));
          break;
        case "claude":
          box.append(hint("Apertar esse botão abre a caixa pra você pedir ao Claude Code um item novo ou qualquer mudança no painel."));
          break;
        case "games":
          box.append(hint("Abre a roda com os jogos da lista. Pra adicionar/tirar jogos use o botão 🎮 Jogos na barra de edição."));
          break;
        case "macros":
          box.append(hint("Abre uma gaveta com todas as macros salvas (🔁 Macros na barra de edição); tocar num card roda a macro."));
          break;
        case "obs": {
          const list = h("datalist", { id: "obs-req-" + Math.random().toString(36).slice(2) }, OBS_REQUESTS.map((r) => h("option", { value: r })));
          const dataBox = h("textarea", { rows: 3, class: "json", placeholder: '{ "sceneName": "Jogo" }', oninput: (e) => {
            const v = e.target.value.trim();
            try { if (v) a.data = JSON.parse(v); else delete a.data; e.target.classList.remove("invalid"); changed(); }
            catch { e.target.classList.add("invalid"); }
          } }, a.data ? JSON.stringify(a.data) : "");
          box.append(row("Request", text(a.request, (v) => { a.request = v; changed(); }, { list: list.id }), list),
            row("Dados (JSON)", dataBox),
            hint("Qualquer request do obs-websocket v5. Ex.: SetCurrentProgramScene com { \"sceneName\": \"Jogo\" }."));
          break;
        }
      }
    }
    render();
    return box;
  }

  // macro: usar uma macro salva (ref) ou passos próprios
  function macroChooser(a, changed, depth) {
    const box = h("div");
    const names = Object.keys(draft.macros || {});
    const opts = { "": "— passos só deste botão —" };
    for (const n of names) opts[n] = "Macro salva: " + n;
    function render() {
      box.innerHTML = "";
      box.append(row("Usar", select(opts, a.ref || "", (v) => {
        if (v) { a.ref = v; delete a.steps; } else { delete a.ref; a.steps = a.steps || []; }
        changed();
        render();
      })));
      if (!a.ref) box.append(stepsEditor(a.steps || (a.steps = []), changed, depth + 1));
      else box.append(hint("Pra mudar os passos dessa macro, use o botão 🔁 Macros na barra de edição."));
    }
    render();
    return box;
  }

  function stepsEditor(steps, changed, depth) {
    const box = h("div", { class: "steps" });
    function render() {
      box.innerHTML = "";
      steps.forEach((st, i) => {
        const tools = h("div", { class: "step-tools" },
          h("button", { type: "button", class: "icon-btn", title: "Subir", disabled: i === 0, onclick: () => { [steps[i - 1], steps[i]] = [steps[i], steps[i - 1]]; changed(); render(); } }, "↑"),
          h("button", { type: "button", class: "icon-btn", title: "Descer", disabled: i === steps.length - 1, onclick: () => { [steps[i + 1], steps[i]] = [steps[i], steps[i + 1]]; changed(); render(); } }, "↓"),
          h("button", { type: "button", class: "icon-btn danger", title: "Remover passo", onclick: () => { steps.splice(i, 1); changed(); render(); } }, "✕"));
        const isDelay = !st.type && st.delay !== undefined;
        const body = isDelay
          ? row("⏱ Esperar", h("input", { type: "number", min: 0, step: 10, value: st.delay, oninput: (e) => { st.delay = Math.max(0, Number(e.target.value) || 0); changed(); } }), " ms")
          : actionEditor(st, changed, depth);
        box.append(h("div", { class: "step" }, h("div", { class: "step-num" }, i + 1), h("div", { class: "step-body" }, body), tools));
      });
      if (!steps.length) box.append(hint("Nenhum passo ainda."));
      box.append(h("div", { class: "step-add" },
        h("button", { type: "button", class: "small", onclick: () => { steps.push({ type: "keys", keys: "" }); changed(); render(); } }, "＋ Ação"),
        h("button", { type: "button", class: "small", onclick: () => { steps.push({ delay: 100 }); changed(); render(); } }, "＋ Espera")));
    }
    render();
    return box;
  }

  // ---------- editor de item ----------
  function kindOf(it) { return it.widget ? "widget" : it.page !== undefined ? "page" : "button"; }

  function openItem(pageIdx, idx) {
    const items = draft.pages[pageIdx].items || (draft.pages[pageIdx].items = []);
    const isNew = idx < 0;
    const it = isNew ? { icon: "⭐", label: "Novo botão", action: { type: "keys", keys: "" }, ...App.NEW_ITEM } : clone(items[idx]);
    let kind = kindOf(it);
    const body = h("div");

    function render() {
      body.innerHTML = "";
      body.append(row("Tipo", select({ button: "Botão com ação", page: "Botão que troca de página", widget: "Widget" }, kind, (k) => {
        kind = k;
        if (k === "widget" && !it.widget) it.widget = "clock";
        if (k === "page" && it.page === undefined) it.page = draft.pages[0].name;
        if (k === "button" && !it.action) it.action = { type: "keys", keys: "" };
        render();
      })));

      if (kind !== "widget") {
        const iconInp = text(it.icon, (v) => { it.icon = v; }, { class: "icon-input" });
        const picks = h("div", { class: "emoji-picks" }, EMOJIS.map((e) =>
          h("button", { type: "button", onclick: () => { it.icon = iconInp.value = e; } }, e)));
        body.append(row("Ícone", iconInp), picks, hint("Dica: Win + . abre o seletor de emoji do Windows."),
          row("Texto", text(it.label, (v) => { it.label = v; })));
      }

      if (kind === "widget") {
        const wopts = {};
        for (const n of Object.keys(window.Widgets)) wopts[n] = WIDGET_NAMES[n] || n;
        body.append(row("Widget", select(wopts, it.widget, (v) => { it.widget = v; render(); })));
        const fields = WIDGET_FIELDS[it.widget];
        if (fields) {
          for (const f of fields) {
            body.append(row(f.label, f.options
              ? select(f.options, it[f.k], (v) => { it[f.k] = v; })
              : h("input", { type: f.type || "text", value: it[f.k] ?? "", oninput: (e) => { it[f.k] = e.target.value; } })));
          }
        } else {
          body.append(hint("Widget personalizado: as opções dele você edita direto no deck.json."));
        }
      } else if (kind === "page") {
        const popts = {};
        draft.pages.forEach((p) => (popts[p.name] = p.name));
        const cur = typeof it.page === "number" ? draft.pages[it.page]?.name : it.page;
        body.append(row("Vai pra página", select(popts, cur, (v) => { it.page = v; })));
      } else {
        body.append(h("h3", {}, "O que ele faz"), actionEditor(it.action || (it.action = { type: "keys", keys: "" }), () => {}));
        body.append(h("h3", {}, "Aceso"), row("Acende", select(STATES, it.state || "", (v) => {
          if (v) { it.state = v; it.on = it.on || {}; } else { delete it.state; delete it.on; }
          render();
        })));
        if (it.state) {
          body.append(row("Ícone aceso", text(it.on.icon, (v) => { it.on.icon = v || undefined; }, { class: "icon-input" })),
            row("Texto aceso", text(it.on.label, (v) => { it.on.label = v || undefined; })));
        }
      }

      body.append(h("h3", {}, "Aparência"));
      const colorOn = !!it.color;
      const colorInp = h("input", { type: "color", value: it.color || "#7c5cff", disabled: !colorOn, oninput: (e) => { it.color = e.target.value; } });
      body.append(row("Cor",
        h("label", { class: "check" }, h("input", { type: "checkbox", checked: colorOn, onchange: (e) => {
          colorInp.disabled = !e.target.checked;
          if (e.target.checked) it.color = colorInp.value; else delete it.color;
        } }), " usar cor própria "), colorInp));
      const pct = (k, min, max) => h("input", { type: "number", min, max, step: 0.5, value: it[k] ?? "", class: "num",
        oninput: (e) => { const v = Number(e.target.value); if (e.target.value !== "" && !isNaN(v)) it[k] = v; else if (k === "z") delete it.z; } });
      body.append(row("Posição", "x ", pct("x", 0, 100), " y ", pct("y", 0, 100), " (% da tela)"),
        row("Tamanho", pct("w", 2, 100), " × ", pct("h", 2, 100), " %"),
        row("Camada", pct("z", -10, 50), " (maior fica por cima)"),
        hint("Mais fácil: feche e arraste o item na tela; a alça no canto redimensiona."));
    }
    render();

    function finalItem() {
      const o = clone(it);
      if (kind === "widget") { delete o.icon; delete o.action; delete o.page; delete o.state; delete o.on; }
      else {
        delete o.widget;
        for (const fs of Object.values(WIDGET_FIELDS)) for (const f of fs) if (f.k !== "label") delete o[f.k];
        if (kind === "page") { delete o.action; delete o.state; delete o.on; }
        else delete o.page;
      }
      if (o.on && !o.on.icon && !o.on.label) delete o.on;
      return o;
    }
    function valid() {
      if (dlg.querySelector(".invalid")) { App.toast("Tem um campo JSON com erro (em vermelho)."); return false; }
      return true;
    }

    const buttons = [
      h("button", { type: "button", class: "primary", onclick: () => {
        if (!valid()) return;
        if (isNew) items.push(finalItem()); else items[idx] = finalItem();
        markDirty(); closeDialog(); App.setDeck(draft, pageIdx);
      } }, isNew ? "Adicionar" : "Aplicar"),
      h("button", { type: "button", onclick: async () => {
        if (kind !== "button") return App.toast("Só botões com ação podem ser testados.");
        if (!valid()) return;
        try { await Bridge.run(finalItem().action); App.toast("Ação executada ✓", "ok"); }
        catch (e) { App.toast(e.message); }
      } }, "▶ Testar"),
    ];
    if (!isNew) {
      buttons.push(
        h("button", { type: "button", title: "Desenhar antes dos outros (fica por baixo)", disabled: idx === 0, onclick: () => {
          [items[idx - 1], items[idx]] = [items[idx], items[idx - 1]]; markDirty(); closeDialog(); App.setDeck(draft, pageIdx);
        } }, "⬇ Pra trás"),
        h("button", { type: "button", title: "Desenhar depois dos outros (fica por cima)", disabled: idx === items.length - 1, onclick: () => {
          [items[idx + 1], items[idx]] = [items[idx], items[idx + 1]]; markDirty(); closeDialog(); App.setDeck(draft, pageIdx);
        } }, "⬆ Pra frente"),
        h("button", { type: "button", onclick: () => {
          items.splice(idx + 1, 0, offset(finalItem())); markDirty(); closeDialog(); App.setDeck(draft, pageIdx);
        } }, "⧉ Duplicar"),
        h("button", { type: "button", class: "danger", onclick: () => {
          items.splice(idx, 1); markDirty(); closeDialog(); App.setDeck(draft, pageIdx);
        } }, "🗑 Apagar"));
    }
    buttons.push(h("button", { type: "button", class: "ghost", onclick: closeDialog }, "Fechar"));
    openDialog(isNew ? "Novo item" : "Editar item", body, buttons);
  }

  // ---------- macros com nome ----------
  function openMacros(selected) {
    draft.macros = draft.macros || {};
    const names = Object.keys(draft.macros);
    let name = selected && draft.macros[selected] ? selected : names[0];
    const body = h("div");
    if (!name) {
      body.append(hint("Nenhuma macro salva ainda. Macros salvas podem ser usadas em vários botões."));
    } else {
      const opts = {};
      names.forEach((n) => (opts[n] = n));
      const renameInp = text(name, () => {});
      body.append(
        row("Macro", select(opts, name, (v) => openMacros(v))),
        row("Nome", renameInp, h("button", { type: "button", class: "small", onclick: () => {
          const nn = renameInp.value.trim();
          if (!nn || nn === name) return;
          if (draft.macros[nn]) return App.toast(`Já existe uma macro "${nn}".`);
          draft.macros[nn] = draft.macros[name];
          delete draft.macros[name];
          forEachAction((a) => { if (a.type === "macro" && a.ref === name) a.ref = nn; });
          markDirty();
          openMacros(nn);
        } }, "Renomear")),
        h("h3", {}, "Passos"),
        stepsEditor(draft.macros[name], markDirty, 1));
    }
    const buttons = [
      h("button", { type: "button", class: "primary", onclick: () => {
        const n = prompt("Nome da nova macro:", "minha-macro");
        if (!n) return;
        if (draft.macros[n]) return App.toast(`Já existe uma macro "${n}".`);
        draft.macros[n] = [];
        markDirty();
        openMacros(n);
      } }, "＋ Nova macro"),
    ];
    if (name) {
      buttons.push(h("button", { type: "button", class: "danger", onclick: () => {
        let uses = 0;
        forEachAction((a) => { if (a.type === "macro" && a.ref === name) uses++; });
        if (uses && !confirm(`${uses} botão(ões) usam a macro "${name}". Apagar mesmo assim?`)) return;
        delete draft.macros[name];
        markDirty();
        openMacros();
      } }, "🗑 Apagar macro"),
      h("button", { type: "button", onclick: async () => {
        try { await Bridge.run({ type: "macro", steps: draft.macros[name] }); App.toast("Macro executada ✓", "ok"); }
        catch (e) { App.toast(e.message); }
      } }, "▶ Testar"));
    }
    buttons.push(h("button", { type: "button", class: "ghost", onclick: closeDialog }, "Fechar"));
    openDialog("🔁 Macros salvas", body, buttons);
  }

  function forEachAction(fn) {
    const visit = (a) => {
      if (!a || typeof a !== "object") return;
      if (a.type) fn(a);
      if (Array.isArray(a.steps)) a.steps.forEach(visit);
    };
    draft.pages.forEach((p) => (p.items || []).forEach((it) => visit(it.action)));
    Object.values(draft.macros || {}).forEach((steps) => steps.forEach(visit));
  }

  // ---------- layout livre: arrastar, redimensionar, guias, teclado ----------
  let sel = -1;      // item selecionado na página aberta
  let snap = true;
  const SNAP = 1, NEAR = 1.2; // grade de 1%; gruda em bordas/centros a menos de 1,2%
  const rd = (v) => Math.round(v * 10) / 10;
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const offset = (o) => ({ ...o, x: clamp(rd(o.x + 2), 0, 100 - o.w), y: clamp(rd(o.y + 2), 0, 100 - o.h) });
  const pageItems = () => draft.pages[App.current].items;

  // posição candidata → posição grudada + linhas-guia. edges = deslocamentos da borda que podem grudar (0, meio, fim)
  function snapAxis(pos, edges, targets) {
    let best = null;
    for (const o of edges) for (const t of targets) {
      const d = Math.abs(pos + o - t);
      if (d < NEAR && (!best || d < best.d)) best = { d, pos: t - o, guide: t };
    }
    return best || { pos: Math.round(pos / SNAP) * SNAP, guide: null };
  }

  function bindTile(el, idx) {
    el.classList.add("clickable", "editable");
    if (idx === sel) el.classList.add("sel");
    el.append(h("div", { class: "rz", title: "Arraste pra redimensionar" }));
    el.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      e.preventDefault();
      const stage = el.parentElement, box = stage.getBoundingClientRect();
      const it = pageItems()[idx];
      const resize = e.target.classList.contains("rz");
      const st = { x: e.clientX, y: e.clientY, ix: it.x, iy: it.y, iw: it.w, ih: it.h };
      const others = pageItems().filter((o) => o !== it);
      const tx = [0, 50, 100, ...others.flatMap((o) => [o.x, o.x + o.w / 2, o.x + o.w])];
      const ty = [0, 50, 100, ...others.flatMap((o) => [o.y, o.y + o.h / 2, o.y + o.h])];
      const tip = h("div", { class: "drag-tip" });
      let moved = false, guides = [];
      stage.querySelectorAll(".tile.sel").forEach((t) => t.classList.remove("sel"));
      sel = idx;
      el.classList.add("sel");
      el.setPointerCapture(e.pointerId);

      const onMove = (ev) => {
        if (!moved && Math.hypot(ev.clientX - st.x, ev.clientY - st.y) < 4) return;
        if (!moved) { moved = true; el.classList.add("dragging"); stage.append(tip); }
        const dx = ((ev.clientX - st.x) / box.width) * 100, dy = ((ev.clientY - st.y) / box.height) * 100;
        const free = !snap || ev.altKey;
        let gx = null, gy = null;
        if (resize) {
          let w = st.iw + dx, hh = st.ih + dy;
          if (!free) {
            const sx = snapAxis(it.x + w, [0], tx), sy = snapAxis(it.y + hh, [0], ty);
            w = sx.pos - it.x; hh = sy.pos - it.y; gx = sx.guide; gy = sy.guide;
          }
          it.w = rd(clamp(w, 3, 100 - it.x));
          it.h = rd(clamp(hh, 3, 100 - it.y));
        } else {
          let x = st.ix + dx, y = st.iy + dy;
          if (!free) {
            const sx = snapAxis(x, [0, it.w / 2, it.w], tx), sy = snapAxis(y, [0, it.h / 2, it.h], ty);
            x = sx.pos; y = sy.pos; gx = sx.guide; gy = sy.guide;
          }
          it.x = rd(clamp(x, 0, 100 - it.w));
          it.y = rd(clamp(y, 0, 100 - it.h));
        }
        App.place(el, it);
        guides.forEach((g) => g.remove());
        guides = [];
        if (gx !== null) guides.push(h("div", { class: "guide v", style: `left:${gx}%` }));
        if (gy !== null) guides.push(h("div", { class: "guide h", style: `top:${gy}%` }));
        stage.append(...guides);
        tip.textContent = resize ? `${it.w} × ${it.h} %` : `x ${it.x}  y ${it.y}  ·  ${it.w} × ${it.h} %`;
        tip.style.left = it.x + "%";
        tip.style.top = it.y + "%";
      };
      const onUp = () => {
        el.removeEventListener("pointermove", onMove);
        el.removeEventListener("pointerup", onUp);
        el.removeEventListener("pointercancel", onUp);
        guides.forEach((g) => g.remove());
        tip.remove();
        el.classList.remove("dragging");
        if (moved) { markDirty(); App.setDeck(draft); } // redesenha: widgets se ajustam ao tamanho novo
        else openItem(App.current, idx);
      };
      el.addEventListener("pointermove", onMove);
      el.addEventListener("pointerup", onUp);
      el.addEventListener("pointercancel", onUp);
    });
  }

  document.addEventListener("keydown", (e) => {
    if (!draft || dlg.open || e.target.closest?.("input, textarea, select")) return;
    const ctrl = e.ctrlKey || e.metaKey;
    if (ctrl && e.code === "KeyZ") { e.preventDefault(); return undo(); }
    const items = pageItems();
    const it = items[sel];
    if (!it) return;
    const step = e.shiftKey ? 5 : 0.5;
    const moves = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
    if (moves[e.code]) {
      e.preventDefault();
      it.x = rd(clamp(it.x + moves[e.code][0], 0, 100 - it.w));
      it.y = rd(clamp(it.y + moves[e.code][1], 0, 100 - it.h));
    } else if (e.code === "Delete" || e.code === "Backspace") {
      e.preventDefault();
      items.splice(sel, 1);
      sel = -1;
    } else if (ctrl && e.code === "KeyD") {
      e.preventDefault();
      items.push(offset(clone(it)));
      sel = items.length - 1;
    } else if (e.code === "Escape") {
      sel = -1;
    } else return;
    markDirty();
    App.setDeck(draft);
  });

  // ---------- 🎮 lista de jogos (roda de jogos) ----------
  function openGames() {
    const games = draft.games || (draft.games = []);
    const body = h("div");
    function render() {
      body.innerHTML = "";
      if (!games.length) body.append(hint("Nenhum jogo ainda."));
      games.forEach((g, i) => {
        const preview = g.image ? h("img", { src: g.image, alt: "" }) : h("div", { class: "noimg" });
        const fields = h("div", {},
          row("Nome", text(g.name, (v) => { g.name = v; markDirty(); })),
          row("Imagem", text(g.image, (v) => { g.image = v || undefined; if (v) preview.src = v; markDirty(); }, { placeholder: "games/meu-jogo.jpg" })),
          actionEditor(g.action || (g.action = { type: "url", url: "steam://rungameid/" }), markDirty));
        const tools = h("div", { class: "step-tools" },
          h("button", { type: "button", class: "icon-btn", title: "Subir", disabled: i === 0, onclick: () => { [games[i - 1], games[i]] = [games[i], games[i - 1]]; markDirty(); render(); } }, "↑"),
          h("button", { type: "button", class: "icon-btn", title: "Descer", disabled: i === games.length - 1, onclick: () => { [games[i + 1], games[i]] = [games[i], games[i + 1]]; markDirty(); render(); } }, "↓"),
          h("button", { type: "button", class: "icon-btn danger", title: "Remover", onclick: () => { games.splice(i, 1); markDirty(); render(); } }, "✕"));
        body.append(h("div", { class: "game-row" }, preview, fields, tools));
      });
      body.append(hint("Imagens: coloque o arquivo na pasta wwwroot/games do PainelDeck e escreva games/nome.jpg. Steam: steam://rungameid/APPID · Ubisoft: uplay://launch/ID/0 · Xbox/Store: Abrir programa explorer.exe com shell:AppsFolder\\..."));
    }
    render();
    openDialog("🎮 Jogos da roda", body, [
      h("button", { type: "button", class: "primary", onclick: () => {
        games.push({ name: "Novo jogo", image: "", action: { type: "url", url: "steam://rungameid/" } }); markDirty(); render();
      } }, "＋ Jogo"),
      h("button", { type: "button", onclick: () => { closeDialog(); Games.open(); } }, "▶ Ver a roda"),
      h("button", { type: "button", class: "ghost", onclick: closeDialog }, "Fechar"),
    ]);
  }

  // ---------- barra de edição ----------
  const bar = h("header", { id: "editbar" });
  document.body.prepend(bar);

  function refreshBar() {
    if (!draft) return;
    const p = App.current;
    const page = draft.pages[p];
    bar.innerHTML = "";
    const nameInp = h("input", { type: "text", class: "page-name", value: page.name, onchange: (e) => {
      const nn = e.target.value.trim();
      if (!nn || nn === page.name) { e.target.value = page.name; return; }
      if (draft.pages.some((x) => x.name === nn)) { App.toast(`Já existe uma página "${nn}".`); e.target.value = page.name; return; }
      const old = page.name;
      page.name = nn;
      draft.pages.forEach((pg) => (pg.items || []).forEach((it) => { if (it.page === old) it.page = nn; }));
      markDirty();
      App.setDeck(draft, p);
    } });
    bar.append(
      h("span", { class: "badge" }, "✏️ Editando"),
      h("label", { class: "inline" }, "Página ", nameInp),
      h("button", { type: "button", title: "Mover página pra esquerda", disabled: p === 0, onclick: () => {
        [draft.pages[p - 1], draft.pages[p]] = [draft.pages[p], draft.pages[p - 1]]; markDirty(); App.setDeck(draft, p - 1);
      } }, "◀"),
      h("button", { type: "button", title: "Mover página pra direita", disabled: p === draft.pages.length - 1, onclick: () => {
        [draft.pages[p + 1], draft.pages[p]] = [draft.pages[p], draft.pages[p + 1]]; markDirty(); App.setDeck(draft, p + 1);
      } }, "▶"),
      h("button", { type: "button", onclick: () => {
        let n = draft.pages.length + 1, name;
        while (draft.pages.some((x) => x.name === (name = "Página " + n))) n++;
        draft.pages.push({ name, layout: "free", items: [] });
        markDirty();
        App.setDeck(draft, draft.pages.length - 1);
      } }, "＋ Página"),
      h("button", { type: "button", class: "danger", disabled: draft.pages.length < 2, onclick: () => {
        if ((page.items || []).length && !confirm(`Apagar a página "${page.name}" e os ${page.items.length} itens dela?`)) return;
        draft.pages.splice(p, 1);
        markDirty();
        App.setDeck(draft, Math.max(0, p - 1));
      } }, "🗑 Página"),
      h("span", { class: "sep" }),
      h("button", { type: "button", onclick: () => openItem(p, -1) }, "＋ Item"),
      h("button", { type: "button", class: snap ? "on" : "", title: "Grudar na grade de 1% e nas bordas dos outros itens (segure Alt pra soltar)",
        onclick: () => { snap = !snap; refreshBar(); } }, "🧲 Snap"),
      h("button", { type: "button", title: "Desfazer (Ctrl+Z)", disabled: !undoStack.length, onclick: undo }, "↶ Desfazer"),
      h("button", { type: "button", onclick: () => openMacros() }, "🔁 Macros"),
      h("button", { type: "button", onclick: () => openGames() }, "🎮 Jogos"),
      h("button", { type: "button", class: "claude", onclick: () => askClaude() }, "✨ Pedir ao Claude"),
      h("span", { class: "grow" }),
      h("button", { type: "button", class: "ghost", onclick: () => stop(false) }, "Cancelar"),
      h("button", { type: "button", class: "primary", onclick: () => stop(true) }, dirty ? "💾 Salvar" : "Concluir"),
    );
  }

  // ---------- entrar / sair ----------
  // ---------- ✨ Pedir ao Claude ----------
  // Manda o pedido pro Claude Code no PC. Ele edita deck.json / widgets.js / style.css / scripts
  // e o painel se atualiza sozinho. Precisa do modo edição ligado pra você conseguir digitar.
  const IDEAS = [
    "Um botão que abre o Discord e o Spotify juntos",
    "Um widget com a previsão do tempo de São Paulo",
    "Uma página nova \"Trabalho\" com atalhos do VS Code",
    "Um widget de pomodoro de 25 minutos com animação",
    "Um botão que liga o modo foco: fecha o Steam e abre o Notion",
  ];
  async function askClaude(prefill = "") {
    if (!draft) await start();
    const ta = h("textarea", { rows: 4, class: "claude-input", placeholder: "Ex.: um botão que abre o Discord e o Spotify juntos" }, prefill);
    const ideas = h("div", { class: "ideas" }, IDEAS.map((t) => h("button", { type: "button", class: "small", onclick: () => { ta.value = t; ta.focus(); } }, t)));
    const body = h("div", {},
      h("div", { class: "hint flush" }, "Descreva o item (ou qualquer mudança). O Claude Code do seu PC edita os arquivos do PainelDeck e o painel atualiza sozinho. Pode pedir coisa louca. 😄"),
      ta, h("h3", {}, "Ideias"), ideas);
    const send = h("button", { type: "button", class: "primary claude", onclick: () => run() }, "✨ Pedir");
    openDialog("✨ Pedir ao Claude", body, [send, h("button", { type: "button", class: "ghost", onclick: closeDialog }, "Fechar")]);
    setTimeout(() => ta.focus(), 50);
    ta.addEventListener("keydown", (e) => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) run(); });

    async function run() {
      const request = ta.value.trim();
      if (!request) return App.toast("Escreva o que você quer primeiro.");
      if (dirty) {
        try { await Bridge.call("saveDeck", { deck: draft }); dirty = false; }
        catch (e) { return App.toast("Não consegui salvar antes de chamar o Claude: " + e.message); }
      }
      const page = draft.pages[App.current]?.name || "";
      openDialog("✨ O Claude está trabalhando…",
        h("div", { class: "claude-wait" }, h("div", { class: "orb" }), h("p", {}, `"${request}"`),
          hint("Pode levar um ou dois minutos. Você pode continuar usando o PC.")), []);
      try {
        const answer = await Bridge.call("askClaude", { request, page });
        await new Promise((r) => setTimeout(r, 700)); // espera o deck novo chegar
        await stop(false);
        openDialog("✨ Feito!", h("p", { class: "claude-answer" }, answer || "Pronto!"),
          [h("button", { type: "button", class: "primary", onclick: closeDialog }, "Valeu!")]);
      } catch (e) {
        openDialog("✨ Não rolou", h("p", { class: "claude-answer" }, e.message), [
          h("button", { type: "button", class: "primary", onclick: () => askClaude(request) }, "Tentar de novo"),
          h("button", { type: "button", class: "ghost", onclick: closeDialog }, "Fechar")]);
      }
    }
  }

  async function start() {
    if (!App.deck) return;
    draft = clone(App.deck);
    dirty = false;
    sel = -1;
    undoStack = [];
    lastSnap = JSON.stringify(draft);
    App.setEditing(true);
    try { await Bridge.call("editMode", { on: true }); } catch (e) { App.toast(e.message); }
    try { scripts = (await Bridge.call("listScripts")) || []; } catch { scripts = []; }
    App.setDeck(draft);
  }

  async function stop(save) {
    if (save && dirty) {
      try {
        await Bridge.call("saveDeck", { deck: draft });
        App.toast(Bridge.preview ? "Preview: baixei o deck.json, substitua o arquivo na pasta" : "Salvo no deck.json ✓", "ok");
      } catch (e) {
        return App.toast("Não consegui salvar: " + e.message);
      }
    } else if (!save && dirty && !confirm("Descartar as mudanças?")) {
      return;
    }
    const keep = save ? draft : Bridge.deck;
    draft = null;
    dirty = false;
    bar.innerHTML = "";
    if (dlg.open) closeDialog();
    App.setEditing(false);
    App.setDeck(keep);
    try { await Bridge.call("editMode", { on: false }); } catch { /* ok */ }
  }

  return { start, openItem, bindTile, refreshBar, askClaude, get editing() { return !!draft; } };
})();
