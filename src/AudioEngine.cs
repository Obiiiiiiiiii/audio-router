using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioRouter;

record AudioDevice(string Id, string Name, bool Connected);

/// <summary>One row in the mixer: every audio session of one exe, merged.</summary>
sealed class AppView
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public string? ExePath { get; init; }
    public bool IsSystemSounds { get; init; }
    public bool Running { get; init; }
    public float Volume { get; init; }
    public bool Muted { get; init; }
    public AppRule? Rule { get; init; }
}

/// <summary>
/// Watches devices and audio sessions and keeps every app matching its saved rule.
/// All Core Audio work happens on the UI thread; COM callbacks just ask for a reconcile.
/// A timer reconciles regularly too, so a missed notification can't leave things stale.
/// </summary>
sealed class AudioEngine : IMMNotificationClient, IDisposable
{
    public const string SystemSoundsKey = "#system";

    readonly RuleStore _store;
    readonly Control _ui;
    readonly AppRouting _routing = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1500 };

    MMDeviceEnumerator? _enumerator;
    readonly Dictionary<string, MMDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    bool _devicesDirty = true;
    int _ticksSinceDeviceScan;
    int _reconcileQueued;

    sealed record ProcInfo(string Key, string? Path, string DisplayName);
    sealed record LiveSession(string DeviceId, uint Pid, string InstanceId, string Key, AudioSessionControl Control);

    readonly Dictionary<uint, ProcInfo> _procCache = new();
    readonly HashSet<string> _volumeApplied = new();
    readonly Dictionary<uint, string> _routedTo = new(); // pid -> device id ("" = system default)
    List<LiveSession> _sessions = new();

    public IReadOnlyList<AppView> Apps { get; private set; } = Array.Empty<AppView>();
    public IReadOnlyList<AudioDevice> Devices { get; private set; } = Array.Empty<AudioDevice>();
    public string? DefaultDeviceId { get; private set; }
    public bool RoutingAvailable => _routing.Available;

    /// <summary>Raised on the UI thread after each reconcile.</summary>
    public event Action? Changed;

    public AudioEngine(RuleStore store, Control uiInvoker)
    {
        _store = store;
        _ui = uiInvoker;
        _timer.Tick += (_, _) => Reconcile();
    }

    public void Start()
    {
        EnsureEnumerator();
        Reconcile();
        _timer.Start();
    }

    void EnsureEnumerator()
    {
        if (_enumerator != null) return;
        _enumerator = new MMDeviceEnumerator();
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    /// <summary>Thread-safe: schedule a reconcile on the UI thread (coalesced).</summary>
    public void RequestReconcile(bool devicesChanged = false)
    {
        if (devicesChanged) _devicesDirty = true;
        if (Interlocked.Exchange(ref _reconcileQueued, 1) == 1) return;
        try
        {
            if (_ui.IsHandleCreated) _ui.BeginInvoke(() => Reconcile());
            else Interlocked.Exchange(ref _reconcileQueued, 0);
        }
        catch
        {
            Interlocked.Exchange(ref _reconcileQueued, 0);
        }
    }

    // ---------------------------------------------------------------- reconcile

    public void Reconcile()
    {
        Interlocked.Exchange(ref _reconcileQueued, 0);
        try
        {
            EnsureEnumerator();
            if (_devicesDirty || ++_ticksSinceDeviceScan >= 10) ScanDevices();
            ScanSessions();
            ApplyRules();
            BuildViews();
        }
        catch (Exception ex)
        {
            Log.Error("Reconcile failed; rebuilding audio objects", ex);
            ResetAudioObjects();
        }
        Changed?.Invoke();
    }

    void ScanDevices()
    {
        _devicesDirty = false;
        _ticksSinceDeviceScan = 0;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dev in _enumerator!.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            string id;
            try { id = dev.ID; } catch { dev.Dispose(); continue; }
            seen.Add(id);

            if (_devices.ContainsKey(id)) { dev.Dispose(); continue; }

            try
            {
                var name = dev.FriendlyName;
                _store.Data.KnownDevices[id] = name;
                dev.AudioSessionManager.OnSessionCreated += (_, _) => RequestReconcile();
                _devices[id] = dev;
                Log.Info($"Device connected: {name}");
            }
            catch (Exception ex)
            {
                Log.Error($"Skipping device {id}", ex);
                dev.Dispose();
            }
        }

        foreach (var gone in _devices.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Log.Info($"Device gone: {_store.Data.KnownDevices.GetValueOrDefault(gone, gone)}");
            try { _devices[gone].Dispose(); } catch { }
            _devices.Remove(gone);
        }

        try
        {
            if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                DefaultDeviceId = def.ID;
            }
            else DefaultDeviceId = null;
        }
        catch { DefaultDeviceId = null; }

        Devices = _store.Data.KnownDevices
            .Select(kv => new AudioDevice(kv.Key, kv.Value, _devices.ContainsKey(kv.Key)))
            .OrderByDescending(d => d.Connected)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // A device change means preferred/backup choices may now resolve differently.
        _routedTo.Clear();
        _store.SaveSoon();
    }

    void ScanSessions()
    {
        var list = new List<LiveSession>();
        foreach (var (deviceId, dev) in _devices)
        {
            try
            {
                var mgr = dev.AudioSessionManager;
                mgr.RefreshSessions();
                var sessions = mgr.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                    string key;
                    uint pid = 0;
                    if (s.IsSystemSoundsSession)
                    {
                        key = SystemSoundsKey;
                    }
                    else
                    {
                        pid = s.GetProcessID;
                        var info = GetProcInfo(pid);
                        if (info == null) continue;
                        key = info.Key;
                    }
                    list.Add(new LiveSession(deviceId, pid, s.GetSessionInstanceIdentifier ?? $"{deviceId}|{pid}|{i}", key, s));
                }
            }
            catch (Exception ex)
            {
                // Typical when a Bluetooth device drops mid-scan; next device scan cleans it up.
                Log.Error($"Session scan failed on {_store.Data.KnownDevices.GetValueOrDefault(deviceId, deviceId)}", ex);
                _devicesDirty = true;
            }
        }
        foreach (var old in _sessions) { try { old.Control.Dispose(); } catch { } }
        _sessions = list;

        // Forget state for sessions/processes that no longer exist (pids get reused).
        var livePids = list.Select(s => s.Pid).ToHashSet();
        var liveInstances = list.Select(s => s.InstanceId).ToHashSet();
        foreach (var pid in _procCache.Keys.Where(p => !livePids.Contains(p)).ToList()) _procCache.Remove(pid);
        foreach (var pid in _routedTo.Keys.Where(p => !livePids.Contains(p)).ToList()) _routedTo.Remove(pid);
        _volumeApplied.RemoveWhere(id => !liveInstances.Contains(id));
    }

    void ApplyRules()
    {
        foreach (var s in _sessions)
        {
            var rule = _store.Get(s.Key);
            if (rule == null) continue;

            // Volume/mute: apply once per new session, so the app starts at its saved level.
            if (_volumeApplied.Add(s.InstanceId) && (rule.Volume != null || rule.Muted != null))
            {
                try
                {
                    var vol = s.Control.SimpleAudioVolume;
                    if (rule.Volume is float v) vol.Volume = Math.Clamp(v, 0f, 1f);
                    if (rule.Muted is bool m) vol.Mute = m;
                    Log.Info($"Applied volume {rule.Volume:P0}{(rule.Muted == true ? " (muted)" : "")} to {s.Key}");
                }
                catch (Exception ex) { Log.Error($"Setting volume for {s.Key} failed", ex); }
            }
        }

        if (!_routing.Available) return;

        foreach (var group in _sessions.Where(s => s.Pid != 0).GroupBy(s => s.Pid))
        {
            var pid = group.Key;
            var key = group.First().Key;
            var rule = _store.Get(key);
            if (rule == null || !rule.ManageOutput) continue;

            var target = ResolveTarget(rule) ?? "";
            if (_routedTo.TryGetValue(pid, out var already) && already == target) continue;

            var current = _routing.GetAppOutput(pid) ?? "";
            if (!string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            {
                if (_routing.SetAppOutput(pid, target == "" ? null : target))
                    Log.Info($"Routed {key} (pid {pid}) to {(target == "" ? "system default" : DeviceName(target))}");
                else
                    continue; // retry next tick
            }
            _routedTo[pid] = target;
        }
    }

    /// <summary>Preferred device if connected, else backup if connected, else system default (null).</summary>
    string? ResolveTarget(AppRule rule)
    {
        if (rule.OutputDeviceId != null && _devices.ContainsKey(rule.OutputDeviceId)) return rule.OutputDeviceId;
        if (rule.OutputDeviceId == null) return null;
        if (rule.BackupDeviceId != null && _devices.ContainsKey(rule.BackupDeviceId)) return rule.BackupDeviceId;
        return null;
    }

    void BuildViews()
    {
        var views = new List<AppView>();
        foreach (var group in _sessions.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            float volume = 1f; bool muted = false;
            try
            {
                var v = first.Control.SimpleAudioVolume;
                volume = v.Volume; muted = v.Mute;
            }
            catch { }

            var info = first.Pid != 0 ? _procCache.GetValueOrDefault(first.Pid) : null;
            views.Add(new AppView
            {
                Key = group.Key,
                DisplayName = group.Key == SystemSoundsKey ? "System sounds" : info?.DisplayName ?? group.Key,
                ExePath = info?.Path,
                IsSystemSounds = group.Key == SystemSoundsKey,
                Running = true,
                Volume = volume,
                Muted = muted,
                Rule = _store.Get(group.Key),
            });
        }

        // Saved apps that aren't making sound right now, so they can be edited or forgotten.
        var running = views.Select(v => v.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, rule) in _store.Data.Apps)
        {
            if (running.Contains(key)) continue;
            views.Add(new AppView
            {
                Key = key,
                DisplayName = string.IsNullOrEmpty(rule.DisplayName) ? key : rule.DisplayName,
                IsSystemSounds = key == SystemSoundsKey,
                Running = false,
                Volume = rule.Volume ?? 1f,
                Muted = rule.Muted ?? false,
                Rule = rule,
            });
        }

        Apps = views
            .OrderByDescending(v => v.Running)
            .ThenBy(v => v.IsSystemSounds ? 0 : 1)
            .ThenBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    void ResetAudioObjects()
    {
        foreach (var d in _devices.Values) { try { d.Dispose(); } catch { } }
        _devices.Clear();
        _sessions = new();
        _routedTo.Clear();
        try { _enumerator?.UnregisterEndpointNotificationCallback(this); } catch { }
        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
        _devicesDirty = true;
    }

    // ---------------------------------------------------------------- user actions (UI thread)

    public void SetVolume(string key, string displayName, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        _store.GetOrCreate(key, displayName).Volume = volume;
        foreach (var s in _sessions.Where(s => s.Key == key))
        {
            try { s.Control.SimpleAudioVolume.Volume = volume; } catch { }
        }
        _store.SaveSoon();
    }

    public void SetMuted(string key, string displayName, bool muted)
    {
        _store.GetOrCreate(key, displayName).Muted = muted;
        foreach (var s in _sessions.Where(s => s.Key == key))
        {
            try { s.Control.SimpleAudioVolume.Mute = muted; } catch { }
        }
        _store.SaveSoon();
    }

    public void SetOutput(string key, string displayName, string? deviceId)
    {
        var rule = _store.GetOrCreate(key, displayName);
        rule.ManageOutput = true;
        rule.OutputDeviceId = deviceId;
        if (deviceId == null) rule.BackupDeviceId = null;
        ForgetRoutesFor(key);
        _store.SaveSoon();
        RequestReconcile(); // async: the caller is often a control that the refresh will replace
    }

    public void SetBackup(string key, string displayName, string? deviceId)
    {
        _store.GetOrCreate(key, displayName).BackupDeviceId = deviceId;
        ForgetRoutesFor(key);
        _store.SaveSoon();
        RequestReconcile(); // async: the caller is often a control that the refresh will replace
    }

    /// <summary>Drop the saved rule; if we were routing the app, hand it back to the system default.</summary>
    public void Forget(string key)
    {
        var rule = _store.Get(key);
        if (rule?.ManageOutput == true)
        {
            foreach (var pid in _sessions.Where(s => s.Key == key && s.Pid != 0).Select(s => s.Pid).Distinct())
                _routing.SetAppOutput(pid, null);
        }
        ForgetRoutesFor(key);
        _store.Forget(key);
        RequestReconcile();
    }

    void ForgetRoutesFor(string key)
    {
        foreach (var pid in _sessions.Where(s => s.Key == key).Select(s => s.Pid)) _routedTo.Remove(pid);
    }

    public (float volume, bool muted)? GetMaster()
    {
        if (DefaultDeviceId == null || !_devices.TryGetValue(DefaultDeviceId, out var dev)) return null;
        try { return (dev.AudioEndpointVolume.MasterVolumeLevelScalar, dev.AudioEndpointVolume.Mute); }
        catch { return null; }
    }

    public void SetMasterVolume(float volume)
    {
        if (DefaultDeviceId == null || !_devices.TryGetValue(DefaultDeviceId, out var dev)) return;
        try { dev.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f); } catch { }
    }

    public void SetMasterMuted(bool muted)
    {
        if (DefaultDeviceId == null || !_devices.TryGetValue(DefaultDeviceId, out var dev)) return;
        try { dev.AudioEndpointVolume.Mute = muted; } catch { }
    }

    public string DeviceName(string? id) =>
        id == null ? "System default" : _store.Data.KnownDevices.GetValueOrDefault(id, "Unknown device");

    // ---------------------------------------------------------------- process lookup

    ProcInfo? GetProcInfo(uint pid)
    {
        if (_procCache.TryGetValue(pid, out var cached)) return cached;

        string? path = QueryImagePath(pid);
        string key;
        string display;
        if (path != null)
        {
            key = System.IO.Path.GetFileName(path).ToLowerInvariant();
            display = System.IO.Path.GetFileNameWithoutExtension(path);
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(desc)) display = desc;
            }
            catch { }
        }
        else
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                key = (p.ProcessName + ".exe").ToLowerInvariant();
                display = p.ProcessName;
            }
            catch
            {
                return null; // process already exited
            }
        }

        var info = new ProcInfo(key, path, display);
        _procCache[pid] = info;
        return info;
    }

    static string? QueryImagePath(uint pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    // ---------------------------------------------------------------- IMMNotificationClient (COM thread)

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => RequestReconcile(devicesChanged: true);
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => RequestReconcile(devicesChanged: true);
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => RequestReconcile(devicesChanged: true);
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render) RequestReconcile(devicesChanged: true);
    }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _timer.Stop();
        _store.SaveNow();
        ResetAudioObjects();
    }
}
