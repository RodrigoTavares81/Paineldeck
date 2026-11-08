// =====================================================================
//  🔁 Gaveta de macros — ação { "type": "macros" }. Mostra todas as macros da seção "macros"
//  do deck.json como cards; clicar num card roda a macro. Clique fora, ✕ ou Esc fecha.
// =====================================================================
window.Macros = (() => {
  let drawer = null;

  // resumo de um passo, ex.: "ctrl+shift+m", "80ms", "“gg wp”"
  function stepText(s) {
    if (!s || typeof s !== "object") return "?";
    if (!s.type && s.delay !== undefined) return `${s.delay}ms`;
    switch (s.type) {
      case "keys": return s.keys || "tecla";
      case "type": return `“${(s.text || "").replace(/\n/g, "⏎")}”`;
      case "media": return `🎵 ${s.key}`;
      case "macro": return s.ref ? `↪ ${s.ref}` : `[${(s.steps || []).map(stepText).join(" → ")}]`;
      case "run": return `🚀 ${(s.path || "").split(/[\\/]/).pop()}`;
      case "url": return `🔗 ${s.url}`;
      case "script": return `📜 ${s.file}`;
      case "mic": return `🎙️ ${s.mode || "toggle"}`;
      case "obs": return `🎬 ${s.request}`;
      default: return s.type;
    }
  }

  function close() {
    if (!drawer) return;
    const d = drawer;
    drawer = null;
    d.classList.add("leaving");
    document.removeEventListener("keydown", onKey);
    setTimeout(() => d.remove(), 350);
  }
  const onKey = (e) => { if (e.key === "Escape") close(); };

  function open() {
    if (drawer) return close();
    const macros = Object.entries(App.deck?.macros || {});
    if (!macros.length) return App.toast("Nenhuma macro salva ainda (✏️ → 🔁 Macros pra criar).", "ok");

    drawer = document.createElement("div");
    drawer.className = "mdrawer";
    drawer.innerHTML = `<div class="md-sheet"><div class="md-head"><b>🔁 Macros</b><span>${macros.length} salvas · toque pra rodar</span><button type="button" class="md-x" title="Fechar">✕</button></div><div class="md-list"></div></div>`;
    const list = drawer.querySelector(".md-list");
    macros.forEach(([name, steps], i) => {
      const card = document.createElement("button");
      card.type = "button";
      card.className = "md-card";
      card.style.animationDelay = 120 + i * 45 + "ms";
      card.style.setProperty("--hue", (i * 47) % 360);
      card.innerHTML = `<div class="md-name"><span class="md-play">▶</span><span></span></div><div class="md-steps"></div>`;
      card.querySelector(".md-name span:last-child").textContent = name;
      const chips = card.querySelector(".md-steps");
      (steps || []).forEach((s, j) => {
        if (j) chips.append(Object.assign(document.createElement("i"), { textContent: "→" }));
        const c = document.createElement("span");
        c.className = !s.type && s.delay !== undefined ? "wait" : "";
        c.textContent = stepText(s);
        chips.append(c);
      });
      if (!steps?.length) chips.textContent = "sem passos";
      card.addEventListener("click", async () => {
        card.classList.remove("ok", "err", "running");
        void card.offsetWidth;
        card.classList.add("running");
        try {
          await Bridge.run({ type: "macro", ref: name });
          card.classList.replace("running", "ok");
        } catch (err) {
          card.classList.replace("running", "err");
          App.toast(`${name}: ${err.message}`);
        }
      });
      list.append(card);
    });
    drawer.addEventListener("click", (e) => { if (e.target === drawer || e.target.closest(".md-x")) close(); });
    document.addEventListener("keydown", onKey);
    document.body.appendChild(drawer);
  }

  return { open, close, stepText };
})();
