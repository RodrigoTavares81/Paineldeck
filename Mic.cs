using NAudio.CoreAudioApi;

namespace PainelDeck;

/// <summary>Mute do microfone padrão do Windows (vale pra Discord, jogo, OBS, tudo).</summary>
internal sealed class Mic
{
    public bool? IsMuted()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return dev.AudioEndpointVolume.Mute;
        }
        catch { return null; } // sem microfone
    }

    /// <param name="mode">"toggle", "mute" ou "unmute"</param>
    public bool Set(string mode)
    {
        using var en = new MMDeviceEnumerator();
        using var console = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        bool target = mode switch
        {
            "mute" => true,
            "unmute" => false,
            _ => !console.AudioEndpointVolume.Mute,
        };
        console.AudioEndpointVolume.Mute = target;

        // Se o "dispositivo de comunicação" for outro mic, muta ele também.
        try
        {
            using var comm = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (comm.ID != console.ID) comm.AudioEndpointVolume.Mute = target;
        }
        catch { /* ignora */ }

        return target;
    }
}
