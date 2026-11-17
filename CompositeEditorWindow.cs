using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DecklinkSwitcher
{
    public sealed class CompositeEditorWindow : Window
    {
        private const double CanvasW = 480.0;
        private const double CanvasH = 270.0;

        private static readonly Color[] LayerColors =
        {
            Color.FromRgb(0x00, 0xe6, 0x76),
            Color.FromRgb(0x29, 0xb6, 0xf6),
            Color.FromRgb(0xff, 0xa7, 0x26),
            Color.FromRgb(0xef, 0x53, 0x50)
        };

        private readonly List<CompositeLayerConfig> _layers;
        private readonly List<string> _availableSources;
        private readonly WriteableBitmap? _livePreviewBmp;
        private readonly Func<string> _getBgHex;
        private readonly Action<string> _setBgHex;
        private readonly Action _onChanged;

        private readonly List<LayerRowControls> _rows = new();
        private readonly Canvas _visualCanvas = new() { Width = CanvasW, Height = CanvasH, ClipToBounds = true, Background = Brushes.Black };
        private readonly List<Border> _canvasBoxes = new();
        private readonly List<TextBlock> _canvasLabels = new();
        private readonly List<Rectangle> _resizeHandles = new();
        private readonly List<Rectangle[]> _cropHandles = new(); // [L, T, R, B]
        private Grid _layerTableGrid = null!;
        private ComboBox _cmbBgColor = null!;
        private RadioButton _rbModeMove = null!;
        private RadioButton _rbModeCrop = null!;
        private bool _isUpdatingUi = false;

        // Drag / resize / crop state
        private int _dragLayerIdx = -1;
        private int _dragMode = 0; // 0=move, 1=resize, 2=cropL, 3=cropT, 4=cropR, 5=cropB
        private Point _dragStartMouse;
        private double _dragStartX, _dragStartY, _dragStartW, _dragStartH;
        private double _dragStartCropL, _dragStartCropT, _dragStartCropR, _dragStartCropB;

        private sealed class LayerRowControls
        {
            public CheckBox ChkEnabled = null!;
            public ComboBox CmbSource = null!;
            public TextBox TxtX = null!;
            public TextBox TxtY = null!;
            public TextBox TxtW = null!;
            public TextBox TxtH = null!;
            public TextBox TxtCropL = null!;
            public TextBox TxtCropT = null!;
            public TextBox TxtCropR = null!;
            public TextBox TxtCropB = null!;
            public TextBox TxtBorder = null!;
        }

        public CompositeEditorWindow(
            List<CompositeLayerConfig> layers,
            List<string> availableSources,
            WriteableBitmap? livePreviewBmp,
            Func<string> getBgHex,
            Action<string> setBgHex,
            Action onChanged)
        {
            _layers = layers;
            _availableSources = availableSources;
            _livePreviewBmp = livePreviewBmp;
            _getBgHex = getBgHex;
            _setBgHex = setBgHex;
            _onChanged = onChanged;

            Title = "Multi-Box / Composite Layout Editor";
            Width = 960;
            Height = 685;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x20));
            Foreground = Brushes.White;

            BuildUi();
            PopulateRows();
            UpdateVisualCanvas();
        }

        private void BuildUi()
        {
            var root = new StackPanel { Margin = new Thickness(14) };

            // Top bar: Presets + Background Color
            var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

            var presetRow = new StackPanel { Orientation = Orientation.Horizontal };
            presetRow.Children.Add(new TextBlock
            {
                Text = "Presets:",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = Brushes.LightGray
            });
            presetRow.Children.Add(MakePresetButton("2-Up Side-by-Side", ApplyPreset2Up));
            presetRow.Children.Add(MakePresetButton("PiP (Main + Corner)", ApplyPresetPip));
            presetRow.Children.Add(MakePresetButton("3-Box (1 + 2 Side)", ApplyPreset3Box));
            presetRow.Children.Add(MakePresetButton("2×2 Quad", ApplyPreset4Quad));
            var btnAddBox = MakePresetButton("+ Add Box", AddNewBox);
            btnAddBox.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xc0));
            btnAddBox.FontWeight = FontWeights.Bold;
            presetRow.Children.Add(btnAddBox);
            DockPanel.SetDock(presetRow, Dock.Left);
            topBar.Children.Add(presetRow);

            var bgRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            bgRow.Children.Add(new TextBlock
            {
                Text = "Background:",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 6, 0),
                Foreground = Brushes.LightGray
            });
            _cmbBgColor = new ComboBox { Width = 130, Height = 26, FontSize = 11 };
            _cmbBgColor.Items.Add(new ComboBoxItem { Content = "Black", Tag = "#000000" });
            _cmbBgColor.Items.Add(new ComboBoxItem { Content = "Dark Navy", Tag = "#0B162C" });
            _cmbBgColor.Items.Add(new ComboBoxItem { Content = "Broadcast Blue", Tag = "#0E2A5C" });
            _cmbBgColor.Items.Add(new ComboBoxItem { Content = "Dark Slate", Tag = "#1F242D" });
            _cmbBgColor.Items.Add(new ComboBoxItem { Content = "Chroma Green", Tag = "#00B140" });
            _cmbBgColor.Items.Add(new ComboBoxItem { Content = "Custom...", Tag = "CUSTOM" });
            SelectBgItem(_getBgHex());
            _cmbBgColor.SelectionChanged += CmbBgColor_SelectionChanged;
            bgRow.Children.Add(_cmbBgColor);
            DockPanel.SetDock(bgRow, Dock.Right);
            topBar.Children.Add(bgRow);

            root.Children.Add(topBar);

            // Canvas Mode bar (Move/Resize vs Visual Crop)
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            modeRow.Children.Add(new TextBlock
            {
                Text = "Canvas Tool:",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Silver,
                Margin = new Thickness(0, 0, 10, 0)
            });
            _rbModeMove = new RadioButton
            {
                Content = "Move / Resize",
                IsChecked = true,
                Foreground = Brushes.White,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            _rbModeCrop = new RadioButton
            {
                Content = "Crop Edges (drag L / T / R / B handles)",
                Foreground = Brushes.Yellow,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            _rbModeMove.Checked += (_, _) => UpdateHandleVisibility();
            _rbModeCrop.Checked += (_, _) => UpdateHandleVisibility();
            modeRow.Children.Add(_rbModeMove);
            modeRow.Children.Add(_rbModeCrop);
            root.Children.Add(modeRow);

            // Interactive 480x270 visual canvas
            var canvasBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x58)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12),
                Child = _visualCanvas
            };

            if (_livePreviewBmp != null)
            {
                var liveImg = new System.Windows.Controls.Image
                {
                    Source = _livePreviewBmp,
                    Width = CanvasW,
                    Height = CanvasH,
                    Stretch = Stretch.Uniform,
                    IsHitTestVisible = false
                };
                _visualCanvas.Children.Add(liveImg);
            }

            root.Children.Add(canvasBorder);

            // Scrollable Layer table
            _layerTableGrid = new Grid();
            var scroller = new ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _layerTableGrid
            };
            root.Children.Add(scroller);

            var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var note = new TextBlock
            {
                Text = "Drag boxes on the preview to move/resize or switch to Crop Edges. Last box draws on top.",
                FontSize = 11,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(note, Dock.Left);
            footer.Children.Add(note);

            var btnClose = new Button
            {
                Content = "Done",
                Width = 90,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromRgb(0x2e, 0x7d, 0x32)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold
            };
            btnClose.Click += (_, _) => Close();
            DockPanel.SetDock(btnClose, Dock.Right);
            footer.Children.Add(btnClose);
            root.Children.Add(footer);

            Content = root;
            RebuildLayerUi();
        }

        private void RebuildLayerUi()
        {
            // Rebuild canvas overlay boxes (keep liveImg at index 0 if present)
            int startKeep = _livePreviewBmp != null ? 1 : 0;
            while (_visualCanvas.Children.Count > startKeep)
                _visualCanvas.Children.RemoveAt(_visualCanvas.Children.Count - 1);

            _canvasBoxes.Clear();
            _canvasLabels.Clear();
            _resizeHandles.Clear();
            _cropHandles.Clear();

            for (int i = 0; i < _layers.Count; i++)
            {
                int idx = i;
                var color = LayerColors[i % LayerColors.Length];
                var box = new Border
                {
                    BorderBrush = new SolidColorBrush(color),
                    BorderThickness = new Thickness(2),
                    Background = new SolidColorBrush(Color.FromArgb(24, color.R, color.G, color.B)),
                    Cursor = Cursors.SizeAll
                };

                var innerGrid = new Grid();
                var lbl = new TextBlock
                {
                    Text = $"Box {i + 1}",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                    Padding = new Thickness(4, 1, 4, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false
                };
                innerGrid.Children.Add(lbl);

                var resizeHandle = new Rectangle
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(color),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Cursor = Cursors.SizeNWSE
                };
                resizeHandle.MouseLeftButtonDown += (s, e) =>
                {
                    StartDrag(idx, 1, e.GetPosition(_visualCanvas));
                    resizeHandle.CaptureMouse();
                    e.Handled = true;
                };
                resizeHandle.MouseMove += (s, e) => OnCanvasMouseMove(e.GetPosition(_visualCanvas));
                resizeHandle.MouseLeftButtonUp += (s, e) =>
                {
                    if (_dragLayerIdx == idx)
                    {
                        _dragLayerIdx = -1;
                        resizeHandle.ReleaseMouseCapture();
                    }
                };
                innerGrid.Children.Add(resizeHandle);
                _resizeHandles.Add(resizeHandle);

                var cropL = MakeCropHandle(idx, 2, Brushes.Yellow, 6, double.NaN, HorizontalAlignment.Left, VerticalAlignment.Stretch, Cursors.SizeWE);
                var cropT = MakeCropHandle(idx, 3, Brushes.Yellow, double.NaN, 6, HorizontalAlignment.Stretch, VerticalAlignment.Top, Cursors.SizeNS);
                var cropR = MakeCropHandle(idx, 4, Brushes.Yellow, 6, double.NaN, HorizontalAlignment.Right, VerticalAlignment.Stretch, Cursors.SizeWE);
                var cropB = MakeCropHandle(idx, 5, Brushes.Yellow, double.NaN, 6, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, Cursors.SizeNS);
                innerGrid.Children.Add(cropL);
                innerGrid.Children.Add(cropT);
                innerGrid.Children.Add(cropR);
                innerGrid.Children.Add(cropB);
                _cropHandles.Add(new[] { cropL, cropT, cropR, cropB });

                box.Child = innerGrid;
                box.MouseLeftButtonDown += (s, e) =>
                {
                    StartDrag(idx, 0, e.GetPosition(_visualCanvas));
                    box.CaptureMouse();
                    e.Handled = true;
                };
                box.MouseMove += (s, e) => OnCanvasMouseMove(e.GetPosition(_visualCanvas));
                box.MouseLeftButtonUp += (s, e) =>
                {
                    if (_dragLayerIdx == idx)
                    {
                        _dragLayerIdx = -1;
                        box.ReleaseMouseCapture();
                    }
                };

                _canvasBoxes.Add(box);
                _canvasLabels.Add(lbl);
                _visualCanvas.Children.Add(box);
            }

            // Rebuild table rows
            _layerTableGrid.Children.Clear();
            _layerTableGrid.RowDefinitions.Clear();
            _layerTableGrid.ColumnDefinitions.Clear();
            _rows.Clear();

            double[] widths = { 28, 44, 46, 122, 48, 48, 48, 48, 48, 48, 48, 48, 46, 48, 28 };
            foreach (double w in widths)
                _layerTableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

            string[] headers = { "On", "Z", "Layer", "Source", "X %", "Y %", "W %", "H %", "Crop L%", "Crop T%", "Crop R%", "Crop B%", "Border", "Crop", "" };
            _layerTableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < headers.Length; c++)
            {
                var tb = new TextBlock
                {
                    Text = headers[c],
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.Silver,
                    Margin = new Thickness(2, 0, 2, 6),
                    HorizontalAlignment = c >= 4 || c == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Left
                };
                Grid.SetRow(tb, 0);
                Grid.SetColumn(tb, c);
                _layerTableGrid.Children.Add(tb);
            }

            for (int i = 0; i < _layers.Count; i++)
            {
                int idx = i;
                _layerTableGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
                int rowIdx = i + 1;
                var row = new LayerRowControls();

                row.ChkEnabled = new CheckBox { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                row.ChkEnabled.Checked += (_, _) => SyncFromUi();
                row.ChkEnabled.Unchecked += (_, _) => SyncFromUi();
                Grid.SetRow(row.ChkEnabled, rowIdx);
                Grid.SetColumn(row.ChkEnabled, 0);
                _layerTableGrid.Children.Add(row.ChkEnabled);

                var zPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var btnUp = MakeMiniButton("▲", "Move layer down (behind)", () => SwapLayers(idx, idx - 1), i > 0);
                var btnDown = MakeMiniButton("▼", "Move layer up (in front)", () => SwapLayers(idx, idx + 1), i < _layers.Count - 1);
                zPanel.Children.Add(btnUp);
                zPanel.Children.Add(btnDown);
                Grid.SetRow(zPanel, rowIdx);
                Grid.SetColumn(zPanel, 1);
                _layerTableGrid.Children.Add(zPanel);

                var lbl = new TextBlock
                {
                    Text = $"Box {i + 1}",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(LayerColors[i % LayerColors.Length]),
                    Margin = new Thickness(3, 0, 3, 0)
                };
                Grid.SetRow(lbl, rowIdx);
                Grid.SetColumn(lbl, 2);
                _layerTableGrid.Children.Add(lbl);

                row.CmbSource = new ComboBox
                {
                    Height = 26,
                    FontSize = 11,
                    Margin = new Thickness(2, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.CmbSource.Items.Add("");
                foreach (string src in _availableSources) row.CmbSource.Items.Add(src);
                row.CmbSource.SelectionChanged += (_, _) => SyncFromUi();
                Grid.SetRow(row.CmbSource, rowIdx);
                Grid.SetColumn(row.CmbSource, 3);
                _layerTableGrid.Children.Add(row.CmbSource);

                row.TxtX = MakeNumBox(_layerTableGrid, rowIdx, 4);
                row.TxtY = MakeNumBox(_layerTableGrid, rowIdx, 5);
                row.TxtW = MakeNumBox(_layerTableGrid, rowIdx, 6);
                row.TxtH = MakeNumBox(_layerTableGrid, rowIdx, 7);
                row.TxtCropL = MakeNumBox(_layerTableGrid, rowIdx, 8);
                row.TxtCropT = MakeNumBox(_layerTableGrid, rowIdx, 9);
                row.TxtCropR = MakeNumBox(_layerTableGrid, rowIdx, 10);
                row.TxtCropB = MakeNumBox(_layerTableGrid, rowIdx, 11);
                row.TxtBorder = MakeNumBox(_layerTableGrid, rowIdx, 12);

                var btnResetCrop = new Button
                {
                    Content = "Reset",
                    Height = 24,
                    FontSize = 10,
                    Margin = new Thickness(2, 0, 2, 0),
                    Background = new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x46)),
                    Foreground = Brushes.White,
                    ToolTip = "Reset all crop edges to 0%"
                };
                btnResetCrop.Click += (_, _) =>
                {
                    if (idx < _layers.Count)
                    {
                        _layers[idx].CropLeftPct = 0;
                        _layers[idx].CropTopPct = 0;
                        _layers[idx].CropRightPct = 0;
                        _layers[idx].CropBottomPct = 0;
                        PopulateRows();
                        UpdateVisualCanvas();
                        _onChanged?.Invoke();
                    }
                };
                Grid.SetRow(btnResetCrop, rowIdx);
                Grid.SetColumn(btnResetCrop, 13);
                _layerTableGrid.Children.Add(btnResetCrop);

                var btnRemove = new Button
                {
                    Content = "✕",
                    Width = 22,
                    Height = 22,
                    FontSize = 10,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Color.FromRgb(0x8e, 0x24, 0x24)),
                    Foreground = Brushes.White,
                    IsEnabled = _layers.Count > 1,
                    ToolTip = "Remove this box"
                };
                btnRemove.Click += (_, _) => RemoveBox(idx);
                Grid.SetRow(btnRemove, rowIdx);
                Grid.SetColumn(btnRemove, 14);
                _layerTableGrid.Children.Add(btnRemove);

                _rows.Add(row);
            }

            UpdateHandleVisibility();
            PopulateRows();
            UpdateVisualCanvas();
        }

        private void AddNewBox()
        {
            int idx = _layers.Count;
            double offset = (idx % 4) * 8.0;
            _layers.Add(new CompositeLayerConfig
            {
                Enabled = true,
                SourceKey = GetSourceOrFallback(idx),
                DestXPct = Math.Min(60, 10 + offset),
                DestYPct = Math.Min(60, 10 + offset),
                DestWPct = 35,
                DestHPct = 35,
                BorderPx = 2
            });
            RebuildLayerUi();
            _onChanged?.Invoke();
        }

        private void RemoveBox(int idx)
        {
            if (_layers.Count <= 1 || idx < 0 || idx >= _layers.Count) return;
            _dragLayerIdx = -1;
            _layers.RemoveAt(idx);
            RebuildLayerUi();
            _onChanged?.Invoke();
        }

        private Rectangle MakeCropHandle(
            int idx, int mode, Brush fill,
            double w, double h,
            HorizontalAlignment hAlign, VerticalAlignment vAlign,
            Cursor cursor)
        {
            var r = new Rectangle
            {
                Fill = fill,
                Opacity = 0.85,
                HorizontalAlignment = hAlign,
                VerticalAlignment = vAlign,
                Cursor = cursor,
                Visibility = Visibility.Collapsed
            };
            if (!double.IsNaN(w)) r.Width = w;
            if (!double.IsNaN(h)) r.Height = h;

            r.MouseLeftButtonDown += (_, e) =>
            {
                StartDrag(idx, mode, e.GetPosition(_visualCanvas));
                r.CaptureMouse();
                e.Handled = true;
            };
            r.MouseMove += (_, e) => OnCanvasMouseMove(e.GetPosition(_visualCanvas));
            r.MouseLeftButtonUp += (_, _) =>
            {
                if (_dragLayerIdx == idx)
                {
                    _dragLayerIdx = -1;
                    r.ReleaseMouseCapture();
                }
            };
            return r;
        }

        private void UpdateHandleVisibility()
        {
            bool isCrop = _rbModeCrop?.IsChecked == true;
            for (int i = 0; i < _resizeHandles.Count; i++)
                _resizeHandles[i].Visibility = isCrop ? Visibility.Collapsed : Visibility.Visible;
            for (int i = 0; i < _cropHandles.Count; i++)
                foreach (var h in _cropHandles[i])
                    h.Visibility = isCrop ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < _canvasBoxes.Count; i++)
                _canvasBoxes[i].Cursor = isCrop ? Cursors.Arrow : Cursors.SizeAll;
        }

        private void StartDrag(int idx, int mode, Point pos)
        {
            if (idx < 0 || idx >= _layers.Count) return;
            _dragLayerIdx = idx;
            _dragMode = mode;
            _dragStartMouse = pos;
            _dragStartX = _layers[idx].DestXPct;
            _dragStartY = _layers[idx].DestYPct;
            _dragStartW = _layers[idx].DestWPct;
            _dragStartH = _layers[idx].DestHPct;
            _dragStartCropL = _layers[idx].CropLeftPct;
            _dragStartCropT = _layers[idx].CropTopPct;
            _dragStartCropR = _layers[idx].CropRightPct;
            _dragStartCropB = _layers[idx].CropBottomPct;
        }

        private void OnCanvasMouseMove(Point pos)
        {
            if (_dragLayerIdx < 0 || _dragLayerIdx >= _layers.Count) return;
            double dxPx = pos.X - _dragStartMouse.X;
            double dyPx = pos.Y - _dragStartMouse.Y;
            double dxPct = (dxPx / CanvasW) * 100.0;
            double dyPct = (dyPx / CanvasH) * 100.0;
            var l = _layers[_dragLayerIdx];

            switch (_dragMode)
            {
                case 0: // Move
                    if (_rbModeCrop?.IsChecked == true) return;
                    l.DestXPct = Math.Round(Math.Clamp(_dragStartX + dxPct, 0.0, 100.0 - l.DestWPct), 1);
                    l.DestYPct = Math.Round(Math.Clamp(_dragStartY + dyPct, 0.0, 100.0 - l.DestHPct), 1);
                    break;
                case 1: // Resize
                    l.DestWPct = Math.Round(Math.Clamp(_dragStartW + dxPct, 5.0, 100.0 - l.DestXPct), 1);
                    l.DestHPct = Math.Round(Math.Clamp(_dragStartH + dyPct, 5.0, 100.0 - l.DestYPct), 1);
                    break;
                case 2: // Crop Left
                {
                    double boxW = Math.Max(20.0, CanvasW * (l.DestWPct / 100.0));
                    l.CropLeftPct = Math.Round(Math.Clamp(_dragStartCropL + (dxPx / boxW) * 100.0, 0.0, 45.0), 1);
                    break;
                }
                case 3: // Crop Top
                {
                    double boxH = Math.Max(20.0, CanvasH * (l.DestHPct / 100.0));
                    l.CropTopPct = Math.Round(Math.Clamp(_dragStartCropT + (dyPx / boxH) * 100.0, 0.0, 45.0), 1);
                    break;
                }
                case 4: // Crop Right
                {
                    double boxW = Math.Max(20.0, CanvasW * (l.DestWPct / 100.0));
                    l.CropRightPct = Math.Round(Math.Clamp(_dragStartCropR - (dxPx / boxW) * 100.0, 0.0, 45.0), 1);
                    break;
                }
                case 5: // Crop Bottom
                {
                    double boxH = Math.Max(20.0, CanvasH * (l.DestHPct / 100.0));
                    l.CropBottomPct = Math.Round(Math.Clamp(_dragStartCropB - (dyPx / boxH) * 100.0, 0.0, 45.0), 1);
                    break;
                }
            }

            PopulateRows();
            UpdateVisualCanvas();
            _onChanged?.Invoke();
        }

        private void UpdateVisualCanvas()
        {
            for (int i = 0; i < _layers.Count && i < _canvasBoxes.Count; i++)
            {
                var l = _layers[i];
                var box = _canvasBoxes[i];
                if (!l.Enabled)
                {
                    box.Visibility = Visibility.Collapsed;
                    continue;
                }

                box.Visibility = Visibility.Visible;
                double left = CanvasW * (Math.Clamp(l.DestXPct, 0, 100) / 100.0);
                double top = CanvasH * (Math.Clamp(l.DestYPct, 0, 100) / 100.0);
                double w = Math.Max(14, CanvasW * (Math.Clamp(l.DestWPct, 1, 100) / 100.0));
                double h = Math.Max(14, CanvasH * (Math.Clamp(l.DestHPct, 1, 100) / 100.0));

                Canvas.SetLeft(box, left);
                Canvas.SetTop(box, top);
                box.Width = w;
                box.Height = h;

                string srcLabel = string.IsNullOrWhiteSpace(l.SourceKey) ? "(none)" : l.SourceKey;
                _canvasLabels[i].Text = $"Box {i + 1}: {srcLabel}";
            }
        }

        private void SwapLayers(int a, int b)
        {
            if (a < 0 || b < 0 || a >= _layers.Count || b >= _layers.Count) return;
            (_layers[a], _layers[b]) = (_layers[b], _layers[a]);
            RebuildLayerUi();
            _onChanged?.Invoke();
        }

        private void SelectBgItem(string hex)
        {
            foreach (ComboBoxItem item in _cmbBgColor.Items)
            {
                if (string.Equals(item.Tag?.ToString(), hex, StringComparison.OrdinalIgnoreCase))
                {
                    _cmbBgColor.SelectedItem = item;
                    return;
                }
            }
            _cmbBgColor.SelectedIndex = 0;
        }

        private void CmbBgColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_cmbBgColor.SelectedItem is not ComboBoxItem item) return;
            string tag = item.Tag?.ToString() ?? "#000000";
            if (tag == "CUSTOM")
            {
                using var dlg = new System.Windows.Forms.ColorDialog();
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    string hex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
                    _setBgHex(hex);
                    _onChanged?.Invoke();
                }
            }
            else
            {
                _setBgHex(tag);
                _onChanged?.Invoke();
            }
        }

        private Button MakeMiniButton(string text, string tip, Action onClick, bool enabled)
        {
            var b = new Button
            {
                Content = text,
                Width = 18,
                Height = 20,
                FontSize = 8,
                Padding = new Thickness(0),
                Margin = new Thickness(1, 0, 1, 0),
                IsEnabled = enabled,
                ToolTip = tip,
                Background = new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x46)),
                Foreground = Brushes.White
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        private Button MakePresetButton(string text, Action onClick)
        {
            var b = new Button
            {
                Content = text,
                Height = 26,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x46)),
                Foreground = Brushes.White,
                FontSize = 11
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        private TextBox MakeNumBox(Grid grid, int rowIdx, int colIdx)
        {
            var tb = new TextBox
            {
                Height = 24,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x30)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x55))
            };
            tb.TextChanged += (_, _) => SyncFromUi();
            Grid.SetRow(tb, rowIdx);
            Grid.SetColumn(tb, colIdx);
            grid.Children.Add(tb);
            return tb;
        }

        private void PopulateRows()
        {
            _isUpdatingUi = true;
            try
            {
                for (int i = 0; i < _layers.Count && i < _rows.Count; i++)
                {
                    var l = _layers[i];
                    var r = _rows[i];
                    r.ChkEnabled.IsChecked = l.Enabled;
                    if (!string.IsNullOrEmpty(l.SourceKey) && !r.CmbSource.Items.Contains(l.SourceKey))
                        r.CmbSource.Items.Add(l.SourceKey);
                    r.CmbSource.SelectedItem = l.SourceKey ?? "";
                    r.TxtX.Text = l.DestXPct.ToString("0.#", CultureInfo.InvariantCulture);
                    r.TxtY.Text = l.DestYPct.ToString("0.#", CultureInfo.InvariantCulture);
                    r.TxtW.Text = l.DestWPct.ToString("0.#", CultureInfo.InvariantCulture);
                    r.TxtH.Text = l.DestHPct.ToString("0.#", CultureInfo.InvariantCulture);
                    r.TxtCropL.Text = l.CropLeftPct.ToString("0.#", CultureInfo.InvariantCulture);
                    r.TxtCropT.Text = l.CropTopPct.ToString("0.#", CultureInfo.InvariantCulture);
                    r.TxtCropR.Text = l.CropRightPct.ToString("0.#", CultureInfo.InvariantCulture);
                    r.TxtCropB.Text = l.CropBottomPct.ToString("0.#", CultureInfo.InvariantCulture);
                    r.TxtBorder.Text = l.BorderPx.ToString(CultureInfo.InvariantCulture);
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private static double ParseDouble(string s, double fallback, double min, double max)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? Math.Clamp(v, min, max)
                : fallback;
        }

        private void SyncFromUi()
        {
            if (_isUpdatingUi) return;
            for (int i = 0; i < _layers.Count && i < _rows.Count; i++)
            {
                var l = _layers[i];
                var r = _rows[i];
                l.Enabled = r.ChkEnabled.IsChecked == true;
                l.SourceKey = r.CmbSource.SelectedItem?.ToString() ?? "";
                l.DestXPct = ParseDouble(r.TxtX.Text, l.DestXPct, 0, 100);
                l.DestYPct = ParseDouble(r.TxtY.Text, l.DestYPct, 0, 100);
                l.DestWPct = ParseDouble(r.TxtW.Text, l.DestWPct, 1, 100);
                l.DestHPct = ParseDouble(r.TxtH.Text, l.DestHPct, 1, 100);
                l.CropLeftPct = ParseDouble(r.TxtCropL.Text, l.CropLeftPct, 0, 45);
                l.CropTopPct = ParseDouble(r.TxtCropT.Text, l.CropTopPct, 0, 45);
                l.CropRightPct = ParseDouble(r.TxtCropR.Text, l.CropRightPct, 0, 45);
                l.CropBottomPct = ParseDouble(r.TxtCropB.Text, l.CropBottomPct, 0, 45);
                l.BorderPx = (int)ParseDouble(r.TxtBorder.Text, l.BorderPx, 0, 12);
            }
            UpdateVisualCanvas();
            _onChanged?.Invoke();
        }

        private string GetSourceOrFallback(int idx)
        {
            if (idx < _availableSources.Count) return _availableSources[idx];
            return _availableSources.Count > 0 ? _availableSources[0] : "";
        }

        private void EnsureLayerCount(int minCount)
        {
            bool changed = false;
            while (_layers.Count < minCount)
            {
                _layers.Add(new CompositeLayerConfig());
                changed = true;
            }
            for (int i = minCount; i < _layers.Count; i++)
                _layers[i].Enabled = false;
            if (changed) RebuildLayerUi();
        }

        private void ApplyPreset2Up()
        {
            EnsureLayerCount(2);
            SetBox(0, true, 0, 10, 50, 80, 2);
            SetBox(1, true, 50, 10, 50, 80, 2);
            PopulateRows();
            UpdateVisualCanvas();
            _onChanged?.Invoke();
        }

        private void ApplyPresetPip()
        {
            EnsureLayerCount(2);
            SetBox(0, true, 0, 0, 100, 100, 0);
            SetBox(1, true, 68, 66, 28, 28, 3);
            PopulateRows();
            UpdateVisualCanvas();
            _onChanged?.Invoke();
        }

        private void ApplyPreset3Box()
        {
            EnsureLayerCount(3);
            SetBox(0, true, 0, 0, 66, 100, 2);
            SetBox(1, true, 66, 0, 34, 50, 2);
            SetBox(2, true, 66, 50, 34, 50, 2);
            PopulateRows();
            UpdateVisualCanvas();
            _onChanged?.Invoke();
        }

        private void ApplyPreset4Quad()
        {
            EnsureLayerCount(4);
            SetBox(0, true, 0, 0, 50, 50, 2);
            SetBox(1, true, 50, 0, 50, 50, 2);
            SetBox(2, true, 0, 50, 50, 50, 2);
            SetBox(3, true, 50, 50, 50, 50, 2);
            PopulateRows();
            UpdateVisualCanvas();
            _onChanged?.Invoke();
        }

        private void SetBox(int idx, bool enabled, double x, double y, double w, double h, int border)
        {
            if (idx >= _layers.Count) return;
            var l = _layers[idx];
            l.Enabled = enabled;
            if (string.IsNullOrEmpty(l.SourceKey))
                l.SourceKey = GetSourceOrFallback(idx);
            l.DestXPct = x;
            l.DestYPct = y;
            l.DestWPct = w;
            l.DestHPct = h;
            l.BorderPx = border;
        }
    }
}
