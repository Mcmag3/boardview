using System.Drawing.Imaging;

namespace BoardviewBuilder;

/// <summary>
/// Pre-OCR image adjustments. The schematic tab keeps the ORIGINAL bitmap
/// from <see cref="SchematicImageLoader"/> untouched, and uses
/// <see cref="Apply"/> to produce a processed copy that is what the user
/// sees in the viewer and what OCR will eventually run on.
///
/// All values are stored as plain numbers so they can be driven directly by
/// WinForms controls (CheckBox / TrackBar).
/// </summary>
public sealed class ImageAdjustments
{
    public bool Grayscale { get; set; }
    public bool Invert { get; set; }

    /// <summary>-100 .. +100. 0 = no change.</summary>
    public int Brightness { get; set; }

    /// <summary>-100 .. +100. 0 = no change.</summary>
    public int Contrast { get; set; }

    public bool ThresholdEnabled { get; set; }

    /// <summary>0..255. Pixels with luma &gt;= this become white, else black.
    /// Only used when <see cref="ThresholdEnabled"/>.</summary>
    public int Threshold { get; set; } = 160;

    /// <summary>0, 90, 180 or 270 — clockwise rotation applied LAST.</summary>
    public int RotationDegrees { get; set; }

    public bool IsIdentity =>
        !Grayscale && !Invert && Brightness == 0 && Contrast == 0
        && !ThresholdEnabled && RotationDegrees == 0;

    /// <summary>Reset all adjustments to identity.</summary>
    public void Reset()
    {
        Grayscale = false;
        Invert = false;
        Brightness = 0;
        Contrast = 0;
        ThresholdEnabled = false;
        Threshold = 160;
        RotationDegrees = 0;
    }

    /// <summary>Apply these adjustments to <paramref name="source"/> and
    /// return a NEW bitmap. The source is not modified. Caller owns the
    /// returned bitmap and must dispose it.</summary>
    public Bitmap Apply(Bitmap source)
    {
        // Normalise to 24bpp RGB up-front so the inner loop is uniform.
        Bitmap working;
        if (source.PixelFormat == PixelFormat.Format24bppRgb)
        {
            working = new Bitmap(source);
        }
        else
        {
            working = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(working);
            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        }

        bool needsPixelPass = Grayscale || Invert
            || Brightness != 0 || Contrast != 0 || ThresholdEnabled;
        if (needsPixelPass)
            ApplyPixelTransform(working);

        if (RotationDegrees != 0)
        {
            var rot = RotationDegrees switch
            {
                90 => RotateFlipType.Rotate90FlipNone,
                180 => RotateFlipType.Rotate180FlipNone,
                270 => RotateFlipType.Rotate270FlipNone,
                _ => RotateFlipType.RotateNoneFlipNone,
            };
            working.RotateFlip(rot);
        }

        return working;
    }

    /// <summary>Per-pixel transform: brightness → contrast → grayscale → invert → threshold.
    /// Operates in-place on a 24bpp RGB bitmap.</summary>
    private unsafe void ApplyPixelTransform(Bitmap bmp)
    {
        // Contrast factor: classic GIMP-style. Contrast=0 → factor 1.0 (no change).
        // Squared so the slider feels smoother near the centre.
        float cFactor = (100 + Contrast) / 100f;
        cFactor *= cFactor;

        // Pre-compute a 256-entry LUT for brightness+contrast — same lookup for R/G/B
        // since brightness and contrast are channel-independent.
        var bcLut = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            float f = v + Brightness;
            if (Contrast != 0)
                f = ((f / 255f - 0.5f) * cFactor + 0.5f) * 255f;
            bcLut[v] = (byte)Math.Clamp((int)f, 0, 255);
        }

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            int width = bmp.Width;
            int height = bmp.Height;
            byte* scan0 = (byte*)data.Scan0;

            bool gray = Grayscale;
            bool invert = Invert;
            bool thresh = ThresholdEnabled;
            int threshVal = Math.Clamp(Threshold, 0, 255);

            for (int y = 0; y < height; y++)
            {
                byte* row = scan0 + y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte* px = row + x * 3;
                    byte b = bcLut[px[0]];
                    byte g = bcLut[px[1]];
                    byte r = bcLut[px[2]];

                    if (gray || thresh)
                    {
                        // Rec.601 luma
                        int luma = (299 * r + 587 * g + 114 * b) / 1000;
                        if (luma > 255) luma = 255;
                        if (gray) { r = g = b = (byte)luma; }
                        if (thresh)
                        {
                            byte v = luma >= threshVal ? (byte)255 : (byte)0;
                            r = g = b = v;
                        }
                    }

                    if (invert) { r = (byte)(255 - r); g = (byte)(255 - g); b = (byte)(255 - b); }

                    px[0] = b;
                    px[1] = g;
                    px[2] = r;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
