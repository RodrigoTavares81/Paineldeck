// =====================================================================
//  Widgets — cada um é um mini-app dentro de um quadrado do painel.
//
//  defineWidget("nome", {
//    mount(el, cfg, api) {
//      el.innerHTML = "...";              // desenha
//      return {
//        tick(now)  { },                   // chamado a cada 1s
//        status(s)  { },                   // status do PC (cpu, ram, micMuted, obs)
//        click(e)   { },                   // clique no widget
//      };
//    }
//  });
//
//  Depois é só usar no deck.json: { widget: "nome", ...suas opções }
// =====================================================================

window.Widgets = {};
const defineWidget = (name, def) => (window.Widgets[name] = def);

// ---------- helpers ----------
const Dates = {
  parse(s) { const [y, m, d] = s.split("-").map(Number); return new Date(y, m - 1, d); },
  startOfDay(d) { return new Date(d.getFullYear(), d.getMonth(), d.getDate()); },
  days(from, to) { return Math.round((Dates.startOfDay(to) - Dates.startOfDay(from)) / 86400000); },
  short(d) { return d.toLocaleDateString("pt-BR", { day: "2-digit", month: "short" }).replace(".", ""); },
};

function pop(el) { el.classList.remove("pop"); void el.offsetWidth; el.classList.add("pop"); }

function confetti(host, n = 60) {
  const colors = ["#ffd166", "#ef476f", "#06d6a0", "#118ab2", "#ffffff", "#ff9f1c"];
  const r = host.getBoundingClientRect();
  for (let i = 0; i < n; i++) {
    const p = document.createElement("i");
    p.className = "confetti";
    p.style.background = colors[i % colors.length];
    p.style.left = r.width / 2 + "px";
    p.style.top = r.height / 2 + "px";
    host.appendChild(p);
    const ang = Math.random() * Math.PI * 2, dist = 80 + Math.random() * r.width * 0.6;
    p.animate([
      { transform: "translate(0,0) rotate(0)", opacity: 1 },
      { transform: `translate(${Math.cos(ang) * dist}px, ${Math.sin(ang) * dist + 120}px) rotate(${Math.random() * 720}deg)`, opacity: 0 },
    ], { duration: 900 + Math.random() * 700, easing: "cubic-bezier(.15,.7,.3,1)" }).onfinish = () => p.remove();
  }
}

// ---------- 🌴 Férias ----------
defineWidget("ferias", {
  mount(el, cfg) {
    el.classList.add("w-ferias");
    el.innerHTML = `
      <div class="sun"></div>
      <div class="palm">🌴</div>
      <div class="content">
        <div class="kicker"></div>
        <div class="big"><span class="num"></span><span class="unit"></span></div>
        <div class="caption"></div>
        <div class="bar"><div class="fill"></div></div>
        <div class="sub"></div>
      </div>
      <svg class="waves" viewBox="0 0 240 20" preserveAspectRatio="none">
        <path d="M0 10 Q15 2 30 10 T60 10 T90 10 T120 10 T150 10 T180 10 T210 10 T240 10 V20 H0Z"/>
      </svg>`;
    const $ = (s) => el.querySelector(s);
    const inicio = Dates.parse(cfg.inicio), volta = Dates.parse(cfg.volta);
    let lastNum = null;

    return {
      tick(now) {
        const ate = Dates.days(now, inicio), ateVolta = Dates.days(now, volta);
        const total = Math.max(1, Dates.days(inicio, volta));
        let kicker, num, unit, caption, sub, fill;

        if (ate > 0) {
          const ms = inicio - now, h = Math.floor(ms / 3.6e6) % 24, m = Math.floor(ms / 6e4) % 60, s = Math.floor(ms / 1000) % 60;
          kicker = "contagem regressiva";
          num = ate; unit = ate === 1 ? "dia" : "dias"; caption = "pras férias";
          sub = `${String(h).padStart(2, "0")}h ${String(m).padStart(2, "0")}m ${String(s).padStart(2, "0")}s · volta em ${ateVolta} dias`;
          fill = null;
          el.dataset.phase = "antes";
        } else if (ateVolta > 0) {
          kicker = ate === 0 ? "🎉 começou!" : "modo férias";
          num = ateVolta; unit = ateVolta === 1 ? "dia" : "dias"; caption = ateVolta === 1 ? "amanhã volta 😭" : "até a volta";
          sub = `${Dates.short(inicio)} → ${Dates.short(volta)}`;
          fill = Math.min(1, (total - ateVolta) / total);
          el.dataset.phase = "durante";
        } else {
          kicker = "acabou 🥲";
          num = "—"; unit = ""; caption = "de volta ao trampo";
          sub = "edite as datas no deck.json";
          fill = 1;
          el.dataset.phase = "depois";
        }

        $(".kicker").textContent = kicker;
        if (num !== lastNum) { $(".num").textContent = num; pop($(".num")); lastNum = num; }
        $(".unit").textContent = unit;
        $(".caption").textContent = caption;
        $(".sub").textContent = sub;
        $(".bar").style.display = fill === null ? "none" : "";
        if (fill !== null) $(".fill").style.width = (fill * 100).toFixed(1) + "%";
      },
      click() { confetti(el); },
    };
  },
});

// ---------- ⏳ Contagem genérica ----------
defineWidget("countdown", {
  mount(el, cfg) {
    el.classList.add("w-countdown");
    el.innerHTML = `<div class="emoji">${cfg.emoji || "⏳"}</div>
      <div><div class="big"><span class="num"></span><span class="unit"></span></div><div class="caption">${cfg.title || ""}</div></div>`;
    const target = Dates.parse(cfg.date);
    let last = null;
    return {
      tick(now) {
        const d = Math.max(0, Dates.days(now, target));
        if (d !== last) { el.querySelector(".num").textContent = d; pop(el.querySelector(".num")); last = d; }
        el.querySelector(".unit").textContent = d === 1 ? "dia" : "dias";
      },
      click() { confetti(el, 30); },
    };
  },
});

// ---------- 🕒 Relógio ----------
defineWidget("clock", {
  mount(el) {
    el.classList.add("w-clock");
    el.innerHTML = `<div class="time"><span class="hh"></span><span class="colon">:</span><span class="mm"></span></div><div class="date"></div>`;
    return {
      tick(now) {
        el.querySelector(".hh").textContent = String(now.getHours()).padStart(2, "0");
        el.querySelector(".mm").textContent = String(now.getMinutes()).padStart(2, "0");
        el.querySelector(".date").textContent = now.toLocaleDateString("pt-BR", { weekday: "long", day: "numeric", month: "short" }).replace(".", "");
      },
    };
  },
});

// ---------- 📊 CPU / RAM ----------
defineWidget("stat", {
  mount(el, cfg) {
    el.classList.add("w-stat");
    el.innerHTML = `<div class="ring"><div class="in"><div class="val">--</div><div class="label">${cfg.label || cfg.metric}</div></div></div><div class="extra"></div>`;
    const ring = el.querySelector(".ring");
    // linha de detalhe embaixo do anel, por métrica (metric: cpu | ram | vram | gpu)
    const extra = {
      ram: (s) => s.ramUsedGb != null && `${s.ramUsedGb} / ${s.ramTotalGb} GB`,
      vram: (s) => s.vramUsedGb != null && `${s.vramUsedGb} / ${s.vramTotalGb} GB · ${Math.round(s.gpuTemp)}°C`,
      gpu: (s) => s.gpuTemp != null && `${Math.round(s.gpuTemp)}°C · VRAM ${Math.round(s.vram)}%`,
    };
    return {
      status(s) {
        const raw = s[cfg.metric], v = Number(raw ?? 0);
        ring.style.setProperty("--v", v);
        ring.style.setProperty("--c", v > 85 ? "#ff4d6d" : v > 60 ? "#ffb703" : "#3ddc97");
        el.querySelector(".val").textContent = raw == null ? "--" : Math.round(v) + "%";
        el.querySelector(".extra").textContent = extra[cfg.metric]?.(s) || "";
      },
    };
  },
});
