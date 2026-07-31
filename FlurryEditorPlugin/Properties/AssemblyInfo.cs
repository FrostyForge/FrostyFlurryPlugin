using Flurry.Editor;
using Flurry.Editor.SourceControl;
using Frosty.Core;
using Frosty.Core.Attributes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None, //where theme specific resource dictionaries are located
                                     //(used if a resource is not found in the page, 
                                     // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly //where the generic resource dictionary is located
                                              //(used if a resource is not found in the page, 
                                              // app, or any theme specific resource dictionaries)
)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("4b612468-9b6a-4304-88a5-055c3575eb3d")]

[assembly: PluginDisplayName("Flurry Tweaks (Editor)")]
[assembly: PluginAuthor("AdamRaichu")]
[assembly: PluginVersion("1.1.1")]
[assembly: RegisterStartupAction(typeof(HarmonyPatcherAction))]
[assembly: RegisterOptionsExtension(typeof(FlurryEditorConfig), PluginManagerType.Editor)]
[assembly: RegisterMenuExtension(typeof(LoadOrderEditorMenuExt), PluginManagerType.Editor)]
[assembly: RegisterMenuExtension(typeof(ExportBinaryFileHashesExt), PluginManagerType.Editor)]
//[assembly: RegisterMenuExtension(typeof(OpenProjectFolderMenuExt), PluginManagerType.Editor)] // legacy/outdated
//[assembly: RegisterDataExplorerContextMenu(typeof(XmlDiffGeneratorExtension))]
[assembly: RegisterMenuExtension(typeof(ExportToFolderMenuExt), PluginManagerType.Editor)]
