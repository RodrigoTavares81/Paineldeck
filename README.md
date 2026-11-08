# PainelDeck 🎛️

Stream Deck caseiro: uma janela em tela cheia no monitor extra (Arzopa) que **não rouba o foco** ao clicar,
com interface em HTML/CSS/JS (animações e widgets à vontade) e ações rodando direto no Windows.

## Rodar

Pré-requisitos: **.NET 8 SDK** e o **WebView2 Runtime** (já vem no Windows 11).

```powershell
cd PainelDeck
dotnet run
```

O painel abre sozinho no monitor menor que não é o principal. Pra escolher outro: ícone na **bandeja** →
**Monitor do painel** (a escolha fica salva no `settings.json`). Pela bandeja também dá pra recarregar, abrir o DevTools e sair.

## Editar (a parte divertida)

**Tudo que o painel faz fica no `deck.json`** (na raiz do projeto): botões, páginas, widgets, atalhos, macros e scripts.
Salvou o arquivo, o painel atualiza na hora, sem reiniciar o app. Dá pra abrir pela bandeja → **Editar deck.json**.
Se o JSON tiver erro, aparece um aviso no painel com a linha e ele continua usando a última versão boa.

### Editor visual ✏️

Clique no **✏️** ao lado das abas. O painel entra em modo edição (só nesse modo ele aceita foco e teclado, pra você digitar):

- a tela é **livre**: **arraste** qualquer item pra onde quiser e use a **alça no canto** pra redimensionar
  (o 🧲 Snap gruda numa grade de 1% e nas bordas/centros dos outros itens, com linhas-guia; segure **Alt** pra soltar);
- com um item selecionado: **setas** movem 0,5% (**Shift** = 5%), **Delete** apaga, **Ctrl+D** duplica, **Ctrl+Z** desfaz;
- **clique num botão ou widget** (sem arrastar) pra editar ícone, texto, cor, posição e o que ele faz;
- **⏺ Gravar** captura o atalho que você apertar (atalhos com a tecla Windows, digite na mão);
- macro: monte os passos (ações e esperas), reordene com ↑ ↓, ou use uma **🔁 Macro salva** em vários botões;
- programa: **📂 Procurar…** abre o seletor de arquivos; script: escolha da pasta `scripts/`;
- **▶ Testar** roda a ação na hora; **＋ Item** (nasce no meio da tela), **＋ Página**, **🔁 Macros** e **🎮 Jogos** ficam na barra de cima.

### ✨ Pedir ao Claude

O **✨** do lado das abas (ou um botão com a ação "Pedir ao Claude") abre uma caixa: você escreve o que quer
("um widget de clima", "um botão que abre Discord e Spotify", qualquer loucura) e o **Claude Code instalado no PC**
edita os arquivos do PainelDeck. O painel atualiza sozinho. As regras que ele segue estão no `CLAUDE.md`.
Precisa do Claude Code instalado e logado uma vez (`claude` no terminal).

**Salvar** grava o `deck.json` e guarda o anterior em `deck.backup.json`. **Cancelar** descarta tudo.
O bloco de comentários do topo do `deck.json` é mantido, mas comentários no meio dos itens se perdem ao salvar pelo editor.

| Arquivo | O que tem |
|---|---|
| `deck.json` | botões, páginas, widgets, macros com nome e a lista de jogos (a lista dos tipos de ação está no topo) |
| `wwwroot/games.js` / `macros.js` | a roda de jogos e a gaveta de macros |
| `wwwroot/games/` | as capas dos jogos da roda |
| `wwwroot/widgets.js` | widgets: férias, contagem regressiva, relógio, CPU/RAM. Dá pra criar os seus com `defineWidget(...)` |
| `wwwroot/style.css` | o visual todo (salvou, recarrega) |
| `wwwroot/overlays.css` | visual do editor, do ✨, da roda de jogos e da gaveta (compartilhado com a Navy) |
| `wwwroot/scenes/` | cenas: `navy/` (painel alternativo) e `rest/` (modo descanso) |
| `wwwroot/editor.js` | o editor visual |

Macros que você usa em vários botões ficam na seção `"macros"` e são chamadas com `{ "type": "macro", "ref": "nome" }`.
Pra trocar de página, `"page"` aceita o número ou o nome da página.

**Layout livre:** página com `"layout": "free"` tem cada item com `"x"`, `"y"`, `"w"`, `"h"` em **% da tela** (0–100) e `"z"`
opcional (maior fica por cima). Página sem `"layout"` usa a grade antiga `"grid"` (`w`/`h` em células); o editor converte pra % ao salvar.

**🎮 Roda de jogos:** a lista `"games"` do `deck.json` (`{ "name", "image": "games/arquivo.jpg", "action" }`) aparece numa roda
animada num botão com `{ "type": "games" }`. **🔁 Gaveta de macros:** `{ "type": "macros" }` mostra todas as macros salvas como cards.

Pra mexer no visual sem o app: na pasta `PainelDeck` rode `python -m http.server` e abra
`http://localhost:8000/wwwroot/`. Ele entra em **modo preview**, lê o `deck.json` e só simula as ações (no editor, Salvar baixa o deck.json pra você substituir).

## Cenas: Clássico e Navy

Bandeja → **Cena do painel** → **Clássico** (o painel editável de sempre) ou **Navy** (tela pronta azul-marinho com contagem
das férias, telemetria com nome da GPU, microfone, "tocando agora" do Windows com capa e um dock de atalhos).
A escolha fica no `settings.json` (`"panelScene": "classic" | "navy"`, padrão `"classic"`). Na Navy, os botões
🎮 Jogos, 🔁 Macros e ✨ Claude abrem a mesma roda, gaveta e caixa do clássico. Arquivos: `wwwroot/scenes/navy/`.
Preview: `python -m http.server` na pasta `PainelDeck` e abrir `http://localhost:8000/wwwroot/scenes/navy/`.

## 🌙 Modo descanso

**Ctrl+Alt+H** (ou o botão **Descansar** da Navy) cobre todos os monitores que não são o painel com um céu noturno
e um mar animados, desenhados como um mundo só, no tamanho físico real de cada tela (lido do EDID): o cometa e as ondas
passam de um monitor pro outro sem pular. O monitor maior mostra a hora e quantos dias faltam pras férias.
As janelas não roubam o foco do jogo. **Qualquer mouse ou tecla acorda** (a luz volta pro painel e tudo fecha);
desplugar um monitor também fecha. O painel continua funcionando durante o descanso.
O atalho muda em `"hotkeys": { "rest": "ctrl+alt+h" }` no `settings.json`. Simulador: `wwwroot/scenes/rest/preview.html`.

## Ações disponíveis

`keys` (atalhos), `type` (digitar texto), `media`, `macro` (sequência com delays), `run` (abrir programa),
`url`, `script` (.ps1/.bat/.py/.js da pasta `scripts/`), `mic` (mute do mic padrão), `obs`, e as que só abrem
coisas no painel: `claude`, `games` (roda de jogos) e `macros` (gaveta de macros). Na cena Navy também tem `rest` (modo descanso).

## OBS

No OBS: **Ferramentas → Configurações do servidor WebSocket** → ativar (porta 4455). Se tiver senha,
coloque no `settings.json`. Os botões da página "OBS" usam nomes de cena de exemplo, então troque pelos seus no `deck.json`.

## Iniciar com o Windows

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

Depois crie um atalho de `publish\PainelDeck.exe` em `shell:startup`.
(Na versão publicada, edite o `publish\deck.json` e os arquivos em `publish\wwwroot`.)

## Se o jogo perder o foco

A janela usa `WS_EX_NOACTIVATE`, que é o mesmo truque do teclado virtual do Windows. Como plano B, antes de mandar
teclas o app devolve o foco pra última janela que estava ativa. Se algum jogo específico ainda reclamar, dá pra ajustar.
