using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PainelDeck;

/// <summary>
/// Janela em tela cheia no monitor do painel. Não ativa ao clicar (WS_EX_NOACTIVATE),
/// então o jogo/app que você estava usando continua com o foco.
/// </summary>
public sealed class PanelForm : Form
{
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    private readonly Settings settings;
    private readonly Deck deck;
    private readonly ActionRunner runner;
    private readonly Stats stats = new();
    private readonly SysInfo sys = new();
    private readonly Mic mic = new();
    private readonly Media media = new();
    private readonly News news = new();
    private readonly Playtime playtime;
    private readonly Fps fps;
    private readonly GameMode game;
    private readonly Updater updater;
    private RestMode? rest; // criado junto com o WebView (usa o mesmo ambiente)
    private readonly NotifyIcon tray = new();
    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer focusTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer reloadDebounce = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer deckDebounce = new() { Interval = 250 };
    // plugar/desplugar/ligar o monitor dispara vários eventos seguidos: espera acalmar antes de reposicionar
    private readonly System.Windows.Forms.Timer screenDebounce = new() { Interval = 1000 };
    private readonly HotKeys hotkeys;
    private bool waitingForMonitor; // o monitor do painel não está ligado: janela escondida
    private bool userHidden;        // escondido pelo "Mostrar / esconder" da bandeja
    private FileSystemWatcher? watcher;
    private FileSystemWatcher? deckWatcher;
    private IntPtr lastExternalWindow;
    private bool editMode;
    private bool quitting; // fechando pra atualizar/reiniciar
    private readonly uint myPid = (uint)Environment.ProcessId;

    internal PanelForm(Settings settings)
    {
        this.settings = settings;
        deck = new Deck(settings.Root);
        deck.Reload();
        runner = new ActionRunner(settings, deck, mic, EnsureTargetFocused);
        playtime = new Playtime(settings.Root);
        fps = new Fps(settings);
        game = new GameMode(deck, playtime, fps, a => runner.RunAsync(a));
        updater = new Updater(settings, () => { quitting = true; Close(); });
        Crash.BeforeExit += () => { sys.Dispose(); fps.Dispose(); playtime.Dispose(); tray.Visible = false; };

        Text = "PainelDeck";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Controls.Add(web);
        SetupTray();
        if (updater.State == "done") tray.ShowBalloonTip(6000, "PainelDeck", updater.Message ?? "Atualizado", ToolTipIcon.Info);
        ApplyScreen(allowShow: false); // antes de aparecer (quem mostra é o Application.Run): sem o monitor, nem aparece

        screenDebounce.Tick += (_, _) => { screenDebounce.Stop(); ApplyScreen(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        hotkeys = new HotKeys(settings.Hotkeys);
        hotkeys.Pressed += name =>
        {
            if (name == "rest") rest?.Start();
            Post(new { type = "hotkey", name });
        };
        if (hotkeys.Failed.Count > 0)
            tray.ShowBalloonTip(5000, "PainelDeck", "Atalho já usado por outro programa: " + string.Join(", ", hotkeys.Failed), ToolTipIcon.Warning);

        focusTimer.Tick += (_, _) => TrackForeground();
        focusTimer.Start();
        statusTimer.Tick += (_, _) => PushStatus();
        reloadDebounce.Tick += async (_, _) => { reloadDebounce.Stop(); await ReloadAsync(); };
        deckDebounce.Tick += (_, _) => { deckDebounce.Stop(); deck.Reload(); PushDeck(); };

        Load += async (_, _) => await InitWebAsync();
        FormClosed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; // evento estático: soltar senão vaza
            hotkeys.Dispose();
            rest?.CloseNow(); media.Dispose();
            game.Dispose(); fps.Dispose(); playtime.Dispose(); updater.Dispose();
            tray.Visible = false; tray.Dispose(); watcher?.Dispose(); deckWatcher?.Dispose();
        };
    }

    // ---------- monitor do painel ----------
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => BeginInvoke(() =>
    {
        rest?.CloseNow(); // monitor plugou/desplugou: o layout do descanso não vale mais
        BumpScreen();
    });
    private void BumpScreen() { screenDebounce.Stop(); screenDebounce.Start(); }

    /// <summary>
    /// Põe o painel no monitor dele (Bounds certos pra resolução/escala atuais). Se o monitor não estiver
    /// ligado, esconde e espera; NUNCA cai em outro monitor (ficaria por cima do jogo).
    /// </summary>
    private void ApplyScreen(bool allowShow = true)
    {
        var s = settings.PickScreen();
        if (s == null)
        {
            waitingForMonitor = true;
            SetTrayText($"PainelDeck esperando o {MonitorLabel(settings.PanelMonitor)}");
            if (Visible) Hide();
            return;
        }
        waitingForMonitor = false;
        SetTrayText("PainelDeck");
        if (Bounds != s.Bounds) Bounds = s.Bounds;
        if (allowShow && !Visible && !userHidden) Show(); // ShowWithoutActivation: volta sem roubar o foco
    }

    // 1ª vez que o Windows tenta mostrar a janela (Application.Run): segura se estiver esperando o monitor
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && !waitingForMonitor && !userHidden);

    private static string MonitorLabel(MonitorInfo? m) =>
        m == null ? "monitor do painel" : m.Name != "" ? m.Name : m.Edid != "" ? m.Edid : "monitor do painel";

    private void SetTrayText(string text) => tray.Text = text.Length > 63 ? text[..63] : text; // limite do Windows

    // ---------- não roubar foco ----------
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE && !editMode)
        {
            m.Result = (IntPtr)Native.MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
        // resolução/escala mudou: o Windows pode ter redimensionado a janela; reaplica os Bounds do monitor
        if (m.Msg is Native.WM_DISPLAYCHANGE or Native.WM_DPICHANGED) BumpScreen();
    }

    /// <summary>
    /// Modo edição: o painel PRECISA de foco pra você digitar nos campos e gravar atalhos.
    /// Liga = tira o WS_EX_NOACTIVATE e ativa a janela. Desliga = volta ao normal e devolve o foco pro jogo/app.
    /// </summary>
    private void SetEditMode(bool on)
    {
        if (editMode == on) return;
        editMode = on;
        var ex = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
        ex = on ? ex & ~Native.WS_EX_NOACTIVATE : ex | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, (IntPtr)ex);
        if (on)
        {
            Native.SetForegroundWindow(Handle);
            Activate();
            web.Focus();
        }
        else if (lastExternalWindow != IntPtr.Zero)
        {
            Native.SetForegroundWindow(lastExternalWindow);
        }
    }

    /// <summary>Guarda qual foi a última janela "de verdade" em foco (o jogo, o VS Code...).</summary>
    private void TrackForeground()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid != myPid) lastExternalWindow = h;
    }

    /// <summary>Plano B: se mesmo assim o painel pegou o foco, devolve pra janela anterior antes de mandar teclas.</summary>
    private void EnsureTargetFocused()
    {
        var h = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid == myPid && lastExternalWindow != IntPtr.Zero)
        {
            Native.SetForegroundWindow(lastExternalWindow);
            Thread.Sleep(50);
        }
    }

    // ---------- WebView ----------
    private async Task InitWebAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PainelDeck", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await web.EnsureCoreWebView2Async(env);
            rest = new RestMode(env, settings, RestSubtitle);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não consegui iniciar o WebView2. Instale o \"WebView2 Runtime\" da Microsoft.\n\n" + ex.Message, "PainelDeck");
            Close();
            return;
        }

        var cw = web.CoreWebView2;
        cw.SetVirtualHostNameToFolderMapping("painel.local", settings.WebRoot, CoreWebView2HostResourceAccessKind.Allow);
        cw.Settings.IsZoomControlEnabled = false;
        cw.Settings.IsStatusBarEnabled = false;
        cw.Settings.AreDefaultContextMenusEnabled = false;
        cw.WebMessageReceived += (_, e) => _ = HandleMessageAsync(e.WebMessageAsJson);
        cw.ProcessFailed += (_, e) => BeginInvoke(() => OnWebViewFailed(e.ProcessFailedKind));
        cw.Navigate(settings.PanelUrl);

        statusTimer.Start();
        StartWatcher();
    }

    /// <summary>A parte do navegador caiu: página travada/fechada recarrega; o navegador inteiro caído reinicia o app.</summary>
    private void OnWebViewFailed(CoreWebView2ProcessFailedKind kind)
    {
        Crash.Log($"WebView2 falhou: {kind}", null);
        if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited) Restart();
        else if (kind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                 or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
            try { web.CoreWebView2?.Reload(); } catch { Restart(); }
    }

    /// <summary>Fecha e abre de novo (a instância nova espera esta sair).</summary>
    private void Restart()
    {
        if (quitting) return;
        if (!Crash.AllowRestart()) return;
        quitting = true;
        Crash.Relaunch();
        Close();
    }

    private async Task HandleMessageAsync(string json)
    {
        int id = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "ready")
            {
                SetEditMode(false); // a página recarregou (ex.: o Claude mexeu no widgets.js): sai do modo edição
                PushDeck(); PushStatus();
                return;
            }
            if (type == "call")
            {
                id = root.GetProperty("id").GetInt32();
                var method = root.GetProperty("method").GetString();
                var args = root.TryGetProperty("args", out var ar) ? ar.Clone() : default;
                if (method == "askClaude") // demora (minutos): roda fora da thread da janela
                {
                    var answer = await ClaudeAgent.AskAsync(settings.Root,
                        args.GetProperty("request").GetString() ?? "",
                        args.TryGetProperty("page", out var pg) ? pg.GetString() ?? "" : "");
                    Post(new { type = "result", id, ok = true, result = answer });
                    return;
                }
                if (method == "news") // rede: só espera se ainda não tiver nada em cache
                {
                    var force = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;
                    Post(new { type = "result", id, ok = true, result = await news.GetAsync(deck.Snapshot(), force) });
                    return;
                }
                Post(new { type = "result", id, ok = true, result = HandleCall(method, args) });
                return;
            }
            if (type != "action") return;

            id = root.GetProperty("id").GetInt32();
            var action = root.GetProperty("action").Clone();
            var result = await Task.Run(() => runner.RunAsync(action));
            Post(new { type = "result", id, ok = true, result });
            if (editMode) { Native.SetForegroundWindow(Handle); web.Focus(); } // testou um atalho no editor: volta o foco pro painel
        }
        catch (Exception ex)
        {
            Post(new { type = "result", id, ok = false, error = ex.Message });
        }
    }

    /// <summary>Chamadas do editor visual (rodam na thread da janela).</summary>
    private object? HandleCall(string? method, JsonElement args)
    {
        switch (method)
        {
            case "editMode":
                SetEditMode(args.GetProperty("on").GetBoolean());
                return null;

            case "saveDeck":
                var node = System.Text.Json.Nodes.JsonNode.Parse(args.GetProperty("deck").GetRawText())
                           ?? throw new InvalidDataException("Deck vazio");
                deck.Save(node);
                return null;

            case "listScripts":
                if (!Directory.Exists(settings.ScriptsDir)) return Array.Empty<string>();
                var exts = new[] { ".ps1", ".bat", ".cmd", ".py", ".js", ".exe" };
                return Directory.GetFiles(settings.ScriptsDir)
                    .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Select(Path.GetFileName).OrderBy(f => f).ToArray();

            case "rest":
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True) rest?.Start();
                else rest?.Exit();
                return null;

            case "playtime":
                return playtime.Summary();

            case "update": // { check: true } confere agora; sem nada, começa a atualização
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("check", out var chk) && chk.ValueKind == JsonValueKind.True)
                    _ = updater.CheckAsync();
                else
                    updater.Start();
                return updater.Status();

            case "autostart": // { on: true|false } liga/desliga; sem nada, só diz como está
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("on", out var au) && au.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    Autostart.Set(au.GetBoolean());
                return Autostart.Enabled;

            case "pickFile":
                using (var dlg = new OpenFileDialog
                {
                    Title = "Escolha o programa ou arquivo",
                    Filter = "Programas (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|Todos os arquivos (*.*)|*.*",
                })
                {
                    return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
                }

            default:
                throw new InvalidOperationException($"Chamada desconhecida: '{method}'");
        }
    }

    private void PushStatus()
    {
        var ram = stats.Ram();
        var gpu = stats.Gpu();
        Post(new
        {
            type = "status",
            cpu = stats.Cpu(),
            ram = ram.Percent,
            ramUsedGb = ram.UsedGb,
            ramTotalGb = ram.TotalGb,
            gpu = gpu?.Usage,
            vram = gpu?.VramPercent,
            vramUsedGb = gpu?.VramUsedGb,
            vramTotalGb = gpu?.VramTotalGb,
            gpuTemp = gpu?.TempC,
            gpuName = gpu?.Name,
            sys = sys.Read(), // { uptimeSec, cpuTemp, cpuTempSrc, net, disk, drives }
            media = media.Current is { } m
                ? new { title = m.Title, artist = m.Artist, art = m.Art, position = m.Position, duration = m.Duration, playing = m.Playing }
                : null,
            micMuted = mic.IsMuted(),
            obs = runner.ObsConnected,
            game = game.Status(),
            update = updater.Status(),
        });
    }

    /// <summary>"faltam N dias pras férias" pelo widget "ferias" do deck (ou deck.navy.ferias); vazio se já passou ou não tem.</summary>
    private string RestSubtitle()
    {
        var d = deck.Snapshot();
        var f = d["navy"]?["ferias"] ?? (d["pages"] as System.Text.Json.Nodes.JsonArray)?
            .SelectMany(p => p?["items"] as System.Text.Json.Nodes.JsonArray ?? new())
            .FirstOrDefault(it => (string?)it?["widget"] == "ferias");
        if (f?["inicio"] is not System.Text.Json.Nodes.JsonValue iv || !iv.TryGetValue<string>(out var inicio)
            || !DateTime.TryParse(inicio, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start)) return "";
        var days = (start.Date - DateTime.Today).Days;
        return days > 1 ? $"faltam {days} dias pras férias" : days == 1 ? "falta 1 dia pras férias" : "";
    }

    /// <summary>Manda o deck atual pro painel (e o erro, se o deck.json estiver quebrado).</summary>
    private void PushDeck()
    {
        if (web.CoreWebView2 == null) return;
        var msg = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "deck",
            ["deck"] = deck.Snapshot(),
            ["error"] = deck.Error,
        };
        web.CoreWebView2.PostWebMessageAsJson(msg.ToJsonString());
    }

    private void Post(object message)
    {
        if (web.CoreWebView2 == null) return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private async Task ReloadAsync()
    {
        if (web.CoreWebView2 == null) return;
        await web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
        web.CoreWebView2.Reload();
    }

    /// <summary>Salvou algum arquivo do wwwroot → recarrega o painel sozinho.</summary>
    private void StartWatcher()
    {
        watcher = new FileSystemWatcher(settings.WebRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        void Bump() => BeginInvoke(new Action(() => { reloadDebounce.Stop(); reloadDebounce.Start(); }));
        watcher.Changed += (_, _) => Bump();
        watcher.Created += (_, _) => Bump();
        watcher.Deleted += (_, _) => Bump();
        watcher.Renamed += (_, _) => Bump();
        watcher.EnableRaisingEvents = true;

        // deck.json mudou → só manda o deck novo, sem recarregar a página
        deckWatcher = new FileSystemWatcher(Path.GetDirectoryName(deck.FilePath)!, Path.GetFileName(deck.FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        void BumpDeck() => BeginInvoke(new Action(() => { deckDebounce.Stop(); deckDebounce.Start(); }));
        deckWatcher.Changed += (_, _) => BumpDeck();
        deckWatcher.Created += (_, _) => BumpDeck();
        deckWatcher.Renamed += (_, _) => BumpDeck();
        deckWatcher.EnableRaisingEvents = true;
    }

    // ---------- bandeja / monitor ----------

    private void SetupTray()
    {
        tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        Icon = tray.Icon;
        tray.Text = "PainelDeck";
        tray.Visible = true;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Recarregar", null, async (_, _) => await ReloadAsync());
        menu.Items.Add("Editar deck.json", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(deck.FilePath) { UseShellExecute = true }); }
            catch { System.Diagnostics.Process.Start("notepad.exe", $"\"{deck.FilePath}\""); } // .json sem programa associado
        });
        menu.Items.Add("DevTools", null, (_, _) => web.CoreWebView2?.OpenDevToolsWindow());

        var monitors = new ToolStripMenuItem("Monitor do painel");
        monitors.DropDownItems.Add("..."); // preenchido ao abrir
        menu.Items.Add(monitors);
        menu.Opening += (_, _) =>
        {
            monitors.DropDownItems.Clear();
            var mons = DisplayConfig.Active();
            var want = settings.PanelMonitor;
            foreach (var s in Screen.AllScreens)
            {
                var info = mons.FirstOrDefault(x => x.GdiName == s.DeviceName);
                var item = new ToolStripMenuItem($"{MonitorLabel(info)} — {s.Bounds.Width}x{s.Bounds.Height}{(s.Primary ? " (principal)" : "")}")
                {
                    Checked = info != null && want != null && (info.Path == want.Path || info.Edid == want.Edid),
                    Enabled = info != null,
                };
                item.Click += (_, _) => { if (info != null) { settings.SavePanelMonitor(info); ApplyScreen(); } };
                monitors.DropDownItems.Add(item);
            }
            if (waitingForMonitor)
                monitors.DropDownItems.Add(new ToolStripMenuItem($"(esperando o {MonitorLabel(want)} voltar)") { Enabled = false });
        };

        var scenes = new ToolStripMenuItem("Cena do painel");
        foreach (var (label, name) in new[] { ("Clássico", "classic"), ("Navy", "navy") })
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) =>
            {
                if (!Settings.IsScene(settings.WebRoot, name)) { tray.ShowBalloonTip(4000, "PainelDeck", $"A cena \"{name}\" não existe em wwwroot/scenes.", ToolTipIcon.Warning); return; }
                settings.SavePanelScene(name);
                SetEditMode(false);
                web.CoreWebView2?.Navigate(settings.PanelUrl);
            };
            scenes.DropDownItems.Add(item);
        }
        scenes.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem it in scenes.DropDownItems) it.Checked = it.Text == (settings.PanelScene == "navy" ? "Navy" : "Clássico");
        };
        menu.Items.Add(scenes);

        menu.Items.Add("Mostrar / esconder", null, (_, _) =>
        {
            userHidden = !userHidden;
            if (userHidden) Hide(); else ApplyScreen();
        });
        menu.Items.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem("Iniciar com o Windows") { CheckOnClick = false };
        autostart.Click += (_, _) =>
        {
            try { Autostart.Set(!Autostart.Enabled); }
            catch (Exception ex) { tray.ShowBalloonTip(4000, "PainelDeck", "Não consegui mudar: " + ex.Message, ToolTipIcon.Warning); }
        };
        menu.Items.Add(autostart);
        var update = new ToolStripMenuItem("Procurar atualização");
        update.Click += async (_, _) =>
        {
            try
            {
                if (updater.State == "available") { updater.Start(); return; }
                await updater.CheckAsync();
                tray.ShowBalloonTip(4000, "PainelDeck", updater.State == "available"
                    ? $"Tem versão nova: {updater.LatestTitle}. Clique em \"Atualizar agora\" na bandeja ou no painel."
                    : updater.Message ?? "", ToolTipIcon.Info);
            }
            catch (Exception ex) { tray.ShowBalloonTip(5000, "PainelDeck", ex.Message, ToolTipIcon.Warning); }
        };
        menu.Items.Add(update);
        menu.Items.Add("Reiniciar", null, (_, _) => { Crash.Relaunch(); quitting = true; Close(); });
        menu.Opening += (_, _) =>
        {
            autostart.Checked = Autostart.Enabled;
            update.Enabled = updater.State is not ("working" or "dev");
            update.Text = updater.State switch
            {
                "available" => $"Atualizar agora ({updater.Latest?[..7]})",
                "working" => "Atualizando…",
                "dev" => "Atualização: use git pull (código-fonte)",
                _ => "Procurar atualização",
            };
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
    }
}
