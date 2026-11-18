using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DeckLinkAPI;

namespace DecklinkSwitcher
{
    public partial class MainWindow : Window
    {
        private int _activeSourceType = 0; // 0=Device, 1=ColorBars, 2=Matte, 3=Media, 4=Screen, 5=Webcam, 6=YouTube, 7=HTML, 8=NDI, 9=Multi-Box
        public static int ActiveSourceType = 0;
        public static string ActiveInputName = "";

        internal static byte[]? LatestCompositeUyvy = new byte[1920 * 1080 * 2];
        internal static byte[]? LatestExtraSourceUyvy = new byte[1920 * 1080 * 2];
        internal static byte[]? LatestMediaCompositeUyvy;
        internal static System.Collections.Generic.List<CompositeLayerConfig> CompositeLayers = CompositeSceneCompositor.CreateDefaultLayers();
        internal static string CompositeBackgroundHex = "#000000";
        private static volatile bool _isCompositeEditorOpen = false;
        private static string? _editingCompositeSceneId = null;
        private string? _activeCompositeSceneId = null;

        private static readonly object _extraSourcesLock = new object();
        private static readonly System.Collections.Generic.List<DynamicExtraSource> _extraSources = new();
        private static DynamicExtraSource? _activeExtraSource = null;

        private static DynamicExtraSource[] GetExtraSourcesSnapshot()
        {
            lock (_extraSourcesLock) return _extraSources.ToArray();
        }

        private sealed class DynamicExtraSource
        {
            public ExtraSourceConfig Config { get; }
            public System.Windows.Controls.Border TileBorder { get; set; } = null!;
            public System.Windows.Controls.Button CutButton { get; set; } = null!;
            public System.Windows.Media.Imaging.WriteableBitmap PreviewBitmap { get; set; } = null!;
            public byte[] Uyvy1080 { get; } = new byte[1920 * 1080 * 2];
            public byte[] Preview480 { get; } = new byte[480 * 270 * 4];
            public volatile bool HasFrame;
            public double AudioLevel { get; set; }
            public AudioState AudioState { get; set; } = AudioState.AFV;
            public HtmlBrowserSource? HtmlSource { get; set; }
            public YouTubeSource? YtSource { get; set; }
            public NdiSource? NdiSrc { get; set; }
            public VlcMediaSource? MediaSrc { get; set; }
            public System.Windows.Threading.DispatcherTimer? MediaSeekTimer { get; set; }
            public System.Windows.Controls.TextBlock? StatusText { get; set; }
            public System.Windows.Controls.TextBlock? TitleText { get; set; }
            public System.Windows.Controls.Border? ColorSwatch { get; set; }
            public System.Windows.Controls.Button? StartStopButton { get; set; }
            public bool IsRunning { get; set; }

            public DynamicExtraSource(ExtraSourceConfig config)
            {
                Config = config;
                AudioLevel = config.AudioLevel;
                AudioState = Enum.TryParse<AudioState>(config.AudioState, true, out var st) ? st : AudioState.AFV;
            }
        }

        internal sealed class DynamicCompositeScene
        {
            public CompositeSceneConfig Config { get; init; } = null!;
            public System.Windows.Media.Imaging.WriteableBitmap PreviewBmp { get; init; } = null!;
            public Border TileBorder { get; init; } = null!;
            public Button CutButton { get; init; } = null!;
        }

        private static readonly System.Collections.Generic.List<DynamicCompositeScene> _extraCompositeScenes = new();

        private static readonly byte[] _compositeWorkUyvy = new byte[1920 * 1080 * 2];
        private static readonly byte[] _compositePreview480 = new byte[480 * 270 * 4];
        private static readonly System.Collections.Generic.Dictionary<string, byte[]> _compositeScratchPerSource = new();
        private static readonly object _compositeLock = new();
        private static byte[]? _compositeColorBarsUyvy;

        internal static bool IsSourceUsedInComposite(string sourceKey)
        {
            if (string.IsNullOrWhiteSpace(sourceKey)) return false;
            string? altKey = null;
            foreach (var extra in GetExtraSourcesSnapshot())
            {
                if (string.Equals(extra.Config.Id, sourceKey, StringComparison.OrdinalIgnoreCase))
                {
                    altKey = extra.Config.Name;
                    break;
                }
            }
            bool match(string? k) =>
                string.Equals(k, sourceKey, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(altKey) && string.Equals(k, altKey, StringComparison.OrdinalIgnoreCase));

            lock (_compositeLock)
            {
                for (int i = 0; i < CompositeLayers.Count; i++)
                {
                    var l = CompositeLayers[i];
                    if (l.Enabled && match(l.SourceKey))
                        return true;
                }
                for (int s = 0; s < _extraCompositeScenes.Count; s++)
                {
                    var layers = _extraCompositeScenes[s].Config.Layers;
                    for (int i = 0; i < layers.Count; i++)
                    {
                        var l = layers[i];
                        if (l.Enabled && match(l.SourceKey))
                            return true;
                    }
                }
            }
            return false;
        }

        internal void SyncCompositeSourceFlags()
        {
            foreach (var input in DynamicInputs)
            {
                if (input.Device != null)
                    input.Device.IsCompositeInput = IsSourceUsedInComposite(input.DisplayName);
            }
        }

        private void ClearPgmPreview()
        {
            if (_bmpOutput == null) return;
            byte[] black = new byte[480 * 270 * 4];
            for (int i = 0; i < black.Length; i += 4)
            {
                black[i] = 0;
                black[i + 1] = 0;
                black[i + 2] = 0;
                black[i + 3] = 255;
            }
            WritePgmPreviewFrame(black);
            AudioOutputL.Value = 0;
            AudioOutputR.Value = 0;
        }



        private void UpdateProgramSourceFlags()
        {
            var defaultBrush = (System.Windows.Media.Brush)FindResource("SourceButtonBg");
            var defaultTextBrush = (System.Windows.Media.Brush)FindResource("SourceButtonText");
            string defaultBgHex = (defaultBrush as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "#333344";
            string defaultFgHex = (defaultTextBrush as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "#ffffff";

            foreach (var input in DynamicInputs)
            {
                bool isActive = _activeSourceType == 0 && input.Device == _activeInput && _activeInput != null;
                if (input.Device != null) input.Device.IsProgramSource = isActive;
                input.BgColor = isActive ? "#d32f2f" : defaultBgHex;
                input.FgColor = isActive ? "#ffffff" : defaultFgHex;
            }

            foreach (var scr in DynamicScreens)
            {
                bool isActive = _activeSourceType == 4 && _activeScreen == scr;
                scr.BgColor = isActive ? "#d32f2f" : defaultBgHex;
                scr.FgColor = isActive ? "#ffffff" : defaultFgHex;
            }
            foreach (var cam in DynamicWebcams)
            {
                bool isActive = _activeSourceType == 5 && _activeWebcam == cam;
                cam.BgColor = isActive ? "#d32f2f" : defaultBgHex;
                cam.FgColor = isActive ? "#ffffff" : defaultFgHex;
            }

            var activeBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xd3, 0x2f, 0x2f));
            var activeTextBrush = System.Windows.Media.Brushes.White;

            Action<System.Windows.Controls.Button?, bool> applySourceButtonStyle = (btn, isActive) =>
            {
                if (btn == null) return;
                btn.Background = isActive ? activeBrush : defaultBrush;
                btn.Foreground = isActive ? activeTextBrush : defaultTextBrush;
            };

            applySourceButtonStyle(BtnColorBars, _activeSourceType == 1);
            applySourceButtonStyle(BtnMatte, _activeSourceType == 2);
            applySourceButtonStyle(BtnMedia, _activeSourceType == 3);
            applySourceButtonStyle(BtnYouTube, _activeSourceType == 6);
            applySourceButtonStyle(BtnNdiSource, _activeSourceType == 8);
            applySourceButtonStyle(BtnHtmlSource, _activeSourceType == 7);
            applySourceButtonStyle(BtnComposite, _activeSourceType == 9 && _activeCompositeSceneId == null);
            foreach (var sc in _extraCompositeScenes)
            {
                applySourceButtonStyle(sc.CutButton, _activeSourceType == 9 && _activeCompositeSceneId == sc.Config.Id);
            }
            foreach (var extra in GetExtraSourcesSnapshot())
            {
                applySourceButtonStyle(extra.CutButton, _activeSourceType == 10 && _activeExtraSource == extra);
            }
            SyncCompositeSourceFlags();

            bool isSysAudioActive = (StateSystemAudio == AudioState.ON) || (StateSystemAudio == AudioState.AFV && _activeSourceType == 4);
            applySourceButtonStyle(BtnSystemAudio, isSysAudioActive);

            ApplyOutputButtonStyle(BtnRecord, LclRecorder?.IsRecording == true, "STOP RECORDING", "START RECORDING");
            ApplyOutputButtonStyle(BtnStream, YtStreamer?.IsStreaming == true, "STOP STREAM", "START STREAM");
            ApplyOutputButtonStyle(BtnNdi, NdiOut?.IsRunning == true, "STOP NDI", "START NDI");
            ApplyOutputButtonStyle(BtnSrt, SrtOut?.IsStreaming == true, "STOP SRT", "START SRT");

            UpdateHtmlSourceMuteState();
        }

        private void UpdateHtmlSourceMuteState()
        {
            if (_htmlSource != null)
            {
                bool shouldPlayAudio = (StateHtml == AudioState.ON) || (StateHtml == AudioState.AFV && _activeSourceType == 7);
                _htmlSource.SetMuted(!shouldPlayAudio);
            }
            foreach (var extra in GetExtraSourcesSnapshot())
            {
                if (extra.HtmlSource != null)
                {
                    bool play = (extra.AudioState == AudioState.ON) || (extra.AudioState == AudioState.AFV && _activeSourceType == 10 && _activeExtraSource == extra);
                    extra.HtmlSource.SetMuted(!play);
                }
            }
        }

        private void BtnColorBars_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSourceType != 1) BeginMixIfEnabled();
            _activeSourceType = 1; ActiveSourceType = 1; ActiveInputName = "Color Bars";
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();
            Log("Switched to Color Bars");
            UpdatePgmPreviewSynthetic();
            double val = 50 * ColorBarsAudioLevel * PgmAudioLevel;
            AudioOutputL.Value = val;
            AudioOutputR.Value = val;
        }
        private void PreviewColorBars_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnColorBars_Click(null, null);

        private void BtnMatte_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSourceType != 2) BeginMixIfEnabled();
            _activeSourceType = 2; ActiveSourceType = 2; ActiveInputName = "Matte";
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();
            Log("Switched to Matte");
            UpdatePgmPreviewSynthetic();
            AudioOutputL.Value = 0;
            AudioOutputR.Value = 0;
        }
        private void PreviewMatte_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnMatte_Click(null, null);

        private byte[]? GetCompositeScratchBuffer(string key)
        {
            if (!_compositeScratchPerSource.TryGetValue(key, out var buf) || buf.Length != 1920 * 1080 * 2)
            {
                buf = new byte[1920 * 1080 * 2];
                _compositeScratchPerSource[key] = buf;
            }
            return buf;
        }

        private byte[]? ResolveCompositeSourceUyvy1080(string sourceKey)
        {
            foreach (var input in DynamicInputs)
            {
                if (string.Equals(input.DisplayName, sourceKey, StringComparison.OrdinalIgnoreCase))
                    return input.Device?.GetLatestCompositeUyvy1080();
            }

            if (string.Equals(sourceKey, "YouTube", StringComparison.OrdinalIgnoreCase))
                return LatestYouTubeUyvy;
            if (string.Equals(sourceKey, "NDI", StringComparison.OrdinalIgnoreCase))
                return LatestNdiUyvy;
            if (string.Equals(sourceKey, "HTML", StringComparison.OrdinalIgnoreCase))
                return LatestHtmlSourceUyvy;
            if (string.Equals(sourceKey, "Color Bars", StringComparison.OrdinalIgnoreCase))
                return _compositeColorBarsUyvy ??= BuildSyntheticUyvy1080(1);

            if (string.Equals(sourceKey, "Media", StringComparison.OrdinalIgnoreCase))
            {
                return LatestMediaCompositeUyvy;
            }

            foreach (var scr in DynamicScreens)
            {
                if (string.Equals(scr.DisplayName, sourceKey, StringComparison.OrdinalIgnoreCase))
                {
                    var bgra = scr.Context?.Pgm1080Buffer;
                    if (bgra == null || bgra.Length != 1920 * 1080 * 4) return null;
                    var dst = GetCompositeScratchBuffer(scr.DisplayName)!;
                    ConvertBgra1080ToUyvy1080(bgra, dst);
                    return dst;
                }
            }

            foreach (var cam in DynamicWebcams)
            {
                if (string.Equals(cam.DisplayName, sourceKey, StringComparison.OrdinalIgnoreCase))
                {
                    var bgra = cam.Source?.GetLatestPgmPixels();
                    if (bgra == null || bgra.Length != 1920 * 1080 * 4) return null;
                    var dst = GetCompositeScratchBuffer(cam.DisplayName)!;
                    ConvertBgra1080ToUyvy1080(bgra, dst);
                    return dst;
                }
            }

            foreach (var extra in GetExtraSourcesSnapshot())
            {
                if (string.Equals(extra.Config.Name, sourceKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extra.Config.Id, sourceKey, StringComparison.OrdinalIgnoreCase))
                {
                    return extra.HasFrame ? extra.Uyvy1080 : null;
                }
            }

            return null;
        }

        private static readonly object _compositeRenderLock = new object();
        private readonly byte[] _compositeEditWorkUyvy = new byte[1920 * 1080 * 2];
        private readonly byte[] _compositeEditPreview480 = new byte[480 * 270 * 4];

        private void RefreshCompositeScene(
            System.Collections.Generic.List<CompositeLayerConfig> layers,
            string bgHex,
            System.Windows.Media.Imaging.WriteableBitmap? tileBmp,
            bool writeToPgmOutput,
            bool updatePgmPreview)
        {
            CompositeLayerConfig[] snapshot;
            lock (_compositeLock)
            {
                snapshot = layers.ToArray();
            }

            byte[] workUyvy = writeToPgmOutput ? _compositeWorkUyvy : _compositeEditWorkUyvy;
            byte[] prev480 = writeToPgmOutput ? _compositePreview480 : _compositeEditPreview480;
            byte[] previewCopy;

            lock (_compositeRenderLock)
            {
                CompositeSceneCompositor.CompositeFrame(snapshot, ResolveCompositeSourceUyvy1080, workUyvy, prev480, bgHex);

                if (writeToPgmOutput)
                {
                    lock (_pgmLock)
                    {
                        if (LatestCompositeUyvy == null || LatestCompositeUyvy.Length != workUyvy.Length)
                            LatestCompositeUyvy = new byte[workUyvy.Length];
                        Buffer.BlockCopy(workUyvy, 0, LatestCompositeUyvy, 0, workUyvy.Length);
                    }
                }

                previewCopy = (byte[])prev480.Clone();
            }

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                tileBmp?.WritePixels(new Int32Rect(0, 0, 480, 270), previewCopy, 480 * 4, 0);
                if (updatePgmPreview && _activeSourceType == 9)
                {
                    WritePgmPreviewFrame(previewCopy);
                }
            });
        }

        private void RefreshCompositeFrame(bool updatePgmPreview)
        {
            string? activeId = _activeCompositeSceneId;
            DynamicCompositeScene? activeExtra = null;
            if (!string.IsNullOrEmpty(activeId))
            {
                for (int i = 0; i < _extraCompositeScenes.Count; i++)
                {
                    if (_extraCompositeScenes[i].Config.Id == activeId)
                    {
                        activeExtra = _extraCompositeScenes[i];
                        break;
                    }
                }
            }

            if (activeExtra != null)
            {
                RefreshCompositeScene(activeExtra.Config.Layers, activeExtra.Config.BackgroundHex, activeExtra.PreviewBmp, true, updatePgmPreview);
            }
            else
            {
                RefreshCompositeScene(CompositeLayers, CompositeBackgroundHex, _bmpComposite, _activeSourceType == 9 || string.IsNullOrEmpty(activeId), updatePgmPreview && activeId == null);
            }

            // If editing a different scene in the editor, keep its preview live too
            if (_isCompositeEditorOpen && !string.IsNullOrEmpty(_editingCompositeSceneId) && _editingCompositeSceneId != activeId)
            {
                for (int i = 0; i < _extraCompositeScenes.Count; i++)
                {
                    var sc = _extraCompositeScenes[i];
                    if (sc.Config.Id == _editingCompositeSceneId)
                    {
                        RefreshCompositeScene(sc.Config.Layers, sc.Config.BackgroundHex, sc.PreviewBmp, false, false);
                        break;
                    }
                }
            }
            else if (_isCompositeEditorOpen && _editingCompositeSceneId == null && activeExtra != null)
            {
                RefreshCompositeScene(CompositeLayers, CompositeBackgroundHex, _bmpComposite, false, false);
            }

            // Refresh background thumbnails when not on Multi-Box PGM
            if (!updatePgmPreview)
            {
                for (int i = 0; i < _extraCompositeScenes.Count; i++)
                {
                    var sc = _extraCompositeScenes[i];
                    if (sc != activeExtra)
                        RefreshCompositeScene(sc.Config.Layers, sc.Config.BackgroundHex, sc.PreviewBmp, false, false);
                }
            }
        }

        private void BtnComposite_Click(object? sender, RoutedEventArgs? e)
        {
            if (_activeSourceType != 9 || _activeCompositeSceneId != null) BeginMixIfEnabled();
            _activeCompositeSceneId = null;
            _activeSourceType = 9; ActiveSourceType = 9; ActiveInputName = "Multi-Box";
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();
            Log("Switched to Multi-Box");
            RefreshCompositeFrame(true);
        }
        private void PreviewComposite_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnComposite_Click(null, null);

        private System.Collections.Generic.List<string> BuildCompositeSourceList()
        {
            var sources = new System.Collections.Generic.List<string>();
            foreach (var input in DynamicInputs) sources.Add(input.DisplayName);
            foreach (var scr in DynamicScreens) sources.Add(scr.DisplayName);
            foreach (var cam in DynamicWebcams) sources.Add(cam.DisplayName);
            sources.Add("Media");
            sources.Add("YouTube");
            sources.Add("HTML");
            sources.Add("NDI");
            sources.Add("Color Bars");
            foreach (var extra in GetExtraSourcesSnapshot())
            {
                sources.Add(extra.Config.Name);
            }
            return sources;
        }

        private void OpenCompositeEditor(
            string? sceneId,
            string title,
            System.Collections.Generic.List<CompositeLayerConfig> layers,
            System.Windows.Media.Imaging.WriteableBitmap previewBmp,
            Func<string> getBgHex,
            Action<string> setBgHex)
        {
            var dlg = new CompositeEditorWindow(
                layers,
                BuildCompositeSourceList(),
                previewBmp,
                getBgHex,
                setBgHex,
                () =>
                {
                    SyncCompositeSourceFlags();
                    if (sceneId == _activeCompositeSceneId)
                        RefreshCompositeFrame(_activeSourceType == 9);
                    else
                        RefreshCompositeScene(layers, getBgHex(), previewBmp, false, false);
                    SaveCurrentSettings();
                })
            {
                Owner = this,
                Title = title
            };
            _editingCompositeSceneId = sceneId;
            _isCompositeEditorOpen = true;
            try
            {
                dlg.ShowDialog();
            }
            finally
            {
                _isCompositeEditorOpen = false;
                _editingCompositeSceneId = null;
            }
        }

        private void BtnEditComposite_Click(object sender, RoutedEventArgs e)
        {
            OpenCompositeEditor(
                null,
                "Multi-Box / Composite Layout Editor",
                CompositeLayers,
                _bmpComposite,
                () => CompositeBackgroundHex,
                hex => CompositeBackgroundHex = hex);
        }

        private void BtnAddCompositeScene_Click(object sender, RoutedEventArgs e)
        {
            int num = _extraCompositeScenes.Count + 2;
            var cfg = new CompositeSceneConfig
            {
                Id = "S" + DateTime.UtcNow.Ticks,
                Name = $"Multi-Box {num}",
                BackgroundHex = "#000000",
                Layers = CompositeSceneCompositor.CreateDefaultLayers()
            };
            var scene = AddDynamicCompositeScene(cfg);
            SyncCompositeSourceFlags();
            RefreshCompositeFrame(false);
            SaveCurrentSettings();
            OpenCompositeEditor(
                scene.Config.Id,
                $"{scene.Config.Name} - Layout Editor",
                scene.Config.Layers,
                scene.PreviewBmp,
                () => scene.Config.BackgroundHex,
                hex => scene.Config.BackgroundHex = hex);
        }

        internal DynamicCompositeScene AddDynamicCompositeScene(CompositeSceneConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.Id)) cfg.Id = "S" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (cfg.Layers == null || cfg.Layers.Count == 0) cfg.Layers = CompositeSceneCompositor.CreateDefaultLayers();

            var bmp = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

            var border = new Border
            {
                Name = "TileComposite_" + cfg.Id,
                Style = (Style)FindResource("MixerPanel")
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };

            var imgBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            imgBorder.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
            imgBorder.SetResourceReference(Border.BackgroundProperty, "ImageBg");
            var img = new System.Windows.Controls.Image
            {
                Source = bmp,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Width = 178,
                Height = 100
            };
            imgBorder.Child = img;
            stack.Children.Add(imgBorder);

            var cutBtn = new Button
            {
                Content = cfg.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Height = 36,
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            cutBtn.SetResourceReference(Button.BackgroundProperty, "ButtonBg");
            cutBtn.SetResourceReference(Button.ForegroundProperty, "TextPrimary");
            stack.Children.Add(cutBtn);

            var bottomGrid = new Grid();
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

            var editBtn = new Button
            {
                Content = "Edit Layout...",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Height = 26,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"Configure layout for {cfg.Name}"
            };
            editBtn.SetResourceReference(Button.BackgroundProperty, "ButtonAltBg");
            editBtn.SetResourceReference(Button.ForegroundProperty, "TextPrimary");
            Grid.SetColumn(editBtn, 0);
            bottomGrid.Children.Add(editBtn);

            var renBtn = new Button
            {
                Content = "✎",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Height = 26,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"Rename {cfg.Name}"
            };
            renBtn.SetResourceReference(Button.BackgroundProperty, "ButtonAltBg");
            renBtn.SetResourceReference(Button.ForegroundProperty, "TextPrimary");
            Grid.SetColumn(renBtn, 2);
            bottomGrid.Children.Add(renBtn);

            var delBtn = new Button
            {
                Content = "✕",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Height = 26,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8e, 0x24, 0x24)),
                Foreground = System.Windows.Media.Brushes.White,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"Remove {cfg.Name} from source panel"
            };
            Grid.SetColumn(delBtn, 4);
            bottomGrid.Children.Add(delBtn);

            stack.Children.Add(bottomGrid);
            border.Child = stack;

            var scene = new DynamicCompositeScene
            {
                Config = cfg,
                PreviewBmp = bmp,
                TileBorder = border,
                CutButton = cutBtn
            };

            Action activateScene = () =>
            {
                if (_activeSourceType != 9 || _activeCompositeSceneId != scene.Config.Id) BeginMixIfEnabled();
                _activeCompositeSceneId = scene.Config.Id;
                _activeSourceType = 9; ActiveSourceType = 9; ActiveInputName = scene.Config.Name;
                _activeScreen = null; _activeWebcam = null; _activeInput = null;
                UpdateProgramSourceFlags();
                Log($"Switched to {scene.Config.Name}");
                RefreshCompositeFrame(true);
            };

            cutBtn.Click += (_, _) => activateScene();
            imgBorder.MouseLeftButtonDown += (_, _) => activateScene();
            renBtn.Click += (_, _) =>
            {
                string? newName = PromptRenameDialog("Rename Multi-Box Scene", scene.Config.Name);
                if (!string.IsNullOrWhiteSpace(newName) && newName != scene.Config.Name)
                {
                    scene.Config.Name = newName;
                    cutBtn.Content = newName;
                    editBtn.ToolTip = $"Configure layout for {newName}";
                    renBtn.ToolTip = $"Rename {newName}";
                    delBtn.ToolTip = $"Remove {newName} from source panel";
                    if (_activeSourceType == 9 && _activeCompositeSceneId == scene.Config.Id)
                        ActiveInputName = newName;
                    SaveCurrentSettings();
                }
            };
            editBtn.Click += (_, _) => OpenCompositeEditor(
                scene.Config.Id,
                $"{scene.Config.Name} - Layout Editor",
                scene.Config.Layers,
                scene.PreviewBmp,
                () => scene.Config.BackgroundHex,
                hex => scene.Config.BackgroundHex = hex);
            delBtn.Click += (_, _) => RemoveDynamicCompositeScene(scene);

            _extraCompositeScenes.Add(scene);

            int insertIdx = SwitcherPanel.Children.IndexOf(TileComposite);
            if (insertIdx >= 0)
            {
                int offset = 1;
                while (insertIdx + offset < SwitcherPanel.Children.Count &&
                       SwitcherPanel.Children[insertIdx + offset] is FrameworkElement fe &&
                       fe.Name.StartsWith("TileComposite_", StringComparison.Ordinal))
                {
                    offset++;
                }
                SwitcherPanel.Children.Insert(insertIdx + offset, border);
            }
            else
            {
                SwitcherPanel.Children.Add(border);
            }

            return scene;
        }

        private void RemoveDynamicCompositeScene(DynamicCompositeScene scene)
        {
            if (_activeSourceType == 9 && _activeCompositeSceneId == scene.Config.Id)
            {
                BtnComposite_Click(null, null);
            }
            _extraCompositeScenes.Remove(scene);
            SwitcherPanel.Children.Remove(scene.TileBorder);
            SyncCompositeSourceFlags();
            SaveCurrentSettings();
        }

        private void BtnAddSourceMenu_Click(object sender, RoutedEventArgs e)
        {
            if (BtnAddSourceMenu?.ContextMenu != null)
            {
                BtnAddSourceMenu.ContextMenu.PlacementTarget = BtnAddSourceMenu;
                BtnAddSourceMenu.ContextMenu.IsOpen = true;
            }
        }

        private string NextExtraSourceName(string baseLabel)
        {
            var existing = GetExtraSourcesSnapshot();
            int n = 2;
            while (existing.Any(x => string.Equals(x.Config.Name, $"{baseLabel} {n}", StringComparison.OrdinalIgnoreCase)))
                n++;
            return $"{baseLabel} {n}";
        }

        private void MenuAddMatte_Click(object sender, RoutedEventArgs e)
        {
            var cfg = new ExtraSourceConfig
            {
                Id = "extra_matte_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = "Matte",
                Name = NextExtraSourceName("Matte"),
                ColorHex = "#0055AA"
            };
            AddDynamicExtraSource(cfg);
            SaveCurrentSettings();
        }

        private void MenuAddHtml_Click(object sender, RoutedEventArgs e)
        {
            var cfg = new ExtraSourceConfig
            {
                Id = "extra_html_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = "Html",
                Name = NextExtraSourceName("HTML"),
                Url = "https://www.google.com",
                Fps = 60,
                AudioState = "AFV"
            };
            AddDynamicExtraSource(cfg);
            SaveCurrentSettings();
        }

        private void MenuAddYouTube_Click(object sender, RoutedEventArgs e)
        {
            var cfg = new ExtraSourceConfig
            {
                Id = "extra_yt_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = "YouTube",
                Name = NextExtraSourceName("YouTube"),
                Url = "",
                AudioLevel = 1.0,
                AudioState = "AFV"
            };
            AddDynamicExtraSource(cfg);
            SaveCurrentSettings();
        }

        private void MenuAddNdi_Click(object sender, RoutedEventArgs e)
        {
            var cfg = new ExtraSourceConfig
            {
                Id = "extra_ndi_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = "Ndi",
                Name = NextExtraSourceName("NDI"),
                Url = "",
                AudioLevel = 1.0,
                AudioState = "AFV"
            };
            AddDynamicExtraSource(cfg);
            SaveCurrentSettings();
        }

        private void MenuAddMedia_Click(object sender, RoutedEventArgs e)
        {
            var cfg = new ExtraSourceConfig
            {
                Id = "extra_media_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = "Media",
                Name = NextExtraSourceName("Media"),
                Url = "",
                AudioLevel = 1.0,
                AudioState = "AFV"
            };
            AddDynamicExtraSource(cfg);
            SaveCurrentSettings();
        }

        private void ActivateExtraSource(DynamicExtraSource extra)
        {
            if (_activeSourceType != 10 || _activeExtraSource != extra) BeginMixIfEnabled();
            _activeSourceType = 10;
            ActiveSourceType = 10;
            ActiveInputName = extra.Config.Id;
            _activeExtraSource = extra;
            _activeScreen = null;
            _activeWebcam = null;
            _activeInput = null;
            _activeCompositeSceneId = null;

            if (extra.HtmlSource != null)
            {
                var pgmBgra = extra.HtmlSource.GetLatestPgmPixels();
                if (pgmBgra != null && pgmBgra.Length == 1920 * 1080 * 4)
                {
                    ConvertBgra1080ToUyvy1080(pgmBgra, extra.Uyvy1080);
                    extra.HasFrame = true;
                }
            }

            lock (_pgmLock)
            {
                if (LatestExtraSourceUyvy == null || LatestExtraSourceUyvy.Length != 1920 * 1080 * 2)
                    LatestExtraSourceUyvy = new byte[1920 * 1080 * 2];
                Buffer.BlockCopy(extra.Uyvy1080, 0, LatestExtraSourceUyvy, 0, extra.Uyvy1080.Length);
            }
            WritePgmPreviewFrame(extra.Preview480);
            PushFullscreenPreviewFrame(LatestExtraSourceUyvy);
            UpdateProgramSourceFlags();
            Log($"Switched to {extra.Config.Name}");
        }

        private void UpdateExtraMatteBuffers(DynamicExtraSource extra)
        {
            CompositeSceneCompositor.FillColorUyvy1080(extra.Uyvy1080, extra.Config.ColorHex);
            extra.HasFrame = true;

            byte r = 0, g = 85, b = 170;
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(extra.Config.ColorHex);
                r = c.R; g = c.G; b = c.B;
            }
            catch { }

            var buf = extra.Preview480;
            for (int i = 0; i < buf.Length; i += 4)
            {
                buf[i] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = 255;
            }
            extra.PreviewBitmap?.WritePixels(new Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
            if (extra.ColorSwatch != null)
            {
                extra.ColorSwatch.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            }
            if (_activeSourceType == 10 && _activeExtraSource == extra)
            {
                lock (_pgmLock)
                {
                    if (LatestExtraSourceUyvy == null || LatestExtraSourceUyvy.Length != 1920 * 1080 * 2)
                        LatestExtraSourceUyvy = new byte[1920 * 1080 * 2];
                    Buffer.BlockCopy(extra.Uyvy1080, 0, LatestExtraSourceUyvy, 0, extra.Uyvy1080.Length);
                }
                WritePgmPreviewFrame(buf);
            }
        }

        private string? PromptRenameDialog(string title, string currentName)
        {
            var dlg = new Window
            {
                Title = title,
                Width = 340,
                Height = 145,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 28, 38)),
                Foreground = System.Windows.Media.Brushes.White
            };
            var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Enter new name:",
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 210, 230)),
                Margin = new Thickness(0, 0, 0, 6)
            });
            var tb = new System.Windows.Controls.TextBox
            {
                Text = currentName,
                Height = 28,
                FontSize = 12.5,
                Padding = new Thickness(6, 3, 6, 3),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 20, 28)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(65, 85, 120)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            tb.SelectAll();
            sp.Children.Add(tb);

            var btnPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnCancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 72, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var btnOk = new System.Windows.Controls.Button
            {
                Content = "Save",
                Width = 72,
                Height = 26,
                IsDefault = true,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 125, 50)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0)
            };
            btnOk.Click += (_, __) => dlg.DialogResult = true;
            btnPanel.Children.Add(btnCancel);
            btnPanel.Children.Add(btnOk);
            sp.Children.Add(btnPanel);
            dlg.Content = sp;
            tb.Focus();

            return dlg.ShowDialog() == true ? tb.Text.Trim() : null;
        }

        private void RenameCompositeSourceKey(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return;
            lock (_compositeLock)
            {
                foreach (var l in CompositeLayers)
                {
                    if (string.Equals(l.SourceKey, oldName, StringComparison.OrdinalIgnoreCase))
                        l.SourceKey = newName;
                }
                foreach (var sc in _extraCompositeScenes)
                {
                    foreach (var l in sc.Config.Layers)
                    {
                        if (string.Equals(l.SourceKey, oldName, StringComparison.OrdinalIgnoreCase))
                            l.SourceKey = newName;
                    }
                }
            }
        }

        private DynamicExtraSource AddDynamicExtraSource(ExtraSourceConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.Id))
                cfg.Id = "extra_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (string.IsNullOrWhiteSpace(cfg.Name))
                cfg.Name = NextExtraSourceName(cfg.Kind);

            var extra = new DynamicExtraSource(cfg);
            lock (_extraSourcesLock) _extraSources.Add(extra);

            var bmp = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            extra.PreviewBitmap = bmp;

            var tile = new System.Windows.Controls.Border
            {
                Name = "TileExtra_" + cfg.Id,
                Width = 232,
                Margin = new Thickness(5),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 34, 48)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 55, 76)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8)
            };

            var stack = new System.Windows.Controls.StackPanel();

            // Header row: Name + Rename (✎) + Remove (✕)
            var headerGrid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 6) };
            headerGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            string icon = cfg.Kind == "Matte" ? "🎨 " : cfg.Kind == "Html" ? "🌐 " : cfg.Kind == "Ndi" ? "📡 " : cfg.Kind == "Media" ? "🎬 " : "▶ ";
            var titleTxt = new System.Windows.Controls.TextBlock
            {
                Text = icon + cfg.Name,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 232, 255)),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = "Double-click to rename"
            };
            extra.TitleText = titleTxt;
            Action renameExtra = () =>
            {
                string? newName = PromptRenameDialog("Rename Source", cfg.Name);
                if (!string.IsNullOrWhiteSpace(newName) && newName != cfg.Name)
                {
                    string oldName = cfg.Name;
                    cfg.Name = newName;
                    titleTxt.Text = icon + newName;
                    extra.CutButton.Content = newName.ToUpperInvariant();
                    RenameCompositeSourceKey(oldName, newName);
                    SyncCompositeSourceFlags();
                    SaveCurrentSettings();
                }
            };
            titleTxt.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) renameExtra(); };
            System.Windows.Controls.Grid.SetColumn(titleTxt, 0);
            headerGrid.Children.Add(titleTxt);

            var btnRename = new System.Windows.Controls.Button
            {
                Content = "✎",
                Width = 20,
                Height = 20,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 170, 190)),
                BorderThickness = new Thickness(0),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                ToolTip = "Rename Source"
            };
            btnRename.Click += (_, __) => renameExtra();
            System.Windows.Controls.Grid.SetColumn(btnRename, 1);
            headerGrid.Children.Add(btnRename);

            var btnRemove = new System.Windows.Controls.Button
            {
                Content = "✕",
                Width = 20,
                Height = 20,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 170, 190)),
                BorderThickness = new Thickness(0),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                ToolTip = "Remove Source"
            };
            btnRemove.Click += (_, __) => RemoveDynamicExtraSource(extra);
            System.Windows.Controls.Grid.SetColumn(btnRemove, 2);
            headerGrid.Children.Add(btnRemove);
            stack.Children.Add(headerGrid);

            // Preview image
            var previewBorder = new System.Windows.Controls.Border
            {
                Height = 118,
                Background = System.Windows.Media.Brushes.Black,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            var img = new System.Windows.Controls.Image
            {
                Source = bmp,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            previewBorder.Child = img;
            previewBorder.MouseDown += (_, __) => ActivateExtraSource(extra);
            stack.Children.Add(previewBorder);

            // Kind-specific controls
            if (cfg.Kind == "Matte")
            {
                var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var swatch = new System.Windows.Controls.Border
                {
                    Width = 26,
                    Height = 24,
                    CornerRadius = new CornerRadius(4),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 95, 120)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Pick Color"
                };
                extra.ColorSwatch = swatch;
                swatch.MouseLeftButtonDown += (_, __) =>
                {
                    using var cd = new System.Windows.Forms.ColorDialog();
                    try
                    {
                        var wc = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(cfg.ColorHex);
                        cd.Color = System.Drawing.Color.FromArgb(wc.R, wc.G, wc.B);
                    }
                    catch { }
                    if (cd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        cfg.ColorHex = $"#{cd.Color.R:X2}{cd.Color.G:X2}{cd.Color.B:X2}";
                        UpdateExtraMatteBuffers(extra);
                        SaveCurrentSettings();
                    }
                };
                System.Windows.Controls.Grid.SetColumn(swatch, 0);
                row.Children.Add(swatch);

                var btnPick = new System.Windows.Controls.Button
                {
                    Content = "Pick Color",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 46, 66)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 77, 112)),
                    BorderThickness = new Thickness(1),
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false
                };
                btnPick.Click += (_, __) =>
                {
                    using var cd = new System.Windows.Forms.ColorDialog();
                    try
                    {
                        var wc = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(cfg.ColorHex);
                        cd.Color = System.Drawing.Color.FromArgb(wc.R, wc.G, wc.B);
                    }
                    catch { }
                    if (cd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        cfg.ColorHex = $"#{cd.Color.R:X2}{cd.Color.G:X2}{cd.Color.B:X2}";
                        UpdateExtraMatteBuffers(extra);
                        SaveCurrentSettings();
                    }
                };
                System.Windows.Controls.Grid.SetColumn(btnPick, 1);
                row.Children.Add(btnPick);
                stack.Children.Add(row);

                UpdateExtraMatteBuffers(extra);
            }
            else if (cfg.Kind == "Html")
            {
                var htmlSrc = new HtmlBrowserSource(false, cfg.Id) { TargetFps = 60 };
                extra.HtmlSource = htmlSrc;

                htmlSrc.OnPreviewBufferUpdated += bgra480 =>
                {
                    Buffer.BlockCopy(bgra480, 0, extra.Preview480, 0, Math.Min(bgra480.Length, extra.Preview480.Length));
                    Dispatcher.BeginInvoke(() =>
                    {
                        extra.PreviewBitmap.WritePixels(new Int32Rect(0, 0, 480, 270), bgra480, 480 * 4, 0);
                        if (_activeSourceType == 10 && _activeExtraSource == extra)
                            WritePgmPreviewFrame(bgra480);
                    });
                };
                htmlSrc.OnUyvy1080Updated += uyvy1080 =>
                {
                    Buffer.BlockCopy(uyvy1080, 0, extra.Uyvy1080, 0, Math.Min(uyvy1080.Length, extra.Uyvy1080.Length));
                    extra.HasFrame = true;
                    if (_activeSourceType == 10 && _activeExtraSource == extra)
                    {
                        lock (_pgmLock)
                        {
                            if (LatestExtraSourceUyvy == null || LatestExtraSourceUyvy.Length != 1920 * 1080 * 2)
                                LatestExtraSourceUyvy = new byte[1920 * 1080 * 2];
                            Buffer.BlockCopy(uyvy1080, 0, LatestExtraSourceUyvy, 0, Math.Min(uyvy1080.Length, LatestExtraSourceUyvy.Length));
                        }
                    }
                };

                var urlBox = new System.Windows.Controls.TextBox
                {
                    Text = cfg.Url ?? "",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 22, 32)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 77, 112)),
                    FontSize = 10.5,
                    Padding = new Thickness(4, 2, 4, 2),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                urlBox.LostFocus += (_, __) => { cfg.Url = urlBox.Text.Trim(); SaveCurrentSettings(); };
                stack.Children.Add(urlBox);

                var ctrlRow = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
                ctrlRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ctrlRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

                var btnStart = new System.Windows.Controls.Button
                {
                    Content = "Load",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 46, 66)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 77, 112)),
                    BorderThickness = new Thickness(1),
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                extra.StartStopButton = btnStart;

                var cmbMute = new System.Windows.Controls.ComboBox
                {
                    Width = 58,
                    Height = 24,
                    FontSize = 10
                };
                cmbMute.Items.Add("AFV");
                cmbMute.Items.Add("ON");
                cmbMute.Items.Add("OFF");
                cmbMute.SelectedIndex = (int)extra.AudioState;
                cmbMute.SelectionChanged += (_, __) =>
                {
                    if (cmbMute.SelectedIndex >= 0)
                    {
                        extra.AudioState = (AudioState)cmbMute.SelectedIndex;
                        cfg.AudioState = extra.AudioState.ToString();
                        UpdateHtmlSourceMuteState();
                        SaveCurrentSettings();
                    }
                };

                btnStart.Click += async (_, __) =>
                {
                    if (!extra.IsRunning)
                    {
                        cfg.Url = urlBox.Text.Trim();
                        if (string.IsNullOrWhiteSpace(cfg.Url)) return;
                        extra.IsRunning = true;
                        btnStart.Content = "Stop";
                        SaveCurrentSettings();
                        await htmlSrc.StartAsync(cfg.Url);
                        UpdateHtmlSourceMuteState();
                    }
                    else
                    {
                        extra.IsRunning = false;
                        btnStart.Content = "Load";
                        htmlSrc.Stop();
                    }
                };

                System.Windows.Controls.Grid.SetColumn(btnStart, 0);
                System.Windows.Controls.Grid.SetColumn(cmbMute, 1);
                ctrlRow.Children.Add(btnStart);
                ctrlRow.Children.Add(cmbMute);
                stack.Children.Add(ctrlRow);

                var statusTxt = new System.Windows.Controls.TextBlock
                {
                    Text = "Idle",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(145, 160, 185)),
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 0, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                extra.StatusText = statusTxt;
                htmlSrc.OnStatusChanged += (st, _, __) => Dispatcher.BeginInvoke(() => statusTxt.Text = st);
                stack.Children.Add(statusTxt);
            }
            else if (cfg.Kind == "YouTube")
            {
                var ytSrc = new YouTubeSource
                {
                    AudioChannelName = cfg.Id
                };
                extra.YtSource = ytSrc;

                var barL = new System.Windows.Controls.ProgressBar
                {
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    Minimum = 0,
                    Maximum = 100,
                    Width = 5,
                    Height = 86,
                    Margin = new Thickness(0, 0, 4, 0),
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 230, 118)),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 22, 32))
                };
                var barR = new System.Windows.Controls.ProgressBar
                {
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    Minimum = 0,
                    Maximum = 100,
                    Width = 5,
                    Height = 86,
                    Margin = new Thickness(4, 0, 4, 0),
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 230, 118)),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 22, 32))
                };
                var sldVol = new System.Windows.Controls.Slider
                {
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    Height = 86,
                    Minimum = 0,
                    Maximum = 1.5,
                    Value = extra.AudioLevel > 0 ? extra.AudioLevel : 1.0,
                    SmallChange = 0.05,
                    LargeChange = 0.1,
                    ToolTip = "Audio Gain"
                };
                extra.AudioLevel = sldVol.Value;
                sldVol.ValueChanged += (_, __) =>
                {
                    extra.AudioLevel = sldVol.Value;
                    cfg.AudioLevel = sldVol.Value;
                    SaveCurrentSettings();
                };

                var overlayIcon = new System.Windows.Controls.TextBlock
                {
                    Text = "▶",
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 51, 51)),
                    Margin = new Thickness(0, 0, 0, 2)
                };
                var overlayStatus = new System.Windows.Controls.TextBlock
                {
                    Text = "OFFLINE",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 170, 190)),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var statusOverlay = new System.Windows.Controls.StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    IsHitTestVisible = false
                };
                statusOverlay.Children.Add(overlayIcon);
                statusOverlay.Children.Add(overlayStatus);

                stack.Children.Remove(previewBorder);
                previewBorder.Child = null;
                previewBorder.Height = 86;
                previewBorder.Width = 152;
                previewBorder.Margin = new Thickness(0);
                var previewGrid = new System.Windows.Controls.Grid();
                previewGrid.Children.Add(img);
                previewGrid.Children.Add(statusOverlay);
                previewBorder.Child = previewGrid;

                var meterRow = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                meterRow.Children.Add(barL);
                meterRow.Children.Add(previewBorder);
                meterRow.Children.Add(barR);
                meterRow.Children.Add(sldVol);
                stack.Children.Add(meterRow);

                ytSrc.OnAudioLevelArrived += (vl, vr) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        barL.Value = Math.Min(100, vl * extra.AudioLevel);
                        barR.Value = Math.Min(100, vr * extra.AudioLevel);
                    });
                };

                ytSrc.OnPreviewBufferUpdated += bgra480 =>
                {
                    Buffer.BlockCopy(bgra480, 0, extra.Preview480, 0, Math.Min(bgra480.Length, extra.Preview480.Length));
                    Dispatcher.BeginInvoke(() =>
                    {
                        statusOverlay.Visibility = Visibility.Collapsed;
                        extra.PreviewBitmap.WritePixels(new Int32Rect(0, 0, 480, 270), bgra480, 480 * 4, 0);
                        if (_activeSourceType == 10 && _activeExtraSource == extra)
                            WritePgmPreviewFrame(bgra480);
                    });
                };
                ytSrc.OnUyvy1080Frame += (ptr, len) =>
                {
                    int copyLen = Math.Min(len, extra.Uyvy1080.Length);
                    System.Runtime.InteropServices.Marshal.Copy(ptr, extra.Uyvy1080, 0, copyLen);
                    extra.HasFrame = true;
                    if (_activeSourceType == 10 && _activeExtraSource == extra)
                    {
                        lock (_pgmLock)
                        {
                            if (LatestExtraSourceUyvy == null || LatestExtraSourceUyvy.Length != 1920 * 1080 * 2)
                                LatestExtraSourceUyvy = new byte[1920 * 1080 * 2];
                            System.Runtime.InteropServices.Marshal.Copy(ptr, LatestExtraSourceUyvy, 0, copyLen);
                        }
                    }
                };

                var urlBox = new System.Windows.Controls.TextBox
                {
                    Text = cfg.Url ?? "",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 22, 32)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 77, 112)),
                    FontSize = 10.5,
                    Padding = new Thickness(4, 2, 4, 2),
                    Margin = new Thickness(0, 0, 0, 4),
                    ToolTip = "YouTube Live URL (e.g. https://www.youtube.com/watch?v=...)"
                };
                urlBox.LostFocus += (_, __) => { cfg.Url = urlBox.Text.Trim(); SaveCurrentSettings(); };
                stack.Children.Add(urlBox);

                var ctrlRow = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
                ctrlRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ctrlRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(4) });
                ctrlRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(60) });

                var btnConnect = new System.Windows.Controls.Button
                {
                    Content = "Connect",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 125, 50)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false
                };
                var btnStop = new System.Windows.Controls.Button
                {
                    Content = "Stop",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(198, 40, 40)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false,
                    IsEnabled = false
                };
                extra.StartStopButton = btnConnect;
                System.Windows.Controls.Grid.SetColumn(btnConnect, 0);
                System.Windows.Controls.Grid.SetColumn(btnStop, 2);
                ctrlRow.Children.Add(btnConnect);
                ctrlRow.Children.Add(btnStop);
                stack.Children.Add(ctrlRow);

                var cmbAudio = new System.Windows.Controls.ComboBox
                {
                    Height = 24,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                cmbAudio.Items.Add("AFV");
                cmbAudio.Items.Add("ON");
                cmbAudio.Items.Add("OFF");
                cmbAudio.SelectedIndex = (int)extra.AudioState;
                cmbAudio.SelectionChanged += (_, __) =>
                {
                    if (cmbAudio.SelectedIndex >= 0)
                    {
                        extra.AudioState = (AudioState)cmbAudio.SelectedIndex;
                        cfg.AudioState = extra.AudioState.ToString();
                        SaveCurrentSettings();
                    }
                };
                stack.Children.Add(cmbAudio);

                var statusTxt = new System.Windows.Controls.TextBlock
                {
                    Text = "OFFLINE",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(145, 160, 185)),
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 0, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                extra.StatusText = statusTxt;
                ytSrc.OnStatusChanged += (st, isLive, isErr) => Dispatcher.BeginInvoke(() =>
                {
                    statusTxt.Text = st;
                    overlayStatus.Text = st;
                    if (isLive)
                    {
                        statusOverlay.Visibility = Visibility.Collapsed;
                        btnConnect.IsEnabled = false;
                        btnStop.IsEnabled = true;
                    }
                    else if (!extra.IsRunning)
                    {
                        statusOverlay.Visibility = Visibility.Visible;
                        btnConnect.IsEnabled = true;
                        btnStop.IsEnabled = false;
                    }
                    else if (isErr)
                    {
                        extra.IsRunning = false;
                        statusOverlay.Visibility = Visibility.Visible;
                        btnConnect.IsEnabled = true;
                        btnStop.IsEnabled = false;
                    }
                });
                stack.Children.Add(statusTxt);

                async Task ConnectYouTubeAsync()
                {
                    cfg.Url = urlBox.Text.Trim();
                    if (string.IsNullOrWhiteSpace(cfg.Url)) return;
                    extra.IsRunning = true;
                    btnConnect.IsEnabled = false;
                    btnStop.IsEnabled = true;
                    overlayStatus.Text = "Connecting...";
                    statusTxt.Text = "Connecting...";
                    SaveCurrentSettings();
                    await ytSrc.StartAsync(cfg.Url);
                }

                btnConnect.Click += async (_, __) => await ConnectYouTubeAsync();
                btnStop.Click += (_, __) =>
                {
                    extra.IsRunning = false;
                    btnConnect.IsEnabled = true;
                    btnStop.IsEnabled = false;
                    barL.Value = 0;
                    barR.Value = 0;
                    statusOverlay.Visibility = Visibility.Visible;
                    overlayStatus.Text = "OFFLINE";
                    ytSrc.Stop();
                };

                // Auto-connect on startup if a URL was saved
                if (!string.IsNullOrWhiteSpace(cfg.Url))
                {
                    Dispatcher.BeginInvoke(async () => await ConnectYouTubeAsync());
                }
            }
            else if (cfg.Kind == "Ndi")
            {
                var ndiSrc = new NdiSource
                {
                    AudioChannelName = cfg.Id
                };
                extra.NdiSrc = ndiSrc;

                ndiSrc.OnPreviewBufferUpdated += bgra480 =>
                {
                    Buffer.BlockCopy(bgra480, 0, extra.Preview480, 0, Math.Min(bgra480.Length, extra.Preview480.Length));
                    Dispatcher.BeginInvoke(() =>
                    {
                        extra.PreviewBitmap.WritePixels(new Int32Rect(0, 0, 480, 270), bgra480, 480 * 4, 0);
                        if (_activeSourceType == 10 && _activeExtraSource == extra)
                            WritePgmPreviewFrame(bgra480);
                    });
                };
                ndiSrc.OnUyvy1080Updated += uyvy1080 =>
                {
                    Buffer.BlockCopy(uyvy1080, 0, extra.Uyvy1080, 0, Math.Min(uyvy1080.Length, extra.Uyvy1080.Length));
                    extra.HasFrame = true;
                    if (_activeSourceType == 10 && _activeExtraSource == extra)
                    {
                        lock (_pgmLock)
                        {
                            if (LatestExtraSourceUyvy == null || LatestExtraSourceUyvy.Length != 1920 * 1080 * 2)
                                LatestExtraSourceUyvy = new byte[1920 * 1080 * 2];
                            Buffer.BlockCopy(uyvy1080, 0, LatestExtraSourceUyvy, 0, Math.Min(uyvy1080.Length, LatestExtraSourceUyvy.Length));
                        }
                    }
                };

                var srcRow = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
                srcRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                srcRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

                var cmbSource = new System.Windows.Controls.ComboBox
                {
                    IsEditable = true,
                    Height = 24,
                    FontSize = 10.5,
                    Text = cfg.Url ?? "",
                    Margin = new Thickness(0, 0, 4, 0)
                };
                if (!string.IsNullOrWhiteSpace(cfg.Url))
                {
                    cmbSource.Items.Add(cfg.Url);
                }
                cmbSource.LostFocus += (_, __) => { cfg.Url = cmbSource.Text.Trim(); SaveCurrentSettings(); };

                var btnScan = new System.Windows.Controls.Button
                {
                    Content = "🔍",
                    Width = 26,
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 46, 66)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 77, 112)),
                    BorderThickness = new Thickness(1),
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false,
                    ToolTip = "Scan NDI Sources"
                };

                System.Windows.Controls.Grid.SetColumn(cmbSource, 0);
                System.Windows.Controls.Grid.SetColumn(btnScan, 1);
                srcRow.Children.Add(cmbSource);
                srcRow.Children.Add(btnScan);
                stack.Children.Add(srcRow);

                var ctrlRow = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
                ctrlRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ctrlRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

                var btnStart = new System.Windows.Controls.Button
                {
                    Content = "Connect",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 46, 66)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 77, 112)),
                    BorderThickness = new Thickness(1),
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                extra.StartStopButton = btnStart;

                var cmbAudio = new System.Windows.Controls.ComboBox
                {
                    Width = 58,
                    Height = 24,
                    FontSize = 10
                };
                cmbAudio.Items.Add("AFV");
                cmbAudio.Items.Add("ON");
                cmbAudio.Items.Add("OFF");
                cmbAudio.SelectedIndex = (int)extra.AudioState;
                cmbAudio.SelectionChanged += (_, __) =>
                {
                    if (cmbAudio.SelectedIndex >= 0)
                    {
                        extra.AudioState = (AudioState)cmbAudio.SelectedIndex;
                        cfg.AudioState = extra.AudioState.ToString();
                        SaveCurrentSettings();
                    }
                };

                var statusTxt = new System.Windows.Controls.TextBlock
                {
                    Text = "Idle",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(145, 160, 185)),
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 0, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                extra.StatusText = statusTxt;
                ndiSrc.OnStatusChanged += (st, _, __) => Dispatcher.BeginInvoke(() => statusTxt.Text = st);

                btnScan.Click += async (_, __) =>
                {
                    btnScan.IsEnabled = false;
                    statusTxt.Text = "Scanning NDI...";
                    try
                    {
                        var found = await Task.Run(() => NdiSource.FindSources(2000, msg => Log(msg)));
                        string prevText = cmbSource.Text;
                        cmbSource.Items.Clear();
                        foreach (var item in found) cmbSource.Items.Add(item.Name);
                        if (!string.IsNullOrWhiteSpace(prevText)) cmbSource.Text = prevText;
                        else if (found.Count > 0) cmbSource.SelectedIndex = 0;
                        statusTxt.Text = found.Count > 0 ? $"Found {found.Count} source(s)" : "No NDI sources";
                    }
                    finally
                    {
                        btnScan.IsEnabled = true;
                    }
                };

                btnStart.Click += (_, __) =>
                {
                    if (!extra.IsRunning)
                    {
                        cfg.Url = cmbSource.Text.Trim();
                        if (string.IsNullOrWhiteSpace(cfg.Url)) return;
                        extra.IsRunning = true;
                        btnStart.Content = "Stop";
                        SaveCurrentSettings();
                        ndiSrc.Start(new NdiSourceInfo { Name = cfg.Url });
                    }
                    else
                    {
                        extra.IsRunning = false;
                        btnStart.Content = "Connect";
                        ndiSrc.Stop();
                    }
                };

                System.Windows.Controls.Grid.SetColumn(btnStart, 0);
                System.Windows.Controls.Grid.SetColumn(cmbAudio, 1);
                ctrlRow.Children.Add(btnStart);
                ctrlRow.Children.Add(cmbAudio);
                stack.Children.Add(ctrlRow);
                stack.Children.Add(statusTxt);
            }
            else if (cfg.Kind == "Media")
            {
                var mediaSrc = new VlcMediaSource();
                extra.MediaSrc = mediaSrc;

                // Audio L/R meters + gain slider row (matches original Media tile)
                var barL = new System.Windows.Controls.ProgressBar
                {
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    Minimum = 0,
                    Maximum = 100,
                    Width = 5,
                    Height = 86,
                    Margin = new Thickness(0, 0, 4, 0),
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 230, 118)),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 22, 32))
                };
                var barR = new System.Windows.Controls.ProgressBar
                {
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    Minimum = 0,
                    Maximum = 100,
                    Width = 5,
                    Height = 86,
                    Margin = new Thickness(4, 0, 4, 0),
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 230, 118)),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 22, 32))
                };
                var sldVol = new System.Windows.Controls.Slider
                {
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    Height = 86,
                    Minimum = 0,
                    Maximum = 1.5,
                    Value = extra.AudioLevel > 0 ? extra.AudioLevel : 1.0,
                    SmallChange = 0.05,
                    LargeChange = 0.1,
                    ToolTip = "Audio Gain"
                };
                extra.AudioLevel = sldVol.Value;
                sldVol.ValueChanged += (_, __) =>
                {
                    extra.AudioLevel = sldVol.Value;
                    cfg.AudioLevel = sldVol.Value;
                    SaveCurrentSettings();
                };

                // Replace the simple previewBorder with meter + preview + meter + vol slider
                stack.Children.Remove(previewBorder);
                previewBorder.Height = 86;
                previewBorder.Width = 152;
                previewBorder.Margin = new Thickness(0);
                var meterRow = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                meterRow.Children.Add(barL);
                meterRow.Children.Add(previewBorder);
                meterRow.Children.Add(barR);
                meterRow.Children.Add(sldVol);
                stack.Children.Add(meterRow);

                mediaSrc.OnAudioLevelArrived += (vl, vr) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        barL.Value = Math.Min(100, vl * extra.AudioLevel);
                        barR.Value = Math.Min(100, vr * extra.AudioLevel);
                    });
                };

                mediaSrc.OnPreviewBufferUpdated += bgra480 =>
                {
                    Buffer.BlockCopy(bgra480, 0, extra.Preview480, 0, Math.Min(bgra480.Length, extra.Preview480.Length));
                    Dispatcher.BeginInvoke(() =>
                    {
                        extra.PreviewBitmap.WritePixels(new Int32Rect(0, 0, 480, 270), bgra480, 480 * 4, 0);
                        if (_activeSourceType == 10 && _activeExtraSource == extra)
                            WritePgmPreviewFrame(bgra480);
                    });
                };
                mediaSrc.OnVideoAndAudioArrived += (vBuf, w, h, rb, aBuf, aCount) =>
                {
                    if (vBuf != IntPtr.Zero && w == 1920 && h == 1080)
                    {
                        int len = Math.Min(1920 * 1080 * 2, extra.Uyvy1080.Length);
                        System.Runtime.InteropServices.Marshal.Copy(vBuf, extra.Uyvy1080, 0, len);
                        extra.HasFrame = true;
                        if (_activeSourceType == 10 && _activeExtraSource == extra)
                        {
                            lock (_pgmLock)
                            {
                                if (LatestExtraSourceUyvy == null || LatestExtraSourceUyvy.Length != 1920 * 1080 * 2)
                                    LatestExtraSourceUyvy = new byte[1920 * 1080 * 2];
                                System.Runtime.InteropServices.Marshal.Copy(vBuf, LatestExtraSourceUyvy, 0, len);
                            }
                        }
                    }
                    if (aBuf != IntPtr.Zero && aCount > 0)
                    {
                        short[] pkt = new short[aCount * 2];
                        System.Runtime.InteropServices.Marshal.Copy(aBuf, pkt, 0, pkt.Length);
                        PushAudioPacket(cfg.Id, pkt);
                    }
                };

                var statusTxt = new System.Windows.Controls.TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(cfg.Url) ? "Idle" : System.IO.Path.GetFileName(cfg.Url),
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(145, 160, 185)),
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 0, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                extra.StatusText = statusTxt;

                var chkLoop = new System.Windows.Controls.CheckBox
                {
                    Content = "Loop",
                    IsChecked = true,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 210, 230)),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };

                // Row 1: Select File + Stop
                var row1 = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
                row1.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row1.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(4) });
                row1.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(60) });

                var btnSelectFile = new System.Windows.Controls.Button
                {
                    Content = "Select File",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 46, 66)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 77, 112)),
                    BorderThickness = new Thickness(1),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false
                };
                var btnStop = new System.Windows.Controls.Button
                {
                    Content = "Stop",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(198, 40, 40)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false
                };
                System.Windows.Controls.Grid.SetColumn(btnSelectFile, 0);
                System.Windows.Controls.Grid.SetColumn(btnStop, 2);
                row1.Children.Add(btnSelectFile);
                row1.Children.Add(btnStop);
                stack.Children.Add(row1);

                // Row 2: Cue + Pause + Resume
                var row2 = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
                row2.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row2.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(4) });
                row2.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row2.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(4) });
                row2.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var btnCue = new System.Windows.Controls.Button
                {
                    Content = "Cue",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 46, 66)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 77, 112)),
                    BorderThickness = new Thickness(1),
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false
                };
                var btnPause = new System.Windows.Controls.Button
                {
                    Content = "Pause",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 46, 66)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 77, 112)),
                    BorderThickness = new Thickness(1),
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false
                };
                var btnResume = new System.Windows.Controls.Button
                {
                    Content = "Resume",
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 125, 50)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false
                };
                System.Windows.Controls.Grid.SetColumn(btnCue, 0);
                System.Windows.Controls.Grid.SetColumn(btnPause, 2);
                System.Windows.Controls.Grid.SetColumn(btnResume, 4);
                row2.Children.Add(btnCue);
                row2.Children.Add(btnPause);
                row2.Children.Add(btnResume);
                stack.Children.Add(row2);

                // Row 3: Loop + Seek Slider
                bool isDraggingSeek = false;
                bool isUpdatingSeek = false;
                var sldSeek = new System.Windows.Controls.Slider
                {
                    Minimum = 0,
                    Maximum = 1,
                    SmallChange = 0.01,
                    LargeChange = 0.1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sldSeek.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
                    new System.Windows.Controls.Primitives.DragStartedEventHandler((_, __) => isDraggingSeek = true));
                sldSeek.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
                    new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, __) =>
                    {
                        isDraggingSeek = false;
                        mediaSrc.Position = (float)sldSeek.Value;
                    }));
                sldSeek.ValueChanged += (_, __) =>
                {
                    if (!isUpdatingSeek && isDraggingSeek)
                        mediaSrc.Position = (float)sldSeek.Value;
                };

                var seekRow = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
                seekRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                seekRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                System.Windows.Controls.Grid.SetColumn(chkLoop, 0);
                System.Windows.Controls.Grid.SetColumn(sldSeek, 1);
                seekRow.Children.Add(chkLoop);
                seekRow.Children.Add(sldSeek);
                stack.Children.Add(seekRow);

                var seekTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                seekTimer.Tick += (_, __) =>
                {
                    if (!isDraggingSeek && extra.IsRunning)
                    {
                        isUpdatingSeek = true;
                        sldSeek.Value = Math.Clamp(mediaSrc.Position, 0f, 1f);
                        isUpdatingSeek = false;
                    }
                };
                extra.MediaSeekTimer = seekTimer;
                seekTimer.Start();

                // Row 4: Audio State ComboBox (AFV / ON / OFF)
                var cmbAudio = new System.Windows.Controls.ComboBox
                {
                    Height = 24,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                cmbAudio.Items.Add("AFV");
                cmbAudio.Items.Add("ON");
                cmbAudio.Items.Add("OFF");
                cmbAudio.SelectedIndex = (int)extra.AudioState;
                cmbAudio.SelectionChanged += (_, __) =>
                {
                    if (cmbAudio.SelectedIndex >= 0)
                    {
                        extra.AudioState = (AudioState)cmbAudio.SelectedIndex;
                        cfg.AudioState = extra.AudioState.ToString();
                        SaveCurrentSettings();
                    }
                };
                stack.Children.Add(cmbAudio);
                stack.Children.Add(statusTxt);

                btnSelectFile.Click += (_, __) =>
                {
                    var ofd = new Microsoft.Win32.OpenFileDialog
                    {
                        Filter = "Media Files|*.mp4;*.mkv;*.mov;*.avi;*.ts;*.mxf;*.webm;*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*"
                    };
                    if (ofd.ShowDialog() == true)
                    {
                        cfg.Url = ofd.FileName;
                        extra.IsRunning = true;
                        statusTxt.Text = "Playing: " + System.IO.Path.GetFileName(cfg.Url);
                        SaveCurrentSettings();
                        mediaSrc.Play(cfg.Url, chkLoop.IsChecked == true);
                    }
                };
                btnCue.Click += (_, __) =>
                {
                    if (string.IsNullOrWhiteSpace(cfg.Url) || !System.IO.File.Exists(cfg.Url))
                    {
                        statusTxt.Text = "Select a file first";
                        return;
                    }
                    extra.IsRunning = true;
                    statusTxt.Text = "Cued: " + System.IO.Path.GetFileName(cfg.Url);
                    mediaSrc.Cue(cfg.Url, chkLoop.IsChecked == true);
                };
                btnPause.Click += (_, __) =>
                {
                    mediaSrc.Pause();
                    statusTxt.Text = "Paused";
                };
                btnResume.Click += (_, __) =>
                {
                    if (!extra.IsRunning && !string.IsNullOrWhiteSpace(cfg.Url) && System.IO.File.Exists(cfg.Url))
                    {
                        extra.IsRunning = true;
                        mediaSrc.Play(cfg.Url, chkLoop.IsChecked == true);
                    }
                    else
                    {
                        mediaSrc.Resume();
                    }
                    statusTxt.Text = string.IsNullOrWhiteSpace(cfg.Url) ? "Playing" : "Playing: " + System.IO.Path.GetFileName(cfg.Url);
                };
                btnStop.Click += (_, __) =>
                {
                    extra.IsRunning = false;
                    mediaSrc.Stop();
                    barL.Value = 0;
                    barR.Value = 0;
                    sldSeek.Value = 0;
                    statusTxt.Text = "Stopped";
                };
            }

            // Cut / Switch button
            var btnCut = new System.Windows.Controls.Button
            {
                Content = cfg.Name.ToUpperInvariant(),
                Height = 28,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 47, 61)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 224, 232)),
                BorderThickness = new Thickness(1)
            };
            btnCut.Click += (_, __) => ActivateExtraSource(extra);
            extra.CutButton = btnCut;
            stack.Children.Add(btnCut);

            tile.Child = stack;
            extra.TileBorder = tile;
            SwitcherPanel.Children.Add(tile);
            UpdateProgramSourceFlags();
            return extra;
        }

        private void RemoveDynamicExtraSource(DynamicExtraSource extra)
        {
            if (_activeSourceType == 10 && _activeExtraSource == extra)
            {
                _activeExtraSource = null;
                BtnColorBars_Click(null, null);
            }
            lock (_extraSourcesLock) _extraSources.Remove(extra);
            SwitcherPanel.Children.Remove(extra.TileBorder);
            try { extra.MediaSeekTimer?.Stop(); } catch { }
            try { extra.HtmlSource?.Dispose(); } catch { }
            try { extra.YtSource?.Dispose(); } catch { }
            try { extra.NdiSrc?.Dispose(); } catch { }
            try { extra.MediaSrc?.Dispose(); } catch { }
            SyncCompositeSourceFlags();
            SaveCurrentSettings();
        }

        private void DisposeExtraSources()
        {
            foreach (var extra in GetExtraSourcesSnapshot())
            {
                try { extra.MediaSeekTimer?.Stop(); } catch { }
                try { extra.HtmlSource?.Dispose(); } catch { }
                try { extra.YtSource?.Dispose(); } catch { }
                try { extra.NdiSrc?.Dispose(); } catch { }
                try { extra.MediaSrc?.Dispose(); } catch { }
            }
        }

        private void BtnMedia_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSourceType != 3) BeginMixIfEnabled();
            _activeSourceType = 3; ActiveSourceType = 3; ActiveInputName = "Media"; Log("Switched to Local Video");
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();
        }
        private void PreviewMedia_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnMedia_Click(null, null);

        private void BtnYouTube_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSourceType != 6) BeginMixIfEnabled();
            _activeSourceType = 6; ActiveSourceType = 6; ActiveInputName = "YouTube"; Log("Switched to YouTube Live");
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();

            if (_youtubeSource != null)
            {
                var preview = _youtubeSource.GetLatestPreviewPixels();
                if (preview != null && _bmpOutput != null)
                {
                    WritePgmPreviewFrame(preview);
                }
                var pgm = _youtubeSource.GetLatestPgmPixels();
                if (pgm != null)
                {
                    lock (_pgmLock)
                    {
                        if (_latestPgmFrame == null || _latestPgmFrame.Length != 1920 * 1080 * 4)
                            _latestPgmFrame = new byte[1920 * 1080 * 4];
                        Array.Copy(pgm, _latestPgmFrame, _latestPgmFrame.Length);
                    }
                }
            }
        }
        private void PreviewYouTube_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnYouTube_Click(null, null);

        // ---------------- NDI Source ----------------
        private void SetupNdiSource()
        {
            _ndiSource.OnLog = Log;
            _ndiSource.OnPreviewBufferUpdated = (buf) => {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    _bmpNdi.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                    if (_activeSourceType == 8)
                    {
                        WritePgmPreviewFrame(buf);
                    }
                });
            };
            _ndiSource.OnAudioLevelArrived = (l, r) => {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    double nL = l * _ndiSource.AudioLevel;
                    double nR = r * _ndiSource.AudioLevel;
                    AudioBarNdiL.Value = nL; AudioBarNdiR.Value = nR;
                    if (_activeSourceType == 8)
                    {
                        AudioOutputL.Value = nL * PgmAudioLevel;
                        AudioOutputR.Value = nR * PgmAudioLevel;
                    }
                });
            };
            _ndiSource.OnStatusChanged = (status, isLive, isError) => {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    TxtNdiStatus.Text = status;
                    if (isError)
                    {
                        TxtNdiStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                        TxtNdiStatusIcon.Foreground = System.Windows.Media.Brushes.IndianRed;
                        PnlNdiStatusOverlay.Visibility = Visibility.Visible;
                        BtnConnectNdi.IsEnabled = true;
                        BtnDisconnectNdi.IsEnabled = false;
                    }
                    else if (isLive)
                    {
                        TxtNdiStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
                        TxtNdiStatusIcon.Foreground = System.Windows.Media.Brushes.LimeGreen;
                        PnlNdiStatusOverlay.Visibility = Visibility.Collapsed;
                        BtnConnectNdi.IsEnabled = false;
                        BtnDisconnectNdi.IsEnabled = true;
                    }
                    else
                    {
                        TxtNdiStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
                        TxtNdiStatusIcon.Foreground = System.Windows.Media.Brushes.MediumOrchid;
                        PnlNdiStatusOverlay.Visibility = Visibility.Visible;
                        // "Connecting..." / "No signal" keep Stop available; OFFLINE re-enables Connect
                        bool offline = status == "OFFLINE";
                        BtnConnectNdi.IsEnabled = offline;
                        BtnDisconnectNdi.IsEnabled = !offline;
                    }
                });
            };
        }

        private void BtnNdiSource_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSourceType != 8) BeginMixIfEnabled();
            _activeSourceType = 8; ActiveSourceType = 8; ActiveInputName = "NDI"; Log("Switched to NDI Source");
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();
            _ = AutoConnectNdiAsync();
        }

        // Scans for sources if needed and connects to the selected / last used one.
        private async Task AutoConnectNdiAsync()
        {
            try
            {
                if (_ndiSource.IsStreaming) return;
                if (CmbNdiSources.Items.Count == 0) await RefreshNdiSourcesAsync();
                if (!_ndiSource.IsStreaming && CmbNdiSources.SelectedItem is NdiSourceInfo)
                    BtnConnectNdi_Click(null, null);
            }
            catch (Exception ex) { Log("NDI auto-connect error: " + ex.Message); }
        }
        private void PreviewNdi_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnNdiSource_Click(null, null);

        private async void BtnRefreshNdi_Click(object sender, RoutedEventArgs e) => await RefreshNdiSourcesAsync();

        private async Task RefreshNdiSourcesAsync()
        {
            string? previous = (CmbNdiSources.SelectedItem as NdiSourceInfo)?.Name ?? _pendingNdiSourceName;
            BtnRefreshNdi.IsEnabled = false;
            TxtNdiStatus.Text = "Searching...";
            try
            {
                var sources = await Task.Run(() => NdiSource.FindSources(2000, Log));
                // Don't list our own NDI program output (would create a feedback loop)
                if (NdiOut.IsRunning)
                    sources.RemoveAll(s => s.Name.Contains(NdiOut.StreamName, StringComparison.OrdinalIgnoreCase));

                CmbNdiSources.ItemsSource = sources;
                CmbNdiSources.DisplayMemberPath = "Name";
                var match = sources.FirstOrDefault(s => s.Name == previous);
                CmbNdiSources.SelectedItem = match ?? (sources.Count > 0 ? sources[0] : null);
                Log($"NDI: found {sources.Count} source(s)");
                if (!_ndiSource.IsStreaming) TxtNdiStatus.Text = sources.Count > 0 ? "OFFLINE" : "No sources";
            }
            catch (Exception ex)
            {
                Log("NDI refresh error: " + ex.Message);
            }
            finally
            {
                BtnRefreshNdi.IsEnabled = true;
            }
        }

        private string? _pendingNdiSourceName;

        private void BtnConnectNdi_Click(object sender, RoutedEventArgs e)
        {
            if (CmbNdiSources.SelectedItem is not NdiSourceInfo info)
            {
                TxtNdiStatus.Text = "Select a source";
                return;
            }
            _pendingNdiSourceName = info.Name;
            BtnConnectNdi.IsEnabled = false;
            BtnDisconnectNdi.IsEnabled = true;
            _ndiSource.Start(info);
            SaveCurrentSettings();
        }

        private void BtnDisconnectNdi_Click(object sender, RoutedEventArgs e)
        {
            _ndiSource.Stop();
            BtnConnectNdi.IsEnabled = true;
            BtnDisconnectNdi.IsEnabled = false;
        }

        private void SldNdiAudio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_ndiSource != null) _ndiSource.AudioLevel = (float)e.NewValue;
            NdiAudioLevel = (float)e.NewValue;
        }

        private async void BtnConnectYouTube_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtYouTubeUrl.Text.Trim();
            SaveCurrentSettings();
            BtnConnectYouTube.IsEnabled = false;
            BtnDisconnectYouTube.IsEnabled = true;
            await _youtubeSource.StartAsync(url);
        }

        private void BtnDisconnectYouTube_Click(object sender, RoutedEventArgs e)
        {
            _youtubeSource.Stop();
            BtnConnectYouTube.IsEnabled = true;
            BtnDisconnectYouTube.IsEnabled = false;
        }

        private void SldYouTubeAudio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_youtubeSource != null) _youtubeSource.AudioLevel = (float)e.NewValue;
            YouTubeAudioLevel = (float)e.NewValue;
        }

        private void BtnHtmlSource_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSourceType != 7) BeginMixIfEnabled();
            _activeSourceType = 7; ActiveSourceType = 7; ActiveInputName = "HTML"; Log("Switched to HTML / Web Source");
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();

            if (_htmlSource != null)
            {
                var preview = _htmlSource.GetLatestPreviewPixels();
                if (preview != null && _bmpOutput != null)
                {
                    WritePgmPreviewFrame(preview);
                }
                var pgm = _htmlSource.GetLatestPgmPixels();
                if (pgm != null)
                {
                    lock (_pgmLock)
                    {
                        if (_latestPgmFrame == null || _latestPgmFrame.Length != 1920 * 1080 * 4)
                            _latestPgmFrame = new byte[1920 * 1080 * 4];
                        Array.Copy(pgm, _latestPgmFrame, _latestPgmFrame.Length);
                    }
                }
            }
        }
        private void PreviewHtmlSource_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnHtmlSource_Click(null, null);

        private void BtnBrowseHtmlSource_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Local HTML File",
                Filter = "HTML Files (*.html;*.htm)|*.html;*.htm|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtHtmlSourceUrl.Text = dlg.FileName;
                SaveCurrentSettings();
            }
        }

        private void TxtHtmlSourceUrl_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                BtnConnectHtmlSource_Click(sender, e);
            }
        }

        private async void BtnConnectHtmlSource_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtHtmlSourceUrl.Text.Trim();
            SaveCurrentSettings();
            BtnDisconnectHtmlSource.IsEnabled = true;
            await _htmlSource.StartAsync(url);
            UpdateHtmlSourceMuteState();
        }

        private void BtnReloadHtmlSource_Click(object sender, RoutedEventArgs e)
        {
            _htmlSource?.Reload();
        }

        private void BtnDisconnectHtmlSource_Click(object sender, RoutedEventArgs e)
        {
            _htmlSource?.Stop();
            BtnConnectHtmlSource.IsEnabled = true;
            BtnDisconnectHtmlSource.IsEnabled = false;
        }

        public void SwitchToScreen(ScreenInputViewModel vm)
        {
            if (_activeSourceType != 4 || _activeScreen != vm) BeginMixIfEnabled();
            _activeSourceType = 4;
            ActiveSourceType = 4;
            _activeScreen = vm;
            _activeWebcam = null;
            _activeInput = null;
            ActiveInputName = vm.DisplayName;
            Log($"Switched to {vm.DisplayName}");
            UpdateProgramSourceFlags();

            if (vm.Context != null && vm.Context.Prev480Buffer != null && _bmpOutput != null)
            {
                WritePgmPreviewFrame(vm.Context.Prev480Buffer);
            }
            if (vm.Context != null && vm.Context.Pgm1080Buffer != null)
            {
                lock (_pgmLock)
                {
                    if (_latestPgmFrame == null || _latestPgmFrame.Length != 1920 * 1080 * 4)
                        _latestPgmFrame = new byte[1920 * 1080 * 4];
                    Array.Copy(vm.Context.Pgm1080Buffer, _latestPgmFrame, _latestPgmFrame.Length);
                }
            }
        }

        public void SwitchToWebcam(WebcamInputViewModel vm)
        {
            if (_activeSourceType != 5 || _activeWebcam != vm) BeginMixIfEnabled();
            _activeSourceType = 5;
            ActiveSourceType = 5;
            _activeWebcam = vm;
            _activeScreen = null;
            _activeInput = null;
            ActiveInputName = vm.DisplayName;
            Log($"Switched to {vm.DisplayName}");
            UpdateProgramSourceFlags();

            if (vm.Source != null)
            {
                var preview = vm.Source.GetLatestPreviewPixels();
                if (preview != null && _bmpOutput != null)
                {
                    WritePgmPreviewFrame(preview);
                }
                var pgm = vm.Source.GetLatestPgmPixels();
                if (pgm != null)
                {
                    lock (_pgmLock)
                    {
                        if (_latestPgmFrame == null || _latestPgmFrame.Length != 1920 * 1080 * 4)
                            _latestPgmFrame = new byte[1920 * 1080 * 4];
                        Array.Copy(pgm, _latestPgmFrame, _latestPgmFrame.Length);
                    }
                }
            }
        }

        private string? FindWebcamAudioDevice(string webcamName, System.Collections.Generic.List<MicDeviceInfo>? availableMics = null)
        {
            try
            {
                var mics = availableMics ?? LocalAudioInput.GetDevices();
                MicDeviceInfo? bestMatch = mics.FirstOrDefault(d => 
                    d.DeviceNumber >= 0 && 
                    !string.IsNullOrEmpty(d.Name) && 
                    (d.Name.IndexOf(webcamName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     webcamName.IndexOf(d.Name, StringComparison.OrdinalIgnoreCase) >= 0));

                if (bestMatch == null)
                {
                    string[] tokens = webcamName.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var token in tokens)
                    {
                        if (token.Length >= 3 && !token.Equals("camera", StringComparison.OrdinalIgnoreCase))
                        {
                            bestMatch = mics.FirstOrDefault(d => d.DeviceNumber >= 0 && d.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (bestMatch != null) break;
                        }
                    }
                }

                if (bestMatch == null)
                {
                    bestMatch = mics.FirstOrDefault(d => d.DeviceNumber >= 0 && d.Name.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (bestMatch != null)
                {
                    Log($"[Webcam] Linked audio device {bestMatch.Name} to {webcamName}");
                    return bestMatch.DeviceId;
                }
                else
                {
                    Log($"[Webcam] No matching microphone found for {webcamName}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log($"[Webcam] Audio link error: {ex.Message}");
                return null;
            }
        }

        private string _lastMediaFile = "";

        private void BtnSelectMedia_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Video and Image Files|*.mp4;*.mkv;*.avi;*.mov;*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*";
            if (dlg.ShowDialog() == true)
            {
                if (_mediaSource != null)
                {
                    _lastMediaFile = dlg.FileName;
                    _mediaSource.Play(dlg.FileName, ChkLoopMedia.IsChecked == true);
                    SaveCurrentSettings();
                }
                else
                {
                    System.Windows.MessageBox.Show("Media source is not initialized. Please check logs.");
                }
            }
        }

        private void BtnCueMedia_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaSource != null && !string.IsNullOrEmpty(_lastMediaFile))
            {
                _mediaSource.Cue(_lastMediaFile, ChkLoopMedia.IsChecked == true);
            }
            else
            {
                System.Windows.MessageBox.Show("No media file selected. Please select a file first.");
            }
        }

        private void BtnPauseMedia_Click(object sender, RoutedEventArgs e)
        {
            _mediaSource?.Pause();
        }

        private void BtnResumeMedia_Click(object sender, RoutedEventArgs e)
        {
            _mediaSource?.Resume();
        }

        private void BtnStopMedia_Click(object sender, RoutedEventArgs e)
        {
            _mediaSource?.Stop();
        }

        private void SeekTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_cpuCounter != null)
                {
                    float cpu = _cpuCounter.NextValue();
                    int cpuInt = (int)Math.Round(cpu);
                    TxtCpuUsage.Text = $"{cpuInt}%";

                    // Color grading: Good (Green < 60%), Warning (Yellow 60% - 84%), Danger (Red >= 85%)
                    if (cpuInt < 60)
                    {
                        TxtCpuUsage.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xe6, 0x76)); // Vibrant Green
                    }
                    else if (cpuInt < 85)
                    {
                        TxtCpuUsage.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xff, 0xd6, 0x00)); // Vivid Yellow/Amber
                    }
                    else
                    {
                        TxtCpuUsage.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xff, 0x17, 0x44)); // Bright Red
                    }
                }
            }
            catch { }
            
            if (_mediaSource != null && !_isDraggingSeek)
            {
                float pos = _mediaSource.Position;
                if (pos >= 0 && pos <= 1)
                {
                    SldMediaSeek.Value = pos;
                }
            }
        }

        private void SldMediaSeek_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _isDraggingSeek = true;
        }

        private void SldMediaSeek_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _isDraggingSeek = false;
            if (_mediaSource != null)
            {
                _mediaSource.Position = (float)SldMediaSeek.Value;
            }
        }

        private void SldMediaSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingSeek && _mediaSource != null)
            {
                _mediaSource.Position = (float)e.NewValue;
            }
        }

        public static byte MatteR = 0;
        public static byte MatteG = 0;
        public static byte MatteB = 255;
        public static byte MatteY = 41;
        public static byte MatteU = 212;
        public static byte MatteV = 114;

        #region Source Tile Drag & Drop and Reordering

        private DependencyObject? GetParentSafe(DependencyObject obj)
        {
            if (obj is System.Windows.Media.Visual || obj is System.Windows.Media.Media3D.Visual3D)
            {
                return System.Windows.Media.VisualTreeHelper.GetParent(obj);
            }
            return System.Windows.LogicalTreeHelper.GetParent(obj);
        }

        private bool IsInteractiveElement(DependencyObject? obj)
        {
            while (obj != null && obj != SwitcherPanel && obj != SwitcherScrollViewer)
            {
                if (obj is System.Windows.Controls.Primitives.Thumb ||
                    obj is System.Windows.Controls.Primitives.Track ||
                    obj is System.Windows.Controls.Primitives.RepeatButton ||
                    obj is System.Windows.Controls.Slider ||
                    obj is System.Windows.Controls.Primitives.ScrollBar ||
                    obj is System.Windows.Controls.ComboBox ||
                    obj is System.Windows.Controls.ComboBoxItem ||
                    obj is System.Windows.Controls.CheckBox ||
                    obj is System.Windows.Controls.TextBox ||
                    obj is System.Windows.Controls.Button)
                {
                    return true;
                }
                obj = GetParentSafe(obj);
            }
            return false;
        }

        private FrameworkElement? GetSourceTileFromElement(DependencyObject? obj)
        {
            while (obj != null && obj != SwitcherPanel && obj != SwitcherScrollViewer)
            {
                if (obj is FrameworkElement fe && SwitcherPanel.Children.Contains(fe))
                {
                    return fe;
                }
                obj = GetParentSafe(obj);
            }
            return null;
        }

        private string GetTileIdentifier(FrameworkElement fe)
        {
            if (fe == TileMedia) return "TileMedia";
            if (fe == TileYouTube) return "TileYouTube";
            if (fe == TileNdi) return "TileNdi";
            if (fe == TileHtmlSource) return "TileHtmlSource";
            if (fe == TileColorBars) return "TileColorBars";
            if (fe == TileMatte) return "TileMatte";
            if (fe == TileSystemAudio) return "TileSystemAudio";

            if (fe.DataContext is DeckLinkInputViewModel dl) return "DeckLink:" + dl.DisplayName;
            if (fe.DataContext is ScreenInputViewModel scr) return "Screen:" + scr.DisplayName;
            if (fe.DataContext is WebcamInputViewModel cam) return "Webcam:" + cam.DisplayName;

            if (fe.Tag is string tag && !string.IsNullOrEmpty(tag)) return "Tag:" + tag;
            if (!string.IsNullOrEmpty(fe.Name)) return fe.Name;

            return fe.GetType().Name + "_" + fe.GetHashCode();
        }

        private void SwitcherPanel_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (IsInteractiveElement(e.OriginalSource as DependencyObject))
            {
                _potentialDragTile = null;
                return;
            }

            var tile = GetSourceTileFromElement(e.OriginalSource as DependencyObject);
            if (tile != null)
            {
                _dragStartPoint = e.GetPosition(SwitcherPanel);
                _potentialDragTile = tile;
            }
            else
            {
                _potentialDragTile = null;
            }
        }

        private void SwitcherPanel_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_potentialDragTile != null && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && !_isDragging)
            {
                System.Windows.Point pos = e.GetPosition(SwitcherPanel);
                Vector diff = _dragStartPoint - pos;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _isDragging = true;
                    _draggedTile = _potentialDragTile;
                    _potentialDragTile = null;

                    _draggedTile.Opacity = 0.55;

                    try
                    {
                        DataObject dragData = new DataObject("DeckLinkSwitcherSourceTile", _draggedTile);
                        DragDrop.DoDragDrop(SwitcherPanel, dragData, DragDropEffects.Move);
                    }
                    catch (Exception ex)
                    {
                        Log("DragDrop error: " + ex.Message);
                    }
                    finally
                    {
                        _autoScrollTimer.Stop();
                        _scrollDelta = 0;
                        if (_draggedTile != null)
                        {
                            _draggedTile.Opacity = 1.0;
                            _draggedTile = null;
                        }
                        _isDragging = false;
                        SaveSourceOrder();
                    }
                }
            }
        }

        private void SwitcherPanel_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _potentialDragTile = null;
        }

        private void SwitcherPanel_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = false;
            System.Windows.Input.Mouse.SetCursor(System.Windows.Input.Cursors.SizeAll);
            e.Handled = true;
        }

        private void SwitcherPanel_DragOver(object sender, DragEventArgs e)
        {
            if (_draggedTile == null) return;

            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // Auto-scrolling near top/bottom edges of SwitcherScrollViewer
            System.Windows.Point scrollPos = e.GetPosition(SwitcherScrollViewer);
            double scrollMargin = 50.0;
            if (scrollPos.Y >= 0 && scrollPos.Y < scrollMargin)
            {
                _scrollDelta = -14;
                if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
            }
            else if (scrollPos.Y > SwitcherScrollViewer.ActualHeight - scrollMargin && scrollPos.Y <= SwitcherScrollViewer.ActualHeight + 40)
            {
                _scrollDelta = 14;
                if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
            }
            else
            {
                _scrollDelta = 0;
                _autoScrollTimer.Stop();
            }

            // Real-time visual reordering
            try
            {
                System.Windows.Point panelPos = e.GetPosition(SwitcherPanel);
                for (int i = 0; i < SwitcherPanel.Children.Count; i++)
                {
                    if (SwitcherPanel.Children[i] is FrameworkElement target && target != _draggedTile)
                    {
                        System.Windows.Media.GeneralTransform transform = target.TransformToAncestor(SwitcherPanel);
                        System.Windows.Rect bounds = transform.TransformBounds(new System.Windows.Rect(0, 0, target.ActualWidth, target.ActualHeight));
                        if (bounds.Contains(panelPos))
                        {
                            int oldIndex = SwitcherPanel.Children.IndexOf(_draggedTile);
                            if (oldIndex != i && oldIndex >= 0)
                            {
                                SwitcherPanel.Children.RemoveAt(oldIndex);
                                SwitcherPanel.Children.Insert(i, _draggedTile);
                            }
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        private void SwitcherPanel_Drop(object sender, DragEventArgs e)
        {
            _autoScrollTimer.Stop();
            _scrollDelta = 0;
            if (_draggedTile != null)
            {
                _draggedTile.Opacity = 1.0;
                _draggedTile = null;
            }
            _isDragging = false;
            e.Handled = true;
            SaveSourceOrder();
        }

        private void AutoScrollTimer_Tick(object? sender, EventArgs e)
        {
            if (_isDragging && _scrollDelta != 0)
            {
                SwitcherScrollViewer.ScrollToVerticalOffset(SwitcherScrollViewer.VerticalOffset + _scrollDelta);
            }
        }

        public void SaveSourceOrder()
        {
            try
            {
                var settings = AppSettings.Load();
                settings.SourceOrder = SwitcherPanel.Children
                    .OfType<FrameworkElement>()
                    .Select(GetTileIdentifier)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToList();
                settings.Save();
            }
            catch (Exception ex)
            {
                Log("SaveSourceOrder error: " + ex.Message);
            }
        }

        public void ApplySavedSourceOrder()
        {
            try
            {
                var settings = AppSettings.Load();
                var order = settings.SourceOrder;
                if (order == null || order.Count == 0) return;

                var currentChildren = SwitcherPanel.Children.Cast<UIElement>().ToList();
                if (currentChildren.Count <= 1) return;

                var childMap = new Dictionary<UIElement, string>();
                foreach (var child in currentChildren)
                {
                    if (child is FrameworkElement fe)
                        childMap[child] = GetTileIdentifier(fe);
                }

                var sorted = currentChildren
                    .Select((element, originalIndex) =>
                    {
                        string id = childMap.TryGetValue(element, out var v) ? v : "";
                        int targetIndex = order.IndexOf(id);
                        return new
                        {
                            Element = element,
                            SortKey = targetIndex >= 0 ? targetIndex : 10000 + originalIndex
                        };
                    })
                    .OrderBy(x => x.SortKey)
                    .Select(x => x.Element)
                    .ToList();

                bool orderChanged = false;
                for (int i = 0; i < currentChildren.Count; i++)
                {
                    if (currentChildren[i] != sorted[i])
                    {
                        orderChanged = true;
                        break;
                    }
                }

                if (orderChanged)
                {
                    SwitcherPanel.Children.Clear();
                    foreach (var el in sorted)
                    {
                        SwitcherPanel.Children.Add(el);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("ApplySavedSourceOrder error: " + ex.Message);
            }
        }

        #endregion


        private void BtnMatteSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string hex)
            {
                SetMatteColorHex(hex);
            }
        }

        private void BtnMatteCustomColor_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(MatteR, MatteG, MatteB)
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                SetMatteColor(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            }
        }

        public void SetMatteColorHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return;
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                SetMatteColor(c.R, c.G, c.B);
            }
            catch { }
        }

        public void SetMatteColor(byte r, byte g, byte b)
        {
            MatteR = r;
            MatteG = g;
            MatteB = b;

            int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
            int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
            int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

            MatteY = (byte)Math.Clamp(y, 16, 235);
            MatteU = (byte)Math.Clamp(u, 16, 240);
            MatteV = (byte)Math.Clamp(v, 16, 240);

            UpdateMatteSwatchSelection();
            UpdateMiniPreviewsSynthetic();
            if (_activeSourceType == 2) UpdatePgmPreviewSynthetic();
            if (IsLoaded) SaveCurrentSettings();
        }

        private void UpdateMatteSwatchSelection()
        {
            if (MatteSwatchGrid == null) return;
            string currentHex = $"#{MatteR:X2}{MatteG:X2}{MatteB:X2}";
            bool matchedPreset = false;
            foreach (var child in MatteSwatchGrid.Children)
            {
                if (child is System.Windows.Controls.Button btn && btn.Tag is string tagHex)
                {
                    bool isSelected = string.Equals(tagHex, currentHex, StringComparison.OrdinalIgnoreCase);
                    if (isSelected) matchedPreset = true;
                    btn.BorderThickness = isSelected ? new Thickness(2.5) : new Thickness(1);
                    btn.BorderBrush = isSelected
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 229, 255))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 102, 102));
                }
            }

            if (BtnMatteCustomColor != null)
            {
                if (!matchedPreset)
                {
                    BtnMatteCustomColor.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(MatteR, MatteG, MatteB));
                    BtnMatteCustomColor.Foreground = (MatteR * 0.299 + MatteG * 0.587 + MatteB * 0.114) > 150
                        ? System.Windows.Media.Brushes.Black
                        : System.Windows.Media.Brushes.White;
                    BtnMatteCustomColor.BorderThickness = new Thickness(2.5);
                    BtnMatteCustomColor.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 229, 255));
                }
                else
                {
                    BtnMatteCustomColor.Background = (System.Windows.Media.Brush)FindResource("ButtonAltBg");
                    BtnMatteCustomColor.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary");
                    BtnMatteCustomColor.BorderThickness = new Thickness(1);
                    BtnMatteCustomColor.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 102, 102));
                }
            }
        }

        private static readonly byte[,] SyntheticBarsUyvy = new byte[8, 4] {
            { 128, 235, 128, 235 }, { 44, 210, 171, 210 }, { 156, 170, 44, 170 }, { 74, 145, 84, 145 },
            { 182, 106, 172, 106 }, { 98, 81, 212, 81 }, { 212, 41, 114, 41 }, { 128, 16, 128, 16 }
        };

        /// <summary>Builds a 1080p UYVY frame for Color Bars (1) or Matte (2) for software outputs (NDI/SRT/etc).</summary>
        private static byte[] BuildSyntheticUyvy1080(int sourceType)
        {
            const int w = 1920, h = 1080, rowBytes = w * 2;
            byte[] frame = new byte[rowBytes * h];
            if (sourceType == 2)
            {
                for (int i = 0; i < frame.Length; i += 4)
                {
                    frame[i] = MatteU; frame[i + 1] = MatteY; frame[i + 2] = MatteV; frame[i + 3] = MatteY;
                }
            }
            else
            {
                for (int x = 0; x < w; x += 2)
                {
                    int bar = (x * 8) / w;
                    int o = x * 2;
                    frame[o] = SyntheticBarsUyvy[bar, 0]; frame[o + 1] = SyntheticBarsUyvy[bar, 1];
                    frame[o + 2] = SyntheticBarsUyvy[bar, 2]; frame[o + 3] = SyntheticBarsUyvy[bar, 3];
                }
                for (int y = 1; y < h; y++) Buffer.BlockCopy(frame, 0, frame, y * rowBytes, rowBytes);
            }
            return frame;
        }

        private void UpdatePgmPreviewSynthetic()
        {
            if (_activeSourceType == 0 || _bmpOutput == null) return;

            byte[] pixels = new byte[480 * 270 * 4];
            
            if (_activeSourceType == 2) // Matte
            {
                byte r = MatteR, g = MatteG, b = MatteB;
                
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = b; // B
                    pixels[i+1] = g; // G
                    pixels[i+2] = r; // R
                    pixels[i+3] = 255; // A
                }
            }
            else if (_activeSourceType == 3 || _activeSourceType == 4 || _activeSourceType == 5 || _activeSourceType == 6 || _activeSourceType == 7 || _activeSourceType == 8 || _activeSourceType == 10) // Media, Desktop, Webcam, YouTube, HTML, NDI, or Extra Source
            {
                // Handled continuously by streaming/capture callbacks
                return;
            }
            else if (_activeSourceType == 1) // Color Bars
            {
                byte[,] colors = new byte[8, 3] {
                    { 255, 255, 255 }, // White
                    { 0, 255, 255 },   // Yellow
                    { 255, 255, 0 },   // Cyan
                    { 0, 255, 0 },     // Green
                    { 255, 0, 255 },   // Magenta
                    { 0, 0, 255 },     // Red
                    { 255, 0, 0 },     // Blue
                    { 0, 0, 0 }        // Black
                };
                
                for (int y = 0; y < 270; y++)
                {
                    for (int x = 0; x < 480; x++)
                    {
                        int barIndex = (x * 8) / 480;
                        int offset = (y * 480 + x) * 4;
                        pixels[offset] = colors[barIndex, 0]; // B
                        pixels[offset+1] = colors[barIndex, 1]; // G
                        pixels[offset+2] = colors[barIndex, 2]; // R
                        pixels[offset+3] = 255; // A
                    }
                }
            }

            // Store directly (no UI thread needed) so stream loop has it immediately
            

            Application.Current.Dispatcher.BeginInvoke(() => {
                if (_bmpOutput != null)
                {
                    WritePgmPreviewFrame(pixels);
                }
            });
        }


        private void UpdateMiniPreviewsSynthetic()
        {
            // Color Bars (Type 1)
            byte[] cbPixels = new byte[480 * 270 * 4];
            byte[,] colors = new byte[8, 3] {
                { 255, 255, 255 }, { 0, 255, 255 }, { 255, 255, 0 }, { 0, 255, 0 },
                { 255, 0, 255 }, { 0, 0, 255 }, { 255, 0, 0 }, { 0, 0, 0 }
            };
            for (int y = 0; y < 270; y++) {
                for (int x = 0; x < 480; x++) {
                    int barIndex = (x * 8) / 480;
                    int offset = (y * 480 + x) * 4;
                    cbPixels[offset] = colors[barIndex, 0];
                    cbPixels[offset+1] = colors[barIndex, 1];
                    cbPixels[offset+2] = colors[barIndex, 2];
                    cbPixels[offset+3] = 255;
                }
            }
            
            // Matte (Type 2)
            byte[] mPixels = new byte[480 * 270 * 4];
            byte r = MatteR, g = MatteG, b = MatteB;
            for (int i = 0; i < mPixels.Length; i += 4) {
                mPixels[i] = b; mPixels[i+1] = g; mPixels[i+2] = r; mPixels[i+3] = 255;
            }

            // Desktop (Type 4)
            // Desktop preview is updated independently by background thread.

            Application.Current.Dispatcher.BeginInvoke(() => {
                if (_bmpColorBars != null) _bmpColorBars.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), cbPixels, 480 * 4, 0);
                if (_bmpMatte != null) _bmpMatte.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), mPixels, 480 * 4, 0);
            });
        }



        internal static unsafe void ConvertUyvy1080ToBgra1080(byte[] uyvy, byte[] bgra)
        {
            const int width = 1920;
            const int height = 1080;
            fixed (byte* srcBase = uyvy)
            fixed (byte* dstBase = bgra)
            {
                byte* pSrcBase = srcBase;
                byte* pDstBase = dstBase;
                Parallel.For(0, height, y =>
                {
                    byte* src = pSrcBase + y * width * 2;
                    byte* dst = pDstBase + y * width * 4;
                    for (int x = 0; x < width; x += 2)
                    {
                        int u = src[0] - 128;
                        int y0 = src[1] - 16;
                        int v = src[2] - 128;
                        int y1 = src[3] - 16;

                        int c0 = 298 * y0;
                        int c1 = 298 * y1;
                        int rDelta = 409 * v + 128;
                        int gDelta = -100 * u - 208 * v + 128;
                        int bDelta = 516 * u + 128;

                        int r0 = (c0 + rDelta) >> 8;
                        int g0 = (c0 + gDelta) >> 8;
                        int b0 = (c0 + bDelta) >> 8;

                        int r1 = (c1 + rDelta) >> 8;
                        int g1 = (c1 + gDelta) >> 8;
                        int b1 = (c1 + bDelta) >> 8;

                        dst[0] = (byte)(b0 < 0 ? 0 : (b0 > 255 ? 255 : b0));
                        dst[1] = (byte)(g0 < 0 ? 0 : (g0 > 255 ? 255 : g0));
                        dst[2] = (byte)(r0 < 0 ? 0 : (r0 > 255 ? 255 : r0));
                        dst[3] = 255;

                        dst[4] = (byte)(b1 < 0 ? 0 : (b1 > 255 ? 255 : b1));
                        dst[5] = (byte)(g1 < 0 ? 0 : (g1 > 255 ? 255 : g1));
                        dst[6] = (byte)(r1 < 0 ? 0 : (r1 > 255 ? 255 : r1));
                        dst[7] = 255;

                        src += 4;
                        dst += 8;
                    }
                });
            }
        }

        internal static byte[] ConvertBgraToUyvy1080(byte[] bgra)
        {
            const int srcW = 480, dstW = 1920, dstH = 1080;
            byte[] uyvy = new byte[dstH * dstW * 2];
            Parallel.For(0, dstH, y =>
            {
                int srcY = _uyvyRowLut[y];
                int dstRow = y * dstW * 2;
                int srcRow = srcY * srcW * 4;
                for (int x = 0; x < dstW; x += 2)
                {
                    int o1 = srcRow + _uyvyColLut[x] * 4;
                    byte b1 = bgra[o1], g1 = bgra[o1+1], r1 = bgra[o1+2];
                    int o2 = srcRow + _uyvyColLut[x+1] * 4;
                    byte b2 = bgra[o2], g2 = bgra[o2+1], r2 = bgra[o2+2];
                    int Y1=((66*r1+129*g1+25*b1+128)>>8)+16, U1=((-38*r1-74*g1+112*b1+128)>>8)+128, V1=((112*r1-94*g1-18*b1+128)>>8)+128, Y2=((66*r2+129*g2+25*b2+128)>>8)+16;
                    if (Y1<16) Y1=16; else if (Y1>235) Y1=235;
                    if (Y2<16) Y2=16; else if (Y2>235) Y2=235;
                    if (U1<16) U1=16; else if (U1>240) U1=240;
                    if (V1<16) V1=16; else if (V1>240) V1=240;
                    int d = dstRow + x*2;
                    uyvy[d]=(byte)U1; uyvy[d+1]=(byte)Y1; uyvy[d+2]=(byte)V1; uyvy[d+3]=(byte)Y2;
                }
            });
            return uyvy;
        }

        internal static byte[] ConvertBgra1080ToUyvy1080(byte[] bgra)
        {
            byte[] uyvy = new byte[1080 * 1920 * 2];
            ConvertBgra1080ToUyvy1080(bgra, uyvy);
            return uyvy;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static unsafe void ConvertBgra1080ToUyvy1080(byte[] bgra, byte[] uyvy)
        {
            const int dstW = 1920, dstH = 1080;
            fixed (byte* pBgra = bgra)
            fixed (byte* pUyvy = uyvy)
            {
                IntPtr ptrBgra = (IntPtr)pBgra;
                IntPtr ptrUyvy = (IntPtr)pUyvy;
                Parallel.For(0, dstH, y =>
                {
                    uint* srcRow32 = (uint*)((byte*)ptrBgra + y * (dstW * 4));
                    uint* dstRow32 = (uint*)((byte*)ptrUyvy + y * (dstW * 2));
                    int pairs = dstW >> 1;
                    for (int p = 0; p < pairs; p++)
                    {
                        uint px1 = srcRow32[p * 2];
                        uint px2 = srcRow32[p * 2 + 1];

                        int b1 = (int)(px1 & 0xFF);
                        int g1 = (int)((px1 >> 8) & 0xFF);
                        int r1 = (int)((px1 >> 16) & 0xFF);

                        int b2 = (int)(px2 & 0xFF);
                        int g2 = (int)((px2 >> 8) & 0xFF);
                        int r2 = (int)((px2 >> 16) & 0xFF);

                        int rAvg = (r1 + r2) >> 1;
                        int gAvg = (g1 + g2) >> 1;
                        int bAvg = (b1 + b2) >> 1;

                        int Y1 = ((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16;
                        int U1 = ((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128;
                        int V1 = ((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128;
                        int Y2 = ((66 * r2 + 129 * g2 + 25 * b2 + 128) >> 8) + 16;

                        if (Y1 < 16) Y1 = 16; else if (Y1 > 235) Y1 = 235;
                        if (Y2 < 16) Y2 = 16; else if (Y2 > 235) Y2 = 235;
                        if (U1 < 16) U1 = 16; else if (U1 > 240) U1 = 240;
                        if (V1 < 16) V1 = 16; else if (V1 > 240) V1 = 240;

                        dstRow32[p] = (uint)(U1 | (Y1 << 8) | (V1 << 16) | (Y2 << 24));
                    }
                });
            }
        }

        private void StartDesktopCaptureLoop()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        for (int i = 0; i < DynamicScreens.Count; i++)
                        {
                            var vm = DynamicScreens[i];
                            var screen = vm.Screen;
                            var ctx = vm.Context;
                            int screenWidth = screen.Bounds.Width;
                            int screenHeight = screen.Bounds.Height;

                            if (ctx.ScreenBmp == null || ctx.ScreenBmp.Width != screenWidth || ctx.ScreenBmp.Height != screenHeight)
                            {
                                ctx.Dispose();
                                ctx.ScreenBmp = new System.Drawing.Bitmap(screenWidth, screenHeight);
                                ctx.ScreenG = System.Drawing.Graphics.FromImage(ctx.ScreenBmp);

                                ctx.Prev480Bmp = new System.Drawing.Bitmap(480, 270);
                                ctx.Prev480G = System.Drawing.Graphics.FromImage(ctx.Prev480Bmp);
                                ctx.Prev480G.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;

                                ctx.Pgm1080Bmp = new System.Drawing.Bitmap(1920, 1080);
                                ctx.Pgm1080G = System.Drawing.Graphics.FromImage(ctx.Pgm1080Bmp);
                                ctx.Pgm1080G.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            }

                            // Capture screen
                            ctx.ScreenG.CopyFromScreen(screen.Bounds.X, screen.Bounds.Y, 0, 0, ctx.ScreenBmp.Size, System.Drawing.CopyPixelOperation.SourceCopy);

                            // Draw preview (480x270)
                            ctx.Prev480G.Clear(System.Drawing.Color.Black);
                            float prevScale = Math.Min(480f / screenWidth, 270f / screenHeight);
                            int pDrawW = (int)(screenWidth * prevScale);
                            int pDrawH = (int)(screenHeight * prevScale);
                            int pDrawX = (480 - pDrawW) / 2;
                            int pDrawY = (270 - pDrawH) / 2;
                            ctx.Prev480G.DrawImage(ctx.ScreenBmp, pDrawX, pDrawY, pDrawW, pDrawH);

                            var data480 = ctx.Prev480Bmp.LockBits(new System.Drawing.Rectangle(0, 0, 480, 270), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            System.Runtime.InteropServices.Marshal.Copy(data480.Scan0, ctx.Prev480Buffer, 0, ctx.Prev480Buffer.Length);
                            ctx.Prev480Bmp.UnlockBits(data480);

                            Application.Current.Dispatcher.BeginInvoke(() => {
                                vm.PreviewBitmap?.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), ctx.Prev480Buffer, 480 * 4, 0);
                            });

                            // Populate 1080p buffer when this screen is active on Program Output or used in Multi-Box
                            bool isPgmScreen = _activeSourceType == 4 && _activeScreen == vm;
                            bool isCompositeScreen = IsSourceUsedInComposite(vm.DisplayName);
                            if (isPgmScreen || isCompositeScreen)
                            {
                                if (screenWidth == 1920 && screenHeight == 1080)
                                {
                                    var dataScreen = ctx.ScreenBmp.LockBits(new System.Drawing.Rectangle(0, 0, 1920, 1080), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                    System.Runtime.InteropServices.Marshal.Copy(dataScreen.Scan0, ctx.Pgm1080Buffer, 0, ctx.Pgm1080Buffer.Length);
                                    ctx.ScreenBmp.UnlockBits(dataScreen);
                                }
                                else
                                {
                                    ctx.Pgm1080G.Clear(System.Drawing.Color.Black);
                                    float pgmScale = Math.Min(1920f / screenWidth, 1080f / screenHeight);
                                    int gDrawW = (int)(screenWidth * pgmScale);
                                    int gDrawH = (int)(screenHeight * pgmScale);
                                    int gDrawX = (1920 - gDrawW) / 2;
                                    int gDrawY = (1080 - gDrawH) / 2;
                                    ctx.Pgm1080G.DrawImage(ctx.ScreenBmp, gDrawX, gDrawY, gDrawW, gDrawH);

                                    var data1080 = ctx.Pgm1080Bmp.LockBits(new System.Drawing.Rectangle(0, 0, 1920, 1080), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                    System.Runtime.InteropServices.Marshal.Copy(data1080.Scan0, ctx.Pgm1080Buffer, 0, ctx.Pgm1080Buffer.Length);
                                    ctx.Pgm1080Bmp.UnlockBits(data1080);
                                }

                                if (isPgmScreen)
                                {
                                    lock (_pgmLock)
                                    {
                                        if (_latestPgmFrame == null || _latestPgmFrame.Length != 1920 * 1080 * 4)
                                            _latestPgmFrame = new byte[1920 * 1080 * 4];
                                        Array.Copy(ctx.Pgm1080Buffer, _latestPgmFrame, _latestPgmFrame.Length);
                                    }

                                    Application.Current.Dispatcher.BeginInvoke(() => {
                                        if (_bmpOutput != null)
                                            WritePgmPreviewFrame(ctx.Prev480Buffer);
                                    });
                                }
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(40);
                }
            });
        }

        // Pre-computed lookup tables for fast BGRA->UYVY scaling 480x270 -> 1920x1080
        private static readonly int[] _uyvyRowLut = BuildRowLut();
        private static readonly int[] _uyvyColLut = BuildColLut();
        private static int[] BuildRowLut() { var t = new int[1080]; for (int y = 0; y < 1080; y++) t[y] = Math.Min((int)(y * 270.0f / 1080f), 269); return t; }
        private static int[] BuildColLut() { var t = new int[1920]; for (int x = 0; x < 1920; x++) t[x] = Math.Min((int)(x * 480.0f / 1920f), 479); return t; }

        public static _BMDDisplayMode ParseDisplayMode(string formatStr)
        {
            if (string.IsNullOrEmpty(formatStr)) return _BMDDisplayMode.bmdModeHD1080i50;
            if (formatStr.Contains("PAL")) return _BMDDisplayMode.bmdModePAL;
            if (formatStr.Contains("1080p")) return _BMDDisplayMode.bmdModeHD1080p25;
            if (formatStr.Contains("720p")) return _BMDDisplayMode.bmdModeHD720p50;
            return _BMDDisplayMode.bmdModeHD1080i50;
        }

        private static byte[]? _softwareDeckLinkUyvyBuffer;
        private static readonly object _softwareDeckLinkLock = new object();

        private static void ScheduleSoftwareDeckLinkFrame(IDeckLinkVideoInputFrame inputFrame, IDeckLinkAudioInputPacket audioPacket, float inputVolume)
        {
            int width = inputFrame.GetWidth();
            int height = inputFrame.GetHeight();
            int rowBytes = inputFrame.GetRowBytes();

            uint audioSampleCount = audioPacket != null ? (uint)audioPacket.GetSampleFrameCount() : 1920;
            IntPtr modifiedAudioBuffer = MixAudio(audioSampleCount, ActiveSourceType, ActiveInputName, "software-decklink-output");

            byte[]? uyvyBytes = null;
            if (YtStreamer.IsStreaming || LclRecorder.IsRecording || NdiOut.IsRunning || SrtOut.IsStreaming || IsFullscreenPreviewActive || IsMixEnabled || IsFtbActive)
            {
                lock (_softwareDeckLinkLock)
                {
                    if (_softwareDeckLinkUyvyBuffer == null || _softwareDeckLinkUyvyBuffer.Length != 1920 * 1080 * 2)
                    {
                        _softwareDeckLinkUyvyBuffer = new byte[1920 * 1080 * 2];
                    }

                    var inputBuf = (IDeckLinkVideoBuffer)inputFrame;
                    inputBuf.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
                    try
                    {
                        inputBuf.GetBytes(out IntPtr inputBuffer);
                        unsafe
                        {
                            fixed (byte* dest = _softwareDeckLinkUyvyBuffer)
                            {
                                if (width == 1920 && height == 1080)
                                {
                                    DeckLinkDevice.memcpy((IntPtr)dest, inputBuffer, new UIntPtr(1920 * 1080 * 2));
                                }
                                else
                                {
                                    DeckLinkDevice.ScaleUyvyTo1080Uyvy((byte*)inputBuffer, width, height, rowBytes, dest, 1920 * 2);
                                }
                                ApplyLogoOverlayUyvy(dest);
                            }
                        }
                    }
                    finally
                    {
                        inputBuf.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
                    }

                    uyvyBytes = (byte[])_softwareDeckLinkUyvyBuffer.Clone();
                }
            }

            if (modifiedAudioBuffer != IntPtr.Zero)
            {
                OutputProgramAudioAndVideo(modifiedAudioBuffer, audioSampleCount, uyvyBytes, true);
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
            }
        }

        private void InitializeDynamicRouting(DeckLinkDeviceInfo outInfo, System.Collections.Generic.List<SelectedInputConfig> selectedInputs, System.Windows.Media.Imaging.WriteableBitmap bmpOutput)
        {
            List<IDeckLink> mtaLinks = new List<IDeckLink>();
            try
            {
                IDeckLinkIterator iterator = new CDeckLinkIterator();
                while (true)
                {
                    iterator.Next(out IDeckLink dl);
                    if (dl == null) break;
                    mtaLinks.Add(dl);
                }
            }
            catch (Exception ex)
            {
                Log("MTA CDeckLinkIterator error: " + ex.Message);
            }

            IDeckLink GetLink(DeckLinkDeviceInfo info)
            {
                if (info == null || info.Index < 0 || info.Index >= mtaLinks.Count) return null;
                return mtaLinks[info.Index];
            }

            IDeckLink outLink = GetLink(outInfo);
            if (outLink != null)
            {
                _activeOutput = new DeckLinkDevice(outLink, "Output");
                _activeOutput.StartPlayback();
            }
            else
            {
                _activeOutput = null;
                Log("No physical output selected (None).");
            }
            
            Application.Current.Dispatcher.Invoke(() => DynamicInputs.Clear());
            _activeInput = null;

            var settings = AppSettings.Load();

            int inputIndex = 1;
            foreach (var inCfg in selectedInputs)
            {
                var roleName = "Input " + inputIndex;
                inputIndex++;
                
                var dl = GetLink(inCfg.Device);
                if (dl == null) continue; // Skip if 'None' or invalid
                
                if (outLink != null && dl == outLink) continue; // Skip output device

                DeckLinkInputViewModel vm = null;
                Application.Current.Dispatcher.Invoke(() => {
                    vm = new DeckLinkInputViewModel(roleName);
                    vm.PreviewBitmap = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    
                    if (settings.InputAudioLevels.ContainsKey(roleName)) vm.AudioLevel = settings.InputAudioLevels[roleName];
                    if (settings.InputAudioStates.ContainsKey(roleName)) vm.AudioStateIndex = (int)settings.InputAudioStates[roleName];
                    
                    DynamicInputs.Add(vm);
                    
                    var template = (DataTemplate)Application.Current.MainWindow.FindResource("InputTileTemplate");
                    var tile = (UIElement)template.LoadContent();
                    if (tile is FrameworkElement fe) fe.DataContext = vm;
                    
                    var window = (MainWindow)Application.Current.MainWindow;
                    window.SwitcherPanel.Children.Insert(DynamicInputs.Count - 1, tile);
                });

                var inputDev = new DeckLinkDevice(dl, roleName);
                vm.Device = inputDev;
                
                inputDev.PreviewBitmap = vm.PreviewBitmap;
                inputDev.OnVideoAndAudioArrived = (frame, audio) => 
                {
                    if (_activeSourceType == 0 && _activeInput == inputDev)
                    {
                        if (_activeOutput != null)
                        {
                            _activeOutput.ScheduleFrame(frame, audio, inputDev.AudioLevel);
                        }
                        else
                        {
                            ScheduleSoftwareDeckLinkFrame(frame, audio, inputDev.AudioLevel);
                        }
                    }
                };
                inputDev.OnPreviewBufferUpdated = (buf) => 
                { 
                    if (_activeSourceType == 0 && _activeInput == inputDev)
                    {
                        WritePgmPreviewFrame(buf);
                    }
                };
                inputDev.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { vm.AudioL = l; vm.AudioR = r; }); };

                string formatStr = inCfg.Format ?? "Auto (Detect)";
                bool autoDetect = formatStr.StartsWith("Auto");
                _BMDDisplayMode mode = ParseDisplayMode(formatStr);
                inputDev.StartCapture(mode, autoDetect);
                
                if (_activeInput == null)
                {
                    _activeInput = inputDev;
                    if (_activeSourceType == 0)
                    {
                        ActiveInputName = roleName;
                        Application.Current.Dispatcher.BeginInvoke(new Action(UpdateProgramSourceFlags));
                    }
                }
            }

            _syntheticCts?.Cancel();
            _syntheticCts = new CancellationTokenSource();
            var token = _syntheticCts.Token;

            Task.Run(async () =>
            {
                double syntheticAudioPhase = 0;
                long frameIntervalTicks = Stopwatch.Frequency / 25;
                long nextFrameTick = Stopwatch.GetTimestamp();
                int compositeThumbTick = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (_activeSourceType == 1 || _activeSourceType == 2 || _activeSourceType == 4 || _activeSourceType == 5 || _activeSourceType == 6 || _activeSourceType == 7 || _activeSourceType == 8 || _activeSourceType == 9 || _activeSourceType == 10)
                        {
                            if (_activeSourceType == 9)
                            {
                                RefreshCompositeFrame(true);
                            }
                            else
                            {
                                // Keep _latestPgmFrame fresh for streaming loop
                                UpdatePgmPreviewSynthetic();
                            }

                            if (_activeOutput != null)
                            {
                                _activeOutput.ScheduleSyntheticFrame(_activeSourceType);
                            }
                            else
                            {
                                uint audioSampleCount = 1920;
                                IntPtr modifiedAudioBuffer = MainWindow.MixAudio(audioSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName, "synthetic-software-output");
                                if (modifiedAudioBuffer != IntPtr.Zero)
                                {
                                    byte[] uyvyBytes = null;
                                    if (_activeSourceType == 6 && LatestYouTubeUyvy != null)
                                    {
                                        lock (MainWindow._pgmLock)
                                        {
                                            uyvyBytes = (byte[])LatestYouTubeUyvy.Clone();
                                        }
                                    }
                                    else if (_activeSourceType == 8 && LatestNdiUyvy != null)
                                    {
                                        lock (MainWindow._pgmLock)
                                        {
                                            uyvyBytes = (byte[])LatestNdiUyvy.Clone();
                                        }
                                    }
                                    else if (_activeSourceType == 7 && LatestHtmlSourceUyvy != null)
                                    {
                                        lock (MainWindow._pgmLock)
                                        {
                                            uyvyBytes = (byte[])LatestHtmlSourceUyvy.Clone();
                                        }
                                    }
                                    else if (_activeSourceType == 9 && LatestCompositeUyvy != null)
                                    {
                                        lock (MainWindow._pgmLock)
                                        {
                                            uyvyBytes = (byte[])LatestCompositeUyvy.Clone();
                                        }
                                    }
                                    else if (_activeSourceType == 10 && LatestExtraSourceUyvy != null)
                                    {
                                        lock (MainWindow._pgmLock)
                                        {
                                            uyvyBytes = (byte[])LatestExtraSourceUyvy.Clone();
                                        }
                                    }
                                    else if (_activeSourceType == 1 || _activeSourceType == 2)
                                    {
                                        uyvyBytes = BuildSyntheticUyvy1080(_activeSourceType);
                                    }
                                    else
                                    {
                                        lock (MainWindow._pgmLock) 
                                        { 
                                            if (_latestPgmFrame != null && _latestPgmFrame.Length == 1920 * 1080 * 4) uyvyBytes = ConvertBgra1080ToUyvy1080(_latestPgmFrame);
                                            else if (_latestPgmFrame != null) uyvyBytes = ConvertBgraToUyvy1080(_latestPgmFrame); 
                                        }
                                    }
                                    ApplyLogoOverlayUyvy(uyvyBytes);
                                    OutputProgramAudioAndVideo(modifiedAudioBuffer, audioSampleCount, uyvyBytes);
                                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
                                }
                            }
                        }
                        else if (_isCompositeEditorOpen || ++compositeThumbTick >= 6)
                        {
                            compositeThumbTick = 0;
                            RefreshCompositeFrame(false);
                        }
                        nextFrameTick += frameIntervalTicks;
                        long remainingTicks = nextFrameTick - Stopwatch.GetTimestamp();
                        if (remainingTicks > 0)
                        {
                            await Task.Delay(TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency), token);
                        }
                        else
                        {
                            nextFrameTick = Stopwatch.GetTimestamp();
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }, token);

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplySavedSourceOrder();
                SwitcherPanel.IsEnabled = true;
                CmbOutput.IsEnabled = true;
                InputSelectorsControl.IsEnabled = true;
                BtnApplySettings.IsEnabled = false;
                bool hasAnyHardware = _activeOutput != null || DynamicInputs.Count > 0;
                TxtStatus.Text = hasAnyHardware ? "Running!" : "Running (None selected)";
                if (_activeInput == null && _activeSourceType == 0)
                {
                    ClearPgmPreview();
                }
            }));
        }

    }
}
