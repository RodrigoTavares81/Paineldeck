using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Media.SpeechRecognition;

namespace PainelDeck;

/// <summary>
/// Áudio pras sessões do Claude: grava o microfone padrão (só enquanto você está gravando) e vira texto.
///  1. Whisper local (whisper.cpp), se existir: melhor e 100% no PC. Instale com scripts\instalar-whisper.ps1
///     (fica em &lt;PainelDeck&gt;\whisper) ou aponte no deck.json: "claude": { "whisper": { "exe": "...", "model": "...", "language": "pt" } }.
///  2. Sem Whisper: ditado do Windows (precisa do "Reconhecimento de fala online" ligado em Privacidade → Fala).
/// </summary>
internal sealed class Voice : IDisposable
{
    private const int MaxSeconds = 180;
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    private readonly string root;
    private readonly object gate = new();
    private WasapiCapture? capture;
    private List<float> samples = new();
    private int rate, channels;
    private float level;
    private DateTimeOffset started;
    private SpeechRecognizer? dictation;
    private readonly StringBuilder dictated = new();
    private string engine = "";

    public Voice(string root) => this.root = root;

    public bool Recording { get { lock (gate) return capture != null; } }

    public async Task<object> StartAsync(JsonNode deck)
    {
        lock (gate) if (capture != null) return State();
        var w = FindWhisper(deck);
        engine = w != null ? "whisper" : "windows";

        using var en = new MMDeviceEnumerator();
        MMDevice dev;
        try { dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
        catch { throw new InvalidOperationException("Não achei microfone no PC."); }
        var cap = new WasapiCapture(dev, true, 50);
        var fmt = cap.WaveFormat;
        lock (gate)
        {
            samples = new List<float>(16000 * 30);
            rate = fmt.SampleRate; channels = fmt.Channels; level = 0;
            started = DateTimeOffset.Now;
            capture = cap;
        }
        cap.DataAvailable += (_, e) => OnData(fmt, e.Buffer, e.BytesRecorded);
        cap.StartRecording();

        if (engine == "windows")
        {
            dictated.Clear();
            try { await StartDictationAsync(); }
            catch (Exception ex)
            {
                Cancel();
                throw new InvalidOperationException(DictationError(ex));
            }
        }
        return State();
    }

    public object State()
    {
        lock (gate) return new
        {
            recording = capture != null,
            seconds = capture != null ? (DateTimeOffset.Now - started).TotalSeconds : 0,
            level = Math.Round(level, 3),
            engine,
        };
    }

    public void Cancel()
    {
        StopCapture();
        var d = dictation; dictation = null;
        if (d != null) _ = Task.Run(async () => { try { await d.ContinuousRecognitionSession.CancelAsync(); } catch { } d.Dispose(); });
    }

    /// <summary>Para de gravar e devolve o texto.</summary>
    public async Task<object> StopAsync(JsonNode deck)
    {
        float[] pcm; int r, ch;
        lock (gate)
        {
            if (capture == null) throw new InvalidOperationException("Não estava gravando.");
            pcm = samples.ToArray(); r = rate; ch = channels;
        }
        StopCapture();
        var seconds = pcm.Length / (double)Math.Max(1, r);
        string text;
        if (engine == "windows")
        {
            var d = dictation; dictation = null;
            try { if (d != null) await d.ContinuousRecognitionSession.StopAsync(); } catch { /* já parou */ }
            await Task.Delay(300);
            d?.Dispose();
            lock (dictated) text = dictated.ToString().Trim();
        }
        else
        {
            if (seconds < 0.4) return new { text = "", seconds };
            var w = FindWhisper(deck) ?? throw new InvalidOperationException("O Whisper sumiu da pasta.");
            text = await TranscribeAsync(w, Resample16k(pcm, r));
        }
        return new { text, seconds = Math.Round(seconds, 1), engine };
    }

    private void StopCapture()
    {
        WasapiCapture? cap;
        lock (gate) { cap = capture; capture = null; }
        if (cap == null) return;
        try { cap.StopRecording(); } catch { }
        cap.Dispose();
    }

    public void Dispose() => Cancel();

    // ---------- gravação ----------

    private void OnData(WaveFormat fmt, byte[] buf, int bytes)
    {
        var bits = fmt.BitsPerSample;
        var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                      || (fmt.Encoding == WaveFormatEncoding.Extensible && bits == 32 && fmt is WaveFormatExtensible ext
                          && ext.SubFormat == FloatSubtype);
        var bps = bits / 8;
        var frame = bps * fmt.Channels;
        var n = bytes / frame;
        float peak = 0;
        lock (gate)
        {
            if (capture == null) return;
            if (samples.Count / (double)rate > MaxSeconds) return; // limite: 3 min
            for (var i = 0; i < n; i++)
            {
                float sum = 0;
                for (var c = 0; c < fmt.Channels; c++)
                {
                    var o = i * frame + c * bps;
                    sum += isFloat ? BitConverter.ToSingle(buf, o)
                         : bits == 16 ? BitConverter.ToInt16(buf, o) / 32768f
                         : bits == 24 ? ((buf[o] | (buf[o + 1] << 8) | (sbyte)buf[o + 2] << 16)) / 8388608f
                         : BitConverter.ToInt32(buf, o) / 2147483648f;
                }
                var s = sum / fmt.Channels;
                samples.Add(s);
                var a = Math.Abs(s);
                if (a > peak) peak = a;
            }
            level = Math.Max(peak, level * 0.6f);
        }
    }

    private static short[] Resample16k(float[] src, int srcRate)
    {
        const int dst = 16000;
        var len = (int)((long)src.Length * dst / Math.Max(1, srcRate));
        var outp = new short[len];
        var step = srcRate / (double)dst;
        for (var i = 0; i < len; i++)
        {
            // média da janela (filtro simples contra serrilhado) em vez de pegar 1 amostra só
            var a = (int)(i * step); var b = Math.Min(src.Length, Math.Max(a + 1, (int)((i + 1) * step)));
            float sum = 0; for (var k = a; k < b; k++) sum += src[k];
            var v = sum / Math.Max(1, b - a);
            outp[i] = (short)Math.Clamp(v * 32767f, short.MinValue, short.MaxValue);
        }
        return outp;
    }

    private static void WriteWav(string path, short[] pcm)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int dataLen = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in pcm) w.Write(s);
    }

    // ---------- Whisper (whisper.cpp) ----------

    private sealed record Whisper(string Exe, string Model, string Language);

    private Whisper? FindWhisper(JsonNode deck)
    {
        var n = deck["claude"]?["whisper"] as JsonObject;
        string? Str(string k) => n?[k] is JsonValue v && v.TryGetValue<string>(out var s) && s != "" ? Environment.ExpandEnvironmentVariables(s) : null;
        var lang = Str("language") ?? "pt";
        var exe = Str("exe");
        var model = Str("model");
        var dir = Path.Combine(root, "whisper");
        if (exe == null && Directory.Exists(dir))
            exe = new[] { "whisper-cli.exe", "main.exe" }
                .SelectMany(name => SafeFind(dir, name)).FirstOrDefault();
        if (model == null && Directory.Exists(dir))
            model = SafeFind(dir, "ggml-*.bin").Select(f => new FileInfo(f)).OrderByDescending(f => f.Length).FirstOrDefault()?.FullName;
        return exe != null && model != null && File.Exists(exe) && File.Exists(model) ? new Whisper(exe, model, lang) : null;
    }

    private static string[] SafeFind(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories); } catch { return Array.Empty<string>(); }
    }

    private static async Task<string> TranscribeAsync(Whisper w, short[] pcm)
    {
        var wav = Path.Combine(Path.GetTempPath(), $"painel-voz-{Guid.NewGuid():N}.wav");
        WriteWav(wav, pcm);
        try
        {
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            var psi = new ProcessStartInfo(w.Exe)
            {
                WorkingDirectory = Path.GetDirectoryName(w.Exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in new[] { "-m", w.Model, "-f", wav, "-l", w.Language, "-t", threads.ToString(), "-nt", "-np" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não consegui abrir o Whisper.");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new TimeoutException("O Whisper demorou demais."); }
            var text = (await outTask).Trim();
            if (p.ExitCode != 0) throw new InvalidOperationException("O Whisper deu erro: " + Last((await errTask).Trim(), 200));
            // o Whisper inventa isso no silêncio
            text = string.Join(" ", text.Split('\n').Select(l => l.Trim()).Where(l => l != "" && l != "[BLANK_AUDIO]" && !l.StartsWith("[Música", StringComparison.OrdinalIgnoreCase)));
            return text;
        }
        finally { try { File.Delete(wav); } catch { } }
    }

    private static string Last(string s, int n) => s.Length > n ? "…" + s[^n..] : s;

    // ---------- ditado do Windows ----------

    private async Task StartDictationAsync()
    {
        SpeechRecognizer rec;
        try { rec = new SpeechRecognizer(new Windows.Globalization.Language("pt-BR")); }
        catch { rec = new SpeechRecognizer(); } // idioma de fala padrão do Windows
        rec.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "ditado"));
        var comp = await rec.CompileConstraintsAsync();
        if (comp.Status != SpeechRecognitionResultStatus.Success) { rec.Dispose(); throw new InvalidOperationException("ditado: " + comp.Status); }
        rec.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromMinutes(3);
        rec.ContinuousRecognitionSession.ResultGenerated += (_, e) =>
        {
            if (e.Result.Status == SpeechRecognitionResultStatus.Success && e.Result.Confidence != SpeechRecognitionConfidence.Rejected)
                lock (dictated) dictated.Append(e.Result.Text).Append(' ');
        };
        await rec.ContinuousRecognitionSession.StartAsync();
        dictation = rec;
    }

    private static string DictationError(Exception ex) =>
        (uint)ex.HResult == 0x80045509
            ? "Pra falar sem o Whisper, ligue \"Reconhecimento de fala online\" no Windows (Configurações → Privacidade → Fala), ou instale o Whisper com scripts\\instalar-whisper.ps1."
            : "Não consegui usar o ditado do Windows (" + ex.Message + "). Instale o Whisper com scripts\\instalar-whisper.ps1.";
}
