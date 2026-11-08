using Windows.Media.Control;
using Windows.Storage.Streams;

namespace PainelDeck;

internal sealed record MediaInfo(string Title, string Artist, string? Art, double Position, double Duration, bool Playing);

/// <summary>
/// "Tocando agora" do Windows (Spotify, navegador, etc.), lido num loop em segundo plano a cada 1s.
/// <see cref="Current"/> nunca bloqueia: devolve a última leitura (null = nada tocando ou API indisponível).
/// </summary>
internal sealed class Media : IDisposable
{
    private readonly CancellationTokenSource cts = new();
    private string? artKey, artData; // capa só é relida quando a música muda

    public MediaInfo? Current { get; private set; }

    public Media() => Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        GlobalSystemMediaTransportControlsSessionManager? mgr = null;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                Current = await ReadAsync(mgr);
            }
            catch { Current = null; mgr = null; } // API sumiu/erro: tenta de novo no próximo ciclo
            try { await Task.Delay(1000, cts.Token); } catch (TaskCanceledException) { }
        }
    }

    private async Task<MediaInfo?> ReadAsync(GlobalSystemMediaTransportControlsSessionManager mgr)
    {
        var s = mgr.GetCurrentSession();
        if (s == null) return null;
        var props = await s.TryGetMediaPropertiesAsync();
        if (props == null || string.IsNullOrEmpty(props.Title)) return null;
        var tl = s.GetTimelineProperties();
        var playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var pos = tl.Position;
        // a posição só é atualizada pelo player de vez em quando: soma o tempo desde a última atualização
        if (playing) pos += DateTimeOffset.Now - tl.LastUpdatedTime;
        var dur = (tl.EndTime - tl.StartTime).TotalSeconds;
        pos = TimeSpan.FromSeconds(Math.Clamp(pos.TotalSeconds, 0, dur > 0 ? dur : double.MaxValue));

        var key = props.Title + "\n" + props.Artist;
        if (key != artKey)
        {
            artKey = key;
            artData = props.Thumbnail == null ? null : await ThumbAsync(props.Thumbnail);
        }
        return new MediaInfo(props.Title, props.Artist ?? "", artData, Math.Round(pos.TotalSeconds, 1), Math.Round(dur, 1), playing);
    }

    /// <summary>Capa → data URL pequena (até 128px, JPEG) pra não pesar a mensagem de status de 1s.</summary>
    private static async Task<string?> ThumbAsync(IRandomAccessStreamReference thumb)
    {
        try
        {
            using var ras = await thumb.OpenReadAsync();
            using var src = System.Drawing.Image.FromStream(ras.AsStreamForRead());
            var k = Math.Min(1.0, 128.0 / Math.Max(src.Width, src.Height));
            using var bmp = new Bitmap(src, Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    public void Dispose() => cts.Cancel();
}
