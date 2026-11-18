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
                Title = "Select PGM Logo Image or Video",
                Filter = "Logo Image / Video (*.png;*.webp;*.bmp;*.jpg;*.jpeg;*.gif;*.mp4;*.mov;*.webm;*.mkv;*.avi)|*.png;*.webp;*.bmp;*.jpg;*.jpeg;*.gif;*.mp4;*.mov;*.webm;*.mkv;*.avi|Image Files (*.png;*.webp;*.bmp;*.jpg;*.jpeg;*.gif)|*.png;*.webp;*.bmp;*.jpg;*.jpeg;*.gif|Video Files (*.mp4;*.mov;*.webm;*.mkv;*.avi)|*.mp4;*.mov;*.webm;*.mkv;*.avi|All Files (*.*)|*.*"
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

        private static int ReadExactBytes(Stream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int n = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (n == 0) break;
                totalRead += n;
            }
            return totalRead;
        }

        private static (System.Drawing.Bitmap[] RawFrames, System.Windows.Media.Imaging.BitmapSource[] PreviewFrames) DecodeLogoVideoFile(string videoPath)
        {
            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
                ffmpegPath = "ffmpeg";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -loglevel error -i \"{videoPath}\" -vf \"scale='min(480,iw)':-2\" -frames:v 240 -c:v bmp -pix_fmt bgra -f image2pipe -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg to decode video logo.");
            var stderrTask = proc.StandardError.ReadToEndAsync();
            Stream stdout = proc.StandardOutput.BaseStream;

            var rawFrames = new List<System.Drawing.Bitmap>();
            var previewFrames = new List<System.Windows.Media.Imaging.BitmapSource>();
            byte[] header = new byte[54];

            try
            {
                while (true)
                {
                    int headerRead = ReadExactBytes(stdout, header, 0, 54);
                    if (headerRead == 0) break;
                    if (headerRead < 54)
                        throw new InvalidOperationException("Unexpected end of FFmpeg BMP stream header.");
                    if (header[0] != (byte)'B' || header[1] != (byte)'M')
                        throw new InvalidOperationException("Invalid BMP frame signature from FFmpeg.");

                    int fileSize = BitConverter.ToInt32(header, 2);
                    int dataOffset = BitConverter.ToInt32(header, 10);
                    int width = BitConverter.ToInt32(header, 18);
                    int signedHeight = BitConverter.ToInt32(header, 22);
                    int height = Math.Abs(signedHeight);
                    bool bottomUp = signedHeight > 0;

                    if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                        throw new InvalidOperationException($"Unsupported decoded video frame size: {width}x{height}");

                    int extraHeader = dataOffset - 54;
                    if (extraHeader > 0)
                    {
                        byte[] skipBuf = new byte[extraHeader];
                        if (ReadExactBytes(stdout, skipBuf, 0, extraHeader) < extraHeader)
                            throw new InvalidOperationException("Unexpected end of FFmpeg BMP extended header.");
                    }

                    int rowStride = (width * 4 + 3) & ~3;
                    int expectedMin = rowStride * height;
                    int pixelDataSize = Math.Max(expectedMin, fileSize - dataOffset);
                    byte[] rawPixels = new byte[pixelDataSize];
                    if (ReadExactBytes(stdout, rawPixels, 0, pixelDataSize) < expectedMin)
                        throw new InvalidOperationException("Unexpected end of FFmpeg BMP pixel data.");

                    int tightStride = width * 4;
                    byte[] topDown = new byte[tightStride * height];
                    for (int y = 0; y < height; y++)
                    {
                        int srcY = bottomUp ? (height - 1 - y) : y;
                        Buffer.BlockCopy(rawPixels, srcY * rowStride, topDown, y * tightStride, tightStride);
                    }

                    var rawBmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var bmpData = rawBmp.LockBits(
                        new System.Drawing.Rectangle(0, 0, width, height),
                        System.Drawing.Imaging.ImageLockMode.WriteOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        Marshal.Copy(topDown, 0, bmpData.Scan0, topDown.Length);
                    }
                    finally
                    {
                        rawBmp.UnlockBits(bmpData);
                    }

                    var previewSrc = System.Windows.Media.Imaging.BitmapSource.Create(
                        width, height, 96, 96,
                        System.Windows.Media.PixelFormats.Bgra32, null, topDown, tightStride);
                    previewSrc.Freeze();

                    rawFrames.Add(rawBmp);
                    previewFrames.Add(previewSrc);
                }
            }
            catch
            {
                foreach (var bmp in rawFrames) bmp.Dispose();
                throw;
            }

            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(); } catch { }
            }

            string stderr = stderrTask.GetAwaiter().GetResult();
            if (rawFrames.Count == 0)
            {
                string detail = string.IsNullOrWhiteSpace(stderr) ? "No video frames were decoded." : stderr.Trim();
                throw new InvalidOperationException(detail);
            }

            return (rawFrames.ToArray(), previewFrames.ToArray());
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

        private void ApplyDecodedLogoSequence(
            System.Drawing.Bitmap[] rawFrames,
            System.Windows.Media.Imaging.BitmapSource[] previewFrames,
            string path,
            string labelName)
        {
            DisposeRawLogoResources();
            _rawLogoSequence = rawFrames;
            _rawLogoBitmap = _rawLogoSequence[0];
            _logoPreviewSequence = previewFrames;
            _logoPreviewImageSource = _logoPreviewSequence[0];
            _logoFilePath = path;
            LogoSequenceStartMs = Environment.TickCount64;

            if (TxtLogoFileName != null)
            {
                TxtLogoFileName.Text = $"{labelName} ({_rawLogoSequence.Length} frames)";
                TxtLogoFileName.ToolTip = path;
            }

            RebuildPreparedLogo();
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
                    var previewFrames = new List<System.Windows.Media.Imaging.BitmapSource>(frameFiles.Count);
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

                    string folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrEmpty(folderName)) folderName = path;
                    ApplyDecodedLogoSequence(rawFrames.ToArray(), previewFrames.ToArray(), path, folderName);
                    return true;
                }

                if (!File.Exists(path)) return false;

                string ext = Path.GetExtension(path);
                if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    var (videoRawFrames, videoPreviewFrames) = DecodeLogoVideoFile(path);
                    ApplyDecodedLogoSequence(videoRawFrames, videoPreviewFrames, path, Path.GetFileName(path));
                    return true;
                }

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
                System.Windows.MessageBox.Show("Failed to load logo media: " + ex.Message, "Logo Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        private static readonly object _mixLock = new();
        public static volatile bool IsMixEnabled = false;
        public static volatile TransitionType ActiveTransitionType = TransitionType.Cut;
        private static volatile TransitionType _runningTransitionType = TransitionType.Mix;
        private static volatile TransitionType _runningPreviewTransitionType = TransitionType.Mix;
        private static volatile bool _isMixActive = false;
        private static long _mixStartTicks = 0;
        public static double MixDurationSeconds = 0.5;
        private static byte[]? _lastCleanUyvy1080;
        private static byte[]? _mixFromUyvy1080;

        private static readonly byte[] _lastCleanPreview480 = new byte[480 * 270 * 4];
        private static readonly byte[] _mixFromPreview480 = new byte[480 * 270 * 4];
        private static readonly byte[] _mixWorkPreview480 = new byte[480 * 270 * 4];
        private static volatile bool _hasLastPreview480 = false;
        private static volatile bool _isPreviewMixActive = false;
        private static long _previewMixStartTicks = 0;

        internal static volatile bool _isAudioMixActive = false;
        internal static long _audioMixStartTicks = 0;
        internal static int _prevAudioSourceType = 0;
        internal static string _prevAudioInputName = "";

        internal static volatile PreparedLogoOverlay[]? StingerFrames1080 = null;
        internal static volatile byte[][]? StingerPreviewFrames480 = null;
        public static string StingerFolderPath = "";
        public static double StingerCutPoint = 0.5;

        private static unsafe byte[] BakePreviewBgra480(System.Drawing.Bitmap rawBmp)
        {
            const int w = 480;
            const int h = 270;
            byte[] bgra = new byte[w * h * 4];
            using var scaled = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(scaled))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(rawBmp, 0, 0, w, h);
            }
            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            var data = scaled.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                byte* scan0 = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy((IntPtr)(scan0 + y * stride), bgra, y * w * 4, w * 4);
                }
            }
            finally
            {
                scaled.UnlockBits(data);
            }
            return bgra;
        }

        public bool LoadStingerSequenceFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return false;

            var allowedExt = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".bmp", ".tif", ".tiff", ".jpg", ".jpeg"
            };

            var files = Directory.GetFiles(folderPath)
                .Where(f => allowedExt.Contains(Path.GetExtension(f)))
                .ToArray();
            Array.Sort(files, CompareNaturalFileNames);

            if (files.Length == 0)
            {
                Log($"Stinger folder has no image frames: {folderPath}");
                return false;
            }

            const int maxFrames = 300;
            if (files.Length > maxFrames)
                files = files.Take(maxFrames).ToArray();

            var frames1080 = new PreparedLogoOverlay[files.Length];
            var frames480 = new byte[files.Length][];

            for (int i = 0; i < files.Length; i++)
            {
                using var bmp = new System.Drawing.Bitmap(files[i]);
                frames1080[i] = BakePreparedLogoOverlay(bmp, 1920, 1080);
                frames480[i] = BakePreviewBgra480(bmp);
            }

            StingerFrames1080 = frames1080;
            StingerPreviewFrames480 = frames480;
            StingerFolderPath = folderPath;

            if (BtnStingerFolder != null)
            {
                BtnStingerFolder.ToolTip = $"Stinger Sequence: {folderPath} ({files.Length} frames)";
            }

            Log($"Loaded Stinger sequence ({files.Length} frames) from {folderPath}");
            return true;
        }

        public static int WipeBorderWidth = 8;
        public static byte WipeBorderR = 255;
        public static byte WipeBorderG = 255;
        public static byte WipeBorderB = 255;
        public static byte WipeBorderY = 235;
        public static byte WipeBorderU = 128;
        public static byte WipeBorderV = 128;

        public void SetWipeBorderColorHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return;
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                SetWipeBorderColor(c.R, c.G, c.B);
            }
            catch { }
        }

        public void SetWipeBorderColor(byte r, byte g, byte b)
        {
            WipeBorderR = r;
            WipeBorderG = g;
            WipeBorderB = b;

            int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
            int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
            int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

            WipeBorderY = (byte)Math.Clamp(y, 16, 235);
            WipeBorderU = (byte)Math.Clamp(u, 16, 240);
            WipeBorderV = (byte)Math.Clamp(v, 16, 240);

            if (BtnWipeBorderColor != null)
            {
                BtnWipeBorderColor.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            }
        }

        private static unsafe void FillBgraSpan(byte* dst, int pixelCount, byte b, byte g, byte r)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                dst[i * 4 + 0] = b;
                dst[i * 4 + 1] = g;
                dst[i * 4 + 2] = r;
                dst[i * 4 + 3] = 255;
            }
        }

        public static void BeginMixIfEnabled()
        {
            if (!IsMixEnabled || ActiveTransitionType == TransitionType.Cut) return;
            lock (_mixLock)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                TransitionType transition = ActiveTransitionType;

                _prevAudioSourceType = ActiveSourceType;
                _prevAudioInputName = ActiveInputName ?? "";
                _audioMixStartTicks = nowTicks;
                _isAudioMixActive = true;

                if (_lastCleanUyvy1080 != null)
                {
                    if (_mixFromUyvy1080 == null || _mixFromUyvy1080.Length != 1920 * 1080 * 2)
                        _mixFromUyvy1080 = new byte[1920 * 1080 * 2];

                    Buffer.BlockCopy(_lastCleanUyvy1080, 0, _mixFromUyvy1080, 0, _mixFromUyvy1080.Length);
                    _runningTransitionType = transition;
                    _mixStartTicks = nowTicks;
                    _isMixActive = true;
                }

                if (_hasLastPreview480)
                {
                    Buffer.BlockCopy(_lastCleanPreview480, 0, _mixFromPreview480, 0, _lastCleanPreview480.Length);
                    _runningPreviewTransitionType = transition;
                    _previewMixStartTicks = nowTicks;
                    _isPreviewMixActive = true;
                }
            }
        }

        private static unsafe void ApplyPreviewTransition480(byte* pFrom, byte* pTo, byte* pOut, double progress, TransitionType type)
        {
            const int width = 480;
            const int height = 270;
            const int rowBytes = width * 4;
            const int totalBytes = rowBytes * height;

            int bw = WipeBorderWidth > 0 ? Math.Max(1, (int)Math.Round(WipeBorderWidth / 4.0)) : 0;
            byte br = WipeBorderR, bg = WipeBorderG, bb = WipeBorderB;

            switch (type)
            {
                case TransitionType.WipeLR:
                {
                    int splitX = Math.Clamp((int)Math.Round(width * progress), 0, width);
                    int splitBytes = splitX * 4;
                    int rightBytes = rowBytes - splitBytes;
                    int bx1 = Math.Max(0, splitX - bw / 2);
                    int bx2 = Math.Min(width, bx1 + bw);
                    int bLen = bx2 - bx1;
                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = y * rowBytes;
                        if (splitBytes > 0) Buffer.MemoryCopy(pTo + rowOffset, pOut + rowOffset, splitBytes, splitBytes);
                        if (rightBytes > 0) Buffer.MemoryCopy(pFrom + rowOffset + splitBytes, pOut + rowOffset + splitBytes, rightBytes, rightBytes);
                        if (bw > 0 && bLen > 0) FillBgraSpan(pOut + rowOffset + bx1 * 4, bLen, bb, bg, br);
                    }
                    break;
                }
                case TransitionType.WipeRL:
                {
                    int splitX = Math.Clamp((int)Math.Round(width * (1.0 - progress)), 0, width);
                    int splitBytes = splitX * 4;
                    int rightBytes = rowBytes - splitBytes;
                    int bx1 = Math.Max(0, splitX - bw / 2);
                    int bx2 = Math.Min(width, bx1 + bw);
                    int bLen = bx2 - bx1;
                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = y * rowBytes;
                        if (splitBytes > 0) Buffer.MemoryCopy(pFrom + rowOffset, pOut + rowOffset, splitBytes, splitBytes);
                        if (rightBytes > 0) Buffer.MemoryCopy(pTo + rowOffset + splitBytes, pOut + rowOffset + splitBytes, rightBytes, rightBytes);
                        if (bw > 0 && bLen > 0) FillBgraSpan(pOut + rowOffset + bx1 * 4, bLen, bb, bg, br);
                    }
                    break;
                }
                case TransitionType.WipeTB:
                {
                    int splitY = Math.Clamp((int)Math.Round(height * progress), 0, height);
                    int topBytes = splitY * rowBytes;
                    int bottomBytes = totalBytes - topBytes;
                    if (topBytes > 0) Buffer.MemoryCopy(pTo, pOut, topBytes, topBytes);
                    if (bottomBytes > 0) Buffer.MemoryCopy(pFrom + topBytes, pOut + topBytes, bottomBytes, bottomBytes);
                    if (bw > 0)
                    {
                        int by1 = Math.Max(0, splitY - bw / 2);
                        int by2 = Math.Min(height, by1 + bw);
                        for (int y = by1; y < by2; y++) FillBgraSpan(pOut + y * rowBytes, width, bb, bg, br);
                    }
                    break;
                }
                case TransitionType.WipeBT:
                {
                    int splitY = Math.Clamp((int)Math.Round(height * (1.0 - progress)), 0, height);
                    int topBytes = splitY * rowBytes;
                    int bottomBytes = totalBytes - topBytes;
                    if (topBytes > 0) Buffer.MemoryCopy(pFrom, pOut, topBytes, topBytes);
                    if (bottomBytes > 0) Buffer.MemoryCopy(pTo + topBytes, pOut + topBytes, bottomBytes, bottomBytes);
                    if (bw > 0)
                    {
                        int by1 = Math.Max(0, splitY - bw / 2);
                        int by2 = Math.Min(height, by1 + bw);
                        for (int y = by1; y < by2; y++) FillBgraSpan(pOut + y * rowBytes, width, bb, bg, br);
                    }
                    break;
                }
                case TransitionType.BoxOut:
                {
                    int halfW = Math.Clamp((int)Math.Round((width / 2.0) * progress), 0, width / 2);
                    int halfH = Math.Clamp((int)Math.Round((height / 2.0) * progress), 0, height / 2);
                    int x1 = width / 2 - halfW;
                    int x2 = width / 2 + halfW;
                    int x1Bytes = x1 * 4;
                    int x2Bytes = x2 * 4;
                    int boxBytes = x2Bytes - x1Bytes;
                    int rightBytes = rowBytes - x2Bytes;
                    int y1 = height / 2 - halfH;
                    int y2 = height / 2 + halfH;
                    int outerX1 = Math.Max(0, x1 - bw);
                    int outerX2 = Math.Min(width, x2 + bw);
                    int outerY1 = Math.Max(0, y1 - bw);
                    int outerY2 = Math.Min(height, y2 + bw);
                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = y * rowBytes;
                        if (y < y1 || y >= y2)
                        {
                            Buffer.MemoryCopy(pFrom + rowOffset, pOut + rowOffset, rowBytes, rowBytes);
                            if (bw > 0 && y >= outerY1 && y < outerY2 && outerX2 > outerX1)
                            {
                                FillBgraSpan(pOut + rowOffset + outerX1 * 4, outerX2 - outerX1, bb, bg, br);
                            }
                        }
                        else
                        {
                            if (x1Bytes > 0) Buffer.MemoryCopy(pFrom + rowOffset, pOut + rowOffset, x1Bytes, x1Bytes);
                            if (boxBytes > 0) Buffer.MemoryCopy(pTo + rowOffset + x1Bytes, pOut + rowOffset + x1Bytes, boxBytes, boxBytes);
                            if (rightBytes > 0) Buffer.MemoryCopy(pFrom + rowOffset + x2Bytes, pOut + rowOffset + x2Bytes, rightBytes, rightBytes);
                            if (bw > 0)
                            {
                                if (x1 > outerX1) FillBgraSpan(pOut + rowOffset + outerX1 * 4, x1 - outerX1, bb, bg, br);
                                if (outerX2 > x2) FillBgraSpan(pOut + rowOffset + x2 * 4, outerX2 - x2, bb, bg, br);
                            }
                        }
                    }
                    break;
                }
                case TransitionType.Stinger:
                {
                    var frames = StingerPreviewFrames480;
                    if (frames != null && frames.Length > 0)
                    {
                        byte* baseBg = (progress < StingerCutPoint) ? pFrom : pTo;
                        Buffer.MemoryCopy(baseBg, pOut, totalBytes, totalBytes);

                        int idx = Math.Clamp((int)(progress * frames.Length), 0, frames.Length - 1);
                        byte[] ovBgra = frames[idx];
                        fixed (byte* pOv = ovBgra)
                        {
                            for (int i = 0; i < totalBytes; i += 4)
                            {
                                int a = pOv[i + 3];
                                if (a == 0) continue;
                                if (a == 255)
                                {
                                    pOut[i] = pOv[i];
                                    pOut[i + 1] = pOv[i + 1];
                                    pOut[i + 2] = pOv[i + 2];
                                }
                                else
                                {
                                    int invA = 255 - a;
                                    pOut[i]     = (byte)((pOut[i]     * invA + pOv[i]     * a + 128) >> 8);
                                    pOut[i + 1] = (byte)((pOut[i + 1] * invA + pOv[i + 1] * a + 128) >> 8);
                                    pOut[i + 2] = (byte)((pOut[i + 2] * invA + pOv[i + 2] * a + 128) >> 8);
                                }
                            }
                        }
                    }
                    else
                    {
                        goto default;
                    }
                    break;
                }
                default: // Mix
                {
                    int alpha = Math.Clamp((int)Math.Round(progress * 256.0), 0, 256);
                    int invAlpha = 256 - alpha;
                    for (int i = 0; i < totalBytes; i++)
                    {
                        pOut[i] = (byte)((pFrom[i] * invAlpha + pTo[i] * alpha + 128) >> 8);
                    }
                    break;
                }
            }
        }

        internal unsafe void WritePgmPreviewFrame(byte[]? bgra480)
        {
            var bmp = _bmpOutput;
            if (bmp == null || bgra480 == null || bgra480.Length < 480 * 270 * 4) return;

            const int len = 480 * 270 * 4;
            byte[] toDraw = bgra480;

            if (IsMixEnabled || _isPreviewMixActive)
            {
                lock (_mixLock)
                {
                    if (_isPreviewMixActive)
                    {
                        double elapsed = (Stopwatch.GetTimestamp() - _previewMixStartTicks) / (double)Stopwatch.Frequency;
                        double dur = Math.Clamp(MixDurationSeconds, 0.1, 10.0);
                        if (elapsed >= dur)
                        {
                            _isPreviewMixActive = false;
                        }
                        else
                        {
                            double progress = Math.Clamp(elapsed / dur, 0.0, 1.0);
                            fixed (byte* pFrom = _mixFromPreview480)
                            fixed (byte* pTo = bgra480)
                            fixed (byte* pOut = _mixWorkPreview480)
                            {
                                ApplyPreviewTransition480(pFrom, pTo, pOut, progress, _runningPreviewTransitionType);
                            }
                            toDraw = _mixWorkPreview480;
                        }
                    }

                    if (IsMixEnabled)
                    {
                        Buffer.BlockCopy(bgra480, 0, _lastCleanPreview480, 0, len);
                        _hasLastPreview480 = true;
                    }
                }
            }

            double ftb = GetFtbLevel();
            if (ftb > 0.001)
            {
                if (ftb >= 0.999)
                {
                    fixed (byte* pOut = _mixWorkPreview480)
                    {
                        uint* p32 = (uint*)pOut;
                        int pixels = len >> 2;
                        for (int i = 0; i < pixels; i++) p32[i] = 0xFF000000;
                    }
                    toDraw = _mixWorkPreview480;
                }
                else
                {
                    int keep = Math.Clamp((int)Math.Round((1.0 - ftb) * 256.0), 0, 256);
                    fixed (byte* pSrc = toDraw)
                    fixed (byte* pOut = _mixWorkPreview480)
                    {
                        for (int i = 0; i < len; i += 4)
                        {
                            pOut[i + 0] = (byte)((pSrc[i + 0] * keep) >> 8);
                            pOut[i + 1] = (byte)((pSrc[i + 1] * keep) >> 8);
                            pOut[i + 2] = (byte)((pSrc[i + 2] * keep) >> 8);
                            pOut[i + 3] = 255;
                        }
                    }
                    toDraw = _mixWorkPreview480;
                }
            }

            bmp.WritePixels(new Int32Rect(0, 0, 480, 270), toDraw, 480 * 4, 0);
        }

        private static unsafe void FillUyvySpan(byte* dst, int evenPixelCount, uint uyvyPattern)
        {
            uint* p32 = (uint*)dst;
            int pairs = evenPixelCount >> 1;
            for (int i = 0; i < pairs; i++)
            {
                p32[i] = uyvyPattern;
            }
        }

        private static unsafe void ApplyUyvyTransition1080(byte* pFrom, byte* uyvyBuffer, double progress, TransitionType type)
        {
            const int width = 1920;
            const int height = 1080;
            const int rowBytes = width * 2;
            int bw = WipeBorderWidth > 0 ? Math.Max(2, WipeBorderWidth & ~1) : 0;
            uint borderUyvy = (uint)(WipeBorderU | (WipeBorderY << 8) | (WipeBorderV << 16) | (WipeBorderY << 24));

            switch (type)
            {
                case TransitionType.WipeLR:
                {
                    int splitX = Math.Clamp((int)Math.Round(width * progress), 0, width) & ~1;
                    int splitBytes = splitX * 2;
                    int copyBytes = rowBytes - splitBytes;
                    int bx1 = Math.Max(0, splitX - bw / 2) & ~1;
                    int bx2 = Math.Min(width, bx1 + bw) & ~1;
                    int bPixels = bx2 - bx1;
                    Parallel.For(0, height, y =>
                    {
                        int rowBase = y * rowBytes;
                        if (copyBytes > 0)
                            Buffer.MemoryCopy(pFrom + rowBase + splitBytes, uyvyBuffer + rowBase + splitBytes, copyBytes, copyBytes);
                        if (bw > 0 && bPixels > 0)
                            FillUyvySpan(uyvyBuffer + rowBase + bx1 * 2, bPixels, borderUyvy);
                    });
                    break;
                }
                case TransitionType.WipeRL:
                {
                    int splitX = Math.Clamp((int)Math.Round(width * (1.0 - progress)), 0, width) & ~1;
                    int copyBytes = splitX * 2;
                    int bx1 = Math.Max(0, splitX - bw / 2) & ~1;
                    int bx2 = Math.Min(width, bx1 + bw) & ~1;
                    int bPixels = bx2 - bx1;
                    Parallel.For(0, height, y =>
                    {
                        int rowBase = y * rowBytes;
                        if (copyBytes > 0)
                            Buffer.MemoryCopy(pFrom + rowBase, uyvyBuffer + rowBase, copyBytes, copyBytes);
                        if (bw > 0 && bPixels > 0)
                            FillUyvySpan(uyvyBuffer + rowBase + bx1 * 2, bPixels, borderUyvy);
                    });
                    break;
                }
                case TransitionType.WipeTB:
                {
                    int splitY = Math.Clamp((int)Math.Round(height * progress), 0, height);
                    int copyBytes = (height - splitY) * rowBytes;
                    if (copyBytes > 0)
                    {
                        int offset = splitY * rowBytes;
                        Buffer.MemoryCopy(pFrom + offset, uyvyBuffer + offset, copyBytes, copyBytes);
                    }
                    if (bw > 0)
                    {
                        int by1 = Math.Max(0, splitY - bw / 2);
                        int by2 = Math.Min(height, by1 + bw);
                        for (int y = by1; y < by2; y++)
                            FillUyvySpan(uyvyBuffer + y * rowBytes, width, borderUyvy);
                    }
                    break;
                }
                case TransitionType.WipeBT:
                {
                    int splitY = Math.Clamp((int)Math.Round(height * (1.0 - progress)), 0, height);
                    int copyBytes = splitY * rowBytes;
                    if (copyBytes > 0)
                    {
                        Buffer.MemoryCopy(pFrom, uyvyBuffer, copyBytes, copyBytes);
                    }
                    if (bw > 0)
                    {
                        int by1 = Math.Max(0, splitY - bw / 2);
                        int by2 = Math.Min(height, by1 + bw);
                        for (int y = by1; y < by2; y++)
                            FillUyvySpan(uyvyBuffer + y * rowBytes, width, borderUyvy);
                    }
                    break;
                }
                case TransitionType.BoxOut:
                {
                    int halfW = Math.Clamp((int)Math.Round((width / 2.0) * progress), 0, width / 2) & ~1;
                    int halfH = Math.Clamp((int)Math.Round((height / 2.0) * progress), 0, height / 2);
                    int x1 = width / 2 - halfW;
                    int x2 = width / 2 + halfW;
                    int x1Bytes = x1 * 2;
                    int x2Bytes = x2 * 2;
                    int rightBytes = rowBytes - x2Bytes;
                    int y1 = height / 2 - halfH;
                    int y2 = height / 2 + halfH;
                    int outerX1 = Math.Max(0, x1 - bw) & ~1;
                    int outerX2 = Math.Min(width, x2 + bw) & ~1;
                    int outerY1 = Math.Max(0, y1 - bw);
                    int outerY2 = Math.Min(height, y2 + bw);
                    Parallel.For(0, height, y =>
                    {
                        int rowOffset = y * rowBytes;
                        if (y < y1 || y >= y2)
                        {
                            Buffer.MemoryCopy(pFrom + rowOffset, uyvyBuffer + rowOffset, rowBytes, rowBytes);
                            if (bw > 0 && y >= outerY1 && y < outerY2 && outerX2 > outerX1)
                            {
                                FillUyvySpan(uyvyBuffer + rowOffset + outerX1 * 2, outerX2 - outerX1, borderUyvy);
                            }
                        }
                        else
                        {
                            if (x1Bytes > 0) Buffer.MemoryCopy(pFrom + rowOffset, uyvyBuffer + rowOffset, x1Bytes, x1Bytes);
                            if (rightBytes > 0) Buffer.MemoryCopy(pFrom + rowOffset + x2Bytes, uyvyBuffer + rowOffset + x2Bytes, rightBytes, rightBytes);
                            if (bw > 0)
                            {
                                if (x1 > outerX1) FillUyvySpan(uyvyBuffer + rowOffset + outerX1 * 2, x1 - outerX1, borderUyvy);
                                if (outerX2 > x2) FillUyvySpan(uyvyBuffer + rowOffset + x2 * 2, outerX2 - x2, borderUyvy);
                            }
                        }
                    });
                    break;
                }
                case TransitionType.Stinger:
                {
                    var frames = StingerFrames1080;
                    if (frames != null && frames.Length > 0)
                    {
                        if (progress < StingerCutPoint)
                        {
                            Buffer.MemoryCopy(pFrom, uyvyBuffer, 1920 * 1080 * 2, 1920 * 1080 * 2);
                        }

                        int idx = Math.Clamp((int)(progress * frames.Length), 0, frames.Length - 1);
                        var ov = frames[idx];
                        BlendPreparedRegionUyvy(uyvyBuffer, 0, 0, ov.Width, ov.Height, ov.Y, ov.U, ov.V, ov.A, 100.0);
                    }
                    else
                    {
                        goto default;
                    }
                    break;
                }
                default: // Mix
                {
                    int alpha = Math.Clamp((int)Math.Round(progress * 256.0), 0, 256);
                    int invAlpha = 256 - alpha;
                    Parallel.For(0, height, y =>
                    {
                        byte* dst = uyvyBuffer + y * rowBytes;
                        byte* srcA = pFrom + y * rowBytes;
                        for (int i = 0; i < rowBytes; i++)
                        {
                            dst[i] = (byte)((srcA[i] * invAlpha + dst[i] * alpha) >> 8);
                        }
                    });
                    break;
                }
            }
        }

        private static unsafe void ApplyMixTransitionUyvy(byte* uyvyBuffer)
        {
            if (!IsMixEnabled && !_isMixActive) return;

            lock (_mixLock)
            {
                if (_isMixActive)
                {
                    double elapsed = (Stopwatch.GetTimestamp() - _mixStartTicks) / (double)Stopwatch.Frequency;
                    double dur = Math.Clamp(MixDurationSeconds, 0.1, 10.0);
                    if (elapsed >= dur)
                    {
                        _isMixActive = false;
                    }
                    else if (_mixFromUyvy1080 != null)
                    {
                        double progress = Math.Clamp(elapsed / dur, 0.0, 1.0);
                        fixed (byte* fromBase = _mixFromUyvy1080)
                        {
                            ApplyUyvyTransition1080(fromBase, uyvyBuffer, progress, _runningTransitionType);
                        }
                    }
                }

                if (IsMixEnabled)
                {
                    if (_lastCleanUyvy1080 == null || _lastCleanUyvy1080.Length != 1920 * 1080 * 2)
                        _lastCleanUyvy1080 = new byte[1920 * 1080 * 2];

                    fixed (byte* cachePtr = _lastCleanUyvy1080)
                    {
                        Buffer.MemoryCopy(uyvyBuffer, cachePtr, 1920 * 1080 * 2, 1920 * 1080 * 2);
                    }
                }
            }
        }

        public static volatile bool IsFtbActive = false;
        private static volatile bool _isFtbFading = false;
        private static long _ftbStartTicks = 0;
        private static double _ftbStartLevel = 0.0;

        public static void ToggleFtb()
        {
            double current = GetFtbLevel();
            _ftbStartLevel = current;
            _ftbStartTicks = Stopwatch.GetTimestamp();
            IsFtbActive = !IsFtbActive;
            _isFtbFading = true;
        }

        public static double GetFtbLevel()
        {
            double target = IsFtbActive ? 1.0 : 0.0;
            if (!_isFtbFading) return target;

            double elapsed = (Stopwatch.GetTimestamp() - _ftbStartTicks) / (double)Stopwatch.Frequency;
            double dur = Math.Clamp(MixDurationSeconds, 0.1, 10.0);
            double t = Math.Clamp(elapsed / dur, 0.0, 1.0);
            if (t >= 1.0)
            {
                _isFtbFading = false;
                return target;
            }
            return _ftbStartLevel + (target - _ftbStartLevel) * t;
        }

        private static unsafe void ApplyFtbUyvy(byte* uyvyBuffer)
        {
            double ftb = GetFtbLevel();
            if (ftb <= 0.001) return;

            const int width = 1920;
            const int height = 1080;
            const int rowBytes = width * 2;

            if (ftb >= 0.999)
            {
                const uint blackUyvy = 0x80108010; // U=128, Y=16, V=128, Y=16
                Parallel.For(0, height, y =>
                {
                    FillUyvySpan(uyvyBuffer + y * rowBytes, width, blackUyvy);
                });
                return;
            }

            int keep = Math.Clamp((int)Math.Round((1.0 - ftb) * 256.0), 0, 256);
            Parallel.For(0, height, y =>
            {
                byte* row = uyvyBuffer + y * rowBytes;
                for (int x = 0; x < rowBytes; x += 4)
                {
                    row[x + 0] = (byte)(128 + (((row[x + 0] - 128) * keep) >> 8));
                    row[x + 1] = (byte)(16  + (((row[x + 1] - 16)  * keep) >> 8));
                    row[x + 2] = (byte)(128 + (((row[x + 2] - 128) * keep) >> 8));
                    row[x + 3] = (byte)(16  + (((row[x + 3] - 16)  * keep) >> 8));
                }
            });
        }

        public static unsafe void ApplyLogoOverlayUyvy(byte* uyvyBuffer)
        {
            if (uyvyBuffer == null) return;

            ApplyMixTransitionUyvy(uyvyBuffer);

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

            ApplyFtbUyvy(uyvyBuffer);
        }

        public static unsafe void ApplyLogoOverlayUyvy(byte[]? uyvyBytes)
        {
            if ((!IsLogoOverlayEnabled && !IsHtmlOverlayEnabled && !IsNdiOverlayEnabled && !IsMixEnabled && !_isMixActive && !IsFtbActive && !_isFtbFading) || uyvyBytes == null || uyvyBytes.Length < 1920 * 1080 * 2) return;
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
