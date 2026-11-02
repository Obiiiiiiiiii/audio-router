using System.Runtime.InteropServices;

namespace AudioRouter;

/// <summary>
/// Per-app output routing through Windows' undocumented IAudioPolicyConfigFactory
/// (the same mechanism behind Settings > Sound > "App volume and device preferences").
/// Calls go straight through the vtable so we don't depend on WinRT interop support.
/// </summary>
sealed unsafe class AppRouting
{
    // IID changed in build 21390; Windows 10 (up to 22H2, build 19045) uses the "downlevel" one.
    static readonly Guid IidDownlevel = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");
    static readonly Guid Iid21H2 = new("ab3d4648-e242-459f-b02f-541c70306324");

    // Vtable slots: IUnknown (3) + IInspectable (3) + 19 methods we don't use.
    const int SlotSetPersisted = 25;
    const int SlotGetPersisted = 26;

    const string RenderPrefix = @"\\?\SWD#MMDEVAPI#";
    const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    const int ERender = 0;
    const int EConsole = 0;
    const int EMultimedia = 1;

    IntPtr _factory;

    public bool Available => _factory != IntPtr.Zero;

    public AppRouting()
    {
        var preferred = Environment.OSVersion.Version.Build >= 21390 ? Iid21H2 : IidDownlevel;
        var other = preferred == Iid21H2 ? IidDownlevel : Iid21H2;
        _factory = TryGetFactory(preferred);
        if (_factory == IntPtr.Zero) _factory = TryGetFactory(other);

        if (Available) Log.Info($"Per-app routing available (Windows build {Environment.OSVersion.Version.Build})");
        else Log.Error("Per-app routing is NOT available on this Windows build; volume rules will still work");
    }

    static IntPtr TryGetFactory(Guid iid)
    {
        IntPtr hClass = IntPtr.Zero;
        try
        {
            const string className = "Windows.Media.Internal.AudioPolicyConfig";
            Check(WindowsCreateString(className, (uint)className.Length, out hClass));
            int hr = RoGetActivationFactory(hClass, ref iid, out var factory);
            return hr >= 0 ? factory : IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Log.Error($"RoGetActivationFactory failed for {iid}", ex);
            return IntPtr.Zero;
        }
        finally
        {
            if (hClass != IntPtr.Zero) WindowsDeleteString(hClass);
        }
    }

    /// <summary>Route a process to a device. deviceId null = follow the system default.</summary>
    public bool SetAppOutput(uint processId, string? deviceId)
    {
        if (!Available || processId == 0) return false;

        IntPtr hDevice = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                var full = RenderPrefix + deviceId + RenderSuffix;
                Check(WindowsCreateString(full, (uint)full.Length, out hDevice));
            }

            var vtbl = *(IntPtr**)_factory;
            var set = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)vtbl[SlotSetPersisted];
            int hr1 = set(_factory, processId, ERender, EMultimedia, hDevice);
            int hr2 = set(_factory, processId, ERender, EConsole, hDevice);
            if (hr1 < 0 || hr2 < 0)
            {
                Log.Error($"SetPersistedDefaultAudioEndpoint pid={processId} failed: 0x{hr1:X8} / 0x{hr2:X8}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Routing pid={processId} failed", ex);
            return false;
        }
        finally
        {
            if (hDevice != IntPtr.Zero) WindowsDeleteString(hDevice);
        }
    }

    /// <summary>The device Windows currently has pinned for this process, or null if it follows the default.</summary>
    public string? GetAppOutput(uint processId)
    {
        if (!Available || processId == 0) return null;
        try
        {
            var vtbl = *(IntPtr**)_factory;
            var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr*, int>)vtbl[SlotGetPersisted];
            IntPtr hDevice = IntPtr.Zero;
            int hr = get(_factory, processId, ERender, EMultimedia, &hDevice);
            if (hr < 0 || hDevice == IntPtr.Zero) return null;
            try
            {
                var raw = WindowsGetStringRawBuffer(hDevice, out var len);
                var full = new string((char*)raw, 0, (int)len);
                if (full.StartsWith(RenderPrefix, StringComparison.OrdinalIgnoreCase)) full = full[RenderPrefix.Length..];
                if (full.EndsWith(RenderSuffix, StringComparison.OrdinalIgnoreCase)) full = full[..^RenderSuffix.Length];
                return string.IsNullOrEmpty(full) ? null : full;
            }
            finally
            {
                WindowsDeleteString(hDevice);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Reading route for pid={processId} failed", ex);
            return null;
        }
    }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}
