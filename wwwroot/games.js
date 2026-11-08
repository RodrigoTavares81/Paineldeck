// =====================================================================
//  🎮 Roda de jogos — ação { "type": "games" }. Lê a lista "games" do deck.json:
//    { "name": "ARC Raiders", "image": "games/arc-raiders.jpg", "action": { ...qualquer ação... } }
//  Abre em tela cheia; clicar numa capa roda a ação do jogo. Clique fora ou Esc fecha.
// =====================================================================
window.Games = (() => {
  let overlay = null;

  function close() {
    if (!overlay) return;
    const o = overlay;
    overlay = null;
    o.classList.add("leaving");
    document.removeEventListener("keydown", onKey);
    setTimeout(() => o.remove(), 320);
  }
  const onKey = (e) => { if (e.key === "Escape") close(); };

  function open() {
    if (overlay) return close();
    const games = (App.deck?.games || []).filter((g) => g && g.name);
    if (!games.length) return App.toast("Nenhum jogo na lista \"games\" do deck.json (ou adicione no ✏️ → 🎮 Jogos).", "ok");

    const n = games.length;
    // tudo em vh: o painel é 1280x720 lógico, a roda é dimensionada pela altura
    // capas 2:3 (altura = 1,5 × largura): R + 0,75 × size cabe na metade da tela, e não encosta no centro (26vh)
    const R = n === 1 ? 0 : 31;
    const size = Math.min(20, ((2 * Math.PI * R) / n) * 0.78) || 20;

    overlay = document.createElement("div");
    overlay.className = "gwheel";
    overlay.style.setProperty("--r", R + "vh");
    overlay.style.setProperty("--s", size + "vh");
    overlay.innerHTML = `
      <div class="gw-orbit"></div>
      <div class="gw-hub"><div class="gw-hub-bg"></div><div class="gw-hub-txt"><small>escolha um</small><b>🎮 Jogo</b></div></div>`;
    const hubBg = overlay.querySelector(".gw-hub-bg");
    const hubTxt = overlay.querySelector(".gw-hub-txt");
    const setHub = (g) => {
      hubBg.style.backgroundImage = g?.image ? `url("${g.image}")` : "";
      hubBg.classList.toggle("on", !!g?.image);
      hubTxt.innerHTML = g ? `<small>jogar</small><b></b>` : `<small>escolha um</small><b>🎮 Jogo</b>`;
      if (g) hubTxt.querySelector("b").textContent = g.name;
    };

    games.forEach((g, i) => {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "gw-item";
      item.style.setProperty("--a", (360 / n) * i + "deg");
      item.style.animationDelay = 60 + i * 55 + "ms";
      item.innerHTML = `<div class="gw-card"><span class="gw-fallback"></span><span class="gw-name"></span></div>`;
      const card = item.querySelector(".gw-card");
      item.querySelector(".gw-fallback").textContent = g.name.slice(0, 2).toUpperCase();
      item.querySelector(".gw-name").textContent = g.name;
      if (g.image) {
        const img = new Image();
        img.alt = "";
        img.onload = () => {
          card.classList.add("has-img");
          // logo quadrado/largo (ex.: Rematch): mostra inteiro sobre um fundo desfocado da própria imagem
          if (img.naturalWidth / img.naturalHeight > 0.8) { card.classList.add("wide"); card.style.setProperty("--img", `url("${g.image}")`); }
        };
        img.src = g.image;
        card.prepend(img);
      }
      item.addEventListener("pointerenter", () => { overlay?.querySelectorAll(".gw-item").forEach((x) => x.classList.toggle("dim", x !== item)); setHub(g); });
      item.addEventListener("pointerleave", () => { overlay?.querySelectorAll(".gw-item").forEach((x) => x.classList.remove("dim")); setHub(null); });
      item.addEventListener("click", async (e) => {
        e.stopPropagation();
        if (!g.action) return App.toast(`${g.name}: sem ação configurada`);
        item.classList.add("launch");
        setHub(g);
        try {
          await Bridge.run(g.action);
          setTimeout(close, 450);
        } catch (err) {
          item.classList.remove("launch");
          App.toast(`${g.name}: ${err.message}`);
        }
      });
      overlay.appendChild(item);
    });

    overlay.addEventListener("click", (e) => { if (!e.target.closest(".gw-item")) close(); });
    document.addEventListener("keydown", onKey);
    document.body.appendChild(overlay);
  }

  return { open, close };
})();
