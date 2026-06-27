using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace STS2BetterRockPaperScissors.Settings;

/// <summary>
/// Persisted, JSON-backed config for the mod plus its RitsuLib settings page.
/// Currently just the per-round gesture countdown.
/// </summary>
public static class ModConfig
{
    private const string DataKey = "config";
    private const string FileName = "better_rock_paper_scissors";

    private const int MinCountdownSeconds = 1;
    private const int DefaultCountdownSeconds = 3;
    private const int MaxCountdownSeconds = 30;

    /// <summary>The serialized config model. Kept tiny on purpose.</summary>
    private class Data
    {
        public int CountdownSeconds = DefaultCountdownSeconds;
    }

    /// <summary>Countdown in seconds before a move is auto-picked, clamped to the valid range.</summary>
    public static int CountdownSeconds
    {
        get
        {
            var value = Store.Get<Data>(DataKey).CountdownSeconds;
            if (value < MinCountdownSeconds) value = MinCountdownSeconds;
            if (value > MaxCountdownSeconds) value = MaxCountdownSeconds;
            return value;
        }
    }

    private static ModDataStore Store => RitsuLibFramework.GetDataStore(Entry.ModId);

    /// <summary>Registers the persistence slot. Call once during Init, before reading values.</summary>
    public static void RegisterData()
    {
        using (RitsuLibFramework.BeginModDataRegistration(Entry.ModId, true))
        {
            Store.Register<Data>(DataKey, FileName, SaveScope.Global, () => new Data());
        }
    }

    /// <summary>Registers the RitsuLib settings page. Call once during Init.</summary>
    public static void RegisterSettings()
    {
        RitsuLibFramework.RegisterModSettings(Entry.ModId, page => page
            .WithTitle(ModSettingsText.Literal("Better Rock Paper Scissors"))
            .AddSection("general", section => section
                .AddIntSlider(
                    "countdown_seconds",
                    ModSettingsText.Literal("Gesture countdown (seconds)"),
                    new ModSettingsCallbackValueBinding<int>(
                        Entry.ModId,
                        "countdown_seconds",
                        SaveScope.Global,
                        () => Store.Get<Data>(DataKey).CountdownSeconds,
                        value => Store.Get<Data>(DataKey).CountdownSeconds = value,
                        () => Store.Save(DataKey)),
                    MinCountdownSeconds,
                    MaxCountdownSeconds,
                    1)));
    }
}
