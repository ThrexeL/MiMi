namespace MiMi.Core;

/// <summary>Small, deterministic moon-phase helper for the local Widgets page.</summary>
public static class MoonPhaseCalculator
{
    private static readonly DateTime NewMoonEpochUtc = new(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);
    private const double SynodicMonthDays = 29.530588853;

    public static (double Phase, double Illumination) Calculate(DateTime localDate)
    {
        var utc = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Utc).AddHours(12);
        var days = (utc - NewMoonEpochUtc).TotalDays;
        var phase = (days / SynodicMonthDays) % 1.0;
        if (phase < 0) phase += 1.0;
        var illumination = (1 - Math.Cos(2 * Math.PI * phase)) / 2;
        return (phase, Math.Clamp(illumination, 0, 1));
    }

    public static string Name(double phase) => phase switch
    {
        < 0.033 or > 0.967 => "New moon",
        < 0.217 => "Waxing crescent",
        < 0.283 => "First quarter",
        < 0.467 => "Waxing gibbous",
        < 0.533 => "Full moon",
        < 0.717 => "Waning gibbous",
        < 0.783 => "Last quarter",
        _ => "Waning crescent",
    };

    public static string Glyph(double phase) => phase switch
    {
        < 0.033 or > 0.967 => "●",
        < 0.217 => "◔",
        < 0.283 => "◑",
        < 0.467 => "◕",
        < 0.533 => "○",
        < 0.717 => "◕",
        < 0.783 => "◑",
        _ => "◔",
    };
}
