using FrostyEditor;
using HarmonyLib;

namespace Flurry.Editor.Patches
{
    [HarmonyPatch(typeof(MainWindow))]
    [HarmonyPatchCategory("flurry.editor")]
    public static class MainWindowThreadSafetyPatch
    {
        [HarmonyPatch("AddRecentProject")]
        [HarmonyPrefix]
        public static bool AddRecentProject_EnsureUiThread(MainWindow __instance, string path)
        {
            if (__instance.Dispatcher.CheckAccess())
                return true;

            __instance.Dispatcher.Invoke(() => __instance.AddRecentProject(path));
            return false;
        }

        [HarmonyPatch("RefreshRecentProjects")]
        [HarmonyPrefix]
        public static bool RefreshRecentProjects_EnsureUiThread(MainWindow __instance)
        {
            if (__instance.Dispatcher.CheckAccess())
                return true;

            __instance.Dispatcher.Invoke(() => __instance.RefreshRecentProjects());
            return false;
        }
    }
}
