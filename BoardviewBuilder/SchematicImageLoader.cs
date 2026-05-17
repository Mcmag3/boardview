using System.Drawing.Imaging;

namespace BoardviewBuilder;

/// <summary>
/// Loads a raster schematic (JPEG / PNG / BMP) and produces a <see cref="Netlist"/>.
///
/// STATUS:
///   * <see cref="Load"/>           — loads the image, captures metadata, returns an empty Netlist.
///   * <see cref="ExtractFromBitmap"/> — runs OCR (Tesseract) on a processed bitmap
///                                     and populates the Netlist with reference designators.
///
/// The two operations are split so the UI can run extraction repeatedly on the
/// CURRENT processed (threshold/grayscale/etc.) bitmap without reloading the file.
/// Future stages will extend ExtractFromBitmap with net-label OCR and wire tracing.
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
    /// Load the image file. Returns the bitmap plus an EMPTY netlist (only
    /// metadata notes). Call <see cref="ExtractFromBitmap"/> on the processed
    /// image to actually populate the netlist.
    /// </summary>
    public static LoadResult Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Schematic image not found: {path}");

        // Bitmap(string) keeps a file lock for the lifetime of the bitmap.
        // Load via a memory copy so the source file stays free.
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
        netlist.Notes.Add("Click \"Extract from image\" to run OCR.");

        return new LoadResult
        {
            Image = bmp,
            Netlist = netlist,
            SourcePath = path,
        };
    }

    /// <summary>Result of one extraction pass — useful for the status bar.</summary>
    public readonly record struct ExtractionStats(
        int WordsRecognised,
        int ReferenceDesignatorsFound,
        long ElapsedMs);

    /// <summary>
    /// Run OCR over <paramref name="processed"/> (typically the user's
    /// adjusted/binarised image), filter the words for reference designators,
    /// and replace the components in <paramref name="netlist"/>.
    ///
    /// Nets are NOT touched — net-label extraction comes in a later step.
    /// Existing components/pins in <paramref name="netlist"/> are REPLACED;
    /// the caller is expected to re-apply manual edits after extraction.
    /// </summary>
    public static ExtractionStats ExtractFromBitmap(Bitmap processed, Netlist netlist)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var words = OcrEngine.RecognizeWords(processed);
        sw.Stop();

        // Deduplicate by uppercased text — Tesseract can return the same
        // designator multiple times if a label is split across two boxes.
        // We keep the FIRST occurrence's bounding box.
        var refs = new Dictionary<string, OcrEngine.Word>(StringComparer.Ordinal);
        foreach (var w in words)
        {
            // Strip common OCR clutter — colons, commas, trailing punctuation.
            string cleaned = w.Text.Trim().Trim(':', ',', '.', ';');
            // Up-case so "r1" and "R1" merge.
            string upper = cleaned.ToUpperInvariant();
            if (OcrEngine.ReferenceDesignatorRegex.IsMatch(upper) && !refs.ContainsKey(upper))
                refs[upper] = w with { Text = upper };
        }

        // Order designators by prefix letters, then numeric suffix, so the
        // netlist looks tidy (C1, C2, C3, R1, R2, U1, …).
        var ordered = refs.Values
            .OrderBy(w => SplitPrefix(w.Text).prefix, StringComparer.Ordinal)
            .ThenBy(w => SplitPrefix(w.Text).number)
            .ToList();

        netlist.Components.Clear();
        foreach (var w in ordered)
            netlist.Components.Add(new NetlistComponent { Reference = w.Text });

        // Refresh diagnostic notes so the user can see what happened.
        // Keep the original "File:" / "Image:" notes; drop the prior extractor notes.
        netlist.Notes.RemoveAll(n => n.StartsWith("OCR:", StringComparison.Ordinal)
                                  || n.StartsWith("Click \"Extract", StringComparison.Ordinal));
        netlist.Notes.Add($"OCR: recognised {words.Count} word(s) in {sw.ElapsedMilliseconds} ms.");
        netlist.Notes.Add($"OCR: {refs.Count} reference designator(s) matched the regex {OcrEngine.ReferenceDesignatorRegex}.");
        if (words.Count > 0)
        {
            // Include the top-10 lowest-confidence words as a quick troubleshooting hint
            // (they're usually noise; if good designators show low confidence,
            // it's a clue the threshold/contrast need tuning).
            var lowConf = words.OrderBy(w => w.Confidence).Take(10).ToList();
            netlist.Notes.Add($"OCR: lowest-confidence sample → " +
                string.Join(", ", lowConf.Select(w => $"\"{w.Text}\"@{w.Confidence:F2}")));
        }

        return new ExtractionStats(words.Count, refs.Count, sw.ElapsedMilliseconds);
    }

    /// <summary>Split a reference designator into its letter prefix and numeric
    /// suffix for sorting. \"IC42\" → (\"IC\", 42).</summary>
    private static (string prefix, int number) SplitPrefix(string designator)
    {
        int i = 0;
        while (i < designator.Length && !char.IsDigit(designator[i])) i++;
        string prefix = designator.Substring(0, i);
        int n = 0;
        int.TryParse(designator.AsSpan(i), out n);
        return (prefix, n);
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

    // ----- Old API kept for callers we haven't updated yet -----

    /// <summary>Render the netlist as a human-readable text block.</summary>
    public static string FormatPreview(Netlist netlist) => NetlistTextFormat.Format(netlist);
}
