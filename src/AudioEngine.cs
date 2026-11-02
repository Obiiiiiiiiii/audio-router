using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using IMMNotificationClient = NAudio.CoreAudioApi.Interfaces.IMMNotificationClient;
using AudioRouter.Interop;

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
    IMMDeviceEnumerator? _comEnumerator;
    readonly Dictionary<string, DeviceEntry> _devices = new(StringComparer.OrdinalIgnoreCase);

    sealed class DeviceEntry
    {
        public required MMDevice Device { get; init; }
        public required string Name { get; init; }
        public required IAudioSessionManager2 Sessions { get; init; }
        public required SessionCreatedSink Sink { get; init; }
        public int LastSessionCount = -1;

        public void Dispose()
        {
            try { Sessions.UnregisterSessionNotification(Sink); } catch { }
            try { Device.Dispose(); } catch { }
        }
    }
    bool _devicesDirty = true;
    int _ticksSinceDeviceScan;
    int _reconcileQueued;

    sealed record ProcInfo(string Key, string? Path, string DisplayName);
    sealed record LiveSession(string DeviceId, uint Pid, string InstanceId, string Key, IAudioSessionControl2 Control)
    {
        public ISimpleAudioVolume? Volume => Control as ISimpleAudioVolume;
    }

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
        // Order matters. Windows hands out one shared MMDeviceEnumerator object, and .NET keeps one
        // wrapper per COM object. NAudio creates it as its own class type, so NAudio must go first;
        // our interface view below then reuses that wrapper. (Creating ours first made NAudio's
        // constructor fail with InvalidCastException.)
        if (_enumerator == null)
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        if (_comEnumerator == null)
        {
            try { _comEnumerator = CoreAudio.CreateDeviceEnumerator(); }
            catch (Exception ex) { Log.Crash("Creating the device enumerator failed", ex); }
        }
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
            Log.Crash("Reconcile failed; rebuilding audio objects", ex);
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

                var mgr = CoreAudio.ActivateSessionManager(_comEnumerator ?? throw new InvalidOperationException("no device enumerator"), id);
                // Windows only sends "session created" after the enumerator has been requested once.
                CoreAudio.Check(mgr.GetSessionEnumerator(out _), "GetSessionEnumerator");
                var sink = new SessionCreatedSink(() => RequestReconcile());
                int hr = mgr.RegisterSessionNotification(sink);
                if (hr < 0) Log.Error($"RegisterSessionNotification on {name} failed (0x{hr:X8}); relying on polling");

                _devices[id] = new DeviceEntry { Device = dev, Name = name, Sessions = mgr, Sink = sink };
                Log.Info($"Device connected: {name}");
            }
            catch (Exception ex)
            {
                Log.Crash($"Skipping device {id}", ex);
                dev.Dispose();
            }
        }

        foreach (var gone in _devices.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Log.Info($"Device gone: {_store.Data.KnownDevices.GetValueOrDefault(gone, gone)}");
            _devices[gone].Dispose();
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
        foreach (var (deviceId, entry) in _devices.ToList())
        {
            IAudioSessionEnumerator sessions;
            int count;
            try
            {
                CoreAudio.Check(entry.Sessions.GetSessionEnumerator(out sessions), "GetSessionEnumerator");
                CoreAudio.Check(sessions.GetCount(out count), "GetCount");
            }
            catch (Exception ex)
            {
                // Typical when a Bluetooth device drops mid-scan. Throw the device away so it's rebuilt fresh.
                Log.Error($"Listing sessions on {entry.Name} failed", ex);
                entry.Dispose();
                _devices.Remove(deviceId);
                _devicesDirty = true;
                continue;
            }

            var keys = new List<string>();
            for (int i = 0; i < count; i++)
            {
                // Each session is handled on its own, so one odd session can't hide the rest.
                try
                {
                    CoreAudio.Check(sessions.GetSession(i, out var s), "GetSession");
                    CoreAudio.Check(s.GetState(out var state), "GetState");
                    if (state == 2) continue; // AudioSessionStateExpired

                    s.GetSessionInstanceIdentifier(out var instPtr);
                    var instanceId = CoreAudio.TakeString(instPtr) ?? $"{deviceId}|{i}";

                    string key;
                    uint pid = 0;
                    if (s.IsSystemSoundsSession() == 0)
                    {
                        key = SystemSoundsKey;
                    }
                    else
                    {
                        int hr = s.GetProcessId(out pid);
                        if (hr < 0) pid = 0;
                        s.GetSessionIdentifier(out var idPtr);
                        var info = GetProcInfo(pid, CoreAudio.TakeString(idPtr));
                        if (info == null)
                        {
                            Log.Error($"Couldn't identify session {i} on {entry.Name} (pid {pid}, hr 0x{hr:X8})");
                            continue;
                        }
                        key = info.Key;
                    }
                    keys.Add(key);
                    list.Add(new LiveSession(deviceId, pid, instanceId, key, s));
                }
                catch (Exception ex)
                {
                    Log.Crash($"Reading session {i} on {entry.Name} failed", ex);
                }
            }

            if (count != entry.LastSessionCount)
            {
                entry.LastSessionCount = count;
                Log.Info($"{entry.Name}: {count} session(s) -> {string.Join(", ", keys)}");
            }
        }
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
                    var vol = s.Volume ?? throw new InvalidOperationException("no ISimpleAudioVolume");
                    if (rule.Volume is float v) CoreAudio.Check(vol.SetMasterVolume(Math.Clamp(v, 0f, 1f), IntPtr.Zero), "SetMasterVolume");
                    if (rule.Muted is bool m) CoreAudio.Check(vol.SetMute(m, IntPtr.Zero), "SetMute");
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
                var v = first.Volume;
                if (v != null && v.GetMasterVolume(out var lvl) >= 0) volume = lvl;
                if (v != null && v.GetMute(out var mu) >= 0) muted = mu;
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
        foreach (var d in _devices.Values) d.Dispose();
        _devices.Clear();
        _sessions = new();
        _routedTo.Clear();
        try { _enumerator?.UnregisterEndpointNotificationCallback(this); } catch { }
        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
        _comEnumerator = null;
        _devicesDirty = true;
    }

    // ---------------------------------------------------------------- user actions (UI thread)

    public void SetVolume(string key, string displayName, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        _store.GetOrCreate(key, displayName).Volume = volume;
        foreach (var s in _sessions.Where(s => s.Key == key))
        {
            try { s.Volume?.SetMasterVolume(volume, IntPtr.Zero); } catch { }
        }
        _store.SaveSoon();
    }

    public void SetMuted(string key, string displayName, bool muted)
    {
        _store.GetOrCreate(key, displayName).Muted = muted;
        foreach (var s in _sessions.Where(s => s.Key == key))
        {
            try { s.Volume?.SetMute(muted, IntPtr.Zero); } catch { }
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
        if (DefaultDeviceId == null || !_devices.TryGetValue(DefaultDeviceId, out var entry)) return null;
        var dev = entry.Device;
        try { return (dev.AudioEndpointVolume.MasterVolumeLevelScalar, dev.AudioEndpointVolume.Mute); }
        catch { return null; }
    }

    public void SetMasterVolume(float volume)
    {
        if (DefaultDeviceId == null || !_devices.TryGetValue(DefaultDeviceId, out var entry)) return;
        var dev = entry.Device;
        try { dev.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f); } catch { }
    }

    public void SetMasterMuted(bool muted)
    {
        if (DefaultDeviceId == null || !_devices.TryGetValue(DefaultDeviceId, out var entry)) return;
        var dev = entry.Device;
        try { dev.AudioEndpointVolume.Mute = muted; } catch { }
    }

    public string DeviceName(string? id) =>
        id == null ? "System default" : _store.Data.KnownDevices.GetValueOrDefault(id, "Unknown device");

    // ---------------------------------------------------------------- process lookup

    /// <summary>
    /// Work out which exe a session belongs to: from the process if we can open it, otherwise from the
    /// session identifier, which embeds the exe path ("...|\Device\HarddiskVolume3\...\Discord.exe%b{...}").
    /// </summary>
    ProcInfo? GetProcInfo(uint pid, string? sessionIdentifier)
    {
        if (pid != 0 && _procCache.TryGetValue(pid, out var cached)) return cached;

        // Sandboxed processes (Chrome, Discord, Spotify and other Chromium apps) refuse to be opened,
        // so fall back to the exe path Windows embeds in the session identifier.
        string? path = (pid != 0 ? QueryImagePath(pid) : null) ?? DosPath(NtPathFromSessionIdentifier(sessionIdentifier));
        string? key = null;
        string? display = null;
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

        if (key == null && NtPathFromSessionIdentifier(sessionIdentifier) is string ntPath)
        {
            var exe = ntPath[(ntPath.LastIndexOf('\\') + 1)..];
            key = exe.ToLowerInvariant();
            display = System.IO.Path.GetFileNameWithoutExtension(exe);
        }

        if (key == null && pid != 0)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                key = (p.ProcessName + ".exe").ToLowerInvariant();
                display = p.ProcessName;
            }
            catch { }
        }

        if (key == null) return null;
        var info = new ProcInfo(key, path, display ?? key);
        if (pid != 0) _procCache[pid] = info;
        return info;
    }

    /// <summary>"{...}|\\Device\\HarddiskVolume3\\...\\Discord.exe%b{...}" -> "\\Device\\HarddiskVolume3\\...\\Discord.exe"</summary>
    static string? NtPathFromSessionIdentifier(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        int bar = id.IndexOf('|');
        if (bar < 0) return null;
        var rest = id[(bar + 1)..];
        int tail = rest.IndexOf("%b", StringComparison.Ordinal);
        if (tail >= 0) rest = rest[..tail];
        return rest.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? rest : null;
    }

    /// <summary>Turn "\\Device\\HarddiskVolume3\\x\\y.exe" into "C:\\x\\y.exe" so we can read its icon and description.</summary>
    static string? DosPath(string? ntPath)
    {
        if (ntPath == null) return null;
        if (ntPath.Length > 2 && ntPath[1] == ':') return File.Exists(ntPath) ? ntPath : null;
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var letter = drive.TrimEnd('\\');
            var sb = new StringBuilder(260);
            if (QueryDosDevice(letter, sb, (uint)sb.Capacity) == 0) continue;
            var device = sb.ToString();
            if (ntPath.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var dos = letter + ntPath[device.Length..];
                return File.Exists(dos) ? dos : null;
            }
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, uint max);

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
