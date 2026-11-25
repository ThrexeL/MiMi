namespace MiMi.Core;

/// <summary>
/// Which utility panel the island should show, resolved from user pins, the Quick
/// Access hub, and the running Pomodoro. Resolution order (highest first):
/// hub → user-pinned utility (tray/interaction) → running focus timer → none.
///
/// Pure and testable; the presenter delegates its pin state here and maps the
/// resolved key to a view.
/// </summary>
public sealed class UtilityPinState
{
    public const string Hub = "hub";
    public const string Pomodoro = "pomodoro";
    public const string Water = "water";
    public const string Todo = "todo";
    public const string Shelf = "shelf";
    public const string Volume = "volume";
    public const string Brightness = "brightness";
    public const string Clipboard = "clipboard";
    public const string Widgets = "widgets";

    /// <summary>Tray/interaction order used when several utilities are pinned.</summary>
    public static readonly IReadOnlyList<string> PinOrder =
        [Pomodoro, Water, Todo, Shelf, Volume, Brightness, Clipboard, Widgets];

    private readonly HashSet<string> _pins = new(StringComparer.Ordinal);
    private bool _hubOpen;

    public event Action? Changed;

    public bool IsHubOpen => _hubOpen;
    public bool IsPinned(string kind) => _pins.Contains(kind);

    /// <summary>The utility panel to show right now, or null (normal content).</summary>
    public string? ActiveUtility
    {
        get
        {
            if (_hubOpen) return Hub;
            foreach (var kind in PinOrder)
                if (_pins.Contains(kind)) return kind;
            return null;
        }
    }

    public void SetPin(string kind, bool pinned)
    {
        var changed = pinned ? _pins.Add(kind) : _pins.Remove(kind);
        if (changed) Changed?.Invoke();
    }

    public void TogglePin(string kind) => SetPin(kind, !_pins.Contains(kind));

    public void SetHub(bool open)
    {
        if (_hubOpen == open) return;
        _hubOpen = open;
        Changed?.Invoke();
    }

    /// <summary>Atomically closes the hub and opens a selected utility in a single change event.</summary>
    public void OpenUtilityFromHub(string kind)
    {
        _hubOpen = false;
        _pins.Clear();
        _pins.Add(kind);
        Changed?.Invoke();
    }

    public void ClearUserPins()
    {
        if (_pins.Count == 0 && !_hubOpen) return;
        _pins.Clear();
        _hubOpen = false;
        Changed?.Invoke();
    }
}
