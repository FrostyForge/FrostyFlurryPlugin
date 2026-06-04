using Frosty.Core.Mod;
using Frosty.ModSupport;
using HarmonyLib;
using MM = FrostyModManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Flurry.Manager.Patches
{
    [HarmonyPatch(typeof(MM.MainWindow))]
    [HarmonyPatchCategory("flurry.manager")]
    internal static class LaunchAssetOverrideContextPatch
    {
        private static AccessTools.FieldRef<MM.MainWindow, MM.FrostyPack> selectedPackRef =
            AccessTools.FieldRefAccess<MM.MainWindow, MM.FrostyPack>("selectedPack");

        [HarmonyPatch("launchButton_Click")]
        [HarmonyPrefix]
        public static void LaunchButton_Click_Prefix(MM.MainWindow __instance)
        {
            MM.FrostyPack selectedPack = selectedPackRef(__instance);
            if (selectedPack == null)
            {
                ConflictAssetOverrideRules.ClearLaunchContext();
                return;
            }

            ConflictAssetOverrideRules.ActivateLaunchContext(selectedPack.Name, selectedPack.AppliedMods);
        }

        [HarmonyPatch("launchButton_Click")]
        [HarmonyPostfix]
        public static void LaunchButton_Click_Postfix()
        {
            ConflictAssetOverrideRules.ClearLaunchContext();
        }
    }

    [HarmonyPatch(typeof(FrostyModExecutor))]
    [HarmonyPatchCategory("flurry.manager")]
    internal static class ProcessModResourcesAssetOverridePatch
    {
        private static readonly FieldInfo resourcesField = AccessTools.Field(typeof(FrostyMod), "resources");

        [HarmonyPatch("ProcessModResources")]
        [HarmonyPrefix]
        public static void ProcessModResources_Prefix(IResourceContainer fmod)
        {
            if (!(fmod is FrostyMod mod))
            {
                return;
            }
            if (resourcesField == null)
            {
                return;
            }

            BaseModResource[] resources = resourcesField.GetValue(mod) as BaseModResource[];
            if (resources == null || resources.Length == 0)
            {
                return;
            }

            BaseModResource[] filtered = resources
                .Where(resource => ConflictAssetOverrideRules.ShouldKeepResourceForLaunch(mod, resource))
                .ToArray();

            if (filtered.Length != resources.Length)
            {
                resourcesField.SetValue(mod, filtered);
            }
        }
    }
}
