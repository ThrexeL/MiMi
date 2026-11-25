using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using MiMi.Core;

namespace MiMi.App.Interop;

/// <summary>Reads and controls the active laptop backlight through WMI. No external
/// monitor DDC/CI, no synthetic values, and no keyboard interception.</summary>
public static class LaptopBrightnessControl
{
    public sealed record Snapshot(bool IsAvailable, int Percent, string? InstanceName);

    public static Task<Snapshot> ReadAsync() => Task.Run(Read);

    private static Snapshot Read()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Options.Timeout = TimeSpan.FromSeconds(5);
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT InstanceName, Active, CurrentBrightness FROM WmiMonitorBrightness"),
                new EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = false });
            using var results = searcher.Get();
            foreach (ManagementObject monitor in results)
            {
                using (monitor)
                {
                    if (monitor["Active"] is not true || monitor["InstanceName"] is not string name
                        || string.IsNullOrWhiteSpace(name) || monitor["CurrentBrightness"] is not byte percent
                        || percent > 100) continue;
                    return new Snapshot(true, percent, name);
                }
            }
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            Logger.Info($"Brightness control unavailable: {ex.GetType().Name}: {ex.Message}");
        }
        return new Snapshot(false, 0, null);
    }

    public static Task<bool> SetAsync(string instanceName, int percent) => Task.Run(() => Set(instanceName, percent));

    private static bool Set(string instanceName, int percent)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return false;
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Options.Timeout = TimeSpan.FromSeconds(5);
            var escaped = instanceName.Replace("'", "''", StringComparison.Ordinal);
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT * FROM WmiMonitorBrightnessMethods WHERE InstanceName = '{escaped}'"));
            using var results = searcher.Get();
            using var method = results.Cast<ManagementObject>().FirstOrDefault();
            if (method is null) return false;
            using var parameters = method.GetMethodParameters("WmiSetBrightness");
            parameters["Timeout"] = (uint)0;
            parameters["Brightness"] = (byte)percent;
            using var result = method.InvokeMethod("WmiSetBrightness", parameters, null);
            return result["ReturnValue"] is uint code && code == 0;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            Logger.Info($"Brightness change unavailable: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool IsUnavailable(Exception ex) =>
        ex is ManagementException or UnauthorizedAccessException or SecurityException
            or COMException or InvalidOperationException or NotSupportedException;
}
