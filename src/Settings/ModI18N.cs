using System.Reflection;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils;

namespace STS2BetterRockPaperScissors.Settings;

/// <summary>
/// Settings-page localization, wired through RitsuLib's <see cref="I18N"/> helper.
///
/// The translation tables are embedded in this assembly as <c>&lt;prefix&gt;.&lt;language&gt;.json</c> (see
/// <see cref="ResourceFolder"/> and the corresponding <c>EmbeddedResource</c> items in the project file), so the
/// settings page stays translatable even though the mod ships as a loose DLL: there is no extra file to deploy,
/// and nothing to lose if a player installs the mod through Steam Workshop.
///
/// RitsuLib resolves the language from the game locale on every load (and reloads on locale changes), so the
/// settings page follows the game's language. Unknown languages / missing keys fall back to the English string
/// passed at the call site.
/// </summary>
internal static class ModI18N
{
    /// <summary>
    /// Embedded-resource prefix for the settings tables; each file must be named
    /// <c>STS2BetterRockPaperScissors.localization.settings.&lt;lang&gt;.json</c>.
    /// </summary>
    private const string ResourceFolder = "STS2BetterRockPaperScissors.localization.settings";

    private static I18N? _settings;

    /// <summary>The settings-page translation table, created once and loaded lazily by RitsuLib.</summary>
    private static I18N Settings => _settings ??= RitsuLibFramework.CreateLocalization(
        instanceName: "BetterRockPaperScissors-Settings",
        resourceFolders: new[] { ResourceFolder },
        resourceAssembly: Assembly.GetExecutingAssembly());

    /// <summary>
    /// Localized settings text for <paramref name="key"/>. <paramref name="fallback"/> is shown when the current
    /// language has no entry (and is also the text the analyzer checks against the JSON tables).
    /// </summary>
    internal static ModSettingsText Text(string key, string fallback) => ModSettingsText.I18N(Settings, key, fallback);
}
