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
das férias, capas dos jogos, notícias de IA, telemetria com temperatura da CPU e da GPU, e dados do PC: tempo ligado, rede, disco e espaço livre).
A escolha fica no `settings.json` (`"panelScene": "classic" | "navy"`, padrão `"classic"`). Na Navy, **roda** e
**Pedir ao Claude** abrem a mesma roda e caixa do clássico. A Navy é feita pra quase não usar a GPU parada (nada de
animação CSS infinita nem blur animado; o mar das férias anda a 10 quadros/s, `"navy": { "animacao": 0 }` deixa parado).
**Temperatura da CPU:** o Windows não dá a do sensor sem admin. Com o [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
aberto, o painel lê dele (sensor de verdade); sem ele, usa a zona térmica do Windows (marcado APROX), quando a placa-mãe tiver. Arquivos: `wwwroot/scenes/navy/`.
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

## 🎮 Modo jogo

Abriu um jogo, o painel percebe sozinho (a cada 2s, só lendo a lista de processos, sem mexer em janela nenhuma) e troca de cara:
na **Navy**, as férias viram o **FPS ao vivo** (com 1% low, média e gráfico do último minuto), a sessão e as temperaturas
de CPU e GPU, e a capa do jogo ganha o selo **JOGANDO**. No topo aparece **JOGANDO · 1h12**. Fechou o jogo, volta tudo.
No clássico tem o widget `{ "widget": "jogo" }` e o `"page"` do jogo (abre aquela página junto com o jogo).

Como ele reconhece: pelo `"process"` do jogo (`"ForHonor.exe"`), pelo pacote da Store/Xbox (o `shell:AppsFolder\...` da ação)
ou pela pasta da biblioteca (Steam, Ubisoft, Epic, Xbox, GOG, Riot, EA, Battle.net) parecida com o nome do jogo. Jogos fora da lista
também contam, com o nome da pasta (`"detectUnknown": false` desliga). Configuração em `"gameMode"` e em cada jogo de `"games"`
(veja o topo do `deck.json`): `macros`, `page`, `onStart`/`onStop` (ex.: mutar o Discord ao abrir).

**FPS:** vem do [PresentMon](https://github.com/GameTechDev/PresentMon) (Intel, open source), que lê os eventos de "present" do
próprio Windows, sem injetar nada no jogo (tranquilo com anti-cheat). Pra ligar, rode **uma vez** `scripts\ativar-fps.ps1`
(botão direito → Executar com o PowerShell): ele baixa o PresentMon pra `tools\` e põe seu usuário no grupo
"Usuários de log de desempenho" do Windows (pede o UAC). Depois saia e entre no Windows. Sem isso, o modo jogo funciona igual, só sem FPS.

**Tempo de jogo:** fica em `playtime.json` (só no seu PC). Conta só enquanto o jogo está aberto **e** você está mexendo:
parado mais de `"idleMinutes"` (padrão 10) não conta. (A Navy não mostra mais o histórico; ele continua em `Bridge.call("playtime")`.)

## Iniciar com o Windows, reinício e atualização

- **Iniciar com o Windows:** bandeja → **Iniciar com o Windows** (fica marcado; desmarca pra desligar).
- **Se der erro grave**, o app grava em `logs\crash.log` e abre de novo sozinho (no máximo 3 vezes em 10 minutos).
  Se a parte do navegador travar, a página recarrega sozinha. Bandeja → **Reiniciar** fecha e abre.
- **Atualizar:** o app confere no GitHub a cada 6h. Quando tem versão nova, aparece **Atualizar · abc1234** no topo da Navy
  (toque duas vezes) e na bandeja. Ele baixa, compila (precisa do .NET 8 SDK) e só fecha o painel se a compilação deu certo;
  aí instala e abre de novo, tudo escondido. **Seus arquivos ficam:** `deck.json`, `settings.json`, `playtime.json` e as capas
  nunca são trocados, e o que você (ou o ✨ Claude) mudou no `wwwroot` e no `scripts` é mantido (se a versão nova também mudou o
  mesmo arquivo, ela fica do lado como `.novo`). Backup das 5 últimas versões em `.atualizacao\backups`.
  Rodando pelo código-fonte (`dotnet run`), use `git pull`.

Pra montar a pasta do app na mão:

```powershell
dotnet publish -c Release -o C:\Users\voce\PainelDeck\app
```

## Se o jogo perder o foco

A janela usa `WS_EX_NOACTIVATE`, que é o mesmo truque do teclado virtual do Windows. Como plano B, antes de mandar
teclas o app devolve o foco pra última janela que estava ativa. Se algum jogo específico ainda reclamar, dá pra ajustar.
