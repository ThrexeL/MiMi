using System.Runtime.InteropServices;

namespace MiMi.App.Interop;

// Core Audio (WASAPI endpoint volume) interop — the documented Windows way to read,
// set, and observe master volume/mute and audio-device changes. Event-driven:
// IAudioEndpointVolumeCallback + IMMNotificationClient, no polling loops.
// Interfaces are namespace-level (internal) so the monitor classes can implement them.

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterNotificationClient(IMMNotificationClient client);
    [PreserveSig] int UnregisterNotificationClient(IMMNotificationClient client);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
}

[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey
{
    public Guid FormatId;
    public int Pid;
}

[StructLayout(LayoutKind.Explicit)]
public struct PropVariant
{
    [FieldOffset(0)] public short VariantType;
    [FieldOffset(8)] public IntPtr PointerValue;
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback client);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback client);
    [PreserveSig] int GetChannelCount(out int count);
    [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float level);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(IntPtr notifyData);
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioVolumeNotificationData
{
    public Guid EventContext;
    [MarshalAs(UnmanagedType.Bool)] public bool Muted;
    public float MasterVolume;
    public uint Channels;
    // channel volume floats follow — MiMi reads only the master values
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

internal static class CoreAudio
{
    public static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private const int CLSCTX_ALL = 23;
    public const int eRender = 0;
    public const int eMultimedia = 1;

    public static readonly PropertyKey PKEY_Device_FriendlyName = new()
    {
        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        Pid = 14,
    };

    /// <summary>Creates the default-render endpoint's volume interface, or null when
    /// no audio device exists (desktops without speakers/headphones).</summary>
    internal static (IAudioEndpointVolume? Volume, IMMDevice? Device) GetDefaultRenderVolume()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0)
            return (null, null);
        var iid = IID_IAudioEndpointVolume;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var iface) != 0)
            return (null, device);
        return ((IAudioEndpointVolume)iface, device);
    }

    internal static string? GetDeviceFriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return null;
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var value) != 0) return null;
            if (value.VariantType != 31 /* VT_LPWSTR */) return null;
            return Marshal.PtrToStringUni(value.PointerValue);
        }
        catch { return null; }
    }
}

/// <summary>
/// Master volume observer: registers a Core Audio callback so hardware volume keys
/// and app-initiated changes surface as events. Event-driven — no polling.
/// </summary>
public sealed class MasterVolumeMonitor : IAudioEndpointVolumeCallback, IDisposable
{
    private IAudioEndpointVolume? _volume;

    public bool IsAvailable { get; private set; }
    public float Volume { get; private set; }
    public bool Muted { get; private set; }

    public event Action<float, bool>? VolumeChanged;

    public void Start()
    {
        try
        {
            (_volume, _) = CoreAudio.GetDefaultRenderVolume();
            if (_volume is null) { IsAvailable = false; return; }
            _volume.GetMasterVolumeLevelScalar(out var level);
            _volume.GetMute(out var mute);
            Volume = level;
            Muted = mute;
            IsAvailable = true;
            _volume.RegisterControlChangeNotify(this);
            VolumeChanged?.Invoke(level, mute);
        }
        catch (COMException)
        {
            _volume = null;
            IsAvailable = false;
        }
    }

    public int OnNotify(IntPtr notifyData)
    {
        try
        {
            var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(notifyData);
            Volume = data.MasterVolume;
            Muted = data.Muted;
            IsAvailable = true;
            VolumeChanged?.Invoke(data.MasterVolume, data.Muted);
        }
        catch { /* malformed notification: ignore */ }
        return 0;
    }

    public void Restart()
    {
        Dispose();
        Start();
    }

    public bool SetVolume(float level)
    {
        if (!IsAvailable || _volume is null || !float.IsFinite(level) || level is < 0f or > 1f) return false;
        var ctx = Guid.Empty;
        return _volume.SetMasterVolumeLevelScalar(level, ref ctx) == 0;
    }

    public bool ToggleMute()
    {
        if (!IsAvailable || _volume is null) return false;
        var ctx = Guid.Empty;
        return _volume.SetMute(!Muted, ref ctx) == 0;
    }

    public void Dispose()
    {
        try { _volume?.UnregisterControlChangeNotify(this); } catch { }
        if (_volume is not null)
        {
            try { Marshal.ReleaseComObject(_volume); } catch { }
            _volume = null;
        }
        IsAvailable = false;
    }
}

/// <summary>
/// Default audio render device observer (headphones/Bluetooth/speakers). Raises an
/// event whenever the default device changes, with its friendly name when readable.
/// </summary>
public sealed class AudioDeviceMonitor : IMMNotificationClient, IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private string? _lastDefaultId;

    public event Action<string?, string?>? DefaultDeviceChanged;

    public void Start()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (_enumerator.GetDefaultAudioEndpoint(CoreAudio.eRender, CoreAudio.eMultimedia, out var device) == 0)
                device.GetId(out _lastDefaultId);
            _enumerator.RegisterNotificationClient(this);
        }
        catch (COMException) { /* audio subsystem unavailable */ }
    }

    public int OnDeviceStateChanged(string deviceId, int newState) => 0;
    public int OnDeviceAdded(string deviceId) => 0;
    public int OnDeviceRemoved(string deviceId) => 0;

    public int OnDefaultDeviceChanged(int dataFlow, int role, string defaultDeviceId)
    {
        if (dataFlow != CoreAudio.eRender || role != CoreAudio.eMultimedia) return 0;
        if (string.Equals(defaultDeviceId, _lastDefaultId, StringComparison.Ordinal)) return 0;
        _lastDefaultId = defaultDeviceId;

        string? friendly = null;
        try
        {
            if (_enumerator?.GetDevice(defaultDeviceId, out var device) == 0)
                friendly = CoreAudio.GetDeviceFriendlyName(device);
        }
        catch { /* name optional */ }
        DefaultDeviceChanged?.Invoke(defaultDeviceId, friendly);
        return 0;
    }

    public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;

    public void Dispose()
    {
        try { _enumerator?.UnregisterNotificationClient(this); } catch { }
    }
}
