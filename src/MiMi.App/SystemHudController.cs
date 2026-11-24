using System.Windows;
using System.Windows.Threading;
using MiMi.App.UI;
using MiMi.App.Interop;
using MiMi.Core;

namespace MiMi.App;

/// <summary>
/// Wires the native system monitors (volume, battery, network, audio devices,
/// keyboard state, laptop brightness) to island HUDs. All inputs are event-driven;
/// the keyboard HUD piggybacks on the island's existing 1 Hz tick. Settings gate each HUD; the same
/// title coalesces so rapid volume keys update one toast.
/// </summary>
public sealed class SystemHudController : IDisposable
{
    private readonly ConfigStore _config;
    private readonly IslandPresenter _presenter;
    private readonly Dispatcher _dispatcher;

    private readonly BrightnessMonitorLifetime _brightness;
    private bool _disposed;
    private readonly MasterVolumeMonitor _volume = new();
    private readonly AudioDeviceMonitor _audioDevices = new();
    private readonly NetworkMonitor _network = new();
    private readonly KeyboardHudPolicy _keyboardPolicy = new();
    private BatteryNoticePolicy _batteryPolicy = new();
    private string _lastLayout = "";

    private DateTimeOffset _volumeHudUntil = DateTimeOffset.MinValue;

    public SystemHudController(ConfigStore config, IslandPresenter presenter, Dispatcher dispatcher)
    {
        dispatcher.VerifyAccess();
        _config = config;
        _presenter = presenter;
        _dispatcher = dispatcher;

        _volume.Start();
        _volume.VolumeChanged += (level, muted) => _dispatcher.BeginInvoke(() => OnVolumeChanged(level, muted));

        _audioDevices.Start();
        _audioDevices.DefaultDeviceChanged += (_, friendly) => _dispatcher.BeginInvoke(() =>
        {
            _volume.Restart();
            if (!_config.Current.HeadphoneHudEnabled) return;
            var name = string.IsNullOrWhiteSpace(friendly) ? "Audio device" : friendly!;
            _presenter.PushHud("\uE7F4", "Audio device changed", name, duration: TimeSpan.FromSeconds(2.2));
        });

        _network.Start();
        _network.StateChanged += state => _dispatcher.BeginInvoke(() =>
        {
            if (!_config.Current.NetworkHudEnabled) return;
            if (state.AnyUp)
                _presenter.PushHud("\uE701", state.KindLabel.Length > 0 ? $"{state.KindLabel} connected" : "Network connected",
                    "Local network available", duration: TimeSpan.FromSeconds(2.2));
            else
                _presenter.PushHud("\uEB55", "Network disconnected", "No active connection",
                    duration: TimeSpan.FromSeconds(2.2));
        });

        ReportBatteryOnce(); // startup baseline (no notice), low-battery check

        _brightness = new BrightnessMonitorLifetime(
            callback => new LaptopBrightnessMonitor(callback),
            action => _dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task,
            _dispatcher.VerifyAccess,
            percent =>
            {
                if (_disposed || !_config.Current.BrightnessHudEnabled) return;
                // A stable title gives all levels one HUD identity (no stacked alerts).
                _presenter.PushHud("\uE706", "Brightness", $"{percent}%",
                    progress: percent / 100.0, duration: TimeSpan.FromSeconds(1.2));
            });
        _config.Changed += OnBrightnessSettingsChanged;
        _brightness.SetEnabled(_config.Current.BrightnessHudEnabled);
    }

    private void OnBrightnessSettingsChanged(AppSettings settings)
    {
        // ConfigStore is UI-thread-owned. Marshal defensively and read the latest
        // preference so a queued notification cannot restore an obsolete value.
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnBrightnessSettingsChanged(_config.Current));
            return;
        }
        if (!_disposed) _brightness.SetEnabled(_config.Current.BrightnessHudEnabled);
    }

    private int _lastShownPercent = -1;
    private bool _lastMuteShown;
    private DateTimeOffset _lastVolumePushUtc;

    /// <summary>Volume/mute changes: the HUD re-pushes only when the shown value
    /// changes (same-value callbacks are absorbed; the alert's own expiry removes it
    /// 1.2 s after the last change). Rapid keypresses update one toast, no stacking.</summary>
    public (float Level, bool Muted, bool IsAvailable) GetVolumeState() =>
        (_volume.Volume, _volume.Muted, _volume.IsAvailable);

    public bool SetVolume(float level) => _volume.SetVolume(Math.Clamp(level, 0f, 1f));
    public bool ToggleMute() => _volume.ToggleMute();

    private void OnVolumeChanged(float level, bool muted)
    {
        if (!_config.Current.VolumeHudEnabled) return;
        var percent = (int)Math.Round(level * 100);
        var now = DateTimeOffset.UtcNow;
        if (percent == _lastShownPercent && muted == _lastMuteShown
            && now < _lastVolumePushUtc + TimeSpan.FromSeconds(1.1))
            return;

        _lastShownPercent = percent;
        _lastMuteShown = muted;
        _lastVolumePushUtc = now;
        _dispatcher.BeginInvoke(() =>
        {
            if (muted)
                _presenter.PushHud("\uE74F", "Muted", "Sound is off", duration: TimeSpan.FromSeconds(1.2), hudId: "volume");
            else
                _presenter.PushHud("\uE767", "Volume", $"{percent}%", progress: level,
                    duration: TimeSpan.FromSeconds(1.2), hudId: "volume");
        });
    }

    private void ReportBatteryOnce()
    {
        var snap = PowerMonitor.Read();
        if (!snap.HasBattery) return; // desktops: the battery module renders nothing
        _batteryPolicy.Evaluate(snap.Charging, snap.Percent);
    }

    /// <summary>Power events (plug/unplug/level) land here from the island wndproc.</summary>
    public void OnPowerStatusChanged()
    {
        if (!_config.Current.BatteryHudEnabled) return;
        var snap = PowerMonitor.Read();
        if (!snap.HasBattery || snap.PercentUnknown) return;
        var notice = _batteryPolicy.Evaluate(snap.Charging, snap.Percent);
        switch (notice)
        {
            case BatteryNoticePolicy.NoticeKind.ChargingConnected:
                _presenter.PushHud("\uE709", $"Charging — {snap.Percent}%", "Power connected",
                    progress: snap.Percent / 100.0);
                break;
            case BatteryNoticePolicy.NoticeKind.ChargingDisconnected:
                _presenter.PushHud("\uE70A", $"On battery — {snap.Percent}%", "Running on battery power",
                    progress: snap.Percent / 100.0);
                break;
            case BatteryNoticePolicy.NoticeKind.LowBattery:
                _presenter.PushHud("\uE850", $"Battery low — {snap.Percent}%", "Consider plugging in",
                    progress: snap.Percent / 100.0, duration: TimeSpan.FromSeconds(3.5));
                break;
        }
    }

    /// <summary>Caps Lock + keyboard layout, evaluated on the 1 Hz island tick while visible.</summary>
    public void OnKeyboardTick()
    {
        if (!_config.Current.CapsLockHudEnabled) return;
        var caps = Interop.KeyboardHud.CapsLockOn;
        var layout = Interop.KeyboardHud.CurrentLayoutLabel();
        var (kind, newLayout) = _keyboardPolicy.Evaluate(caps, layout);
        _lastLayout = newLayout;
        switch (kind)
        {
            case KeyboardHudPolicy.Kind.CapsOn:
                _presenter.PushHud("\uE752", "Caps Lock ON", "", duration: TimeSpan.FromSeconds(1.5));
                break;
            case KeyboardHudPolicy.Kind.CapsOff:
                _presenter.PushHud("\uE752", "Caps Lock OFF", "", duration: TimeSpan.FromSeconds(1.2));
                break;
            case KeyboardHudPolicy.Kind.LayoutChanged when newLayout.Length > 0:
                _presenter.PushHud("\uE7F2", newLayout, "Input language", duration: TimeSpan.FromSeconds(1.5));
                break;
        }
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _config.Changed -= OnBrightnessSettingsChanged;
        _brightness.Dispose();
        _volume.Dispose();
        _audioDevices.Dispose();
        _network.Dispose();
    }
}
