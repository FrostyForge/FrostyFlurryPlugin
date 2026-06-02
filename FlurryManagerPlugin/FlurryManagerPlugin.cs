using Frosty.Core;
using HarmonyLib;
using FrostySdk.Interfaces;
using System;
using System.Threading;

namespace Flurry.Manager
{
    public class HarmonyPatcherManagerHack : ExecutionAction
    {
        public override Action<ILogger, PluginManagerType, CancellationToken> PreLaunchAction =>
            (logger, type, cancelToken) => { };

        public override Action<ILogger, PluginManagerType, CancellationToken> PostLaunchAction =>
            (logger, type, cancelToken) => { };

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
            FileLog.Debug("[Flurry] Applying exception box patches...");
        }
    }
}
