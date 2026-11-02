using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace PhoneAudioConnector;

internal enum LinkState
{
    Off,
    Waiting,
    Connected,
    Error
}

/// <summary>
///     Keeps A2DP links open to the selected devices only.
///     - Listens for phone-initiated connections (manual connect from the phone works).
///     - Retries on a timer, so it recovers after out-of-range, BT off/on, sleep/resume.
///     Events are raised on background threads.
/// </summary>
internal sealed class ConnectionManager : IDisposable
{
    private const int RetrySeconds = 10;
    private readonly ConcurrentDictionary<string, AudioPlaybackConnection> _connections = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, string> _devices = new(); // paired A2DP sources: id -> name
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, byte> _selected = new(); // ids the user ticked
    private readonly ConcurrentDictionary<string, LinkState> _states = new();
    private DeviceWatcher? _watcher;
    private int _watcherGeneration;

    public ConnectionManager(IEnumerable<string> selectedIds)
    {
        foreach (var id in selectedIds)
            _selected[id] = 0;
    }

    public IReadOnlyList<string> SelectedIds => _selected.Keys.ToList();

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        Logger.Debug(LogCat.App, "Shutting down connections");

        _cts.Cancel();
        try
        {
            _watcher?.Stop();
        }
        catch
        {
            // ignored
        }

        foreach (var id in _connections.Keys)
            Drop(id, "app exit");
    }

    public event Action<string, string>? DeviceAdded; // id, name
    public event Action<string>? DeviceRemoved; // id
    public event Action<string, LinkState>? StateChanged; // id, state

    public bool IsSelected(string id)
    {
        return _selected.ContainsKey(id);
    }

    public LinkState GetState(string id)
    {
        return _states.GetValueOrDefault(id, LinkState.Off);
    }

    private string Name(string id)
    {
        return _devices.GetValueOrDefault(id, id);
    }

    public void Start()
    {
        Logger.Info("Started. Waiting for paired devices...");
        Logger.Debug(LogCat.App, $"Saved selection: {_selected.Count} device(s)");
        foreach (var id in _selected.Keys)
            Logger.Debug(LogCat.App, $"  selected id: {id}");

        StartWatcher();
        _ = RetryLoopAsync(_cts.Token);
    }

    public void SetSelected(string id, bool selected)
    {
        Logger.Debug(LogCat.Connection, $"{Name(id)}: selection -> {(selected ? "on" : "off")}");

        if (selected)
        {
            _selected[id] = 0;
            SetState(id, LinkState.Waiting);
            _ = EnsureAsync(id, reason: "ticked");
        }
        else
        {
            _selected.TryRemove(id, out _);
            _ = DisconnectAsync(id);
        }
    }

    public void ConnectNow(string id)
    {
        if (IsSelected(id))
            _ = EnsureAsync(id, reason: "Connect now");
        else
            Logger.Debug(LogCat.Connection, $"{Name(id)}: Connect now ignored, not selected");
    }

    private async Task RetryLoopAsync(CancellationToken ct)
    {
        Logger.Debug(LogCat.Retry, $"Retry loop started, every {RetrySeconds}s");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(RetrySeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var status = _watcher?.Status.ToString() ?? "null";
            Logger.Debug(LogCat.Retry,
                $"Tick: watcher={status}, known={_devices.Count}, selected={_selected.Count}, " +
                $"open={_connections.Values.Count(c => SafeState(c) == AudioPlaybackConnectionState.Opened)}");

            if (_watcher is null ||
                _watcher.Status is DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted)
            {
                Logger.Info("Device watcher stopped, restarting.");
                StartWatcher();
            }

            foreach (var id in _selected.Keys)
                _ = EnsureAsync(id, reason: "timer");
        }

        Logger.Debug(LogCat.Retry, "Retry loop stopped");
    }

    private void StartWatcher()
    {
        try
        {
            _watcher?.Stop();
        }
        catch (Exception ex)
        {
            Logger.Debug(LogCat.Devices, $"Old watcher stop failed: {ex.Message}");
        }

        var gen = Interlocked.Increment(ref _watcherGeneration);
        var selector = AudioPlaybackConnection.GetDeviceSelector();
        Logger.Debug(LogCat.Devices, $"Watcher #{gen} starting. Selector: {selector}");

        _watcher = DeviceInformation.CreateWatcher(selector);

        _watcher.Added += (w, d) =>
        {
            Logger.RegisterName(d.Name, d.Id); // before any log line mentions it
            var isNew = !_devices.ContainsKey(d.Id);
            _devices[d.Id] = d.Name;
            if (isNew) Logger.Info($"Found {d.Name}");
            Logger.Debug(LogCat.Devices,
                $"Added (watcher #{gen}): \"{d.Name}\" enabled={d.IsEnabled} kind={d.Kind} id={d.Id}");
            DeviceAdded?.Invoke(d.Id, d.Name);

            if (!IsSelected(d.Id)) return;

            if (GetState(d.Id) == LinkState.Off) SetState(d.Id, LinkState.Waiting);
            _ = EnsureAsync(d.Id, reason: "device added");
        };

        _watcher.Updated += (w, u) =>
        {
            if (Logger.IsOn(LogCat.Devices))
            {
                var props = string.Join(", ", u.Properties.Select(kv => $"{kv.Key}={kv.Value}"));
                Logger.Debug(LogCat.Devices, $"Updated: {Name(u.Id)} [{props}]");
            }

            if (_devices.ContainsKey(u.Id))
                _ = EnsureAsync(u.Id, reason: "device updated");
        };

        _watcher.Removed += (w, u) =>
        {
            if (_devices.TryRemove(u.Id, out var name))
                Logger.Info($"Removed {name}");
            Logger.Debug(LogCat.Devices, $"Removed (watcher #{gen}): id={u.Id}");
            Drop(u.Id, "device removed");
            _states.TryRemove(u.Id, out _);
            DeviceRemoved?.Invoke(u.Id);
        };

        _watcher.EnumerationCompleted += (_, _) =>
            Logger.Debug(LogCat.Devices, $"Watcher #{gen}: initial scan done, {_devices.Count} device(s)");

        _watcher.Stopped += (w, _) =>
            Logger.Debug(LogCat.Devices, $"Watcher #{gen}: stopped (status={w.Status})");

        _watcher.Start();
    }

    private async Task EnsureAsync(string id, bool recreate = false, string reason = "")
    {
        var tag = $"{Name(id)} [{reason}]";

        if (_cts.IsCancellationRequested) return;
        if (!IsSelected(id))
        {
            Logger.Debug(LogCat.Connection, $"{tag}: skip, not selected");
            return;
        }

        if (!_devices.ContainsKey(id))
        {
            Logger.Debug(LogCat.Connection, $"{tag}: skip, device not present (not paired or not found yet)");
            return;
        }

        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0))
        {
            Logger.Debug(LogCat.Connection, $"{tag}: skip, another attempt is running");
            return;
        }

        try
        {
            if (recreate) Drop(id, "recreate after close");

            _connections.TryGetValue(id, out var conn);
            if (conn is not null && SafeState(conn) == AudioPlaybackConnectionState.Opened)
            {
                Logger.Debug(LogCat.Connection, $"{tag}: already open");
                return;
            }

            if (conn is null)
            {
                Logger.Debug(LogCat.Connection, $"{tag}: creating connection object");
                conn = AudioPlaybackConnection.TryCreateFromId(id);
                if (conn is null)
                {
                    // Log once, not on every retry.
                    if (GetState(id) != LinkState.Error)
                        Logger.Info($"{Name(id)} can't be used as an audio source.");
                    SetState(id, LinkState.Error);
                    return;
                }

                conn.StateChanged += (s, _) => OnConnectionStateChanged(id, s);

                // StartAsync = accept connections started from the phone.
                var sw = Stopwatch.StartNew();
                await conn.StartAsync();
                _connections[id] = conn;
                Logger.Debug(LogCat.Connection,
                    $"{tag}: StartAsync done in {sw.ElapsedMilliseconds} ms (listening), state={SafeState(conn)}");
            }
            else
            {
                Logger.Debug(LogCat.Connection, $"{tag}: reusing connection object, state={SafeState(conn)}");
            }

            // OpenAsync = PC actively connects to the phone.
            Logger.Debug(LogCat.Connection, $"{tag}: OpenAsync...");
            var openSw = Stopwatch.StartNew();
            var result = await conn.OpenAsync();
            Logger.Debug(LogCat.Connection,
                $"{tag}: OpenAsync -> {result.Status} in {openSw.ElapsedMilliseconds} ms" +
                (result.ExtendedError is { } err ? $", HRESULT=0x{err.HResult:X8} {err.Message}" : ""));

            // User unticked the device while we were connecting.
            if (!IsSelected(id))
            {
                Logger.Debug(LogCat.Connection, $"{tag}: unticked during open, dropping");
                Drop(id, "unticked during open");
                SetState(id, LinkState.Off);
                return;
            }

            switch (result.Status)
            {
                case AudioPlaybackConnectionOpenResultStatus.Success:
                    SetState(id, LinkState.Connected);
                    break;
                case AudioPlaybackConnectionOpenResultStatus.RequestTimedOut:
                    // Phone out of range or BT off. Keep listening, retry later.
                    SetState(id, LinkState.Waiting);
                    break;
                case AudioPlaybackConnectionOpenResultStatus.DeniedBySystem:
                    // A real settings problem. Log it once, not on every retry.
                    if (GetState(id) != LinkState.Error)
                        Logger.Info($"Denied by system: {Name(id)} (check Bluetooth/privacy settings)");
                    SetState(id, LinkState.Error);
                    break;
                case AudioPlaybackConnectionOpenResultStatus.UnknownFailure:
                default:
                    // Usually the device has Bluetooth off or is out of range. Fail silently
                    // (details only in verbose mode, logged above) and keep retrying.
                    SetState(id, LinkState.Waiting);
                    Drop(id, "open failed"); // fresh object next time
                    break;
            }
        }
        catch (Exception ex)
        {
            // Same as above: an unreachable device can also surface as an exception.
            SetState(id, LinkState.Waiting);
            Logger.Debug(LogCat.Connection, $"{tag}: exception {ex.GetType().Name} HRESULT=0x{ex.HResult:X8}\n{ex}");
            Drop(id, "exception");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DisconnectAsync(string id)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        Logger.Debug(LogCat.Connection, $"{Name(id)}: disconnect requested, waiting for lock");
        await gate.WaitAsync();
        try
        {
            Drop(id, "unticked");
            SetState(id, LinkState.Off);
        }
        finally
        {
            gate.Release();
        }
    }

    private void OnConnectionStateChanged(string id, AudioPlaybackConnection sender)
    {
        var state = SafeState(sender);

        // Ignore events from connection objects we already dropped.
        if (!_connections.TryGetValue(id, out var current) || !ReferenceEquals(current, sender))
        {
            Logger.Debug(LogCat.Connection, $"{Name(id)}: ignored state {state} from a dropped object");
            return;
        }

        Logger.Debug(LogCat.Connection, $"{Name(id)}: link state -> {state}");

        if (state == AudioPlaybackConnectionState.Opened)
        {
            SetState(id, LinkState.Connected);
        }
        else if (state == AudioPlaybackConnectionState.Closed)
        {
            SetState(id, IsSelected(id) ? LinkState.Waiting : LinkState.Off);

            // Rebuild the connection object so the phone can reconnect.
            Logger.Debug(LogCat.Connection, $"{Name(id)}: closed, rebuilding in 2 s");
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                await EnsureAsync(id, true, "after close");
            });
        }
    }

    private void SetState(string id, LinkState state)
    {
        var old = GetState(id);
        if (old == state) return;
        _states[id] = state;

        Logger.Debug(LogCat.Connection, $"{Name(id)}: status {old} -> {state}");
        if (state == LinkState.Connected) Logger.Info($"Connected: {Name(id)}");
        else if (old == LinkState.Connected) Logger.Info($"Disconnected: {Name(id)}");

        StateChanged?.Invoke(id, state);
    }

    private void Drop(string id, string why)
    {
        if (!_connections.TryRemove(id, out var c)) return;

        Logger.Debug(LogCat.Connection, $"{Name(id)}: disposing connection object ({why})");
        try
        {
            c.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Debug(LogCat.Connection, $"{Name(id)}: dispose failed: {ex.Message}");
        }
    }

    /// <summary>Reading State on a disposed object can throw; treat that as Closed.</summary>
    private static AudioPlaybackConnectionState SafeState(AudioPlaybackConnection c)
    {
        try
        {
            return c.State;
        }
        catch
        {
            return AudioPlaybackConnectionState.Closed;
        }
    }
}