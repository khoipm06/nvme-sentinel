using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NvmeSentinel.Core.Models;
using NvmeSentinel.Core.Storage;

namespace NvmeSentinel.Core.Ipc;

public sealed class SentinelIpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, StreamWriter> _liveSubscribers = new();
    private readonly List<Task> _acceptLoops = [];

    public int SubscriberCount => _liveSubscribers.Count;

    public SentinelIpcServer(
        Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler,
        string? customPipeName = null)
    {
        _pipeName = customPipeName ?? AppPaths.GetIpcPipeName();
        _handler = handler;
    }

    public void Start(int maxConcurrentAccepts = 4)
    {
        for (int i = 0; i < maxConcurrentAccepts; i++)
        {
            _acceptLoops.Add(Task.Run(() => AcceptLoopAsync(_cts.Token)));
        }
    }

    public async Task BroadcastLiveAsync(LiveTelemetry telemetry)
    {
        if (_liveSubscribers.IsEmpty)
        {
            return;
        }

        var envelope = new IpcResponse
        {
            Id = "live",
            Ok = true,
            Live = telemetry
        };
        var line = JsonSerializer.Serialize(envelope);

        foreach (var kv in _liveSubscribers)
        {
            try
            {
                await kv.Value.WriteLineAsync(line);
                await kv.Value.FlushAsync();
            }
            catch
            {
                _liveSubscribers.TryRemove(kv.Key, out _);
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);
                _ = Task.Run(() => HandleClientAsync(server, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                server?.Dispose();
                await Task.Delay(250, CancellationToken.None);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        Guid? subId = null;
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
            {
                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null)
                    {
                        break;
                    }

                    IpcRequest? req;
                    try
                    {
                        req = JsonSerializer.Deserialize<IpcRequest>(line);
                    }
                    catch (Exception ex)
                    {
                        var errResp = new IpcResponse { Ok = false, Error = $"Invalid JSON: {ex.Message}" };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(errResp));
                        continue;
                    }

                    if (req is null)
                    {
                        continue;
                    }

                    if (req.Command.Equals("SubscribeLive", StringComparison.OrdinalIgnoreCase))
                    {
                        subId = Guid.NewGuid();
                        _liveSubscribers[subId.Value] = writer;
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcResponse
                        {
                            Id = req.Id,
                            Ok = true,
                            Message = "Subscribed"
                        }));

                        // Hold connection open until client disconnects or server stops
                        while (pipe.IsConnected && !ct.IsCancellationRequested)
                        {
                            var ping = await reader.ReadLineAsync(ct);
                            if (ping is null)
                            {
                                break;
                            }
                        }

                        break;
                    }

                    var response = await _handler(req, ct);
                    response.Id = req.Id;
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response));
                }
            }
        }
        catch
        {
            // Client disconnected
        }
        finally
        {
            if (subId.HasValue)
            {
                _liveSubscribers.TryRemove(subId.Value, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _liveSubscribers.Clear();
        try
        {
            await Task.WhenAll(_acceptLoops).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown timeouts
        }

        _cts.Dispose();
    }
}

public sealed class SentinelIpcClient
{
    private readonly string _pipeName;

    public SentinelIpcClient(string? customPipeName = null)
    {
        _pipeName = customPipeName ?? AppPaths.GetIpcPipeName();
    }

    public async Task<bool> PingAsync(int timeoutMs = 600, CancellationToken ct = default)
    {
        try
        {
            var resp = await SendAsync(new IpcRequest { Command = "Ping" }, timeoutMs, ct);
            return resp.Ok;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, int connectTimeoutMs = 2000, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectTimeoutMs, ct);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line))
        {
            return new IpcResponse { Id = request.Id, Ok = false, Error = "Empty response from daemon." };
        }

        return JsonSerializer.Deserialize<IpcResponse>(line) ?? new IpcResponse { Id = request.Id, Ok = false, Error = "Malformed daemon response." };
    }

    public async Task SubscribeLiveAsync(
        Func<LiveTelemetry, Task> onTelemetry,
        CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2500, ct);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest { Command = "SubscribeLive" }));

        while (pipe.IsConnected && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            var resp = JsonSerializer.Deserialize<IpcResponse>(line);
            if (resp?.Live is not null)
            {
                await onTelemetry(resp.Live);
            }
        }
    }
}
