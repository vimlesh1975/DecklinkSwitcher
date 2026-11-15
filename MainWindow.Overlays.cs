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
        private void BtnSelectLogo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select PGM Logo Image",
                Filter = "Image Files (*.png;*.webp;*.bmp;*.jpg;*.jpeg)|*.png;*.webp;*.bmp;*.jpg;*.jpeg|PNG Files (*.png)|*.png|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                if (LoadLogoFromFile(dlg.FileName))
                {
                    SetLogoOverlayActiveState(true);
                    SaveCurrentSettings();
                }
            }
        }

        private void BtnSelectLogoSeq_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select PNG / Image Sequence Folder"
            };
            if (!string.IsNullOrWhiteSpace(_logoFilePath))
            {
                string? initialDir = Directory.Exists(_logoFilePath) ? _logoFilePath : Path.GetDirectoryName(_logoFilePath);
                if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                    dlg.InitialDirectory = initialDir;
            }
            if (dlg.ShowDialog() == true)
            {
                if (LoadLogoFromFile(dlg.FolderName))
                {
                    SetLogoOverlayActiveState(true);
                    SaveCurrentSettings();
                }
            }
        }

        private void CmbLogoSeqFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingLogoUi || !IsLoaded) return;
            if (CmbLogoSeqFps?.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Content?.ToString(), out int fps))
            {
                LogoSequenceFps = Math.Clamp(fps, 1, 120);
                LogoSequenceStartMs = Environment.TickCount64;
                UpdateLogoPreviewElement();
                SaveCurrentSettings();
            }
        }

        private void BtnToggleLogo_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentLogoOverlay == null && !IsLogoOverlayEnabled)
            {
                BtnSelectLogo_Click(sender, e);
                return;
            }
            SetLogoOverlayActiveState(!IsLogoOverlayEnabled);
            SaveCurrentSettings();
        }

        private void SetLogoOverlayActiveState(bool enabled)
        {
            IsLogoOverlayEnabled = enabled && CurrentLogoOverlay != null;
            if (IsLogoOverlayEnabled)
                LogoSequenceStartMs = Environment.TickCount64;
            _isUpdatingLogoUi = true;
            try
            {
                if (BtnToggleLogo != null)
                {
                    BtnToggleLogo.Content = IsLogoOverlayEnabled ? "LOGO: ON" : "LOGO: OFF";
                    BtnToggleLogo.Background = IsLogoOverlayEnabled
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2e, 0x7d, 0x32))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x42, 0x42));
                }
            }
            finally
            {
                _isUpdatingLogoUi = false;
            }
            UpdateLogoPreviewElement();
        }

        private static int CompareNaturalFileNames(string? a, string? b)
        {
            string sa = Path.GetFileName(a) ?? "";
            string sb = Path.GetFileName(b) ?? "";
            int ia = 0, ib = 0;
            while (ia < sa.Length && ib < sb.Length)
            {
                if (char.IsDigit(sa[ia]) && char.IsDigit(sb[ib]))
                {
                    int startA = ia, startB = ib;
                    while (ia < sa.Length && char.IsDigit(sa[ia])) ia++;
                    while (ib < sb.Length && char.IsDigit(sb[ib])) ib++;

                    string numA = sa.Substring(startA, ia - startA).TrimStart('0');
                    string numB = sb.Substring(startB, ib - startB).TrimStart('0');
                    if (numA.Length != numB.Length)
                        return numA.Length.CompareTo(numB.Length);
                    int cmpNum = string.Compare(numA, numB, StringComparison.Ordinal);
                    if (cmpNum != 0) return cmpNum;
                    int lenCmp = (ia - startA).CompareTo(ib - startB);
                    if (lenCmp != 0) return lenCmp;
                }
                else
                {
                    int cmpChar = char.ToUpperInvariant(sa[ia]).CompareTo(char.ToUpperInvariant(sb[ib]));
                    if (cmpChar != 0) return cmpChar;
                    ia++;
                    ib++;
                }
            }
            return sa.Length.CompareTo(sb.Length);
        }

        private static (System.Drawing.Bitmap RawBitmap, System.Windows.Media.Imaging.BitmapImage PreviewImage) DecodeLogoImageFile(string filePath)
        {
            byte[] fileBytes = File.ReadAllBytes(filePath);
            using var ms1 = new MemoryStream(fileBytes);
            using var loadedBmp = new System.Drawing.Bitmap(ms1);
            var clonedBmp = new System.Drawing.Bitmap(loadedBmp.Width, loadedBmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(clonedBmp))
            {
                g.DrawImage(loadedBmp, 0, 0, loadedBmp.Width, loadedBmp.Height);
            }

            var bmpImg = new System.Windows.Media.Imaging.BitmapImage();
            using (var ms2 = new MemoryStream(fileBytes))
            {
                bmpImg.BeginInit();
                bmpImg.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmpImg.StreamSource = ms2;
                bmpImg.EndInit();
                bmpImg.Freeze();
            }

            return (clonedBmp, bmpImg);
        }

        private void DisposeRawLogoResources()
        {
            var seq = _rawLogoSequence;
            _rawLogoSequence = null;
            if (seq != null)
            {
                foreach (var bmp in seq)
                {
                    if (!ReferenceEquals(bmp, _rawLogoBitmap))
                        bmp?.Dispose();
                }
            }
            _rawLogoBitmap?.Dispose();
            _rawLogoBitmap = null;
        }

        private bool LoadLogoFromFile(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".bmp", ".jpg", ".jpeg", ".gif" };
                    var frameFiles = Directory.EnumerateFiles(path)
                        .Where(f => supportedExts.Contains(Path.GetExtension(f)))
                        .ToList();
                    frameFiles.Sort(CompareNaturalFileNames);

                    if (frameFiles.Count == 0)
                    {
                        System.Windows.MessageBox.Show("No supported image frames (.png, .bmp, .jpg, .gif) found in the selected folder.", "Sequence Empty", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }

                    var rawFrames = new List<System.Drawing.Bitmap>(frameFiles.Count);
                    var previewFrames = new List<System.Windows.Media.Imaging.BitmapImage>(frameFiles.Count);
                    try
                    {
                        foreach (string frameFile in frameFiles)
                        {
                            var (rawBmp, previewImg) = DecodeLogoImageFile(frameFile);
                            rawFrames.Add(rawBmp);
                            previewFrames.Add(previewImg);
                        }
                    }
                    catch
                    {
                        foreach (var bmp in rawFrames) bmp.Dispose();
                        throw;
                    }

                    DisposeRawLogoResources();
                    _rawLogoSequence = rawFrames.ToArray();
                    _rawLogoBitmap = _rawLogoSequence[0];
                    _logoPreviewSequence = previewFrames.ToArray();
                    _logoPreviewImageSource = _logoPreviewSequence[0];
                    _logoFilePath = path;
                    LogoSequenceStartMs = Environment.TickCount64;

                    if (TxtLogoFileName != null)
                    {
                        string folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (string.IsNullOrEmpty(folderName)) folderName = path;
                        TxtLogoFileName.Text = $"{folderName} ({_rawLogoSequence.Length} frames)";
                        TxtLogoFileName.ToolTip = path;
                    }

                    RebuildPreparedLogo();
                    return true;
                }

                if (!File.Exists(path)) return false;

                var (singleBmp, singlePreview) = DecodeLogoImageFile(path);

                DisposeRawLogoResources();
                _rawLogoBitmap = singleBmp;
                _logoPreviewSequence = null;
                _logoPreviewImageSource = singlePreview;
                _logoFilePath = path;

                if (TxtLogoFileName != null)
                {
                    TxtLogoFileName.Text = Path.GetFileName(path);
                    TxtLogoFileName.ToolTip = path;
                }

                RebuildPreparedLogo();
                return true;
            }
            catch (Exception ex)
            {
                Log("LoadLogoFromFile error: " + ex.Message);
                System.Windows.MessageBox.Show("Failed to load logo image: " + ex.Message, "Logo Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private unsafe PreparedLogoOverlay BakePreparedLogoOverlay(System.Drawing.Bitmap sourceBmp, int w, int h)
        {
            using var scaled = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(scaled))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(sourceBmp, new System.Drawing.Rectangle(0, 0, w, h));
            }

            int totalPixels = w * h;
            byte[] yArr = new byte[totalPixels];
            byte[] uArr = new byte[totalPixels];
            byte[] vArr = new byte[totalPixels];
            byte[] aArr = new byte[totalPixels];

            var bmpData = scaled.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                byte* scan0 = (byte*)bmpData.Scan0;
                int stride = bmpData.Stride;
                for (int y = 0; y < h; y++)
                {
                    byte* row = scan0 + y * stride;
                    int rowOffset = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 4 + 0];
                        byte gVal = row[x * 4 + 1];
                        byte r = row[x * 4 + 2];
                        byte a = row[x * 4 + 3];

                        int yVal = ((66 * r + 129 * gVal + 25 * b + 128) >> 8) + 16;
                        int uVal = ((-38 * r - 74 * gVal + 112 * b + 128) >> 8) + 128;
                        int vVal = ((112 * r - 94 * gVal - 18 * b + 128) >> 8) + 128;

                        if (yVal < 16) yVal = 16; else if (yVal > 235) yVal = 235;
                        if (uVal < 16) uVal = 16; else if (uVal > 240) uVal = 240;
                        if (vVal < 16) vVal = 16; else if (vVal > 240) vVal = 240;

                        int idx = rowOffset + x;
                        yArr[idx] = (byte)yVal;
                        uArr[idx] = (byte)uVal;
                        vArr[idx] = (byte)vVal;
                        aArr[idx] = a;
                    }
                }
            }
            finally
            {
                scaled.UnlockBits(bmpData);
            }

            return new PreparedLogoOverlay
            {
                Width = w,
                Height = h,
                Y = yArr,
                U = uArr,
                V = vArr,
                A = aArr
            };
        }

        private void RebuildPreparedLogo()
        {
            if (_rawLogoBitmap == null || _rawLogoBitmap.Width <= 0 || _rawLogoBitmap.Height <= 0)
            {
                CurrentLogoOverlay = null;
                CurrentLogoSequence = null;
                UpdateLogoPreviewElement();
                return;
            }

            int w = Math.Max(2, (int)Math.Round(1920.0 * (Math.Clamp(LogoSizePercent, 3.0, 50.0) / 100.0)));
            if ((w & 1) != 0) w++;
            if (w > 1920) w = 1920;

            int h = Math.Max(2, (int)Math.Round(w * ((double)_rawLogoBitmap.Height / _rawLogoBitmap.Width)));
            if (h > 1080)
            {
                h = 1080;
                w = Math.Max(2, (int)Math.Round(h * ((double)_rawLogoBitmap.Width / _rawLogoBitmap.Height))) & ~1;
            }

            if (_rawLogoSequence != null && _rawLogoSequence.Length > 0)
            {
                var bakedFrames = new PreparedLogoOverlay[_rawLogoSequence.Length];
                for (int i = 0; i < _rawLogoSequence.Length; i++)
                {
                    bakedFrames[i] = BakePreparedLogoOverlay(_rawLogoSequence[i], w, h);
                }
                CurrentLogoSequence = bakedFrames;
                CurrentLogoOverlay = bakedFrames[0];
                LogoSequenceStartMs = Environment.TickCount64;
            }
            else
            {
                CurrentLogoSequence = null;
                CurrentLogoOverlay = BakePreparedLogoOverlay(_rawLogoBitmap, w, h);
            }

            UpdateLogoPreviewElement();
        }

        internal static int GetActiveLogoSequenceIndex(int frameCount)
        {
            if (frameCount <= 1) return 0;
            int fps = Math.Clamp(LogoSequenceFps, 1, 120);
            long elapsedMs = Math.Max(0L, Environment.TickCount64 - LogoSequenceStartMs);
            return (int)((elapsedMs * fps / 1000L) % frameCount);
        }

        internal static PreparedLogoOverlay? GetActiveLogoOverlay()
        {
            var seq = CurrentLogoSequence;
            if (seq != null && seq.Length > 1)
            {
                return seq[GetActiveLogoSequenceIndex(seq.Length)];
            }
            return CurrentLogoOverlay;
        }

        private void UpdateLogoPreviewElement()
        {
            if (PreviewLogoOverlay == null) return;
            var ov = CurrentLogoOverlay;
            if (!IsLogoOverlayEnabled || _logoPreviewImageSource == null || ov == null)
            {
                _logoPreviewTimer?.Stop();
                PreviewLogoOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            double w1080 = ov.Width;
            double h1080 = ov.Height;
            double x1080 = (1920.0 - w1080) * (Math.Clamp(LogoXPercent, 0.0, 100.0) / 100.0);
            double y1080 = (1080.0 - h1080) * (Math.Clamp(LogoYPercent, 0.0, 100.0) / 100.0);

            var previewSeq = _logoPreviewSequence;
            if (previewSeq != null && previewSeq.Length > 1)
            {
                PreviewLogoOverlay.Source = previewSeq[GetActiveLogoSequenceIndex(previewSeq.Length)];
                if (_logoPreviewTimer == null)
                {
                    _logoPreviewTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render);
                    _logoPreviewTimer.Tick += (_, _) =>
                    {
                        var seq = _logoPreviewSequence;
                        if (!IsLogoOverlayEnabled || seq == null || seq.Length <= 1 || PreviewLogoOverlay == null)
                        {
                            _logoPreviewTimer?.Stop();
                            return;
                        }
                        PreviewLogoOverlay.Source = seq[GetActiveLogoSequenceIndex(seq.Length)];
                    };
                }
                _logoPreviewTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(LogoSequenceFps, 1, 120));
                if (!_logoPreviewTimer.IsEnabled)
                    _logoPreviewTimer.Start();
            }
            else
            {
                _logoPreviewTimer?.Stop();
                PreviewLogoOverlay.Source = _logoPreviewImageSource;
            }

            PreviewLogoOverlay.Width = w1080 * 0.25;
            PreviewLogoOverlay.Height = h1080 * 0.25;
            System.Windows.Controls.Canvas.SetLeft(PreviewLogoOverlay, x1080 * 0.25);
            System.Windows.Controls.Canvas.SetTop(PreviewLogoOverlay, y1080 * 0.25);
            PreviewLogoOverlay.Opacity = Math.Clamp(LogoOpacityPercent / 100.0, 0.0, 1.0);
            PreviewLogoOverlay.Visibility = Visibility.Visible;
        }

        private void CmbLogoPosition_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingLogoUi || !IsLoaded) return;
            string preset = (CmbLogoPosition.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Top-Right";
            if (preset == "Custom")
            {
                SaveCurrentSettings();
                return;
            }

            double targetX = LogoXPercent;
            double targetY = LogoYPercent;
            switch (preset)
            {
                case "Top-Right":    targetX = 96.0; targetY = 4.0; break;
                case "Top-Left":     targetX = 4.0;  targetY = 4.0; break;
                case "Bottom-Right": targetX = 96.0; targetY = 94.0; break;
                case "Bottom-Left":  targetX = 4.0;  targetY = 94.0; break;
                case "Center":       targetX = 50.0; targetY = 50.0; break;
            }

            _isUpdatingLogoUi = true;
            try
            {
                LogoXPercent = targetX;
                LogoYPercent = targetY;
                if (SldLogoX != null) SldLogoX.Value = targetX;
                if (TxtLogoXVal != null) TxtLogoXVal.Text = $"{(int)Math.Round(targetX)}%";
                if (SldLogoY != null) SldLogoY.Value = targetY;
                if (TxtLogoYVal != null) TxtLogoYVal.Text = $"{(int)Math.Round(targetY)}%";
            }
            finally
            {
                _isUpdatingLogoUi = false;
            }

            UpdateLogoPreviewElement();
            SaveCurrentSettings();
        }

        private void SldLogoSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            LogoSizePercent = e.NewValue;
            if (TxtLogoSizeVal != null) TxtLogoSizeVal.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (_isUpdatingLogoUi || !IsLoaded) return;
            RebuildPreparedLogo();
            SaveCurrentSettings();
        }

        private void SldLogoOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            LogoOpacityPercent = e.NewValue;
            if (TxtLogoOpacityVal != null) TxtLogoOpacityVal.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (_isUpdatingLogoUi || !IsLoaded) return;
            UpdateLogoPreviewElement();
            SaveCurrentSettings();
        }

        private void SldLogoPos_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SldLogoX != null) LogoXPercent = SldLogoX.Value;
            if (SldLogoY != null) LogoYPercent = SldLogoY.Value;
            if (TxtLogoXVal != null) TxtLogoXVal.Text = $"{(int)Math.Round(LogoXPercent)}%";
            if (TxtLogoYVal != null) TxtLogoYVal.Text = $"{(int)Math.Round(LogoYPercent)}%";
            if (_isUpdatingLogoUi || !IsLoaded) return;

            if (CmbLogoPosition != null)
            {
                _isUpdatingLogoUi = true;
                try
                {
                    foreach (ComboBoxItem item in CmbLogoPosition.Items)
                    {
                        if (string.Equals(item.Content?.ToString(), "Custom", StringComparison.OrdinalIgnoreCase))
                        {
                            CmbLogoPosition.SelectedItem = item;
                            break;
                        }
                    }
                }
                finally
                {
                    _isUpdatingLogoUi = false;
                }
            }

            UpdateLogoPreviewElement();
            SaveCurrentSettings();
        }

        private static unsafe void BlendPreparedRegionUyvy(byte* uyvyBuffer, int startX, int startY, int boxW, int boxH, byte[] yArr, byte[] uArr, byte[] vArr, byte[] aArr, double opacityPercent)
        {
            if (boxW <= 0 || boxH <= 0) return;
            int opacityScale = (int)Math.Round(Math.Clamp(opacityPercent, 0.0, 100.0) * 255.0 / 100.0);
            if (opacityScale <= 0) return;

            startX &= ~1;
            if (startX < 0) startX = 0;
            if (startY < 0) startY = 0;
            if (startX + boxW > 1920) startX = (1920 - boxW) & ~1;
            if (startY + boxH > 1080) startY = 1080 - boxH;

            fixed (byte* pY = yArr)
            fixed (byte* pU = uArr)
            fixed (byte* pV = vArr)
            fixed (byte* pA = aArr)
            {
                for (int ly = 0; ly < boxH; ly++)
                {
                    byte* dstRow = uyvyBuffer + ((startY + ly) * 1920 + startX) * 2;
                    int rowIdx = ly * boxW;
                    for (int lx = 0; lx + 1 < boxW; lx += 2)
                    {
                        int idx0 = rowIdx + lx;
                        int idx1 = idx0 + 1;
                        int a0 = (pA[idx0] * opacityScale + 127) / 255;
                        int a1 = (pA[idx1] * opacityScale + 127) / 255;
                        if ((a0 | a1) == 0) continue;

                        int d = lx * 2;
                        if (a0 > 0)
                        {
                            dstRow[d + 1] = (byte)((pY[idx0] * a0 + dstRow[d + 1] * (255 - a0) + 127) / 255);
                        }
                        if (a1 > 0)
                        {
                            dstRow[d + 3] = (byte)((pY[idx1] * a1 + dstRow[d + 3] * (255 - a1) + 127) / 255);
                        }

                        // Alpha-weighted chroma so transparent neighbours don't desaturate/fringe glyph edges
                        int wSum = pA[idx0] + pA[idx1];
                        int uLogo, vLogo;
                        if (wSum > 0)
                        {
                            uLogo = (pU[idx0] * pA[idx0] + pU[idx1] * pA[idx1]) / wSum;
                            vLogo = (pV[idx0] * pA[idx0] + pV[idx1] * pA[idx1]) / wSum;
                        }
                        else
                        {
                            uLogo = 128; vLogo = 128;
                        }
                        int aChroma = (a0 + a1 + 2 * Math.Max(a0, a1)) >> 2;
                        if (aChroma > 0)
                        {
                            dstRow[d + 0] = (byte)((uLogo * aChroma + dstRow[d + 0] * (255 - aChroma) + 127) / 255);
                            dstRow[d + 2] = (byte)((vLogo * aChroma + dstRow[d + 2] * (255 - aChroma) + 127) / 255);
                        }
                    }
                }
            }
        }

        public static unsafe void ApplyLogoOverlayUyvy(byte* uyvyBuffer)
        {
            if (uyvyBuffer == null) return;

            if (IsNdiOverlayEnabled)
            {
                var ndiOv = CurrentNdiOverlay;
                if (ndiOv != null)
                {
                    BlendPreparedRegionUyvy(uyvyBuffer, ndiOv.StartX, ndiOv.StartY, ndiOv.Width, ndiOv.Height, ndiOv.Y, ndiOv.U, ndiOv.V, ndiOv.A, NdiOverlayOpacityPercent);
                }
            }

            if (IsHtmlOverlayEnabled)
            {
                var htmlOv = CurrentHtmlOverlay;
                if (htmlOv != null)
                {
                    BlendPreparedRegionUyvy(uyvyBuffer, htmlOv.StartX, htmlOv.StartY, htmlOv.Width, htmlOv.Height, htmlOv.Y, htmlOv.U, htmlOv.V, htmlOv.A, HtmlOverlayOpacityPercent);
                }
            }

            if (IsLogoOverlayEnabled)
            {
                var ov = GetActiveLogoOverlay();
                if (ov != null && ov.Width > 0 && ov.Height > 0)
                {
                    int startX = (int)Math.Round((1920 - ov.Width) * (Math.Clamp(LogoXPercent, 0.0, 100.0) / 100.0));
                    int startY = (int)Math.Round((1080 - ov.Height) * (Math.Clamp(LogoYPercent, 0.0, 100.0) / 100.0));
                    BlendPreparedRegionUyvy(uyvyBuffer, startX, startY, ov.Width, ov.Height, ov.Y, ov.U, ov.V, ov.A, LogoOpacityPercent);
                }
            }
        }

        public static unsafe void ApplyLogoOverlayUyvy(byte[]? uyvyBytes)
        {
            if ((!IsLogoOverlayEnabled && !IsHtmlOverlayEnabled && !IsNdiOverlayEnabled) || uyvyBytes == null || uyvyBytes.Length < 1920 * 1080 * 2) return;
            fixed (byte* ptr = uyvyBytes)
            {
                ApplyLogoOverlayUyvy(ptr);
            }
        }

        private void BtnBrowseHtmlOverlay_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select HTML CG Overlay File",
                Filter = "HTML Files (*.html;*.htm)|*.html;*.htm|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtHtmlOverlayUrl.Text = dlg.FileName;
                SaveCurrentSettings();
            }
        }

        private void TxtHtmlOverlayUrl_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                BtnConnectHtmlOverlay_Click(sender, e);
            }
        }

        private async void BtnConnectHtmlOverlay_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtHtmlOverlayUrl.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                BtnBrowseHtmlOverlay_Click(sender, e);
                url = TxtHtmlOverlayUrl.Text.Trim();
                if (string.IsNullOrEmpty(url)) return;
            }
            SaveCurrentSettings();
            await _htmlOverlaySource.StartAsync(url);
            SaveCurrentSettings();
        }

        private void BtnReloadHtmlOverlay_Click(object sender, RoutedEventArgs e)
        {
            _htmlOverlaySource?.Reload();
        }

        private void BtnDisconnectHtmlOverlay_Click(object sender, RoutedEventArgs e)
        {
            _htmlOverlaySource?.Stop();
            SetHtmlOverlayActiveState(false);
            SaveCurrentSettings();
        }

        private void CmbHtmlOverlayFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_htmlOverlaySource != null && CmbHtmlOverlayFps.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out int fps))
            {
                _htmlOverlaySource.TargetFps = fps;
                Log($"[HtmlOverlay] TargetFps set to {fps}");
            }
        }

        private void CmbHtmlSourceFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_htmlSource != null && CmbHtmlSourceFps.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out int fps))
            {
                _htmlSource.TargetFps = fps;
                Log($"[HtmlSource] TargetFps set to {fps}");
            }
        }

        private async void BtnToggleHtmlOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (!IsHtmlOverlayEnabled && (_htmlOverlaySource == null || !_htmlOverlaySource.IsRunning))
            {
                string url = TxtHtmlOverlayUrl.Text.Trim();
                if (!string.IsNullOrEmpty(url))
                {
                    await _htmlOverlaySource!.StartAsync(url);
                }
                else
                {
                    BtnConnectHtmlOverlay_Click(sender, e);
                    return;
                }
            }
            SetHtmlOverlayActiveState(!IsHtmlOverlayEnabled);
            SaveCurrentSettings();
        }

        private void SetHtmlOverlayActiveState(bool enabled)
        {
            IsHtmlOverlayEnabled = enabled && _htmlOverlaySource != null && _htmlOverlaySource.IsRunning;
            _isUpdatingLogoUi = true;
            try
            {
                if (BtnToggleHtmlOverlay != null)
                {
                    BtnToggleHtmlOverlay.Content = IsHtmlOverlayEnabled ? "CG: ON" : "CG: OFF";
                    BtnToggleHtmlOverlay.Background = IsHtmlOverlayEnabled
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2e, 0x7d, 0x32))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x42, 0x42));
                }
            }
            finally
            {
                _isUpdatingLogoUi = false;
            }
            UpdateHtmlOverlayPreviewElement();
        }

        private void UpdateHtmlOverlayPreviewElement()
        {
            if (PreviewHtmlOverlay == null) return;
            if (!IsHtmlOverlayEnabled)
            {
                PreviewHtmlOverlay.Visibility = Visibility.Collapsed;
                return;
            }
            PreviewHtmlOverlay.Opacity = Math.Clamp(HtmlOverlayOpacityPercent / 100.0, 0.0, 1.0);
            PreviewHtmlOverlay.Visibility = Visibility.Visible;
        }

        private void SldHtmlOverlayOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            HtmlOverlayOpacityPercent = e.NewValue;
            if (TxtHtmlOverlayOpacityVal != null) TxtHtmlOverlayOpacityVal.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (_isUpdatingLogoUi || !IsLoaded) return;
            UpdateHtmlOverlayPreviewElement();
            SaveCurrentSettings();
        }

        // ---------------- NDI CG Overlay Controls ----------------
        private bool _pendingHtmlOverlayEnabled;
        private string? _pendingNdiOverlaySourceName;

        private void SetupNdiOverlaySource()
        {
            _ndiOverlaySource.OnLog = Log;
            _ndiOverlaySource.OnOverlayFrameUpdated = (frame) => {
                CurrentNdiOverlay = frame;
            };
            _ndiOverlaySource.OnPreviewBufferUpdated = (buf) => {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    _bmpNdiOverlay.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                    if (TxtNdiOverlayPreviewPlaceholder != null && TxtNdiOverlayPreviewPlaceholder.Visibility != Visibility.Collapsed)
                        TxtNdiOverlayPreviewPlaceholder.Visibility = Visibility.Collapsed;
                });
            };
            _ndiOverlaySource.OnStatusChanged = (status, isLive, isError) => {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    if (TxtNdiOverlayStatus != null)
                    {
                        TxtNdiOverlayStatus.Text = "Status: " + status;
                        TxtNdiOverlayStatus.Foreground = isError
                            ? System.Windows.Media.Brushes.IndianRed
                            : (isLive ? System.Windows.Media.Brushes.LimeGreen : (System.Windows.Media.Brush)FindResource("TextDim"));
                    }
                    if (!isLive && TxtNdiOverlayPreviewPlaceholder != null)
                        TxtNdiOverlayPreviewPlaceholder.Visibility = Visibility.Visible;

                    bool offline = status == "OFFLINE";
                    BtnConnectNdiOverlay.IsEnabled = offline;
                    BtnDisconnectNdiOverlay.IsEnabled = !offline;
                });
            };
        }

        private async void BtnRefreshNdiOverlay_Click(object sender, RoutedEventArgs e) => await RefreshNdiOverlaySourcesAsync();

        private async Task RefreshNdiOverlaySourcesAsync()
        {
            string? previous = (CmbNdiOverlaySources.SelectedItem as NdiSourceInfo)?.Name ?? _pendingNdiOverlaySourceName;
            BtnRefreshNdiOverlay.IsEnabled = false;
            if (TxtNdiOverlayStatus != null) TxtNdiOverlayStatus.Text = "Status: Searching...";
            try
            {
                var sources = await Task.Run(() => NdiSource.FindSources(2000, Log));
                if (NdiOut.IsRunning)
                    sources.RemoveAll(s => s.Name.Contains(NdiOut.StreamName, StringComparison.OrdinalIgnoreCase));

                CmbNdiOverlaySources.ItemsSource = sources;
                CmbNdiOverlaySources.DisplayMemberPath = "Name";
                var match = sources.FirstOrDefault(s => s.Name == previous);
                CmbNdiOverlaySources.SelectedItem = match ?? (sources.Count > 0 ? sources[0] : null);
                Log($"NDI Overlay: found {sources.Count} source(s)");
                if (!_ndiOverlaySource.IsRunning && TxtNdiOverlayStatus != null)
                    TxtNdiOverlayStatus.Text = sources.Count > 0 ? "Status: OFFLINE" : "Status: No sources";
            }
            catch (Exception ex)
            {
                Log("NDI Overlay refresh error: " + ex.Message);
            }
            finally
            {
                BtnRefreshNdiOverlay.IsEnabled = true;
            }
        }

        private void BtnConnectNdiOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (CmbNdiOverlaySources.SelectedItem is not NdiSourceInfo info)
            {
                if (TxtNdiOverlayStatus != null) TxtNdiOverlayStatus.Text = "Status: Select source";
                return;
            }
            _pendingNdiOverlaySourceName = info.Name;
            BtnConnectNdiOverlay.IsEnabled = false;
            BtnDisconnectNdiOverlay.IsEnabled = true;
            _ndiOverlaySource.Start(info.Name, info.Url);
            SaveCurrentSettings();
        }

        private void BtnDisconnectNdiOverlay_Click(object sender, RoutedEventArgs e)
        {
            _ndiOverlaySource.Stop();
            SetNdiOverlayActiveState(false);
            BtnConnectNdiOverlay.IsEnabled = true;
            BtnDisconnectNdiOverlay.IsEnabled = false;
            SaveCurrentSettings();
        }

        private async void BtnToggleNdiOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (!IsNdiOverlayEnabled && !_ndiOverlaySource.IsRunning)
            {
                if (CmbNdiOverlaySources.SelectedItem is NdiSourceInfo)
                {
                    BtnConnectNdiOverlay_Click(sender, e);
                    return;
                }
                else
                {
                    await RefreshNdiOverlaySourcesAsync();
                    if (CmbNdiOverlaySources.SelectedItem is NdiSourceInfo)
                    {
                        BtnConnectNdiOverlay_Click(sender, e);
                        return;
                    }
                }
            }
            SetNdiOverlayActiveState(!IsNdiOverlayEnabled);
            SaveCurrentSettings();
        }

        private void SetNdiOverlayActiveState(bool enabled)
        {
            IsNdiOverlayEnabled = enabled && _ndiOverlaySource != null && _ndiOverlaySource.IsRunning;
            _isUpdatingLogoUi = true;
            try
            {
                if (BtnToggleNdiOverlay != null)
                {
                    BtnToggleNdiOverlay.Content = IsNdiOverlayEnabled ? "CG: ON" : "CG: OFF";
                    BtnToggleNdiOverlay.Background = IsNdiOverlayEnabled
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2e, 0x7d, 0x32))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x42, 0x42));
                }
            }
            finally
            {
                _isUpdatingLogoUi = false;
            }
            UpdateNdiOverlayPreviewElement();
        }

        private void UpdateNdiOverlayPreviewElement()
        {
            if (PreviewNdiOverlay == null) return;
            if (!IsNdiOverlayEnabled)
            {
                PreviewNdiOverlay.Visibility = Visibility.Collapsed;
                return;
            }
            PreviewNdiOverlay.Opacity = Math.Clamp(NdiOverlayOpacityPercent / 100.0, 0.0, 1.0);
            PreviewNdiOverlay.Visibility = Visibility.Visible;
        }

        private void SldNdiOverlayOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            NdiOverlayOpacityPercent = e.NewValue;
            if (TxtNdiOverlayOpacityVal != null) TxtNdiOverlayOpacityVal.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (_isUpdatingLogoUi || !IsLoaded) return;
            UpdateNdiOverlayPreviewElement();
            SaveCurrentSettings();
        }
    }
}
