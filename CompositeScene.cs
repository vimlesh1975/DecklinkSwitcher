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

                    double cropL = Math.Clamp(layer.CropLeftPct, 0, 45);
                    double cropR = Math.Clamp(layer.CropRightPct, 0, 45);
                    double cropT = Math.Clamp(layer.CropTopPct, 0, 45);
                    double cropB = Math.Clamp(layer.CropBottomPct, 0, 45);

                    int srcX = ((int)Math.Round(Width * (cropL / 100.0))) & ~1;
                    int srcEndX = ((int)Math.Round(Width * (1.0 - cropR / 100.0))) & ~1;
                    int srcY = Math.Clamp((int)Math.Round(Height * (cropT / 100.0)), 0, Height - 2);
                    int srcEndY = Math.Clamp((int)Math.Round(Height * (1.0 - cropB / 100.0)), srcY + 2, Height);
                    int srcW = Math.Max(2, srcEndX - srcX);
                    int srcH = Math.Max(2, srcEndY - srcY);

                    int dstX = Math.Clamp(((int)Math.Round(Width * (Math.Clamp(layer.DestXPct, 0, 100) / 100.0))) & ~1, 0, Width - 2);
                    int dstY = Math.Clamp((int)Math.Round(Height * (Math.Clamp(layer.DestYPct, 0, 100) / 100.0)), 0, Height - 2);
                    int dstW = ((int)Math.Round(Width * (Math.Clamp(layer.DestWPct, 1, 100) / 100.0))) & ~1;
                    int dstH = (int)Math.Round(Height * (Math.Clamp(layer.DestHPct, 1, 100) / 100.0));

                    if (dstX + dstW > Width) dstW = (Width - dstX) & ~1;
                    if (dstY + dstH > Height) dstH = Height - dstY;
                    if (dstW < 2 || dstH < 2) continue;

                    int border = Math.Clamp(layer.BorderPx, 0, 12);

                    fixed (byte* pSrcBase = srcUyvy)
                    {
                        byte* srcPtr = pSrcBase;
                        int capSrcX = srcX, capSrcY = srcY, capSrcW = srcW, capSrcH = srcH;
                        int capDstX = dstX, capDstY = dstY, capDstW = dstW, capDstH = dstH;

                        Parallel.For(0, capDstH, dy =>
                        {
                            int sy = capSrcY + (dy * capSrcH) / capDstH;
                            byte* sRow = srcPtr + sy * strideBytes;
                            byte* dRow = dstPtr + (capDstY + dy) * strideBytes + capDstX * 2;

                            bool isBorderRow = border > 0 && (dy < border || dy >= capDstH - border);
                            if (isBorderRow)
                            {
                                uint* dRow32 = (uint*)dRow;
                                int pairs = capDstW >> 1;
                                for (int p = 0; p < pairs; p++)
                                    dRow32[p] = 0xEB80EB80u; // White border (U=128, Y=235, V=128, Y=235)
                                return;
                            }

                            for (int dx = 0; dx < capDstW; dx += 2)
                            {
                                if (border > 0 && (dx < border || dx >= capDstW - border))
                                {
                                    *(uint*)(dRow + dx * 2) = 0xEB80EB80u;
                                    continue;
                                }

                                int sx0 = capSrcX + (dx * capSrcW) / capDstW;
                                int sx1 = capSrcX + ((dx + 1) * capSrcW) / capDstW;
                                int mp0 = (sx0 >> 1) << 2;
                                int mp1 = (sx1 >> 1) << 2;

                                dRow[dx * 2 + 0] = sRow[mp0 + 0];
                                dRow[dx * 2 + 1] = sRow[mp0 + ((sx0 & 1) == 0 ? 1 : 3)];
                                dRow[dx * 2 + 2] = sRow[mp0 + 2];
                                dRow[dx * 2 + 3] = sRow[mp1 + ((sx1 & 1) == 0 ? 1 : 3)];
                            }
                        });
                    }
                }

                fixed (byte* pPrev = outPreview480)
                {
                    DeckLinkDevice.ConvertUYVYToBGRA_Downsampled((IntPtr)dstPtr, Width, Height, strideBytes, (IntPtr)pPrev, 480, 270, 480 * 4);
                }
            }
        }
    }
}

