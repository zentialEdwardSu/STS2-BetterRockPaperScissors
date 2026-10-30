using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace STS2BetterRockPaperScissors.Settings;

/// <summary>
/// Persisted, JSON-backed config for the mod plus its RitsuLib settings page.
/// Currently the per-round gesture countdown, the two conflict-replacement toggles, and the pause-fix switch.
/// </summary>
public static class ModConfig
{
    private const string DataKey = "config";
    private const string FileName = "better_rock_paper_scissors";

    private const int MinCountdownSeconds = 1;
    private const int DefaultCountdownSeconds = 3;
    private const int MaxCountdownSeconds = 30;
    private const bool DefaultAllowReset = false;
    private const bool DefaultRestoreHandsOnResume = false;
    private const bool DefaultConflictRps = true;

    /// <summary>
    /// Hard ceiling on rounds for one conflict fight. A tie-only fight against a player who never picks would
    /// otherwise run until somebody gives up; past this many rounds the host falls back to the stock random
    /// decision, which is exactly what the game would have done without the mod.
    /// </summary>
    internal const int MaxConflictRounds = 16;

    /// <summary>The serialized config model. Kept tiny on purpose.</summary>
    private class Data
    {
        public int CountdownSeconds = DefaultCountdownSeconds;
        public bool AllowReset = DefaultAllowReset;
        public bool RestoreHandsOnResume = DefaultRestoreHandsOnResume;
        public bool MapConflictRps = DefaultConflictRps;
        public bool EventConflictRps = DefaultConflictRps;
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

    /// <summary>
    /// When true, the mod reseats each frozen fighter hand's resting position when the pause/ESC menu
    /// closes, so a hand that retracted off-screen while paused animates back on-screen. This works
    /// around a bug in the STABLE game branch where <c>NHandImage.AnimateIn</c> re-runs the slide-in
    /// tween but never restores a frozen hand's target position, so the pauser's hand stays off-screen
    /// until the relic grab. The BETA branch already fixes this in-engine, so leave this OFF on beta to
    /// avoid fighting the game's own restore. Local/cosmetic only — never touched on non-fighter peers.
    /// </summary>
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
                    new ModSettingsCallbackValueBinding<int>(
                        Entry.ModId,
                        "countdown_seconds",
                        SaveScope.Global,
                        () => Store.Get<Data>(DataKey).CountdownSeconds,
                        value => Store.Get<Data>(DataKey).CountdownSeconds = value,
                        () => Store.Save(DataKey)),
                    MinCountdownSeconds,
                    MaxCountdownSeconds,
                    1)
                .AddToggle(
                    "allow_reset",
                    ModI18N.Text("settings.allow_reset.label", "Allow re-choosing (host only; always uses full countdown)"),
                    new ModSettingsCallbackValueBinding<bool>(
                        Entry.ModId,
                        "allow_reset",
                        SaveScope.Global,
                        () => Store.Get<Data>(DataKey).AllowReset,
                        value => Store.Get<Data>(DataKey).AllowReset = value,
                        () => Store.Save(DataKey)))
                .AddToggle(
                    "restore_hands_on_resume",
                    ModI18N.Text("settings.restore_hands.label", "Fix pause-hidden hands (stable branch only; turn off on beta)"),
                    new ModSettingsCallbackValueBinding<bool>(
                        Entry.ModId,
                        "restore_hands_on_resume",
                        SaveScope.Global,
                        () => Store.Get<Data>(DataKey).RestoreHandsOnResume,
                        value => Store.Get<Data>(DataKey).RestoreHandsOnResume = value,
                        () => Store.Save(DataKey)))
                .AddToggle(
                    "map_conflict_rps",
                    ModI18N.Text("settings.map_conflict.label", "Manual RPS for map path conflicts (host only)"),
                    new ModSettingsCallbackValueBinding<bool>(
                        Entry.ModId,
                        "map_conflict_rps",
                        SaveScope.Global,
                        () => Store.Get<Data>(DataKey).MapConflictRps,
                        value => Store.Get<Data>(DataKey).MapConflictRps = value,
                        () => Store.Save(DataKey)))
                .AddToggle(
                    "event_conflict_rps",
                    ModI18N.Text("settings.event_conflict.label", "Manual RPS for shared event option conflicts (host only)"),
                    new ModSettingsCallbackValueBinding<bool>(
                        Entry.ModId,
                        "event_conflict_rps",
                        SaveScope.Global,
                        () => Store.Get<Data>(DataKey).EventConflictRps,
                        value => Store.Get<Data>(DataKey).EventConflictRps = value,
                        () => Store.Save(DataKey)))));
    }
}
