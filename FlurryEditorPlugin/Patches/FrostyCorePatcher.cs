using Frosty.Core;
using Frosty.Core.Attributes;
using Frosty.Core.Controls;
using Frosty.Core.Controls.Editors;
using Frosty.Core.Misc;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Flurry.Editor.Patches
{
    [HarmonyPatch(typeof(FrostyAssetEditor))]
    [HarmonyPatchCategory("flurry.editor")]
    public class FrostyAssetEditor_ViewInstancesPatch
    {
        [HarmonyPatch(nameof(FrostyAssetEditor.RegisterToolbarItems))]
        [HarmonyPostfix]
        public static void PostFix(FrostyAssetEditor __instance, ref List<ToolbarItem> __result)
        {
            ToolbarItem viewInstances = __result.First();
            Traverse assetEditorTraversal = Traverse.Create(__instance);
            __result = new List<ToolbarItem>
            {
                new ToolbarItem($"View Instances ({__instance.Asset.RootObjects.Count()})", "View class instances", "Images/Database.png", new RelayCommand(blah => assetEditorTraversal.Method("ViewInstances_Click", typeof(object)).GetValue(), state => true))
            };

            FlurryEditorConfig config = new FlurryEditorConfig();
            config.Load();

            if (__instance.AssetEntry is EbxAssetEntry && config.BlueprintEditorTweaks)
            {
                __result.Add(new ToolbarItem("Open in Blueprint Editor", "Open this asset in the Blueprint Editor", "Images/Grid.png", new RelayCommand(blah => FlurryEditorUtils.OpenInBlueprintEditor(__instance.AssetEntry as EbxAssetEntry), state => true)));
            }
        }
    }

    [HarmonyPatch(typeof(FrostyPointerRefControl))]
    [HarmonyPatchCategory("flurry.editor")]
    public class PointerRefControl_BlueprintEditorOpenGraph {
        //private static AccessTools.FieldRef<FrostyPointerRefControl, Button> popupMenuRef = AccessTools.FieldRefAccess<FrostyPointerRefControl, Button>("PART_FindButton");
        private static AccessTools.FieldRef<FrostyPointerRefControl, ComboBox> popupRef = AccessTools.FieldRefAccess<FrostyPointerRefControl, ComboBox>("popup");

        private static Button blueprintEditorButton = new Button()
        {
            Content = "Open as Graph",
            FontFamily = new FontFamily("MS Reference Sans Serif"),
            Height = 22
        };

        private static RoutedEventHandler findButtonClickHandler = null;


        [HarmonyPatch("Popup_DropDownOpened")]
        [HarmonyPostfix]
        public static void InsertButton(FrostyPointerRefControl __instance) {
            FlurryEditorConfig config = new FlurryEditorConfig();
            config.Load();
            if (!config.BlueprintEditorTweaks)
            {
                return;
            }

            FileLog.Debug("Inserting Blueprint Editor Button");
            ComboBox popup = popupRef(__instance);
            FileLog.Debug("Found popup ComboBox");
            Popup popupMenu = (popup.Template.FindName("PART_PopupMenu", popup) as Popup);
            FileLog.Debug("Found popup menu Popup");
            Button findButton = (popupMenu.FindName("PART_FindButton") as Button);
            FileLog.Debug("Found find button");
            //Button findButton = findButtonRef(__instance);
            StackPanel parent = findButton.Parent as StackPanel;

            FileLog.Debug("Found parent StackPanel, inserting button");

            blueprintEditorButton.Style = __instance.FindResource("MenuButtonStyle") as Style;
            FileLog.Debug("Set button style");
            if (findButtonClickHandler != null)
            {
                blueprintEditorButton.Click -= findButtonClickHandler;
            }
            findButtonClickHandler = (s, e) => { BlueprintEditorButton_Click(s, e, __instance); };
            blueprintEditorButton.Click += findButtonClickHandler;
            FileLog.Debug("Set button click event");
            PointerRef ptrRef = (PointerRef)__instance.Value;
            blueprintEditorButton.IsEnabled = !(ptrRef.Type == PointerRefType.Internal || ptrRef.Type == PointerRefType.Null);
            FileLog.Debug("Set button enabled state");

            if (blueprintEditorButton.Parent != null)
            {
                (blueprintEditorButton.Parent as Panel).Children.Remove(blueprintEditorButton);
                FileLog.Debug("Removed button from previous parent");
            }
            parent.Children.Insert(3, blueprintEditorButton);
            FileLog.Debug("Inserted button into popup menu");
        }

        private static void BlueprintEditorButton_Click(object sender, RoutedEventArgs e, FrostyPointerRefControl instance)
        {
            PointerRef ptr = (PointerRef)instance.Value;
            if (ptr.Type == PointerRefType.External)
            {
                EbxAssetEntry asset = App.AssetManager.GetEbxEntry(ptr.External.FileGuid);
                if (asset == null)
                {
                    return;
                }
                FlurryEditorUtils.OpenInBlueprintEditor(asset);
            }
            popupRef(instance).IsDropDownOpen = false;
        }
    }
}
