using Frosty.Core;
using FrostySdk.Interfaces;
using HarmonyLib;
using System;
using System.Threading;

namespace Flurry.Manager
{
    public class HarmonyPatcherManagerHack : ExecutionAction
    {
        // ExecutionAction.PreLaunchAction and PostLaunchAction are virtual properties
        // that return Action delegates. The base class returns null by default, and
        // FrostyModManager invokes them directly without null-checking, so we must
        // override both with no-op actions to prevent NullReferenceException on launch.
        public override Action<ILogger, PluginManagerType, CancellationToken> PreLaunchAction =>
            (logger, type, token) => { };
        public override Action<ILogger, PluginManagerType, CancellationToken> PostLaunchAction =>
            (logger, type, token) => { };

        public HarmonyPatcherManagerHack()
        {
            FlurryManagerConfig config = new FlurryManagerConfig();
            config.Load();
            Harmony.DEBUG = config.HarmonyDebug;
            if (App.PluginManager.ManagerType != PluginManagerType.ModManager)
            {
                FileLog.Debug("[Flurry] Skipping manager patches (not in Mod Manager)...");
                return;
            }
            var harmony = new Harmony("io.github.adamraichu.frosty.flurry.manager");
            FileLog.Debug("[Flurry] Applying manager patches...");
            harmony.PatchCategory("flurry.manager");
        }
    }
}
