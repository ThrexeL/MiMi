using System.Windows.Input;
using System.Windows.Threading;
using MiMi.Agents;

namespace MiMi.App.UI;

/// <summary>
/// The "ImpressiveBot" tile in Settings: is the status mirror installed, and what is
/// ImpressiveBot doing right now. Reads MiMi's own session table through
/// <see cref="ImpressiveBotStatusReader"/>, so it can never disagree with the island.
///
/// Nothing here decides anything: approvals are answered in ImpressiveBot, and a missing
/// CLI is reported as "not installed" rather than guessed at.
/// </summary>
public sealed class StatusIndicatorViewModel : ObservableObject
{
    private readonly Func<ImpressiveBotStatus> _read;
    private readonly DispatcherTimer? _timer;

    private ImpressiveBotStatus _status;
    private string _error = "";

    public string Title => "ImpressiveBot";

    /// <summary>"live", "idle", "waiting for you", "not installed", ...</summary>
    public string Status => _status.StateLabel;

    /// <summary>What is happening, plus what to do when the mirror is missing.</summary>
    public string Detail => _error.Length > 0 ? _error : _status.Detail;

    public bool Healthy => _status.IsHealthy;

    /// <summary>Green when healthy, amber when something needs the person, grey when absent.</summary>
    public string Accent => _status.Link == ImpressiveBotLink.NotInstalled ? "#9A9AA0"
        : _status.FailedCount > 0 ? "#FF6B6B"
        : _status.WaitingCount > 0 ? "#FFB454"
        : _status.IsHealthy ? "#5DD97C"
        : "#9A9AA0";

    /// <summary>Approvals are never answered here; the note keeps that explicit.</summary>
    public string ApprovalNote => ImpressiveBotStatus.ApprovalNote;

    public ICommand RefreshCommand { get; }

    /// <summary>Live tile: reads the island's session table on every refresh.</summary>
    public static StatusIndicatorViewModel ForPresenter(IslandPresenter presenter) =>
        new(presenter.ImpressiveBotStatus, TimeSpan.FromSeconds(5));

    /// <summary>Testable core: any status source, optional refresh timer.</summary>
    internal StatusIndicatorViewModel(Func<ImpressiveBotStatus> read, TimeSpan refreshEvery)
    {
        _read = read;
        _status = new ImpressiveBotStatus(ImpressiveBotLink.NotInstalled, "",
            Array.Empty<ImpressiveBotBotStatus>());
        RefreshCommand = new RelayCommand(Refresh);
        if (refreshEvery > TimeSpan.Zero)
        {
            _timer = new DispatcherTimer { Interval = refreshEvery };
            _timer.Tick += OnTick;
            _timer.Start();
        }
        Refresh();
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    public void Stop()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    /// <summary>Synchronous read: the session table is in memory, so this never blocks.</summary>
    public void Refresh()
    {
        try
        {
            _status = _read();
            _error = "";
        }
        catch (Exception ex)
        {
            // A status tile must never take Settings down with it.
            _error = "Status unavailable: " + ex.Message;
        }
        Raise(nameof(Status));
        Raise(nameof(Detail));
        Raise(nameof(Healthy));
        Raise(nameof(Accent));
    }
}
