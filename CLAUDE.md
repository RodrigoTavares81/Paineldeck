# PainelDeck — guia pro Claude Code

Stream Deck caseiro do Rodrigo: janela em tela cheia num monitor extra que **não rouba o foco** (WinForms + WebView2, .NET 8).
A interface é HTML/CSS/JS em `wwwroot/`; as ações rodam no Windows pelo C#.
Quando um pedido chega pelo botão "✨ Pedir ao Claude", o painel está rodando: **salvar um arquivo já atualiza o painel**.

## Onde mexer (em ordem de preferência)

1. **`deck.json`** (raiz) — botões, páginas, widgets, macros. É JSON com comentários (`//`). A lista completa dos tipos
   de ação está no comentário do topo; mantenha esse bloco. Item novo = adicionar no `items` da página certa.
   - Botão: `{ "icon": "🎧", "label": "Discord", "action": {...}, "color": "#5865f2" }`
   - Widget: `{ "widget": "nome", ...opções, "x": 40, "y": 0, "w": 20, "h": 33.3 }`
   - Trocar de página: `{ "icon": "🎬", "label": "OBS", "page": "OBS" }`
   - Ações: `keys`, `type`, `media`, `macro` (`steps` ou `ref` pra `"macros"`), `run`, `url`, `script`, `mic`, `obs`,
     e as que só abrem coisas no painel: `claude`, `games` (roda de jogos), `macros` (gaveta com todas as macros).
   - **Layout livre:** página com `"layout": "free"` → cada item tem `"x"`, `"y"`, `"w"`, `"h"` em **% da tela** (0–100,
     `x + w ≤ 100`, `y + h ≤ 100`) e `"z"` opcional (maior fica por cima). Ache um espaço vazio olhando os x/y/w/h dos
     itens da página; se não tiver espaço, crie uma página nova (e um botão de ida e volta) em vez de sobrepor.
     Página sem `"layout"` é do formato antigo: grade `grid.cols × grid.rows` com `w`/`h` em células (o painel converte sozinho).
   - **Jogos:** a lista raiz `"games"` = `[{ "name": "...", "image": "games/arquivo.jpg", "action": {...} }]`; as capas
     ficam em `wwwroot/games/` (2:3 em pé fica melhor; logo quadrado aparece inteiro). Steam: `steam://rungameid/APPID`,
     Ubisoft: `uplay://launch/ID/0`, Xbox/Store: `run` com `explorer.exe` e `shell:AppsFolder\AUMID`.
2. **`wwwroot/widgets.js`** — widgets são mini-apps com `defineWidget("nome", { mount(el, cfg, api) { ...; return { tick(now), status(s), click(e), destroy() } } })`.
   - `tick` roda a cada 1s, `status(s)` recebe `{ cpu, ram, ramUsedGb, ramTotalGb, gpu, vram, vramUsedGb, vramTotalGb, gpuTemp, micMuted, obs, sys }`
     (os de GPU vêm `null` sem placa NVIDIA).
   - `api.run(action)` executa qualquer ação do deck; `api.toast(msg)` mostra um aviso.
   - Dá pra usar `fetch` pra APIs públicas sem chave (ex.: open-meteo.com pro clima). Nada de chave/segredo no código.
   - Use `container units` (`cqh`/`cqw`) pro tamanho do texto, igual aos widgets existentes, pra caber em qualquer tamanho de tile.
     Só nos FILHOS do tile: no próprio `.tile` (gap/padding) o `cqh` mede a tela, não o tile — use `margin` nos filhos.
   - Animações e efeitos à vontade: o Rodrigo gosta de coisa bonita e divertida.
3. **`wwwroot/style.css`** — CSS dos widgets novos vai no fim, com prefixo `.w-nome`. Use as variáveis de `:root`.
   O CSS do editor, do diálogo ✨, da roda de jogos e da gaveta de macros fica em `wwwroot/overlays.css` (a cena Navy também usa).
   - **Cenas:** o painel pode mostrar outra tela no lugar do clássico: `"panelScene"` no `settings.json` (`"classic"` ou `"navy"`,
     trocado pela bandeja → **Cena do painel**). A cena Navy fica em `wwwroot/scenes/navy/` (desenhada em 1280x720, sem widgets;
     sem dock nem card de microfone; as férias vêm do widget `"ferias"` ou de `deck.navy.ferias`).
     Se o Rodrigo pedir mudança "no painel" e a cena for Navy, mexa em `scenes/navy/` (`navy.css`/`navy.js`).
     O `status` da Navy também traz `gpuName` e `media` = `{ title, artist, art, position, duration, playing }` (ou `null`).
     `status.sys` = `{ uptimeSec, cpuTemp, cpuTempSrc, net: { down, up }, disk: { read, write, busy }, drives: [{ name, label, freeGb, totalGb }] }`
     (rede/disco em bytes/s; `cpuTemp` vem do ajudante de sensores `Sensors.cs` (`cpuTempSrc: "ajudante"`, instalado por `scripts/instalar-sensores.ps1`),
     do LibreHardwareMonitor aberto ou da zona térmica do Windows (`"acpi"`, aproximado), ou `null` com `cpuTempHint` dizendo o que falta;
     com o ajudante vêm também `cpuPower` (W) e `cpuClockMhz`).
   - **Leve na GPU (importante):** na Navy, nada de animação CSS `infinite`, `backdrop-filter` ou `filter: blur` animado: qualquer
     coisa que nunca para faz o WebView desenhar 60 quadros/s pra sempre. Movimento contínuo vai no loop `fx()` do `navy.js`
     (poucos quadros/s, `deck.navy.animacao`, padrão 10; 0 = parado). Animação de entrada/clique que termina pode.
   - **Notícias:** `Bridge.call("news")` (ou `{ force: true }` pra buscar de novo) devolve
     `{ title, items: [{ title, source, url, published }], updated, error }`. Quem busca é o C# (os feeds não liberam CORS),
     com cache e atualização em segundo plano. Os feeds ficam na seção raiz opcional `"news"` do `deck.json`:
     `{ "title": "IA", "feeds": ["https://...rss"], "max": 8, "refreshMinutes": 20 }` (sem ela: Google News sobre IA em PT-BR).
   - **Modo jogo:** o C# acha o jogo aberto sozinho e manda `status.game` = `{ name, image, exe, known, since, sessionSec,
     todaySec, idle, fps, fpsLow, fpsAvg, fpsInfo }` (ou `null`). Na Navy ele troca as férias pela tela do jogo (FPS, sessão, temperaturas);
     no clássico existe o widget `"jogo"` e o `"page"` do jogo. Por jogo em `"games"`: `"process"`, `"macros"` (botões da tela do jogo),
     `"page"`, `"onStart"`/`"onStop"`; geral em `"gameMode"`. Tempo de jogo: `Bridge.call("playtime")` →
     `{ today, week: [{ date, label, today, total, games }], weekTotal, top, monthTotal, sessions, playing }` (segundos).
   - **Atualização:** `status.update` = `{ state, message, installed, latest, title }`; `Bridge.call("update")` instala
     (não chame sozinho: só com o Rodrigo pedindo).
   - **Modo descanso:** `wwwroot/scenes/rest/` desenha um céu/mar nos monitores que não são o painel (Ctrl+Alt+H,
     ou a ação `{ "type": "rest" }` só na Navy). Qualquer mouse/teclado acorda. Simulador: `scenes/rest/preview.html`.
4. **`scripts/`** — pra coisas que o JS e as ações não fazem (PowerShell `.ps1` é o padrão). Chame com
   `{ "type": "script", "file": "nome.ps1", "hidden": true }`.

## Regras

- Não rode o app, `dotnet`, nem scripts. Só edite arquivos.
- Só mexa no C# (`*.cs`) se for impossível fazer de outro jeito, e avise na resposta que precisa reiniciar o app.
- Não apague nem renomeie itens que o Rodrigo não pediu pra mudar.
- Mantenha o `deck.json` válido (vírgulas!). Um JSON quebrado faz o painel avisar e ignorar a mudança.
- Textos da interface em português do Brasil.
- Responda no fim com 1 ou 2 frases curtas dizendo o que fez (aparece num aviso no painel).
