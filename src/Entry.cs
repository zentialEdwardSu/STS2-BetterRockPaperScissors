using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using STS2BetterRockPaperScissors.Settings;
using STS2RitsuLib;
using STS2RitsuLib.Interop;

namespace STS2BetterRockPaperScissors;

[ModInitializer(nameof(Init))]
public class Entry
{
    public const string ModId = "BetterRockPaperScissors";
    public static readonly Logger Logger = RitsuLibFramework.CreateLogger(ModId);

    public static void Init()
    {
        var harmony = new Harmony(ModId);
        harmony.PatchAll();
        var assembly = Assembly.GetExecutingAssembly();
        RitsuLibFramework.EnsureGodotScriptsRegistered(assembly, Logger);
        ModTypeDiscoveryHub.RegisterModAssembly(ModId, assembly);

        ModConfig.RegisterData();
        ModConfig.RegisterSettings();
    }
}