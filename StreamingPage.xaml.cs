using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace DiapStash_Plugin
{
    public sealed partial class StreamingPage : UserControl
    {
        private bool _isInitialized = false;
        private System.Collections.Generic.List<OverlayElement> _selectedModels = new();
        private System.Collections.Generic.List<FrameworkElement> _selectedElements = new();
        private OverlayElement _selectedModel => _selectedModels.Count == 1 ? _selectedModels[0] : null;
        private FrameworkElement _selectedElement => _selectedElements.Count == 1 ? _selectedElements[0] : null;

        private void ClearSelection() { _selectedModels.Clear(); _selectedElements.Clear(); UpdateSelectionBox(); RefreshPropertiesPanel(); }
        private void SelectOnly(OverlayElement model) { 
            _selectedModels.Clear(); _selectedElements.Clear(); 
            if (model != null) { 
                _selectedModels.Add(model); 
                if (_modelToUi.TryGetValue(model, out var ui)) _selectedElements.Add(ui); 
            }
            UpdateSelectionBox(); RefreshPropertiesPanel();
        }
        private void ToggleSelection(OverlayElement model) {
            if (model == null) return;
            if (_selectedModels.Contains(model)) {
                _selectedModels.Remove(model);
                if (_modelToUi.TryGetValue(model, out var ui)) _selectedElements.Remove(ui);
            } else {
                _selectedModels.Add(model);
                if (_modelToUi.TryGetValue(model, out var ui)) _selectedElements.Add(ui);
            }
            UpdateSelectionBox(); RefreshPropertiesPanel();
        }

        private readonly string _presetPath = System.IO.Path.Combine(DiapStashClient.AppDataFolder, "overlay_preset.json");
        private readonly string _pagesPath = System.IO.Path.Combine(DiapStashClient.AppDataFolder, "overlay_pages.json");
        private System.Collections.Generic.List<OverlayPreset> _pages = new();
        private int _activePageIndex = 0;
        private OverlayPreset _activePage => (_pages != null && _activePageIndex >= 0 && _activePageIndex < _pages.Count) ? _pages[_activePageIndex] : null;

        private bool _previewMode = false;
        private DispatcherTimer _previewTimer = null;
        private bool _isRefreshingProperties = false;
        private System.Collections.Generic.Dictionary<TreeViewNode, object> _nodeTags = new();
        private System.Collections.Generic.Dictionary<OverlayElement, FrameworkElement> _modelToUi = new();
        private OverlayElement _dragTargetModel;

        private System.Collections.Generic.Stack<string> _undoStack = new();
        private System.Collections.Generic.Stack<string> _redoStack = new();

        public StreamingPage()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => { OverlayServer.Instance.IsEditing = true; UpdateLocalPreview(); };
            this.Unloaded += (s, e) => { OverlayServer.Instance.IsEditing = false; };
            _isInitialized = true;
            LoadPages();
            _ = SyncDesignWithOverlayServerAsync();
        }

        private void LoadPages()
        {
            _isRefreshingProperties = true;
            try
            {
                if (File.Exists(_pagesPath))
                {
                    string json = File.ReadAllText(_pagesPath);
                    _pages = JsonSerializer.Deserialize<System.Collections.Generic.List<OverlayPreset>>(json) ?? new();
                }
                else if (File.Exists(_presetPath))
                {
                    string json = File.ReadAllText(_presetPath);
                    var p = JsonSerializer.Deserialize<OverlayPreset>(json);
                    if (p != null) { p.Name = "Default Layout"; _pages.Add(p); }
                }

                if (_pages.Count == 0)
                {
                    _pages.Add(CreateDefaultPreset("Page 1"));
                }
                
                RefreshPagesList();
                LoadActivePage();
            }
            catch { }
            finally
            {
                _isRefreshingProperties = false;
            }
        }

        private void RefreshPagesList()
        {
            PagesListView.Items.Clear();
            for (int i = 0; i < _pages.Count; i++)
            {
                var li = new ListViewItem { Content = _pages[i].Name ?? $"Page {i + 1}", Tag = i };
                PagesListView.Items.Add(li);
                if (i == _activePageIndex) PagesListView.SelectedItem = li;
            }
        }

        private OverlayPreset CreateDefaultPreset(string name)
        {
            return new OverlayPreset
            {
                Name = name,
                CardW = 800, CardH = 200, TransitionType = 0, TransitionDurationMs = 400, CardBackgroundHex = "#FFFFFFFF",
                Elements = new System.Collections.Generic.List<OverlayElement>
                {
                    new ImageElement { X = 30, Y = 30, Width = 120, Height = 120, ZIndex = 0, DataSource = "DiapStashImage" },
                    new TextElement { X = 170, Y = 30, Width = 400, Height = 40, ZIndex = 1, DataSource = "ProductName", FontFamily = "Outfit", FontSize = 28, FontWeight = "Bold", FontStyle = "Normal", ColorHex = "#FF1E1E1E" },
                    new TextElement { X = 170, Y = 75, Width = 200, Height = 30, ZIndex = 2, DataSource = "Size", FontFamily = "Outfit", FontSize = 18, FontWeight = "Normal", FontStyle = "Normal", ColorHex = "#FF7F7F7F" },
                    new BarElement { X = 170, Y = 115, Width = 250, Height = 16, ZIndex = 3, DataSource = "Wetness", FillColorHex = "#FF0078D7", BgColorHex = "#FFE6E6E6", Orientation = "Horizontal" },
                    new BarElement { X = 170, Y = 145, Width = 250, Height = 16, ZIndex = 4, DataSource = "Messiness", FillColorHex = "#FFE81123", BgColorHex = "#FFE6E6E6", Orientation = "Horizontal" }
                }
            };
        }

        private void LoadActivePage()
        {
            _isRefreshingProperties = true;
            try
            {
                var p = _activePage;
                if (p != null)
                {
                    CardWidthSlider.Value = p.CardW;
                    CardHeightSlider.Value = p.CardH;
                    if (CardWidthBox != null) CardWidthBox.Text = Math.Round(p.CardW).ToString();
                    if (CardHeightBox != null) CardHeightBox.Text = Math.Round(p.CardH).ToString();
                    TransitionTypeCombo.SelectedIndex = p.TransitionType;
                    TransitionSpeedSlider.Value = p.TransitionDurationMs;
                    DisplayDurationSlider.Value = p.StayOnScreenDurationMs;
                    CardBgColorPicker.Color = HexToColor(p.CardBackgroundHex);
                    OverlayServer.Instance.Elements = p.Elements;
                }
            }
            finally
            {
                _isRefreshingProperties = false;
            }
            ClearSelection();
            UpdateLocalPreview();
        }

        private void SetToDefaultDesign()
        {
            if (_activePage != null)
            {
                var def = CreateDefaultPreset(_activePage.Name);
                _pages[_activePageIndex] = def;
                LoadActivePage();
                SavePages();
                _ = SyncDesignWithOverlayServerAsync();
            }
        }

        private void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            SetToDefaultDesign();
        }

        private void SaveStateForUndo()
        {
            if (_activePage != null)
            {
                _undoStack.Push(JsonSerializer.Serialize(_activePage.Elements, new JsonSerializerOptions { WriteIndented = false }));
                _redoStack.Clear();
            }
        }

        private async void UndoBtn_Click(object sender, RoutedEventArgs e)
        {
            try {
                if (_undoStack.Count > 0 && _activePage != null)
                {
                    _redoStack.Push(JsonSerializer.Serialize(_activePage.Elements, new JsonSerializerOptions { WriteIndented = false }));
                    string state = _undoStack.Pop();
                    _activePage.Elements = JsonSerializer.Deserialize<System.Collections.Generic.List<OverlayElement>>(state);
                    OverlayServer.Instance.Elements = _activePage.Elements;
                    ClearSelection();
                    UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
                }
            } catch (Exception ex) {
                var dialog = new ContentDialog { Title = "Undo Error", Content = ex.ToString(), CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot };
                await dialog.ShowAsync();
            }
        }

        private async void RedoBtn_Click(object sender, RoutedEventArgs e)
        {
            try {
                if (_redoStack.Count > 0 && _activePage != null)
                {
                    _undoStack.Push(JsonSerializer.Serialize(_activePage.Elements, new JsonSerializerOptions { WriteIndented = false }));
                    string state = _redoStack.Pop();
                    _activePage.Elements = JsonSerializer.Deserialize<System.Collections.Generic.List<OverlayElement>>(state);
                    OverlayServer.Instance.Elements = _activePage.Elements;
                    ClearSelection();
                    UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
                }
            } catch (Exception ex) {
                var dialog = new ContentDialog { Title = "Redo Error", Content = ex.ToString(), CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot };
                await dialog.ShowAsync();
            }
        }

        private void SavePages()
        {
            try
            {
                var p = _activePage;
                if (p != null)
                {
                    p.CardW = CardWidthSlider.Value;
                    p.CardH = CardHeightSlider.Value;
                    p.CornerRadius = CardCornerRadiusSlider.Value;
                    p.TransitionType = TransitionTypeCombo.SelectedIndex;
                    p.TransitionDurationMs = TransitionSpeedSlider.Value;
                    p.StayOnScreenDurationMs = DisplayDurationSlider.Value;
                    p.CardBackgroundHex = ColorToHex(CardBgColorPicker.Color);
                    p.Elements = OverlayServer.Instance.Elements;
                }
                File.WriteAllText(_pagesPath, JsonSerializer.Serialize(_pages, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // --- SPAWNING ---
        private void AddTextBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveStateForUndo();
            var el = new TextElement { X = 50, Y = 50, Width = 200, Height = 40, ZIndex = OverlayServer.Instance.Elements.Count, CustomText = "New Text" };
            OverlayServer.Instance.Elements.Add(el);
            UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
        }
        private void AddBarBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveStateForUndo();
            var el = new BarElement { X = 50, Y = 100, Width = 200, Height = 12, ZIndex = OverlayServer.Instance.Elements.Count };
            OverlayServer.Instance.Elements.Add(el);
            UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
        }
        private void AddImageBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveStateForUndo();
            var el = new ImageElement { X = 50, Y = 150, Width = 100, Height = 100, ZIndex = OverlayServer.Instance.Elements.Count };
            OverlayServer.Instance.Elements.Add(el);
            UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
        }

        private void AddPageBtn_Click(object sender, RoutedEventArgs e)
        {
            _pages.Add(CreateDefaultPreset($"Page {_pages.Count + 1}"));
            SavePages();
            RefreshPagesList();
        }

        private void PagesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PagesListView.SelectedItem is ListViewItem li && li.Tag is int idx)
            {
                _activePageIndex = idx;
                _undoStack.Clear(); _redoStack.Clear();
                LoadActivePage();
            }
        }

        private void ContextDeletePage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is OverlayPreset p) DeleteDesign(p);
        }

        private void InlineDeleteDesign_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is OverlayPreset p) DeleteDesign(p);
        }

        private void DeleteDesign(OverlayPreset p)
        {
            if (_pages.Count <= 1) return; // Don't delete last page
            _pages.Remove(p);
            _activePageIndex = Math.Min(_activePageIndex, _pages.Count - 1);
            _undoStack.Clear(); _redoStack.Clear();
            SavePages();
            RefreshPagesList();
            LoadActivePage();
        }

        private void PagesListGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.ContextFlyout != null)
            {
                var options = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(fe) };
                fe.ContextFlyout.ShowAt(fe, options);
                e.Handled = true;
            }
        }

        private void ContextRenamePage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is OverlayPreset p) RenameDesign(p);
        }

        private void InlineRenameDesign_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is OverlayPreset p) RenameDesign(p);
        }

        private async void RenameDesign(OverlayPreset p)
        {
            var dialog = new ContentDialog
            {
                Title = "Rename Design",
                PrimaryButtonText = "Rename",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            var textBox = new TextBox { Text = p.Name, AcceptsReturn = false };
            dialog.Content = textBox;

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                p.Name = string.IsNullOrWhiteSpace(textBox.Text) ? "Unnamed Design" : textBox.Text;
                RefreshPagesList();
                SavePages();
            }
        }

        private void GroupBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModels.Count > 0)
            {
                SaveStateForUndo();
                var group = new GroupElement { Name = "New Group" };
                var rootElements = OverlayServer.Instance.Elements;
                
                foreach (var model in _selectedModels.ToList())
                {
                    group.Children.Add(model);
                    RemoveElementRecursive(rootElements, model);
                }
                
                rootElements.Add(group);
                SelectOnly(group);
                UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
            }
        }

        private void ContextUngroup_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel is GroupElement ge)
            {
                SaveStateForUndo();
                var elements = OverlayServer.Instance.Elements;
                elements.Remove(ge);
                foreach (var child in ge.Children)
                {
                    child.ZIndex = ge.ZIndex;
                    elements.Add(child);
                }
                ClearSelection();
                UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
            }
        }

        // --- RENDER ---
        private void UpdateLocalPreview()
        {
            if (WidgetArtboard != null)
            {
                WidgetArtboard.Width = CardWidthSlider.Value;
                WidgetArtboard.Height = CardHeightSlider.Value;
                WidgetArtboard.Background = new SolidColorBrush(HexToColor(ColorToHex(CardBgColorPicker.Color)));
                WidgetArtboard.CornerRadius = new CornerRadius(CardCornerRadiusSlider.Value);
            }

            var sg = SelectionGroup;
            var mr = MarqueeRect;
            var gc = GridCanvas;
            EditorCanvas.Children.Clear();
            EditorCanvas.Children.Add(mr);
            EditorCanvas.Children.Add(gc);
            EditorCanvas.Children.Add(sg);
            _nodeTags.Clear();
            _modelToUi.Clear();
            
            var rootNodes = new System.Collections.Generic.List<TreeViewNode>();
            
            void RenderElement(OverlayElement el, TreeViewNode parentNode)
            {
                string name = string.IsNullOrWhiteSpace(el.Name) ? "Element" : el.Name;
                string iconGlyph = "\uE718";
                
                if (el is GroupElement ge)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    sp.Children.Add(new FontIcon { Glyph = "\uE8B7", FontSize = 14 }); // Folder icon
                    sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                    
                    var node = new TreeViewNode { Content = sp };
                    _nodeTags[node] = el;
                    if (parentNode != null) parentNode.Children.Add(node); else rootNodes.Add(node);
                    
                    foreach (var child in ge.Children.OrderBy(x => x.ZIndex))
                    {
                        RenderElement(child, node);
                    }
                    return;
                }

                FrameworkElement ui = null;
                double absoluteX = el.X;
                double absoluteY = el.Y;

                if (el is TextElement te)
                {
                    iconGlyph = "\uE8D2";
                    
                    string displayText = te.DataSource == "Custom" ? te.CustomText : $"[{te.DataSource}]";
                    if (_previewMode && te.DataSource != "Custom")
                    {
                        var s = OverlayServer.Instance;
                        if (te.DataSource == "ProductName") displayText = s.LiveProductName;
                        else if (te.DataSource == "Size") displayText = s.LiveSize;
                        else if (te.DataSource == "Wetness") displayText = s.LiveWetPercentage + "%";
                        else if (te.DataSource == "Messiness") displayText = s.LiveMessPercentage + "%";
                        else if (te.DataSource == "LiveStatus") displayText = s.LiveStatusMessage;
                    }

                    var weight = te.FontWeight == "Bold" ? Microsoft.UI.Text.FontWeights.Bold :
                                 te.FontWeight == "SemiBold" ? Microsoft.UI.Text.FontWeights.SemiBold :
                                 Microsoft.UI.Text.FontWeights.Normal;

                    var style = te.FontStyle == "Italic" ? Windows.UI.Text.FontStyle.Italic :
                                Windows.UI.Text.FontStyle.Normal;

                    var txtAlign = te.TextAlignment == "Right" ? Microsoft.UI.Xaml.TextAlignment.Right :
                                   te.TextAlignment == "Center" ? Microsoft.UI.Xaml.TextAlignment.Center :
                                   Microsoft.UI.Xaml.TextAlignment.Left;

                    ui = new TextBlock
                    {
                        Text = displayText,
                        FontSize = te.FontSize,
                        Foreground = new SolidColorBrush(HexToColor(te.ColorHex)),
                        FontWeight = weight,
                        FontStyle = style,
                        FontFamily = new FontFamily(te.FontFamily ?? "Outfit"),
                        TextWrapping = te.TextWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextAlignment = txtAlign
                    };
                }
                else if (el is BarElement be)
                {
                    iconGlyph = "\uE90B";
                    var bg = new Border { Background = new SolidColorBrush(HexToColor(be.BgColorHex)), CornerRadius = new CornerRadius(be.CornerRadius) };
                    var fg = new Border { Background = new SolidColorBrush(HexToColor(be.FillColorHex)), CornerRadius = new CornerRadius(be.CornerRadius) };
                    
                    double percentage = 0.5;
                    if (_previewMode)
                    {
                        if (be.DataSource == "Wetness") percentage = OverlayServer.Instance.LiveWetPercentage / 100.0;
                        else if (be.DataSource == "Messiness") percentage = OverlayServer.Instance.LiveMessPercentage / 100.0;
                    }

                    if (be.Orientation == "Horizontal") { fg.Width = el.Width * percentage; fg.HorizontalAlignment = HorizontalAlignment.Left; }
                    else { fg.Height = el.Height * percentage; fg.VerticalAlignment = VerticalAlignment.Bottom; }
                    var grid = new Grid(); grid.Children.Add(bg); grid.Children.Add(fg);
                    ui = grid;
                }
                else if (el is ImageElement ie)
                {
                    iconGlyph = "\uEB9F";
                    var b = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(10, 0, 0, 0)), CornerRadius = new CornerRadius(ie.CornerRadius), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 0, 0, 0)), BorderThickness = new Thickness(1) };
                    
                    string url = "";
                    if (ie.DataSource == "Custom")
                    {
                        url = ie.CustomUrl;
                    }
                    else
                    {
                        if (_previewMode) url = OverlayServer.Instance.LiveImageUrl;
                        if (string.IsNullOrEmpty(url)) url = "https://diapstash.com/diapstash/assets/icons/Diaper.png";
                    }

                    var stretchMode = Stretch.UniformToFill;
                    if (ie.Stretch == "Uniform") stretchMode = Stretch.Uniform;
                    else if (ie.Stretch == "Fill") stretchMode = Stretch.Fill;

                    if (!string.IsNullOrEmpty(url)) {
                        try { 
                            Microsoft.UI.Xaml.Media.ImageSource imgSource;
                            if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) imgSource = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(url));
                            else imgSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url));
                            
                            var imgControl = new Image { Source = imgSource, Stretch = stretchMode };
                            if (url == "https://diapstash.com/diapstash/assets/icons/Diaper.png") imgControl.Opacity = 0.8;
                            
                            b.Child = imgControl;
                        } catch { }
                    } else {
                        b.Child = new FontIcon { Glyph = "\uEB9F", FontSize = 24, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 0, 0, 0)) };
                    }
                    ui = b;
                }

                if (ui != null)
                {
                    ui.Width = el.Width;
                    ui.Height = el.Height;
                    Canvas.SetLeft(ui, absoluteX);
                    Canvas.SetTop(ui, absoluteY);
                    Canvas.SetZIndex(ui, el.ZIndex);
                    ui.Tag = el;
                    ui.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
                    ui.ManipulationStarted += (s, ev) => { 
                        SaveStateForUndo(); 
                        _dragStartPositions.Clear();
                        if (!_selectedModels.Contains(el)) SelectOnly(el);
                        foreach(var m in _selectedModels) _dragStartPositions[m] = (m.X, m.Y);
                    };
                    ui.ManipulationDelta += Element_ManipulationDelta;
                    ui.ManipulationCompleted += (s, ev) => { SavePages(); _ = SyncDesignWithOverlayServerAsync(); };
                    ui.PointerPressed += Element_PointerPressed;
                    ui.DoubleTapped += Element_DoubleTapped;
                    ui.RightTapped += Element_RightTapped;
                    ui.ContextFlyout = CanvasContextMenu;
                    EditorCanvas.Children.Add(ui);
                    _modelToUi[el] = ui;
                    
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    sp.Children.Add(new FontIcon { Glyph = iconGlyph, FontSize = 14 });
                    sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                    
                    var node = new TreeViewNode { Content = sp };
                    _nodeTags[node] = el;

                    if (parentNode != null) {
                        parentNode.Children.Add(node);
                    } else {
                        rootNodes.Add(node);
                    }
                }
            }

            var sorted = OverlayServer.Instance.Elements.OrderBy(x => x.ZIndex).ToList();
            foreach (var el in sorted)
            {
                RenderElement(el, null);
            }
            
            ZOrderTree.RootNodes.Clear();
            var cardSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            cardSp.Children.Add(new FontIcon { Glyph = "\uE7B5", FontSize = 14 }); // DockBottom or Card-like icon
            cardSp.Children.Add(new TextBlock { Text = "Card Properties", FontWeight = Microsoft.UI.Text.FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            
            var cardNode = new TreeViewNode { Content = cardSp };
            _nodeTags[cardNode] = "CARD";
            ZOrderTree.RootNodes.Add(cardNode);
            
            // Add rootNodes in reverse order so top elements are higher in the list
            for (int i = rootNodes.Count - 1; i >= 0; i--)
            {
                ZOrderTree.RootNodes.Add(rootNodes[i]);
            }

            _selectedElements.Clear();
            foreach (var sm in _selectedModels) {
                if (_modelToUi.TryGetValue(sm, out var u)) _selectedElements.Add(u);
            }

            if (_selectedModels.Count > 0) {
                UpdateSelectionBox();
            } else {
                SelectionGroup.Visibility = Visibility.Collapsed;
            }
            DrawGrid();
        }

        // --- PROPERTIES ---
        private GroupElement GetTopParentGroup(OverlayElement element)
        {
            var parent = GetParentGroup(element);
            if (parent == null) return null;
            while (GetParentGroup(parent) != null) parent = GetParentGroup(parent);
            return parent;
        }

        private void Element_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement ui && ui.Tag is OverlayElement clickedModel)
            {
                var ctrl = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
                if (ctrl)
                {
                    ToggleSelection(clickedModel);
                }
                else
                {
                    if (!_selectedModels.Contains(clickedModel))
                        SelectOnly(clickedModel);
                }
                e.Handled = true;
            }
            UpdateSelectionBox();
            RefreshPropertiesPanel();
        }

        private void Element_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement ui && ui.Tag is OverlayElement clickedModel)
            {
                SelectOnly(clickedModel);
                e.Handled = true;
            }
            UpdateSelectionBox();
            RefreshPropertiesPanel();
        }

        private void Element_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.Tag is OverlayElement o && !_selectedModels.Contains(o))
            {
                SelectOnly(o);
            }
            UpdateSelectionBox();
            RefreshPropertiesPanel();
            
            if (CanvasContextMenu != null && sender is FrameworkElement ctxFe)
            {
                var options = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(ctxFe) };
                CanvasContextMenu.ShowAt(ctxFe, options);
            }
        }

        private bool _isMarqueeSelecting = false;
        private Windows.Foundation.Point _marqueeStart;
        private double _dragStartX;
        private double _dragStartY;

        private void EditorCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ClearSelection();
            ZOrderTree.SelectedNodes.Clear();
            SelectionGroup.Visibility = Visibility.Collapsed;
            RefreshPropertiesPanel();

            _isMarqueeSelecting = true;
            _marqueeStart = e.GetCurrentPoint(EditorCanvas).Position;
            MarqueeRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(MarqueeRect, _marqueeStart.X);
            Canvas.SetTop(MarqueeRect, _marqueeStart.Y);
            MarqueeRect.Width = 0;
            MarqueeRect.Height = 0;
            EditorCanvas.CapturePointer(e.Pointer);
        }

        private void EditorCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isMarqueeSelecting)
            {
                var current = e.GetCurrentPoint(EditorCanvas).Position;
                double x = Math.Min(_marqueeStart.X, current.X);
                double y = Math.Min(_marqueeStart.Y, current.Y);
                double width = Math.Abs(_marqueeStart.X - current.X);
                double height = Math.Abs(_marqueeStart.Y - current.Y);

                Canvas.SetLeft(MarqueeRect, x);
                Canvas.SetTop(MarqueeRect, y);
                MarqueeRect.Width = width;
                MarqueeRect.Height = height;
            }
        }

        private void EditorCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isMarqueeSelecting)
            {
                _isMarqueeSelecting = false;
                EditorCanvas.ReleasePointerCapture(e.Pointer);
                MarqueeRect.Visibility = Visibility.Collapsed;

                double mX = Canvas.GetLeft(MarqueeRect);
                double mY = Canvas.GetTop(MarqueeRect);
                double mW = MarqueeRect.Width;
                double mH = MarqueeRect.Height;

                var selectedNodes = new System.Collections.Generic.List<TreeViewNode>();

                foreach (var child in EditorCanvas.Children)
                {
                    if (child is FrameworkElement ui && ui.Tag is OverlayElement el)
                    {
                        double ex = Canvas.GetLeft(ui);
                        double ey = Canvas.GetTop(ui);
                        double ew = ui.ActualWidth;
                        double eh = ui.ActualHeight;

                        // Check intersection
                        if (ex < mX + mW && ex + ew > mX && ey < mY + mH && ey + eh > mY)
                        {
                            var node = _nodeTags.FirstOrDefault(x => x.Value == el).Key;
                            if (node != null)
                            {
                                selectedNodes.Add(node);
                            }
                        }
                    }
                }

                if (selectedNodes.Count > 0)
                {
                    ZOrderTree.SelectedNodes.Clear();
                    foreach (var n in selectedNodes)
                    {
                        ZOrderTree.SelectedNodes.Add(n);
                    }
                    if (selectedNodes.Count == 1)
                    {
                        var tag = _nodeTags[selectedNodes[0]];
                        if (tag is OverlayElement el)
                        {
                            SelectOnly(el);
                        }
                    }
                    UpdateSelectionBox();
                    RefreshPropertiesPanel();
                }
            }
        }

        private void RefreshPropertiesPanel()
        {
            _isRefreshingProperties = true;
            try
            {
                // Global UI sync
                if (_pages.Count > 0 && _activePageIndex >= 0)
                {
                    var preset = _pages[_activePageIndex];
                    CardCornerRadiusSlider.Value = preset.CornerRadius;
                    CardCornerRadiusBox.Text = preset.CornerRadius.ToString();
                    
                    var bgHex = preset.CardBackgroundHex ?? "#FFFFFF";
                    CardBgColorPreview.Background = new SolidColorBrush(HexToColor(bgHex));
                    CardBgColorHex.Text = bgHex;
                    if (CardBgColorPicker.Color != HexToColor(bgHex)) CardBgColorPicker.Color = HexToColor(bgHex);
                }
                Properties_Global.Visibility = _selectedModel == null ? Visibility.Visible : Visibility.Collapsed;
                Properties_Specific.Visibility = _selectedModel != null ? Visibility.Visible : Visibility.Collapsed;
                Properties_Text.Visibility = Visibility.Collapsed;
                Properties_Bar.Visibility = Visibility.Collapsed;
                Properties_Image.Visibility = Visibility.Collapsed;

                if (_selectedModel != null)
                {
                    ElementNameBox.Text = _selectedModel.Name ?? "";
                    ElementCornerRadiusBox.Text = _selectedModel.CornerRadius.ToString();
                    ElementCornerRadiusSlider.Value = _selectedModel.CornerRadius;
                }

                if (_selectedModel is TextElement te)
                {
                    Properties_Text.Visibility = Visibility.Visible;
                    TextDataSourceCombo.SelectedIndex = te.DataSource == "Custom" ? 0 : (te.DataSource == "ProductName" ? 1 : (te.DataSource == "Size" ? 2 : (te.DataSource == "Wetness" ? 3 : (te.DataSource == "Messiness" ? 4 : 5))));
                    TextCustomBox.Text = te.CustomText ?? "";
                    
                    // Hide custom input if not Custom
                    TextCustomBox.Visibility = te.DataSource == "Custom" ? Visibility.Visible : Visibility.Collapsed;

                    // Sync fonts dropdowns
                    TextFontFamilyCombo.SelectedIndex = te.FontFamily == "Outfit" ? 0 :
                                                        te.FontFamily == "Segoe UI" ? 1 :
                                                        te.FontFamily == "Arial" ? 2 :
                                                        te.FontFamily == "Consolas" ? 3 : 0;

                    TextFontWeightCombo.SelectedIndex = te.FontWeight == "Normal" ? 0 :
                                                        te.FontWeight == "SemiBold" ? 1 :
                                                        te.FontWeight == "Bold" ? 2 : 2;

                    TextFontStyleCombo.SelectedIndex = te.FontStyle == "Normal" ? 0 :
                                                       te.FontStyle == "Italic" ? 1 : 0;

                    TextSizeSlider.Value = te.FontSize;
                    TextWrapToggle.IsOn = te.TextWrap;
                    TextColorPicker.Color = HexToColor(te.ColorHex);
                    TextColorPreview.Background = new SolidColorBrush(HexToColor(te.ColorHex));
                    TextColorHex.Text = te.ColorHex;
                    
                    AlignLeftBtn.IsChecked = te.TextAlignment == "Left";
                    AlignCenterBtn.IsChecked = te.TextAlignment == "Center";
                    AlignRightBtn.IsChecked = te.TextAlignment == "Right";
                }
                else if (_selectedModel is BarElement be)
                {
                    Properties_Bar.Visibility = Visibility.Visible;
                    BarDataSourceCombo.SelectedIndex = be.DataSource == "Wetness" ? 0 : 1;
                    BarOrientationCombo.SelectedIndex = be.Orientation == "Horizontal" ? 0 : 1;
                    BarFillColorPicker.Color = HexToColor(be.FillColorHex);
                    BarFillColorPreview.Background = new SolidColorBrush(HexToColor(be.FillColorHex));
                    BarFillColorHex.Text = be.FillColorHex;
                    
                    BarBgColorPicker.Color = HexToColor(be.BgColorHex);
                    BarBgColorPreview.Background = new SolidColorBrush(HexToColor(be.BgColorHex));
                    BarBgColorHex.Text = be.BgColorHex;
                }
                else if (_selectedModel is ImageElement ie)
                {
                    Properties_Image.Visibility = Visibility.Visible;
                    ImageDataSourceCombo.SelectedIndex = ie.DataSource == "DiapStashImage" ? 0 : (ie.DataSource == "Custom" ? 1 : 0);
                    ImageCustomUrlBox.Text = ie.CustomUrl ?? "";
                    
                    // Hide custom URL box if not Custom
                    ImageCustomUrlContainer.Visibility = ie.DataSource == "Custom" ? Visibility.Visible : Visibility.Collapsed;

                    ImageStretchCombo.SelectedIndex = ie.Stretch == "Uniform" ? 0 : (ie.Stretch == "UniformToFill" ? 1 : 2);
                }
                
                // Sync ZOrderTree selection
                if (_selectedModel != null) {
                    // TreeView node syncing requires recursive search, skip for simplicity unless needed
                }
            }
            finally
            {
                _isRefreshingProperties = false;
            }
        }

        private void ElementNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitialized || _isRefreshingProperties || _selectedModel == null) return;
            _selectedModel.Name = ElementNameBox.Text;
            
            // Sync TreeView visually
            foreach (var kvp in _nodeTags)
            {
                if (kvp.Value == _selectedModel && kvp.Key.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
                {
                    string defaultName = _selectedModel is GroupElement ? "Group" : _selectedModel is TextElement tmpText ? (tmpText.DataSource == "Custom" ? "Text" : tmpText.DataSource) : _selectedModel is BarElement tmpBar ? tmpBar.DataSource + " Bar" : _selectedModel is ImageElement tmpImg ? (tmpImg.DataSource == "Custom" ? "Image" : "DiapStash Avatar") : "Element";
                    tb.Text = string.IsNullOrWhiteSpace(_selectedModel.Name) ? defaultName : _selectedModel.Name;
                    break;
                }
            }
            
            SavePages();
        }

        private void ElementCornerRadiusSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (ElementCornerRadiusBox != null) ElementCornerRadiusBox.Text = e.NewValue.ToString("0");
        }

        private void ImageStretchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Just dummy logic if we need to expand.
            ElementProp_Changed(sender, null);
        }

        private async void ImageCustomUrlBrowseBtn_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var window = MainWindow.Instance;
            if (window == null) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".webp");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                string encodedPath = Uri.EscapeDataString(file.Path);
                ImageCustomUrlBox.Text = $"http://localhost:8890/overlay/local?path={encodedPath}";
            }
        }

        private void ElementProp_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _selectedModel == null || _isRefreshingProperties) return;
            
            if (double.TryParse(ElementCornerRadiusBox.Text, out double cr))
            {
                _selectedModel.CornerRadius = cr;
            }
            
            if (_selectedModel is TextElement te) {
                te.DataSource = (TextDataSourceCombo.SelectedItem as ComboBoxItem)?.Content.ToString();
                te.CustomText = TextCustomBox.Text;
                
                te.FontFamily = (TextFontFamilyCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Outfit";
                te.FontWeight = (TextFontWeightCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Bold";
                te.FontStyle = (TextFontStyleCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Normal";

                te.FontSize = TextSizeSlider.Value;
                te.TextWrap = TextWrapToggle.IsOn;
                te.ColorHex = ColorToHex(TextColorPicker.Color);
                TextColorPreview.Background = new SolidColorBrush(TextColorPicker.Color);
                TextColorHex.Text = te.ColorHex;

                // Update custom input visibility
                TextCustomBox.Visibility = te.DataSource == "Custom" ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (_selectedModel is BarElement be) {
                be.DataSource = (BarDataSourceCombo.SelectedItem as ComboBoxItem)?.Content.ToString();
                be.Orientation = (BarOrientationCombo.SelectedItem as ComboBoxItem)?.Content.ToString();
                be.FillColorHex = ColorToHex(BarFillColorPicker.Color);
                BarFillColorPreview.Background = new SolidColorBrush(BarFillColorPicker.Color);
                BarFillColorHex.Text = be.FillColorHex;
                
                be.BgColorHex = ColorToHex(BarBgColorPicker.Color);
                BarBgColorPreview.Background = new SolidColorBrush(BarBgColorPicker.Color);
                BarBgColorHex.Text = be.BgColorHex;
            }
            else if (_selectedModel is ImageElement ie) {
                ie.DataSource = (ImageDataSourceCombo.SelectedItem as ComboBoxItem)?.Content.ToString();
                ie.CustomUrl = ImageCustomUrlBox.Text;
                ie.Stretch = (ImageStretchCombo.SelectedItem as ComboBoxItem)?.Content.ToString();

                // Update custom URL input visibility
                ImageCustomUrlBox.Visibility = ie.DataSource == "Custom" ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
        }

        private (double X, double Y) GetAbsolutePosition(OverlayElement element)
        {
            var parent = GetParentGroup(element);
            if (parent != null)
            {
                var parentPos = GetAbsolutePosition(parent);
                return (parentPos.X + element.X, parentPos.Y + element.Y);
            }
            return (element.X, element.Y);
        }

        private void UpdateVisualPositions(OverlayElement element)
        {
            if (_modelToUi.TryGetValue(element, out var ui))
            {
                var absPos = GetAbsolutePosition(element);
                Canvas.SetLeft(ui, absPos.X);
                Canvas.SetTop(ui, absPos.Y);
            }
            if (element is GroupElement ge)
            {
                foreach (var child in ge.Children) UpdateVisualPositions(child);
            }
        }

        private Dictionary<OverlayElement, (double X, double Y)> _dragStartPositions = new();

        private void Element_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            foreach (var kvp in _dragStartPositions)
            {
                var model = kvp.Key;
                double targetX = kvp.Value.X + e.Cumulative.Translation.X;
                double targetY = kvp.Value.Y + e.Cumulative.Translation.Y;
                
                if (SnapGridToggle != null && SnapGridToggle.IsOn && GridSizeBox != null && double.TryParse(GridSizeBox.Text, out double grid) && grid > 0)
                {
                    targetX = Math.Round(targetX / grid) * grid;
                    targetY = Math.Round(targetY / grid) * grid;
                }
                
                model.X = targetX;
                model.Y = targetY;
                UpdateVisualPositions(model);
            }
            UpdateSelectionBox();
        }

        private void ResizeHandle_DragStarted(object sender, Microsoft.UI.Xaml.Controls.Primitives.DragStartedEventArgs e)
        {
            SaveStateForUndo();
            if (_selectedModel != null)
            {
                _dragStartPositions[_selectedModel] = (_selectedModel.X, _selectedModel.Y);
            }
        }

        private void ResizeHandle_DragDelta(object sender, Microsoft.UI.Xaml.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (_selectedElement != null && _selectedModel != null && sender is Microsoft.UI.Xaml.Controls.Primitives.Thumb thumb)
            {
                string tag = thumb.Tag?.ToString() ?? "BR";
                double minW = 10; double minH = 10;
                double dX = e.HorizontalChange; double dY = e.VerticalChange;

                if (tag.Contains("L")) { 
                    double oldW = _selectedModel.Width;
                    _selectedModel.Width = Math.Max(minW, oldW - dX); 
                    _selectedModel.X += (oldW - _selectedModel.Width); 
                }
                else if (tag.Contains("R")) { _selectedModel.Width = Math.Max(minW, _selectedModel.Width + dX); }

                if (tag.Contains("T")) { 
                    double oldH = _selectedModel.Height;
                    _selectedModel.Height = Math.Max(minH, oldH - dY); 
                    _selectedModel.Y += (oldH - _selectedModel.Height); 
                }
                else if (tag.Contains("B")) { _selectedModel.Height = Math.Max(minH, _selectedModel.Height + dY); }

                _selectedElement.Width = _selectedModel.Width; 
                _selectedElement.Height = _selectedModel.Height;
                UpdateVisualPositions(_selectedModel);
                UpdateSelectionBox(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
            }
        }

        private void UpdateSelectionBox()
        {
            if (SelectionGroup != null)
            {
                if (_selectedElements.Count > 0)
                {
                    SelectionGroup.Visibility = Visibility.Visible;
                    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                    foreach(var ui in _selectedElements)
                    {
                        double x = Canvas.GetLeft(ui);
                        double y = Canvas.GetTop(ui);
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x + ui.Width);
                        maxY = Math.Max(maxY, y + ui.Height);
                    }
                    SelectionGroup.Width = Math.Max(10, maxX - minX) + 10;
                    SelectionGroup.Height = Math.Max(10, maxY - minY) + 10;
                    Canvas.SetLeft(SelectionGroup, minX - 5);
                    Canvas.SetTop(SelectionGroup, minY - 5);
                }
                else
                {
                    SelectionGroup.Visibility = Visibility.Collapsed;
                }
            }
        }

        // --- Z-INDEX & ACTIONS ---
        // --- ACTIONS ---
        private void DeleteElementBtn_Click(object sender, RoutedEventArgs e) { 
            if (_selectedModels.Count > 0)
            {
                SaveStateForUndo();
                foreach (var model in _selectedModels.ToList())
                {
                    RemoveElementRecursive(OverlayServer.Instance.Elements, model);
                }
                ClearSelection(); 
                UpdateLocalPreview(); 
                SavePages(); 
                _ = SyncDesignWithOverlayServerAsync(); 
            }
        }

        private bool RemoveElementRecursive(System.Collections.Generic.List<OverlayElement> list, OverlayElement target)
        {
            if (list.Remove(target)) return true;
            foreach (var item in list.OfType<GroupElement>())
            {
                if (RemoveElementRecursive(item.Children, target)) return true;
            }
            return false;
        }

        private void DrawGrid()
        {
            if (GridCanvas == null) return;
            GridCanvas.Children.Clear();

            if (SnapGridToggle != null && SnapGridToggle.IsOn && GridSizeBox != null && double.TryParse(GridSizeBox.Text, out double gridSize) && gridSize > 0)
            {
                double width = CardWidthSlider.Value;
                double height = CardHeightSlider.Value;
                var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 0, 0, 0)); // Semi-transparent black

                for (double x = 0; x <= width; x += gridSize)
                {
                    GridCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = brush, StrokeThickness = 1 });
                }
                for (double y = 0; y <= height; y += gridSize)
                {
                    GridCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = 1 });
                }
            }
        }

        private void ZOrderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            var node = args.InvokedItem as TreeViewNode;
            if (node != null && _nodeTags.TryGetValue(node, out var tag))
            {
                if (tag?.ToString() == "CARD")
                {
                    ClearSelection();
                }
                else if (tag is OverlayElement el)
                {
                    SelectOnly(el);
                }
            }
        }

        // --- GLOBAL ---
        private void SnapGridToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (GridSizeBox != null)
            {
                GridSizeBox.Visibility = SnapGridToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            }
            Control_Changed(sender, e);
        }

        private void Control_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _isRefreshingProperties) return;

            _isRefreshingProperties = true;
            try
            {
                if (ReferenceEquals(sender, CardWidthSlider) && CardWidthBox != null)
                {
                    CardWidthBox.Text = Math.Round(CardWidthSlider.Value).ToString();
                }
                else if (ReferenceEquals(sender, CardHeightSlider) && CardHeightBox != null)
                {
                    CardHeightBox.Text = Math.Round(CardHeightSlider.Value).ToString();
                }

                SavePages();
                _ = SyncDesignWithOverlayServerAsync();
                UpdateLocalPreview();
            }
            finally
            {
                _isRefreshingProperties = false;
            }
        }

        private void CardDimensionBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitialized || _isRefreshingProperties) return;

            if (sender is TextBox tb && double.TryParse(tb.Text, out double val))
            {
                _isRefreshingProperties = true;
                try
                {
                    if (tb == CardWidthBox && CardWidthSlider != null)
                    {
                        double clamped = Math.Max(CardWidthSlider.Minimum, Math.Min(CardWidthSlider.Maximum, val));
                        CardWidthSlider.Value = clamped;
                    }
                    else if (tb == CardHeightBox && CardHeightSlider != null)
                    {
                        double clamped = Math.Max(CardHeightSlider.Minimum, Math.Min(CardHeightSlider.Maximum, val));
                        CardHeightSlider.Value = clamped;
                    }

                    SavePages();
                    _ = SyncDesignWithOverlayServerAsync();
                    UpdateLocalPreview();
                }
                finally
                {
                    _isRefreshingProperties = false;
                }
            }
        }

        private void Color_Changed(ColorPicker sender, ColorChangedEventArgs args) 
        { 
            if (!_isRefreshingProperties) {
                var hex = ColorToHex(sender.Color);
                if (sender == CardBgColorPicker) {
                    CardBgColorPreview.Background = new SolidColorBrush(sender.Color);
                    CardBgColorHex.Text = hex;
                    if (_pages.Count > 0 && _activePageIndex >= 0) _pages[_activePageIndex].CardBackgroundHex = hex;
                }
                Control_Changed(sender, null);
            }
        }
        
        private void ElementPropColor_Changed(ColorPicker sender, ColorChangedEventArgs args) => ElementProp_Changed(sender, null);
        private void SaveBtn_Click(object sender, RoutedEventArgs e) { SavePages(); _ = SyncDesignWithOverlayServerAsync(); }
        
        private GroupElement GetParentGroup(OverlayElement element, List<OverlayElement> searchList = null)
        {
            searchList ??= OverlayServer.Instance.Elements;
            foreach (var el in searchList)
            {
                if (el is GroupElement g)
                {
                    if (g.Children.Contains(element)) return g;
                    var res = GetParentGroup(element, g.Children);
                    if (res != null) return res;
                }
            }
            return null;
        }

        private void AlignLeftBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel != null)
            {
                SaveStateForUndo();
                _selectedModel.X = 0;
                UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
            }
        }
        
        private void AlignCenterH_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel != null)
            {
                SaveStateForUndo();
                var parentGroup = GetParentGroup(_selectedModel);
                double parentWidth = parentGroup != null ? parentGroup.Width : CardWidthSlider.Value;
                _selectedModel.X = (parentWidth - _selectedModel.Width) / 2;
                UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
            }
        }

        private void AlignRightBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel != null)
            {
                SaveStateForUndo();
                var parentGroup = GetParentGroup(_selectedModel);
                double parentWidth = parentGroup != null ? parentGroup.Width : CardWidthSlider.Value;
                _selectedModel.X = parentWidth - _selectedModel.Width;
                UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
            }
        }
        
        private void AlignTopBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel != null)
            {
                SaveStateForUndo();
                _selectedModel.Y = 0;
                UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
            }
        }

        private void AlignCenterV_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel != null)
            {
                SaveStateForUndo();
                var parentGroup = GetParentGroup(_selectedModel);
                double parentHeight = parentGroup != null ? parentGroup.Height : CardHeightSlider.Value;
                _selectedModel.Y = (parentHeight - _selectedModel.Height) / 2;
                UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
            }
        }

        private void AlignBottomBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel != null)
            {
                SaveStateForUndo();
                var parentGroup = GetParentGroup(_selectedModel);
                double parentHeight = parentGroup != null ? parentGroup.Height : CardHeightSlider.Value;
                _selectedModel.Y = parentHeight - _selectedModel.Height;
                UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
            }
        }
        
        private void TextAlign_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton btn && _selectedModel is TextElement te)
            {
                SaveStateForUndo();
                te.TextAlignment = btn.Tag.ToString();
                UpdateLocalPreview(); SavePages(); _ = SyncDesignWithOverlayServerAsync();
            }
        }

        private async Task SyncDesignWithOverlayServerAsync()
        {
            var s = OverlayServer.Instance;
            s.CardW = CardWidthSlider.Value;
            s.CardH = CardHeightSlider.Value;
            s.TransitionType = TransitionTypeCombo.SelectedIndex;
            s.TransitionDurationMs = TransitionSpeedSlider.Value;
            s.StayOnScreenDurationMs = DisplayDurationSlider.Value;
            s.CardCornerRadius = CardCornerRadiusSlider.Value;
            s.CardBackgroundHex = ColorToHex(CardBgColorPicker.Color);

            var state = await DiapStashClient.Instance.FetchLatestChangeStateObjectAsync();
            if (state != null)
            {
                s.LiveProductName = $"{state.ProductName}";
                s.LiveSize = state.Size;
                s.LiveWetPercentage = state.WetnessPercentage;
                s.LiveMessPercentage = state.MessyPercentage;
                s.LiveImageUrl = state.ImageUrl;
            }
        }

        private bool _isPanning = false;
        private Windows.Foundation.Point _panStartPos;

        private void WorkspaceGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var ptr = e.GetCurrentPoint(WorkspaceGrid);
            var delta = ptr.Properties.MouseWheelDelta;
            
            double zoomFactor = delta > 0 ? 1.1 : 1/1.1;
            double oldScale = ArtboardTransform.ScaleX;
            double newScale = oldScale * zoomFactor;
            
            if (newScale < 0.2) newScale = 0.2;
            if (newScale > 5.0) newScale = 5.0;
            
            zoomFactor = newScale / oldScale; // Recalculate true zoom in case of clamping

            var mousePos = ptr.Position;
            double centerX = WorkspaceGrid.ActualWidth / 2 + ArtboardTransform.TranslateX;
            double centerY = WorkspaceGrid.ActualHeight / 2 + ArtboardTransform.TranslateY;
            double dx = mousePos.X - centerX;
            double dy = mousePos.Y - centerY;
            
            ArtboardTransform.TranslateX += dx * (1 - zoomFactor);
            ArtboardTransform.TranslateY += dy * (1 - zoomFactor);
            ArtboardTransform.ScaleX = newScale;
            ArtboardTransform.ScaleY = newScale;
        }

        private void WorkspaceGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var ptr = e.GetCurrentPoint(WorkspaceGrid);
            if (ptr.Properties.IsMiddleButtonPressed || ptr.Properties.IsRightButtonPressed)
            {
                _isPanning = true;
                _panStartPos = ptr.Position;
                WorkspaceGrid.CapturePointer(e.Pointer);
            }
            else if (ptr.Properties.IsLeftButtonPressed)
            {
                ClearSelection();
                SelectionGroup.Visibility = Visibility.Collapsed;
            }
        }

        private void WorkspaceGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isPanning)
            {
                var ptr = e.GetCurrentPoint(WorkspaceGrid);
                ArtboardTransform.TranslateX += (ptr.Position.X - _panStartPos.X);
                ArtboardTransform.TranslateY += (ptr.Position.Y - _panStartPos.Y);
                _panStartPos = ptr.Position;
            }
        }

        private void WorkspaceGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                WorkspaceGrid.ReleasePointerCapture(e.Pointer);
            }
        }

        private void BackgroundGridCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var canvas = sender as Canvas;
            canvas.Children.Clear();
            var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)); 
            
            for (double x = 0; x < canvas.ActualWidth; x += 40)
            {
                canvas.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = canvas.ActualHeight, Stroke = brush, StrokeThickness = 1 });
            }
            for (double y = 0; y < canvas.ActualHeight; y += 40)
            {
                canvas.Children.Add(new Line { X1 = 0, Y1 = y, X2 = canvas.ActualWidth, Y2 = y, Stroke = brush, StrokeThickness = 1 });
            }
        }

        private async void ObsHelpBtn_Click(object sender, RoutedEventArgs e)
        {
            var stack = new StackPanel { Spacing = 12, Width = 450 };
            
            stack.Children.Add(new TextBlock { 
                Text = "Follow these steps to add the telemetry overlay to OBS Studio:", 
                TextWrapping = TextWrapping.Wrap, 
                Margin = new Thickness(0,0,0,4) 
            });

            var urlLabel = new TextBlock {
                Text = "Overlay URL",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 128, 128, 128))
            };
            stack.Children.Add(urlLabel);

            var urlPanel = new Grid { ColumnSpacing = 8 };
            urlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            urlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var urlBox = new TextBox { 
                Text = "http://localhost:8890/overlay/", 
                IsReadOnly = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            urlPanel.Children.Add(urlBox);
            Grid.SetColumn(urlBox, 0);

            var copyBtn = new Button { 
                Content = "📋 Copy", 
                VerticalAlignment = VerticalAlignment.Center 
            };
            copyBtn.Click += (s, ev) => {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(urlBox.Text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                copyBtn.Content = "✅ Copied!";
                var queue = this.DispatcherQueue;
                Task.Run(async () => {
                    await Task.Delay(1500);
                    queue.TryEnqueue(() => { copyBtn.Content = "📋 Copy"; });
                });
            };
            urlPanel.Children.Add(copyBtn);
            Grid.SetColumn(copyBtn, 1);
            stack.Children.Add(urlPanel);

            var instLabel = new TextBlock {
                Text = "Setup Steps",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 128, 128, 128)),
                Margin = new Thickness(0,8,0,0)
            };
            stack.Children.Add(instLabel);

            var stepsPanel = new StackPanel { Spacing = 6 };
            
            var steps = new string[] {
                "1. Open OBS Studio, go to 'Sources' and click '+' to add a Browser Source.",
                "2. Name it (e.g., 'DiapStash Card') and click OK.",
                "3. Paste the copied Overlay URL into the URL field.",
                $"4. Set Width to {(int)CardWidthSlider.Value} and Height to {(int)CardHeightSlider.Value} (matches your current canvas dimensions).",
                "5. (Recommended) Check both 'Shutdown source when not visible' and 'Refresh browser when scene becomes active'.",
                "6. Click OK. Press the '🚀 Send to OBS' button in this app to test the layout animations."
            };

            foreach (var step in steps)
            {
                stepsPanel.Children.Add(new TextBlock { 
                    Text = step, 
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13
                });
            }
            stack.Children.Add(stepsPanel);

            var dialog = new ContentDialog
            {
                Title = "OBS Setup Instructions",
                Content = stack,
                CloseButtonText = "Done",
                XamlRoot = this.Content.XamlRoot
            };
            
            await dialog.ShowAsync();
        }
        private async void TtsHelpBtn_Click(object sender, RoutedEventArgs e)
        {
            var stack = new StackPanel { Spacing = 12, Width = 480 };
            
            stack.Children.Add(new TextBlock { 
                Text = "JakeyTTS triggers this overlay automatically when mapped commands or channel rewards are executed.", 
                TextWrapping = TextWrapping.Wrap, 
                Margin = new Thickness(0,0,0,4) 
            });

            var recLabel = new TextBlock {
                Text = "Method 1: UI Trigger Plugin Column (Recommended)",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Margin = new Thickness(0,8,0,0)
            };
            stack.Children.Add(recLabel);

            var stepsPanel1 = new StackPanel { Spacing = 6 };
            var steps1 = new string[] {
                "1. Open JakeyTTS and navigate to the 'Commands' or 'Twitch Rewards' page.",
                "2. Find the command/reward you want to use (or create a new one).",
                "3. In the 'Trigger Plugin' column, click the dropdown and select:",
                "   👉 'diapstash_show (DiapStash Integration Bridge)'",
                "4. Click 'Save All Changes'. When this command/reward triggers on Twitch, the overlay will automatically slide in."
            };
            foreach (var step in steps1)
            {
                stepsPanel1.Children.Add(new TextBlock { 
                    Text = step, 
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13
                });
            }
            stack.Children.Add(stepsPanel1);

            var legacyLabel = new TextBlock {
                Text = "Method 2: TTS response variables (Alternative)",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Margin = new Thickness(0,10,0,0)
            };
            stack.Children.Add(legacyLabel);

            var stepsPanel2 = new StackPanel { Spacing = 6 };
            var steps2 = new string[] {
                "1. Add '{diapstash_show}' anywhere in a command or reward's TTS response text box.",
                "2. JakeyTTS will automatically filter out the brace tag (returning empty text for it to the speaker) and send a WebSocket event to show the DiapStash overlay."
            };
            foreach (var step in steps2)
            {
                stepsPanel2.Children.Add(new TextBlock { 
                    Text = step, 
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13
                });
            }
            stack.Children.Add(stepsPanel2);

            var dialog = new ContentDialog
            {
                Title = "JakeyTTS Setup Instructions",
                Content = stack,
                CloseButtonText = "Close",
                XamlRoot = this.Content.XamlRoot
            };
            
            await dialog.ShowAsync();
        }
        private void StartPreviewTimer()
        {
            if (_previewTimer == null)
            {
                _previewTimer = new DispatcherTimer();
                _previewTimer.Interval = TimeSpan.FromSeconds(6);
                _previewTimer.Tick += (s, e) =>
                {
                    _previewTimer.Stop();
                    _previewMode = false;
                    UpdateLocalPreview();
                };
            }
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void PlayLocalTransition()
        {
            double duration = TransitionSpeedSlider.Value;
            int type = TransitionTypeCombo.SelectedIndex;

            // Reset animation transform properties first
            AnimationTransform.ScaleX = 1.0;
            AnimationTransform.ScaleY = 1.0;
            AnimationTransform.TranslateX = 0.0;
            AnimationTransform.TranslateY = 0.0;
            WidgetArtboard.Opacity = 1.0;

            var sb = new Storyboard();
            var dur = TimeSpan.FromMilliseconds(duration);

            var opacityAnim = new DoubleAnimation { To = 1.0, Duration = dur };
            Storyboard.SetTarget(opacityAnim, WidgetArtboard);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            sb.Children.Add(opacityAnim);

            switch (type)
            {
                case 0: // Fade In
                    opacityAnim.From = 0.0;
                    break;
                case 1: // Zoom In
                    opacityAnim.From = 0.0;
                    var scaleX = new DoubleAnimation { From = 0.85, To = 1.0, Duration = dur, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    var scaleY = new DoubleAnimation { From = 0.85, To = 1.0, Duration = dur, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    Storyboard.SetTarget(scaleX, AnimationTransform);
                    Storyboard.SetTargetProperty(scaleX, "ScaleX");
                    Storyboard.SetTarget(scaleY, AnimationTransform);
                    Storyboard.SetTargetProperty(scaleY, "ScaleY");
                    sb.Children.Add(scaleX);
                    sb.Children.Add(scaleY);
                    break;
                case 2: // Slide Left
                    opacityAnim.From = 0.0;
                    var slideL = new DoubleAnimation { From = 100.0, To = 0.0, Duration = dur, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    Storyboard.SetTarget(slideL, AnimationTransform);
                    Storyboard.SetTargetProperty(slideL, "TranslateX");
                    sb.Children.Add(slideL);
                    break;
                case 3: // Slide Right
                    opacityAnim.From = 0.0;
                    var slideR = new DoubleAnimation { From = -100.0, To = 0.0, Duration = dur, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    Storyboard.SetTarget(slideR, AnimationTransform);
                    Storyboard.SetTargetProperty(slideR, "TranslateX");
                    sb.Children.Add(slideR);
                    break;
                case 4: // Slide Top
                    opacityAnim.From = 0.0;
                    var slideT = new DoubleAnimation { From = 100.0, To = 0.0, Duration = dur, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    Storyboard.SetTarget(slideT, AnimationTransform);
                    Storyboard.SetTargetProperty(slideT, "TranslateY");
                    sb.Children.Add(slideT);
                    break;
                case 5: // Slide Bottom
                    opacityAnim.From = 0.0;
                    var slideB = new DoubleAnimation { From = -100.0, To = 0.0, Duration = dur, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    Storyboard.SetTarget(slideB, AnimationTransform);
                    Storyboard.SetTargetProperty(slideB, "TranslateY");
                    sb.Children.Add(slideB);
                    break;
                case 6: // Bounce Pop
                    opacityAnim.From = 0.0;
                    var bounceX = new DoubleAnimation { From = 0.5, To = 1.0, Duration = dur, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
                    var bounceY = new DoubleAnimation { From = 0.5, To = 1.0, Duration = dur, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
                    Storyboard.SetTarget(bounceX, AnimationTransform);
                    Storyboard.SetTargetProperty(bounceX, "ScaleX");
                    Storyboard.SetTarget(bounceY, AnimationTransform);
                    Storyboard.SetTargetProperty(bounceY, "ScaleY");
                    sb.Children.Add(bounceX);
                    sb.Children.Add(bounceY);
                    break;
            }

            sb.Begin();
        }

        private async void PreviewCanvasBtn_Click(object sender, RoutedEventArgs e)
        {
            await SyncDesignWithOverlayServerAsync();
            _previewMode = true;
            UpdateLocalPreview();
            PlayLocalTransition();
            StartPreviewTimer();
        }
        private async void LaunchObsBtn_Click(object sender, RoutedEventArgs e) { SavePages(); await SyncDesignWithOverlayServerAsync(); OverlayServer.Instance.ForcePreviewTrigger = true; }
        
        private string ColorToHex(Windows.UI.Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        private Windows.UI.Color HexToColor(string hex) {
            try {
                hex = hex.Replace("#", "");
                if (hex.Length == 6) hex = "FF" + hex;
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return Windows.UI.Color.FromArgb(a, r, g, b);
            } catch { return Microsoft.UI.Colors.Transparent; }
        }
    }
}