using System.Runtime.InteropServices;

namespace AudioRouter.Interop;

// Minimal Core Audio session interfaces. We use these instead of NAudio's session wrappers so that
// listing sessions doesn't unregister/re-register notifications on every scan, and so each call's
// HRESULT is visible to us.

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    // IAudioSessionManager
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);
    // IAudioSessionManager2
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
    [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionNotification
{
    [PreserveSig] int OnSessionCreated(IntPtr newSession);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName(IntPtr value, IntPtr eventContext);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath(IntPtr value, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, IntPtr eventContext);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

/// <summary>Our IAudioSessionNotification sink; just pokes a callback (runs on a COM worker thread).</summary>
[ComVisible(true)]
sealed class SessionCreatedSink : IAudioSessionNotification
{
    readonly Action _onCreated;
    public SessionCreatedSink(Action onCreated) => _onCreated = onCreated;

    public int OnSessionCreated(IntPtr newSession)
    {
        try { _onCreated(); } catch { }
        return 0;
    }
}

static class CoreAudio
{
    public const int AUDCLNT_S_NO_SINGLE_PROCESS = 0x08890010;
    const int CLSCTX_ALL = 0x17;
    static Guid IidSessionManager2 = typeof(IAudioSessionManager2).GUID;

    /// <summary>Take ownership of a CoTaskMem string returned by Core Audio.</summary>
    public static string? TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>CoCreateInstance straight to the interface, so there is no managed cast involved.</summary>
    public static IMMDeviceEnumerator CreateDeviceEnumerator()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var iid = typeof(IMMDeviceEnumerator).GUID;
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out var e), "CoCreateInstance(MMDeviceEnumerator)");
        return e;
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsCtx, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IMMDeviceEnumerator instance);

    public static IAudioSessionManager2 ActivateSessionManager(IMMDeviceEnumerator enumerator, string deviceId)
    {
        Check(enumerator.GetDevice(deviceId, out var device), "GetDevice");
        Check(device.Activate(ref IidSessionManager2, CLSCTX_ALL, IntPtr.Zero, out var obj), "Activate(IAudioSessionManager2)");
        return (IAudioSessionManager2)obj;
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    public static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) { try { Marshal.ReleaseComObject(o); } catch { } }
    }
}
