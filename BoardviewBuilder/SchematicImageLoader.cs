using System.Drawing.Imaging;

namespace BoardviewBuilder;

/// <summary>
/// Loads a raster schematic (JPEG / PNG / BMP) and produces a <see cref="Netlist"/>.
///
/// STATUS (step 1 of the image → netlist pipeline):
///   * Loads the image from disk into a <see cref="Bitmap"/>.
///   * Captures basic metadata (dimensions, pixel format, DPI, file size).
///   * Returns an empty <see cref="Netlist"/> with diagnostic notes attached.
///
/// Future steps will plug into <see cref="Extract"/> to populate the netlist
/// (OCR for reference designators &amp; net labels, line/wire tracing for
/// connectivity, symbol recognition for pin counts).
///
/// The loader is kept as a pure helper class — the same shape as
/// <see cref="CsvLoader"/> — so the UI doesn't depend on its internals.
/// </summary>
public static class SchematicImageLoader
{
    /// <summary>Supported file extensions for the open-file dialog.</summary>
    public static readonly string[] SupportedExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp" };

    public sealed class LoadResult : IDisposable
    {
        public required Bitmap Image { get; init; }
        public required Netlist Netlist { get; init; }
        public required string SourcePath { get; init; }

        public void Dispose() => Image.Dispose();
    }

    /// <summary>
    /// Load the image file and run the (currently stub) extraction.
    /// Throws on missing file or unreadable image.
    /// </summary>
    public static LoadResult Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Schematic image not found: {path}");

        // Bitmap(string) keeps a file lock for the lifetime of the bitmap.
        // Load via a memory copy so the source file stays free (lets the user
        // move/rename it while the app is open).
        Bitmap bmp;
        byte[] bytes = File.ReadAllBytes(path);
        using (var ms = new MemoryStream(bytes, writable: false))
        using (var src = new Bitmap(ms))
        {
            bmp = new Bitmap(src);
        }

        var netlist = new Netlist
        {
            Source = path,
        };

        netlist.Notes.Add($"File: {Path.GetFileName(path)} ({bytes.Length:N0} bytes)");
        netlist.Notes.Add($"Image: {bmp.Width} × {bmp.Height} px, " +
                          $"{bmp.HorizontalResolution:F0}×{bmp.VerticalResolution:F0} DPI, " +
                          $"{PixelFormatName(bmp.PixelFormat)}");

        Extract(bmp, netlist);

        return new LoadResult
        {
            Image = bmp,
            Netlist = netlist,
            SourcePath = path,
        };
    }

    /// <summary>
    /// Schematic-to-netlist extraction. CURRENTLY A STUB — populates only
    /// diagnostic notes. Future implementations will:
    ///   1. Pre-process the image (grayscale, threshold, deskew).
    ///   2. Run OCR to find reference designators (R1, U3, …) and net labels.
    ///   3. Trace orthogonal wire segments between symbol pins.
    ///   4. Resolve junctions/dots into nets.
    ///   5. Emit <see cref="NetlistComponent"/>s and <see cref="NetlistNet"/>s.
    /// </summary>
    private static void Extract(Bitmap image, Netlist netlist)
    {
        netlist.Notes.Add("Extractor: stub (no OCR / line tracing yet).");
        netlist.Notes.Add("Next step: add OCR pass for reference designators.");
        // Intentionally empty — components/nets populated by later iterations.
    }

    /// <summary>Render the netlist as a human-readable text block for the UI
    /// preview pane. Mirrors the BRD preview behaviour of the CSV pipeline.</summary>
    public static string FormatPreview(Netlist netlist)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Netlist preview");
        if (!string.IsNullOrEmpty(netlist.Source))
            sb.AppendLine($"# Source: {netlist.Source}");
        sb.AppendLine();

        if (netlist.Notes.Count > 0)
        {
            sb.AppendLine("## Extractor notes");
            foreach (var n in netlist.Notes)
                sb.Append("  - ").AppendLine(n);
            sb.AppendLine();
        }

        sb.AppendLine($"## Nets ({netlist.Nets.Count})");
        for (int i = 0; i < netlist.Nets.Count; i++)
            sb.Append("  ").Append(i + 1).Append(' ').AppendLine(netlist.Nets[i].Name);
        if (netlist.Nets.Count == 0) sb.AppendLine("  (none)");
        sb.AppendLine();

        sb.AppendLine($"## Components ({netlist.Components.Count})");
        if (netlist.Components.Count == 0) sb.AppendLine("  (none)");
        foreach (var c in netlist.Components)
        {
            sb.Append("  ").Append(c.Reference);
            if (!string.IsNullOrEmpty(c.Value)) sb.Append("  [").Append(c.Value).Append(']');
            sb.AppendLine();
            foreach (var p in c.Pins)
            {
                sb.Append("      pin ").Append(p.Number);
                if (!string.IsNullOrEmpty(p.Name)) sb.Append(" (").Append(p.Name).Append(')');
                sb.Append(" → ").AppendLine(string.IsNullOrEmpty(p.Net) ? "(NC)" : p.Net);
            }
        }

        return sb.ToString();
    }

    private static string PixelFormatName(PixelFormat pf) => pf switch
    {
        PixelFormat.Format24bppRgb     => "24bpp RGB",
        PixelFormat.Format32bppArgb    => "32bpp ARGB",
        PixelFormat.Format32bppRgb     => "32bpp RGB",
        PixelFormat.Format32bppPArgb   => "32bpp PARGB",
        PixelFormat.Format8bppIndexed  => "8bpp indexed",
        PixelFormat.Format1bppIndexed  => "1bpp indexed",
        _ => pf.ToString(),
    };
}
