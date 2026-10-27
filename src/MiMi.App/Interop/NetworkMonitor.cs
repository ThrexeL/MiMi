using System.Net.NetworkInformation;

namespace MiMi.App.Interop;

/// <summary>
/// Network state via System.Net.NetworkInformation — event-driven
/// (NetworkAvailabilityChanged + NetworkInterfaceChanged). Reports truthful local
/// connectivity: Wi-Fi vs Ethernet by interface type, no internet-access claims,
/// no external pings.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    public sealed record NetworkState(bool AnyUp, string KindLabel);

    public event Action<NetworkState>? StateChanged;

    private bool _lastUp;
    private string _lastKind = "";
    private bool _initialized;

    public NetworkState Read()
    {
        var anyUp = NetworkInterface.GetIsNetworkAvailable();
        var kind = "";
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel);
            if (up is not null)
                kind = up.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet => "Ethernet",
                    _ => up.NetworkInterfaceType.ToString(),
                };
        }
        catch { /* enumeration can fail on odd adapters */ }
        return new NetworkState(anyUp, kind);
    }

    public void Start()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        Announce(); // initial state
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Announce();
    private void OnAddressChanged(object? sender, EventArgs e) => Announce();

    private void Announce()
    {
        var state = Read();
        if (_initialized && state.AnyUp == _lastUp && state.KindLabel == _lastKind) return;
        _initialized = true;
        _lastUp = state.AnyUp;
        _lastKind = state.KindLabel;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
    }
}
