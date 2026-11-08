using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PainelDeck;

/// <summary>
/// Modo descanso: cada monitor que NÃO é o painel ganha uma <see cref="RestForm"/> com wwwroot/scenes/rest.
/// Todas recebem o mesmo "start" e o layout físico (mm do EDID) pra desenhar um mundo contínuo.
/// Qualquer mouse/teclado depois de start+800ms acorda: anima a saída e fecha. O painel segue normal.
/// </summary>
internal sealed class RestMode
{
    private const int GraceMs = 800, ExitTimeoutMs = 1500;
    private readonly CoreWebView2Environment env;
    private readonly Settings settings;
    private readonly Func<string> subtitle;
    private readonly List<RestForm> forms = new();
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer exitTimeout = new() { Interval = ExitTimeoutMs };
    private object? init;     // mensagem rest-init (sem o "mon", que é por janela)
    private int startTick;    // Environment.TickCount no início (mesma base do GetLastInputInfo)
    private bool exiting;

    public bool Active => forms.Count > 0;

    public RestMode(CoreWebView2Environment env, Settings settings, Func<string> subtitle)
    {
        (this.env, this.settings, this.subtitle) = (env, settings, subtitle);
        poll.Tick += (_, _) => { if (!exiting && unchecked(Environment.TickCount - startTick) - Native.IdleMs() > GraceMs) Exit(); };
        exitTimeout.Tick += (_, _) => CloseNow();
    }

    public void Start()
    {
        if (Active) return;
        var panel = settings.PickScreen();
        var mons = DisplayConfig.Active();
        var screens = Screen.AllScreens;
        if (!screens.Any(s => s.DeviceName != panel?.DeviceName)) return; // só tem o painel: nada pra descansar

        startTick = Environment.TickCount;
        exiting = false;
        var layout = new
        {
            gapMm = 18,
            monitors = screens.Select(s =>
            {
                var info = mons.FirstOrDefault(m => m.GdiName == s.DeviceName);
                var (w, h) = PhysicalMm(info?.Path, s.Bounds);
                return new
                {
                    id = s.DeviceName,
                    name = info?.Name is { Length: > 0 } n ? n : s.DeviceName,
                    px = new { x = s.Bounds.X, y = s.Bounds.Y, w = s.Bounds.Width, h = s.Bounds.Height },
                    mm = new { w, h },
                    panel = s.DeviceName == panel?.DeviceName,
                };
            }).ToArray(),
        };
        init = new { layout, start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), subtitle = subtitle() };

        foreach (var s in screens.Where(s => s.DeviceName != panel?.DeviceName))
        {
            var mon = s.DeviceName;
            var f = new RestForm(s.Bounds, env, settings.WebRoot);
            f.Message += (form, type) => OnMessage(form, mon, type);
            forms.Add(f);
            f.Show();
        }
        poll.Start();
    }

    private void OnMessage(RestForm f, string mon, string? type)
    {
        if (type == "rest-ready" && init != null)
        {
            // junta o "mon" desta janela na mensagem comum
            var node = JsonSerializer.SerializeToNode(init)!.AsObject();
            node["type"] = "rest-init";
            node["mon"] = mon;
            f.Post(node.ToJsonString());
        }
        else if (type == "rest-exited")
        {
            f.Exited = true;
            if (forms.All(x => x.Exited)) CloseNow();
        }
    }

    /// <summary>Acordou: animação de saída em todas e fecha quando todas responderem (ou depois de 1,5s).</summary>
    public void Exit()
    {
        if (!Active || exiting) return;
        exiting = true;
        poll.Stop();
        foreach (var f in forms) f.Post("{\"type\":\"rest-exit\"}");
        exitTimeout.Start();
    }

    /// <summary>Fecha na hora (sem animação): fim da saída, ou um monitor foi plugado/desplugado.</summary>
    public void CloseNow()
    {
        poll.Stop();
        exitTimeout.Stop();
        foreach (var f in forms) f.Close();
        forms.Clear();
        init = null;
        exiting = false;
    }

    /// <summary>
    /// Tamanho físico (mm) do EDID no registro: o "detailed timing descriptor" (bytes 66–68, em mm);
    /// se não tiver, os bytes 21/22 (em cm). Sem EDID: chuta 0,27 mm por pixel.
    /// </summary>
    public static (int w, int h) PhysicalMm(string? devicePath, Rectangle px)
    {
        (int w, int h) mm = (0, 0);
        try
        {
            // \\?\DISPLAY#GWD1161#5&1a2b&0&UID4352#{guid} → Enum\DISPLAY\GWD1161\5&1a2b&0&UID4352
            var parts = devicePath?.Split('#');
            if (parts is { Length: >= 3 })
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
                if (key?.GetValue("EDID") is byte[] e) mm = EdidMm(e);
            }
        }
        catch { /* sem acesso ao registro: usa o chute */ }
        if (mm.w <= 0 || mm.h <= 0) mm = ((int)(px.Width * 0.27), (int)(px.Height * 0.27));
        // monitor em pé: o EDID sempre fala deitado
        if ((px.Height > px.Width) != (mm.h > mm.w)) mm = (mm.h, mm.w);
        return mm;
    }

    public static (int w, int h) EdidMm(byte[] e)
    {
        if (e.Length < 128) return (0, 0);
        if (e[54] != 0 || e[55] != 0) // 1º descriptor é um timing (pixel clock ≠ 0)
        {
            int w = e[66] | ((e[68] & 0xF0) << 4), h = e[67] | ((e[68] & 0x0F) << 8);
            if (w > 0 && h > 0) return (w, h);
        }
        return (e[21] * 10, e[22] * 10);
    }
}

/// <summary>Janela sem borda, por cima de tudo, que nunca ativa (o jogo continua com o foco). O cursor some pelo CSS da cena.</summary>
internal sealed class RestForm : Form
{
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    public event Action<RestForm, string?>? Message;
    public bool Exited { get; set; }

    public RestForm(Rectangle bounds, CoreWebView2Environment env, string webRoot)
    {
        Text = "PainelDeck · descanso";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Controls.Add(web);
        Load += async (_, _) =>
        {
            try
            {
                await web.EnsureCoreWebView2Async(env); // mesmo ambiente do painel: sobe rápido
                var cw = web.CoreWebView2;
                cw.SetVirtualHostNameToFolderMapping("painel.local", webRoot, CoreWebView2HostResourceAccessKind.Allow);
                cw.Settings.AreDefaultContextMenusEnabled = false;
                cw.Settings.IsStatusBarEnabled = false;
                cw.WebMessageReceived += (_, e) =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                        Message?.Invoke(this, doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null);
                    }
                    catch (JsonException) { }
                };
                cw.Navigate("https://painel.local/scenes/rest/index.html");
            }
            catch { Exited = true; } // sem WebView: fica preta até acordar
        };
    }

    public void Post(string json) { if (!IsDisposed) web.CoreWebView2?.PostWebMessageAsJson(json); }

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
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }
}
