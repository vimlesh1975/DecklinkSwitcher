using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DecklinkSwitcher
{
    public class CompositeLayerConfig
    {
        public bool Enabled { get; set; } = false;
        public string SourceKey { get; set; } = "";
        public double DestXPct { get; set; } = 0;
        public double DestYPct { get; set; } = 0;
        public double DestWPct { get; set; } = 50;
        public double DestHPct { get; set; } = 50;
        public double CropLeftPct { get; set; } = 0;
        public double CropTopPct { get; set; } = 0;
        public double CropRightPct { get; set; } = 0;
        public double CropBottomPct { get; set; } = 0;
        public int BorderPx { get; set; } = 2;
        public string Label { get; set; } = "";
    }

    public class CompositeSceneConfig
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Scene";
        public string BackgroundHex { get; set; } = "#000000";
        public List<CompositeLayerConfig> Layers { get; set; } = new();
    }

    internal static class CompositeSceneCompositor
    {
        public const int Width = 1920;
        public const int Height = 1080;
        public const int UyvyBytes = Width * Height * 2;
        public const int Preview480Bytes = 480 * 270 * 4;

        public static List<CompositeLayerConfig> CreateDefaultLayers()
        {
            return new List<CompositeLayerConfig>
            {
                new CompositeLayerConfig { Enabled = true, SourceKey = "Input 1", DestXPct = 0, DestYPct = 10, DestWPct = 50, DestHPct = 80, BorderPx = 2 },
                new CompositeLayerConfig { Enabled = true, SourceKey = "Input 2", DestXPct = 50, DestYPct = 10, DestWPct = 50, DestHPct = 80, BorderPx = 2 },
                new CompositeLayerConfig { Enabled = false, SourceKey = "", DestXPct = 0, DestYPct = 50, DestWPct = 50, DestHPct = 50, BorderPx = 2 },
                new CompositeLayerConfig { Enabled = false, SourceKey = "", DestXPct = 50, DestYPct = 50, DestWPct = 50, DestHPct = 50, BorderPx = 2 }
            };
        }

        public static unsafe void FillColorUyvy1080(byte[] uyvy, string? hexColor)
        {
            byte r = 0, g = 0, b = 0;
            if (!string.IsNullOrWhiteSpace(hexColor))
            {
                string h = hexColor.Trim().TrimStart('#');
                if (h.Length == 6 &&
                    byte.TryParse(h.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte pr) &&
                    byte.TryParse(h.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte pg) &&
                    byte.TryParse(h.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte pb))
                {
                    r = pr; g = pg; b = pb;
                }
            }

            int yVal = Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 16, 235);
            int uVal = Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 16, 240);
            int vVal = Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 16, 240);
            uint macro = (uint)(uVal | (yVal << 8) | (vVal << 16) | (yVal << 24));

            fixed (byte* p = uyvy)
            {
                uint* p32 = (uint*)p;
                int count = uyvy.Length >> 2;
                for (int i = 0; i < count; i++)
                    p32[i] = macro;
            }
        }

        public static unsafe void CompositeFrame(
            IReadOnlyList<CompositeLayerConfig> layers,
            Func<string, byte[]?> resolveSourceUyvy1080,
            byte[] outUyvy1080,
            byte[] outPreview480,
            string? backgroundHex = "#000000")
        {
            FillColorUyvy1080(outUyvy1080, backgroundHex);

            fixed (byte* pDstBase = outUyvy1080)
            {
                byte* dstPtr = pDstBase;
                const int strideBytes = Width * 2;

                for (int li = 0; li < layers.Count; li++)
                {
                    var layer = layers[li];
                    if (!layer.Enabled || string.IsNullOrWhiteSpace(layer.SourceKey)) continue;

                    byte[]? srcUyvy = resolveSourceUyvy1080(layer.SourceKey);
                    if (srcUyvy == null || srcUyvy.Length < UyvyBytes) continue;

                    double cropL = Math.Clamp(layer.CropLeftPct, 0, 98);
                    double cropR = Math.Clamp(layer.CropRightPct, 0, Math.Max(0, 98 - cropL));
                    double cropT = Math.Clamp(layer.CropTopPct, 0, 98);
                    double cropB = Math.Clamp(layer.CropBottomPct, 0, Math.Max(0, 98 - cropT));

                    int dstX = ((int)Math.Round(Width * (Math.Clamp(layer.DestXPct, -95, 100) / 100.0))) & ~1;
                    int dstY = (int)Math.Round(Height * (Math.Clamp(layer.DestYPct, -95, 100) / 100.0));
                    int dstW = ((int)Math.Round(Width * (Math.Clamp(layer.DestWPct, 1, 200) / 100.0))) & ~1;
                    int dstH = (int)Math.Round(Height * (Math.Clamp(layer.DestHPct, 1, 200) / 100.0));
                    if (dstW < 2 || dstH < 2) continue;

                    // Crop masks the frame within the box without stretching, leaving cropped-out areas blank.
                    int rawVisX = dstX + (((int)Math.Round(dstW * (cropL / 100.0))) & ~1);
                    int rawVisEndX = dstX + (((int)Math.Round(dstW * (1.0 - cropR / 100.0))) & ~1);
                    int rawVisY = dstY + (int)Math.Round(dstH * (cropT / 100.0));
                    int rawVisEndY = dstY + (int)Math.Round(dstH * (1.0 - cropB / 100.0));

                    int visX = Math.Clamp(rawVisX, 0, Width - 2) & ~1;
                    int visEndX = Math.Clamp(rawVisEndX, visX + 2, Width) & ~1;
                    int visY = Math.Clamp(rawVisY, 0, Height - 2);
                    int visEndY = Math.Clamp(rawVisEndY, visY + 2, Height);
                    int visW = visEndX - visX;
                    int visH = visEndY - visY;
                    if (visW < 2 || visH < 2) continue;

                    int border = Math.Clamp(layer.BorderPx, 0, 12);

                    fixed (byte* pSrcBase = srcUyvy)
                    {
                        byte* srcPtr = pSrcBase;
                        int capDstX = dstX, capDstY = dstY, capDstW = dstW, capDstH = dstH;
                        int capVisX = visX, capVisY = visY, capVisW = visW, capVisH = visH;

                        Parallel.For(0, capVisH, vy =>
                        {
                            int boxY = (capVisY - capDstY) + vy;
                            int sy = Math.Clamp((boxY * Height) / capDstH, 0, Height - 1);
                            byte* sRow = srcPtr + sy * strideBytes;
                            byte* dRow = dstPtr + (capVisY + vy) * strideBytes + capVisX * 2;

                            bool isBorderRow = border > 0 && (vy < border || vy >= capVisH - border);
                            if (isBorderRow)
                            {
                                uint* dRow32 = (uint*)dRow;
                                int pairs = capVisW >> 1;
                                for (int p = 0; p < pairs; p++)
                                    dRow32[p] = 0xEB80EB80u; // White border (U=128, Y=235, V=128, Y=235)
                                return;
                            }

                            int boxXOffset = capVisX - capDstX;
                            for (int dx = 0; dx < capVisW; dx += 2)
                            {
                                if (border > 0 && (dx < border || dx >= capVisW - border))
                                {
                                    *(uint*)(dRow + dx * 2) = 0xEB80EB80u;
                                    continue;
                                }

                                int sx0 = Math.Clamp(((boxXOffset + dx) * Width) / capDstW, 0, Width - 1);
                                int sx1 = Math.Clamp(((boxXOffset + dx + 1) * Width) / capDstW, 0, Width - 1);
                                int mp0 = (sx0 >> 1) << 2;
                                int mp1 = (sx1 >> 1) << 2;

                                dRow[dx * 2 + 0] = sRow[mp0 + 0];
                                dRow[dx * 2 + 1] = sRow[mp0 + ((sx0 & 1) == 0 ? 1 : 3)];
                                dRow[dx * 2 + 2] = sRow[mp0 + 2];
                                dRow[dx * 2 + 3] = sRow[mp1 + ((sx1 & 1) == 0 ? 1 : 3)];
                            }
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(layer.Label))
                    {
                        BlendLabelUyvy(dstPtr, strideBytes, visX, visY, visW, visH, border, layer.Label.Trim());
                    }
                }

                fixed (byte* pPrev = outPreview480)
                {
                    DeckLinkDevice.ConvertUYVYToBGRA_Downsampled((IntPtr)dstPtr, Width, Height, strideBytes, (IntPtr)pPrev, 480, 270, 480 * 4);
                }
            }
        }

        private sealed class CachedLabel
        {
            public int Width;
            public int Height;
            public byte[] Y = Array.Empty<byte>();
            public byte[] U = Array.Empty<byte>();
            public byte[] V = Array.Empty<byte>();
            public byte[] A = Array.Empty<byte>();
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedLabel> _labelCache = new();

        private static CachedLabel GetOrBuildLabel(string text, int maxW)
        {
            int cappedMaxW = Math.Max(60, Math.Min(maxW, 900)) & ~1;
            string key = $"{cappedMaxW}|{text}";
            return _labelCache.GetOrAdd(key, _ =>
            {
                using var measureBmp = new System.Drawing.Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var measureG = System.Drawing.Graphics.FromImage(measureBmp);
                using var font = new System.Drawing.Font("Segoe UI", 18f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
                var sz = measureG.MeasureString(text, font);

                int padL = 16, padR = 14, padY = 6;
                int w = (Math.Clamp((int)Math.Ceiling(sz.Width) + padL + padR, 60, cappedMaxW) + 1) & ~1;
                int h = Math.Clamp((int)Math.Ceiling(sz.Height) + padY * 2, 28, 56);

                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                    using var bgBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(215, 16, 22, 34));
                    g.FillRectangle(bgBrush, 0, 0, w, h);

                    using var accentBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 56, 189, 248));
                    g.FillRectangle(accentBrush, 0, 0, 5, h);

                    using var sf = new System.Drawing.StringFormat { Trimming = System.Drawing.StringTrimming.EllipsisCharacter, FormatFlags = System.Drawing.StringFormatFlags.NoWrap };
                    var textRect = new System.Drawing.RectangleF(12, padY - 1, w - 20, h - padY * 2 + 4);
                    g.DrawString(text, font, System.Drawing.Brushes.White, textRect, sf);
                }

                int total = w * h;
                var yArr = new byte[total];
                var uArr = new byte[total];
                var vArr = new byte[total];
                var aArr = new byte[total];

                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* p = (byte*)data.Scan0;
                        for (int py = 0; py < h; py++)
                        {
                            byte* row = p + py * data.Stride;
                            for (int px = 0; px < w; px++)
                            {
                                byte b = row[px * 4 + 0];
                                byte g = row[px * 4 + 1];
                                byte r = row[px * 4 + 2];
                                byte a = row[px * 4 + 3];
                                int idx = py * w + px;
                                aArr[idx] = a;
                                int yy = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                                int uu = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                                int vv = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                                yArr[idx] = (byte)Math.Clamp(yy, 16, 235);
                                uArr[idx] = (byte)Math.Clamp(uu, 16, 240);
                                vArr[idx] = (byte)Math.Clamp(vv, 16, 240);
                            }
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                if (_labelCache.Count > 64) _labelCache.Clear();
                return new CachedLabel { Width = w, Height = h, Y = yArr, U = uArr, V = vArr, A = aArr };
            });
        }

        private static unsafe void BlendLabelUyvy(byte* dstPtr, int strideBytes, int visX, int visY, int visW, int visH, int border, string label)
        {
            int availW = visW - border * 2 - 20;
            int availH = visH - border * 2 - 16;
            if (availW < 60 || availH < 28) return;

            var lbl = GetOrBuildLabel(label, availW);

            // 5-second animation loop: 0.55s left-to-right wipe reveal, 3.90s hold, 0.55s right-to-left wipe hide
            double t = ((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency) % 5.0;
            double revealFrac;
            if (t < 0.55)
            {
                double p = t / 0.55;
                revealFrac = 1.0 - (1.0 - p) * (1.0 - p) * (1.0 - p);
            }
            else if (t > 4.45)
            {
                double p = (t - 4.45) / 0.55;
                revealFrac = 1.0 - (p * p * p);
            }
            else
            {
                revealFrac = 1.0;
            }

            int lx = (visX + border + 12) & ~1;
            int ly = visY + visH - border - lbl.Height - 12;
            int minLx = (visX + border + 2) & ~1;
            int maxRx = (visX + visW - border - 2) & ~1;
            if (ly < 0 || ly + lbl.Height > Height) return;

            int revealW = ((int)Math.Round(lbl.Width * revealFrac)) & ~1;
            int startPx = lx < minLx ? (minLx - lx) : 0;
            int endPx = Math.Min(revealW, maxRx - lx);
            if (endPx <= startPx + 1) return;

            // Subtle sheen highlight sweeping across the pill between 1.5s and 2.5s
            int sheenCenter = (t >= 1.5 && t <= 2.5)
                ? (int)(((t - 1.5) / 1.0) * (lbl.Width + 60)) - 30
                : -999;

            for (int py = 0; py < lbl.Height; py++)
            {
                byte* dRow = dstPtr + (ly + py) * strideBytes + lx * 2;
                int sOff = py * lbl.Width;
                for (int px = startPx; px < endPx; px += 2)
                {
                    int i0 = sOff + px;
                    int i1 = i0 + 1;
                    int edgeFade = (revealFrac < 0.999 && endPx - px <= 8)
                        ? Math.Max(32, ((endPx - px) * 256) / 8)
                        : 256;
                    int a0 = (lbl.A[i0] * edgeFade) >> 8;
                    int a1 = (lbl.A[i1] * edgeFade) >> 8;
                    if ((a0 | a1) == 0) continue;

                    int y0 = lbl.Y[i0];
                    int y1 = lbl.Y[i1];
                    if (sheenCenter > -30)
                    {
                        int dist = Math.Abs(px - sheenCenter);
                        if (dist < 22)
                        {
                            int boost = ((22 - dist) * 28) / 22;
                            y0 = Math.Min(235, y0 + boost);
                            y1 = Math.Min(235, y1 + boost);
                        }
                    }

                    int dOff = px * 2;
                    if (a0 == 255 && a1 == 255)
                    {
                        dRow[dOff + 0] = (byte)((lbl.U[i0] + lbl.U[i1]) >> 1);
                        dRow[dOff + 1] = (byte)y0;
                        dRow[dOff + 2] = (byte)((lbl.V[i0] + lbl.V[i1]) >> 1);
                        dRow[dOff + 3] = (byte)y1;
                    }
                    else
                    {
                        int inv0 = 255 - a0;
                        int inv1 = 255 - a1;
                        int aAvg = (a0 + a1) >> 1;
                        int invAvg = 255 - aAvg;
                        int uAvg = (lbl.U[i0] + lbl.U[i1]) >> 1;
                        int vAvg = (lbl.V[i0] + lbl.V[i1]) >> 1;
                        dRow[dOff + 0] = (byte)((dRow[dOff + 0] * invAvg + uAvg * aAvg) / 255);
                        dRow[dOff + 1] = (byte)((dRow[dOff + 1] * inv0 + y0 * a0) / 255);
                        dRow[dOff + 2] = (byte)((dRow[dOff + 2] * invAvg + vAvg * aAvg) / 255);
                        dRow[dOff + 3] = (byte)((dRow[dOff + 3] * inv1 + y1 * a1) / 255);
                    }
                }
            }
        }
    }
}

