using System.Runtime.InteropServices;

namespace MiMi.App.Interop;

/// <summary>
/// Battery and power events via the documented Win32 power APIs.
/// GetSystemPowerStatus gives real percentages/charging state; WM_POWERBROADCAST
/// (PBT_APMPOWERSTATUSCHANGE) delivers plug/unplug and level-change events —
/// event-driven, no polling.
/// </summary>
public static class PowerMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;       // 0 offline, 1 online, 255 unknown
        public byte BatteryFlag;        // 1 high, 2 low, 4 critical, 8 charging, 128 no battery, 255 unknown
        public byte BatteryLifePercent; // 0-100, 255 unknown
        public byte Reserved;
        public uint BatteryLifeTime;    // seconds, -1 unknown
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint PBT_APMPOWERSTATUSCHANGE = 0x0010;

    public sealed record BatterySnapshot(bool HasBattery, bool Charging, int Percent, bool PercentUnknown);

    public static BatterySnapshot Read()
    {
        if (!GetSystemPowerStatus(out var status)) return new(false, false, 0, true);
        var hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255 || status.ACLineStatus == 1;
        // Some desktops report AC only with no battery flag; treat "no battery" bit as authoritative.
        if ((status.BatteryFlag & 128) != 0) hasBattery = false;
        var unknown = status.BatteryLifePercent == 255;
        return new BatterySnapshot(
            HasBattery: hasBattery,
            Charging: (status.BatteryFlag & 8) != 0,
            Percent: unknown ? 0 : status.BatteryLifePercent,
            PercentUnknown: unknown);
    }
}

/// <summary>
/// Pure notice policy for battery transitions (testable): a notice fires only on a
/// real state change — charger connected, charger unplugged, crossing the low-battery
/// threshold downward. Never repeats for the same state.
/// </summary>
public sealed class BatteryNoticePolicy
{
    public const int LowBatteryThreshold = 20;

    public enum NoticeKind { None, ChargingConnected, ChargingDisconnected, LowBattery }

    private bool _lastCharging;
    private bool _lastLow;
    private bool _initialized;

    public NoticeKind Evaluate(bool charging, int percent)
    {
        if (!_initialized)
        {
            _initialized = true;
            _lastCharging = charging;
            _lastLow = percent <= LowBatteryThreshold;
            return NoticeKind.None; // never announce state that predates startup
        }

        NoticeKind notice;
        if (charging != _lastCharging)
        {
            notice = charging ? NoticeKind.ChargingConnected : NoticeKind.ChargingDisconnected;
            _lastCharging = charging;
        }
        else if (!charging && percent <= LowBatteryThreshold && !_lastLow)
        {
            notice = NoticeKind.LowBattery;
            _lastLow = true;
        }
        else notice = NoticeKind.None;

        if (percent > LowBatteryThreshold) _lastLow = false;
        return notice;
    }
}
