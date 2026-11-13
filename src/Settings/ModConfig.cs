using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace STS2BetterRockPaperScissors.Settings;

/// <summary>
/// Persisted, JSON-backed config for the mod plus its RitsuLib settings page.
/// Stores the gesture countdown, re-choose option, pause hand fix, and conflict toggles.
/// </summary>
public static class ModConfig
{
    private const string DataKey = "config";
    private const string FileName = "better_rock_paper_scissors";

    private const int MinCountdownSeconds = 1;
    private const int DefaultCountdownSeconds = 3;
    private const int MaxCountdownSeconds = 30;
    private const bool DefaultAllowReset = false;
    private const bool DefaultConflictRps = false;

    /// <summary>
    /// Hard ceiling on rounds for one conflict fight. A tie-only fight against a player who never picks would
    /// otherwise run until somebody gives up; past this many rounds the host falls back to the stock random
    /// decision, which is exactly what the game would have done without the mod.
    /// </summary>
    internal const int MaxConflictRounds = 16;

    /// <summary>The serialized config model. Kept tiny on purpose.</summary>
    private class Data
    {
        public int CountdownSeconds { get; set; } = DefaultCountdownSeconds;
        public bool AllowReset { get; set; } = DefaultAllowReset;
        public bool RestoreHandsOnResume { get; set; }
        public bool MapConflictRps { get; set; } = DefaultConflictRps;
        public bool EventConflictRps { get; set; } = DefaultConflictRps;
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

    /// <summary>
    /// Host-authoritative: when true, players can clear/re-choose their gesture (a reset button is
    /// shown) and the round always rides the full countdown so there's time to re-choose. When false,
    /// a pick is final and the round resolves as soon as every survivor has picked. Clients follow the
    /// host's value (carried on the round-began message), not their own.
    /// </summary>
    public static bool AllowReset => Store.Get<Data>(DataKey).AllowReset;

    /// <summary>Local option to restore frozen relic-fight hands when they slide back in.</summary>
    public static bool RestoreHandsOnResume => Store.Get<Data>(DataKey).RestoreHandsOnResume;

    /// <summary>
    /// Host-authoritative: when true, a multiplayer map vote that ends up split is decided by a manual
    /// rock-paper-scissors fight instead of the stock random pick, and the winner's voted coordinate is where the
    /// party travels. The client's own value is deliberately ignored — refusing to take part would only starve
    /// the host's countdown and hand that player a random gesture.
    /// </summary>
    public static bool MapConflictRps => Store.Get<Data>(DataKey).MapConflictRps;

    /// <summary>
    /// Host-authoritative: when true, a shared event whose players voted for different options is decided by a
    /// manual rock-paper-scissors fight instead of the stock random pick, and the winner's option is the one that
    /// runs. Same client caveat as <see cref="MapConflictRps"/>.
    /// </summary>
    public static bool EventConflictRps => Store.Get<Data>(DataKey).EventConflictRps;

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
            .WithTitle(ModI18N.Text("settings.title", "Better Rock Paper Scissors"))
            .AddSection("general", section => section
                .AddIntSlider(
                    "countdown_seconds",
                    ModI18N.Text("settings.countdown.label", "Gesture countdown (seconds)"),
                    new ModSettingsValueBinding<Data, int>(
                        Entry.ModId,
                        DataKey,
                        SaveScope.Global,
                        data => data.CountdownSeconds,
                        (data, value) => data.CountdownSeconds = value),
                    MinCountdownSeconds,
                    MaxCountdownSeconds,
                    1)
                .AddToggle(
                    "allow_reset",
                    ModI18N.Text("settings.allow_reset.label", "Allow re-choosing (host only; always uses full countdown)"),
                    new ModSettingsValueBinding<Data, bool>(
                        Entry.ModId,
                        DataKey,
                        SaveScope.Global,
                        data => data.AllowReset,
                        (data, value) => data.AllowReset = value))
                .AddToggle(
                    "restore_hands_on_resume",
                    ModI18N.Text("settings.restore_hands.label", "Fix missing relic-fight hands after pause (local)"),
                    new ModSettingsValueBinding<Data, bool>(
                        Entry.ModId,
                        DataKey,
                        SaveScope.Global,
                        data => data.RestoreHandsOnResume,
                        (data, value) => data.RestoreHandsOnResume = value))
                .AddToggle(
                    "map_conflict_rps",
                    ModI18N.Text("settings.map_conflict.label", "Manual RPS for map path conflicts (host only)"),
                    new ModSettingsValueBinding<Data, bool>(
                        Entry.ModId,
                        DataKey,
                        SaveScope.Global,
                        data => data.MapConflictRps,
                        (data, value) => data.MapConflictRps = value))
                .AddToggle(
                    "event_conflict_rps",
                    ModI18N.Text("settings.event_conflict.label", "Manual RPS for shared event option conflicts (host only)"),
                    new ModSettingsValueBinding<Data, bool>(
                        Entry.ModId,
                        DataKey,
                        SaveScope.Global,
                        data => data.EventConflictRps,
                        (data, value) => data.EventConflictRps = value))));
    }
}
