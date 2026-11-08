using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PainelDeck;

/// <summary>
/// Cliente mínimo do obs-websocket v5 (já vem no OBS 28+).
/// Qualquer request da documentação funciona: SetCurrentProgramScene, ToggleRecord,
/// ToggleStream, ToggleInputMute, SaveReplayBuffer...
/// </summary>
internal sealed class ObsClient
{
    private readonly string url, password;
    private ClientWebSocket? ws;
    private readonly SemaphoreSlim connectLock = new(1, 1), sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> pending = new();
    private TaskCompletionSource<JsonObject>? helloTcs, identifiedTcs;

    public ObsClient(string url, string password) { this.url = url; this.password = password; }

    public bool IsConnected => ws?.State == WebSocketState.Open;

    public async Task<JsonNode?> RequestAsync(string requestType, JsonNode? data)
    {
        await EnsureConnectedAsync();
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;

        await SendAsync(new JsonObject
        {
            ["op"] = 6,
            ["d"] = new JsonObject
            {
                ["requestType"] = requestType,
                ["requestId"] = id,
                ["requestData"] = data ?? new JsonObject(),
            }
        });

        if (await Task.WhenAny(tcs.Task, Task.Delay(5000)) != tcs.Task)
        {
            pending.TryRemove(id, out _);
            throw new TimeoutException("OBS não respondeu");
        }
        return await tcs.Task;
    }

    private async Task EnsureConnectedAsync()
    {
        if (IsConnected) return;
        await connectLock.WaitAsync();
        try
        {
            if (IsConnected) return;
            ws?.Dispose();
            var socket = ws = new ClientWebSocket();
            helloTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            identifiedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                using var cts = new CancellationTokenSource(3000);
                await socket.ConnectAsync(new Uri(url), cts.Token);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Não conectei no OBS ({url}). O OBS está aberto com o servidor WebSocket ligado? ({ex.Message})");
            }

            _ = Task.Run(() => ReceiveLoop(socket));

            var hello = await WithTimeout(helloTcs.Task, 3000, "OBS não mandou Hello");
            var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };

            if (hello["authentication"] is JsonObject auth)
            {
                if (string.IsNullOrEmpty(password))
                    throw new InvalidOperationException("O OBS pede senha: coloque em settings.json → obs.password");
                var salt = auth["salt"]!.GetValue<string>();
                var challenge = auth["challenge"]!.GetValue<string>();
                identify["authentication"] = B64Sha(B64Sha(password + salt) + challenge);
            }

            await SendAsync(new JsonObject { ["op"] = 1, ["d"] = identify });
            await WithTimeout(identifiedTcs.Task, 3000, "OBS recusou a conexão (senha errada?)");
        }
        finally { connectLock.Release(); }
    }

    private async Task ReceiveLoop(ClientWebSocket socket)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await socket.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                var node = JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                int op = node?["op"]?.GetValue<int>() ?? -1;
                var d = node?["d"] as JsonObject;

                switch (op)
                {
                    case 0: helloTcs?.TrySetResult(d ?? new JsonObject()); break;
                    case 2: identifiedTcs?.TrySetResult(d ?? new JsonObject()); break;
                    case 7:
                        var id = d?["requestId"]?.GetValue<string>();
                        if (id != null && pending.TryRemove(id, out var tcs))
                        {
                            var status = d!["requestStatus"];
                            if (status?["result"]?.GetValue<bool>() == true)
                                tcs.TrySetResult(d["responseData"]?.DeepClone());
                            else
                                tcs.TrySetException(new InvalidOperationException(
                                    $"OBS: {status?["comment"]?.GetValue<string>() ?? "erro"} (código {status?["code"]})"));
                        }
                        break;
                }
            }
        }
        catch { /* conexão caiu */ }
        finally
        {
            foreach (var kv in pending)
                if (pending.TryRemove(kv.Key, out var t))
                    t.TrySetException(new InvalidOperationException("Conexão com o OBS caiu"));
        }
    }

    private async Task SendAsync(JsonObject msg)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await sendLock.WaitAsync();
        try { await ws!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { sendLock.Release(); }
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int ms, string message)
    {
        if (await Task.WhenAny(task, Task.Delay(ms)) != task) throw new TimeoutException(message);
        return await task;
    }

    private static string B64Sha(string s) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
