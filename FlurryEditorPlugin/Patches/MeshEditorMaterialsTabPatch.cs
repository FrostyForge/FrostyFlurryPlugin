using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Controls;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using HarmonyLib;
using MeshSetPlugin;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Flurry.Editor.Patches
{
    [HarmonyPatch(typeof(FrostyMeshSetEditor))]
    [HarmonyPatchCategory("flurry.editor")]
    public class MeshEditorMaterialsTabPatch
    {
        private static readonly Dictionary<FrostyMeshSetEditor, TabState> _states
            = new Dictionary<FrostyMeshSetEditor, TabState>();

        private class TabState
        {
            public StackPanel Panel;
            public FrostyTabControl TabControl;
            public bool Populated;
        }

        private class MaterialInfo
        {
            public string Name { get; set; }
            public dynamic Material;
            public List<TextureInfo> Textures = new List<TextureInfo>();
            public List<VectorInfo> Vectors = new List<VectorInfo>();
        }

        private class TextureInfo
        {
            public string ParamName;
            public dynamic Param;
        }

        private class VectorInfo
        {
            public string ParamName;
            public dynamic Param;
            public float[] DefaultValue;
        }

        [HarmonyPatch(nameof(FrostyMeshSetEditor.OnApplyTemplate))]
        [HarmonyPostfix]
        public static void OnApplyTemplate_Postfix(FrostyMeshSetEditor __instance)
        {
            try
            {
                FrostyTabControl tabControl = Traverse.Create(__instance)
                    .Field("meshTabControl").GetValue<FrostyTabControl>();
                if (tabControl == null) return;

                StackPanel panel = new StackPanel();
                ScrollViewer sv = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = panel
                };

                FrostyTabItem materialsTab = new FrostyTabItem
                {
                    Header = "Materials",
                    CloseButtonVisible = false,
                    Content = sv
                };
                tabControl.Items.Insert(2, materialsTab);

                TabState state = new TabState
                {
                    Panel = panel,
                    TabControl = tabControl
                };
                _states[__instance] = state;

                __instance.Loaded += (s, e) =>
                {
                    __instance.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        state.Populated = true;
                        PopulateAllMaterials(__instance);
                    }), DispatcherPriority.ContextIdle);
                };

                ComboBox variationsComboBox = Traverse.Create(__instance)
                    .Field("variationsComboBox").GetValue<ComboBox>();
                if (variationsComboBox != null)
                {
                    variationsComboBox.SelectionChanged += (s, e) =>
                    {
                        if (!state.Populated) return;
                        __instance.Dispatcher.BeginInvoke(new Action(() =>
                            PopulateAllMaterials(__instance)),
                            DispatcherPriority.ContextIdle);
                    };
                }
            }
            catch (Exception ex)
            {
                App.Logger?.Log($"[Flurry] Failed to add Materials tab: {ex.Message}");
            }
        }

        private static void PopulateAllMaterials(FrostyMeshSetEditor editor)
        {
            if (!_states.TryGetValue(editor, out TabState state)) return;

            state.Panel.Children.Clear();

            try
            {
                dynamic rootObject = Traverse.Create(editor).Property("RootObject").GetValue();
                if (rootObject == null) return;

                dynamic materials = rootObject.Materials;
                if (materials == null) return;

                int totalTextures = 0;
                int totalVectors = 0;
                int idx = 0;
                foreach (dynamic materialRef in materials)
                {
                    try
                    {
                        PointerRef pref = (PointerRef)materialRef;
                        dynamic material = pref.Internal;
                        if (material == null) { idx++; continue; }

                        string name = "Material " + idx;
                        try
                        {
                            string id = material.__Id;
                            if (!string.IsNullOrEmpty(id)) name = id;
                        }
                        catch { }

                        MaterialInfo info = new MaterialInfo { Name = name, Material = material };

                        try
                        {
                            dynamic shader = material.Shader;
                            if (shader != null)
                            {
                                if (shader.TextureParameters != null)
                                {
                                    foreach (dynamic param in shader.TextureParameters)
                                    {
                                        info.Textures.Add(new TextureInfo
                                        {
                                            ParamName = param.ParameterName,
                                            Param = param
                                        });
                                        totalTextures++;
                                    }
                                }
                                if (shader.VectorParameters != null)
                                {
                                    foreach (dynamic param in shader.VectorParameters)
                                    {
                                        float[] defaults = new float[4];
                                        try
                                        {
                                            defaults[0] = (float)param.Value.x;
                                            defaults[1] = (float)param.Value.y;
                                            defaults[2] = (float)param.Value.z;
                                            defaults[3] = (float)param.Value.w;
                                        }
                                        catch { }

                                        info.Vectors.Add(new VectorInfo
                                        {
                                            ParamName = param.ParameterName,
                                            Param = param,
                                            DefaultValue = defaults
                                        });
                                        totalVectors++;
                                    }
                                }
                            }
                        }
                        catch { }

                        state.Panel.Children.Add(CreateMaterialHeader(info));

                        // Textures section
                        state.Panel.Children.Add(CreateSectionLabel("Textures"));
                        if (info.Textures.Count == 0)
                        {
                            state.Panel.Children.Add(CreateEmptyLabel("No texture parameters"));
                        }
                        else
                        {
                            foreach (TextureInfo tex in info.Textures)
                            {
                                state.Panel.Children.Add(CreateTextureRow(tex, editor));
                            }
                        }

                        // Vector parameters section
                        state.Panel.Children.Add(CreateSectionLabel("Vector Parameters"));
                        if (info.Vectors.Count == 0)
                        {
                            state.Panel.Children.Add(CreateEmptyLabel("No vector parameters"));
                        }
                        else
                        {
                            foreach (VectorInfo vec in info.Vectors)
                            {
                                state.Panel.Children.Add(CreateVectorRow(vec, editor));
                            }
                        }

                        state.Panel.Children.Add(new Border
                        {
                            Height = 8,
                            Background = Brushes.Transparent
                        });
                    }
                    catch { }
                    idx++;
                }

                if (state.TabControl.Items[2] is FrostyTabItem tab)
                    tab.Header = $"Materials ({totalTextures}T, {totalVectors}V)";
            }
            catch (Exception ex)
            {
                App.Logger?.Log($"[Flurry] Error populating materials: {ex.Message}");
            }
        }

        private static UIElement CreateSectionLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Brush("FontColor"),
                Opacity = 0.5,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(14, 8, 0, 2)
            };
        }

        private static UIElement CreateEmptyLabel(string text)
        {
            return new TextBlock
            {
                Text = "  " + text,
                Foreground = Brush("FontColor"),
                Opacity = 0.35,
                FontSize = 12,
                Margin = new Thickness(10, 2, 0, 4)
            };
        }

        private static UIElement CreateMaterialHeader(MaterialInfo info)
        {
            Border header = new Border
            {
                Background = Brush("ControlBackground"),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 0)
            };

            TextBlock label = new TextBlock
            {
                Text = $"{info.Name}  ({info.Textures.Count}T, {info.Vectors.Count}V)",
                Foreground = Brush("FontColor"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            };

            header.Child = label;
            return header;
        }

        private static UIElement CreateTextureRow(TextureInfo tex, FrostyMeshSetEditor editor)
        {
            PointerRef value = tex.Param.Value;

            EbxAssetEntry textureEntry = null;
            string assetName = "(null)";
            string assetPath = "";
            if (value.Type == PointerRefType.External)
            {
                textureEntry = App.AssetManager.GetEbxEntry(value.External.FileGuid);
                if (textureEntry != null)
                {
                    assetName = textureEntry.Filename;
                    assetPath = textureEntry.Name;
                }
                else
                {
                    assetName = value.External.FileGuid.ToString();
                }
            }
            else if (value.Type == PointerRefType.Internal)
            {
                assetName = "(internal ref)";
            }

            Border rowBorder = new Border
            {
                BorderBrush = Brush("ControlBackground"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(6, 5, 6, 5)
            };

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

            TextBlock nameBlock = new TextBlock
            {
                Text = tex.ParamName,
                Foreground = Brush("FontColor"),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = tex.ParamName
            };
            Grid.SetColumn(nameBlock, 0);
            row.Children.Add(nameBlock);

            StackPanel refPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0)
            };

            if (textureEntry != null)
            {
                try
                {
                    Image typeIcon = new Image
                    {
                        Width = 14, Height = 14,
                        Margin = new Thickness(0, 0, 4, 0),
                        Source = new ImageSourceConverter().ConvertFromString(
                            "pack://application:,,,/FrostyEditor;component/Images/Reference.png") as ImageSource
                    };
                    refPanel.Children.Add(typeIcon);
                }
                catch { }
            }

            TextBlock nameText = new TextBlock
            {
                Text = assetName,
                Foreground = Brush("FontColor"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (textureEntry == null) nameText.Opacity = 0.5;
            refPanel.Children.Add(nameText);

            if (!string.IsNullOrEmpty(assetPath))
            {
                refPanel.Children.Add(new TextBlock
                {
                    Text = $" ({assetPath})",
                    Foreground = Brush("FontColor"),
                    Opacity = 0.5,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            Grid.SetColumn(refPanel, 1);
            row.Children.Add(refPanel);

            Button assignBtn = new Button
            {
                Width = 22, Height = 22,
                ToolTip = "Assign from selected asset in Data Explorer",
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = new TextBlock
                {
                    Text = "\u2190", FontSize = 14,
                    Foreground = Brush("FontColor"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            assignBtn.Click += (s, e) => AssignTextureFromDataExplorer(tex, editor);
            Grid.SetColumn(assignBtn, 2);
            row.Children.Add(assignBtn);

            Button optBtn = new Button
            {
                Width = 22, Height = 22,
                ToolTip = "More options",
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = new TextBlock
                {
                    Text = "\u2026", FontSize = 14, FontWeight = FontWeights.Bold,
                    Foreground = Brush("FontColor"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            optBtn.Click += (s, e) =>
            {
                ContextMenu cm = new ContextMenu();

                MenuItem openItem = new MenuItem { Header = "Open asset", IsEnabled = textureEntry != null };
                if (textureEntry != null)
                {
                    EbxAssetEntry captured = textureEntry;
                    openItem.Click += (s2, e2) => App.EditorWindow.OpenAsset(captured);
                }
                cm.Items.Add(openItem);

                MenuItem findItem = new MenuItem { Header = "Find in data explorer", IsEnabled = textureEntry != null };
                if (textureEntry != null)
                {
                    EbxAssetEntry captured = textureEntry;
                    findItem.Click += (s2, e2) => App.EditorWindow.DataExplorer.SelectAsset(captured);
                }
                cm.Items.Add(findItem);

                cm.Items.Add(new Separator());

                MenuItem clearItem = new MenuItem { Header = "Clear assigned texture", IsEnabled = value.Type != PointerRefType.Null };
                clearItem.Click += (s2, e2) =>
                {
                    tex.Param.Value = new PointerRef();
                    MarkModified(editor);
                    PopulateAllMaterials(editor);
                };
                cm.Items.Add(clearItem);

                cm.IsOpen = true;
                optBtn.ContextMenu = cm;
            };
            Grid.SetColumn(optBtn, 3);
            row.Children.Add(optBtn);

            rowBorder.Child = row;
            return rowBorder;
        }

        private static UIElement CreateVectorRow(VectorInfo vec, FrostyMeshSetEditor editor)
        {
            dynamic param = vec.Param;
            bool isVecValue = false;
            float vx = 0, vy = 0, vz = 0, vw = 0;
            string paramType = "";

            try { paramType = param.ParameterType?.ToString() ?? ""; } catch { }

            try
            {
                dynamic val = param.Value;
                if (val != null)
                {
                    vx = (float)val.x;
                    vy = (float)val.y;
                    vz = (float)val.z;
                    vw = (float)val.w;
                    isVecValue = true;
                }
            }
            catch
            {
                try
                {
                    PointerRef ptr = param.Value;
                    if (ptr.Type != PointerRefType.Null)
                    {
                        isVecValue = false;
                    }
                }
                catch { }
            }

            Border rowBorder = new Border
            {
                BorderBrush = Brush("ControlBackground"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(6, 5, 6, 5)
            };

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

            TextBlock nameBlock = new TextBlock
            {
                Text = vec.ParamName,
                Foreground = Brush("FontColor"),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = vec.ParamName
            };
            Grid.SetColumn(nameBlock, 0);
            row.Children.Add(nameBlock);

            if (isVecValue)
            {
                Grid vecGrid = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                vecGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
                vecGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
                vecGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
                vecGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });

                string[] labels = { "X", "Y", "Z", "W" };
                float[] values = { vx, vy, vz, vw };
                SolidColorBrush[] colors = {
                    new SolidColorBrush(Color.FromRgb(255, 80, 80)),
                    new SolidColorBrush(Color.FromRgb(80, 220, 80)),
                    new SolidColorBrush(Color.FromRgb(80, 140, 255)),
                    new SolidColorBrush(Color.FromRgb(180, 180, 180))
                };
                TextBox[] boxes = new TextBox[4];

                for (int i = 0; i < 4; i++)
                {
                    Border cellBorder = new Border
                    {
                        BorderBrush = Brush("ControlBackground"),
                        BorderThickness = new Thickness(1, 0, 0, 0),
                        Padding = new Thickness(2, 2, 2, 2)
                    };

                    StackPanel cell = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };

                    TextBlock label = new TextBlock
                    {
                        Text = labels[i],
                        Foreground = colors[i],
                        FontSize = 9,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    cell.Children.Add(label);

                    TextBox tb = new TextBox
                    {
                        Text = values[i].ToString("G6"),
                        Foreground = Brush("FontColor"),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        FontSize = 12,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        Padding = new Thickness(0),
                        Margin = new Thickness(0, 0, 0, 0)
                    };
                    boxes[i] = tb;

                    int capturedIndex = i;
                    tb.LostFocus += (s, e) =>
                    {
                        try
                        {
                            if (float.TryParse(tb.Text, out float parsed))
                            {
                                switch (capturedIndex)
                                {
                                    case 0: param.Value.x = parsed; break;
                                    case 1: param.Value.y = parsed; break;
                                    case 2: param.Value.z = parsed; break;
                                    case 3: param.Value.w = parsed; break;
                                }
                                MarkModified(editor);
                            }
                            else
                            {
                                tb.Text = values[capturedIndex].ToString("G6");
                            }
                        }
                        catch { }
                    };
                    tb.KeyDown += (s, e) =>
                    {
                        if (e.Key == System.Windows.Input.Key.Enter)
                        {
                            tb.MoveFocus(new System.Windows.Input.TraversalRequest(
                                System.Windows.Input.FocusNavigationDirection.Next));
                        }
                    };
                    cell.Children.Add(tb);
                    cellBorder.Child = cell;
                    Grid.SetColumn(cellBorder, i);
                    vecGrid.Children.Add(cellBorder);
                }

                Grid.SetColumn(vecGrid, 1);
                row.Children.Add(vecGrid);
            }
            else
            {
                StackPanel refPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 6, 0)
                };

                TextBlock placeholder = new TextBlock
                {
                    Text = "(pointer ref or empty)",
                    Foreground = Brush("FontColor"),
                    Opacity = 0.4,
                    FontSize = 12,
                    FontStyle = FontStyles.Italic
                };
                refPanel.Children.Add(placeholder);

                Grid.SetColumn(refPanel, 1);
                row.Children.Add(refPanel);
            }

            Button optBtn = new Button
            {
                Width = 22, Height = 22,
                ToolTip = "More options",
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = new TextBlock
                {
                    Text = "\u2026", FontSize = 14, FontWeight = FontWeights.Bold,
                    Foreground = Brush("FontColor"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            optBtn.Click += (s, e) =>
            {
                ContextMenu cm = new ContextMenu();

                if (!isVecValue)
                {
                    try
                    {
                        PointerRef ptr = param.Value;
                        EbxAssetEntry targetEntry = null;
                        if (ptr.Type == PointerRefType.External)
                            targetEntry = App.AssetManager.GetEbxEntry(ptr.External.FileGuid);

                        MenuItem openItem = new MenuItem { Header = "Open asset", IsEnabled = targetEntry != null };
                        if (targetEntry != null)
                        {
                            EbxAssetEntry captured = targetEntry;
                            openItem.Click += (s2, e2) => App.EditorWindow.OpenAsset(captured);
                        }
                        cm.Items.Add(openItem);

                        MenuItem findItem = new MenuItem { Header = "Find in data explorer", IsEnabled = targetEntry != null };
                        if (targetEntry != null)
                        {
                            EbxAssetEntry captured = targetEntry;
                            findItem.Click += (s2, e2) => App.EditorWindow.DataExplorer.SelectAsset(captured);
                        }
                        cm.Items.Add(findItem);

                        cm.Items.Add(new Separator());

                        MenuItem clearItem = new MenuItem { Header = "Clear value", IsEnabled = ptr.Type != PointerRefType.Null };
                        clearItem.Click += (s2, e2) =>
                        {
                            param.Value = new PointerRef();
                            MarkModified(editor);
                            PopulateAllMaterials(editor);
                        };
                        cm.Items.Add(clearItem);
                    }
                    catch { }
                }
                else
                {
                    MenuItem revertItem = new MenuItem { Header = "Revert to default" };
                    revertItem.Click += (s2, e2) =>
                    {
                        param.Value.x = vec.DefaultValue[0];
                        param.Value.y = vec.DefaultValue[1];
                        param.Value.z = vec.DefaultValue[2];
                        param.Value.w = vec.DefaultValue[3];
                        MarkModified(editor);
                        PopulateAllMaterials(editor);
                    };
                    cm.Items.Add(revertItem);
                }

                cm.IsOpen = true;
                optBtn.ContextMenu = cm;
            };
            Grid.SetColumn(optBtn, 2);
            row.Children.Add(optBtn);

            rowBorder.Child = row;
            return rowBorder;
        }

        private static void AssignTextureFromDataExplorer(TextureInfo tex, FrostyMeshSetEditor editor)
        {
            EbxAssetEntry selectedAsset = App.SelectedAsset;
            if (selectedAsset == null)
            {
                App.Logger?.Log("[Flurry] No asset selected in data explorer");
                return;
            }

            if (!IsTextureAsset(selectedAsset))
            {
                App.Logger?.Log($"[Flurry] Selected asset is not a texture: {selectedAsset.Type}");
                return;
            }

            try
            {
                EbxAsset texAsset = App.AssetManager.GetEbx(selectedAsset);
                AssetClassGuid guid = ((dynamic)texAsset.RootObject).GetInstanceGuid();

                EbxImportReference reference = new EbxImportReference
                {
                    FileGuid = selectedAsset.Guid,
                    ClassGuid = guid.ExportedGuid
                };

                AddDependentObject(editor, selectedAsset.Guid);
                tex.Param.Value = new PointerRef(reference);
                MarkModified(editor);
                PopulateAllMaterials(editor);
            }
            catch (Exception ex)
            {
                App.Logger?.Log($"[Flurry] Failed to assign texture: {ex.Message}");
            }
        }

        private static void AssignVectorFromDataExplorer(VectorInfo vec, FrostyMeshSetEditor editor)
        {
            EbxAssetEntry selectedAsset = App.SelectedAsset;
            if (selectedAsset == null)
            {
                App.Logger?.Log("[Flurry] No asset selected in data explorer");
                return;
            }

            try
            {
                EbxAsset targetAsset = App.AssetManager.GetEbx(selectedAsset);
                AssetClassGuid guid = ((dynamic)targetAsset.RootObject).GetInstanceGuid();

                EbxImportReference reference = new EbxImportReference
                {
                    FileGuid = selectedAsset.Guid,
                    ClassGuid = guid.ExportedGuid
                };

                AddDependentObject(editor, selectedAsset.Guid);
                vec.Param.Value = new PointerRef(reference);
                MarkModified(editor);
                PopulateAllMaterials(editor);
            }
            catch (Exception ex)
            {
                App.Logger?.Log($"[Flurry] Failed to assign vector: {ex.Message}");
            }
        }

        private static void MarkModified(FrostyMeshSetEditor editor)
        {
            try
            {
                Traverse.Create(editor).Property("AssetModified").SetValue(true);
                EbxAsset asset = Traverse.Create(editor).Property("Asset").GetValue<EbxAsset>();
                if (asset != null)
                    App.AssetManager.ModifyEbx(editor.AssetEntry.Name, asset);
            }
            catch (Exception ex)
            {
                App.Logger?.Log($"[Flurry] Failed to mark modified: {ex.Message}");
            }
        }

        private static void AddDependentObject(FrostyMeshSetEditor editor, Guid guid)
        {
            try
            {
                Traverse.Create(editor).Method("AddDependentObject", guid).GetValue();
            }
            catch (Exception ex)
            {
                App.Logger?.Log($"[Flurry] Failed to add dependent object {guid}: {ex.Message}");
            }
        }

        private static bool IsTextureAsset(EbxAssetEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Type))
                return false;

            try
            {
                if (FrostySdk.TypeLibrary.IsSubClassOf(entry.Type, "TextureBaseAsset"))
                    return true;
            }
            catch
            {
            }

            return entry.Type.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static SolidColorBrush Brush(string key)
        {
            try
            {
                return Application.Current.FindResource(key) as SolidColorBrush ?? Brushes.White;
            }
            catch { return Brushes.White; }
        }
    }
}
