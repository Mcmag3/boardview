using System.Drawing.Imaging;
using PDFtoImage;
using SkiaSharp;

namespace BoardviewBuilder;

/// <summary>
/// Loads a raster schematic (JPEG / PNG / BMP / PDF) and produces a <see cref="Netlist"/>.
///
/// STATUS:
///   * <see cref="Load"/>              — loads the image, captures metadata, returns an empty Netlist.
///   * <see cref="ExtractFromBitmap"/> — runs OCR (Tesseract) + wire-tracing on a processed bitmap
///                                       and populates the Netlist with components, nets, and a
///                                       single best-guess pin per component.
///
/// The two operations are split so the UI can run extraction repeatedly on the
/// CURRENT processed (threshold/grayscale/etc.) bitmap without reloading the file.
/// </summary>
public static class SchematicImageLoader
{
    /// <summary>Supported file extensions for the open-file dialog.</summary>
    public static readonly string[] SupportedExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".pdf" };

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
    public static LoadResult Load(string path) => Load(path, pageIndex: 0);

    /// <summary>
    /// Load the image file with optional page index for multi-page PDFs.
    /// </summary>
    public static LoadResult Load(string path, int pageIndex)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Schematic image not found: {path}");

        string ext = Path.GetExtension(path).ToLowerInvariant();
        byte[] bytes = File.ReadAllBytes(path);

        Bitmap bmp;
        string formatNote;
        int totalPages = 1;

        if (ext == ".pdf")
        {
            // Load PDF and render the specified page
            (bmp, totalPages) = LoadPdfPage(bytes, pageIndex);
            formatNote = totalPages > 1
                ? $"PDF page {pageIndex + 1} of {totalPages}"
                : "PDF (single page)";
        }
        else
        {
            // Load regular image
            // Bitmap(string) keeps a file lock for the lifetime of the bitmap.
            // Load via a memory copy so the source file stays free.
            using var ms = new MemoryStream(bytes, writable: false);
            using var src = new Bitmap(ms);
            bmp = new Bitmap(src);
            formatNote = PixelFormatName(bmp.PixelFormat);
        }

        var netlist = new Netlist
        {
            Source = path,
        };

        netlist.Notes.Add($"File: {Path.GetFileName(path)} ({bytes.Length:N0} bytes)");
        netlist.Notes.Add($"Image: {bmp.Width} × {bmp.Height} px, " +
                          $"{bmp.HorizontalResolution:F0}×{bmp.VerticalResolution:F0} DPI, " +
                          $"{formatNote}");
        netlist.Notes.Add("Click \"Extract from image\" to run OCR.");

        return new LoadResult
        {
            Image = bmp,
            Netlist = netlist,
            SourcePath = path,
        };
    }

    /// <summary>Get the number of pages in a PDF file.</summary>
    public static int GetPdfPageCount(string path)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Conversion.GetPageCount(bytes);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Load a specific page from a PDF as a bitmap.</summary>
    private static (Bitmap bmp, int totalPages) LoadPdfPage(byte[] pdfBytes, int pageIndex)
    {
        int totalPages = Conversion.GetPageCount(pdfBytes);
        if (pageIndex < 0 || pageIndex >= totalPages)
            pageIndex = 0;

        // Render at 200 DPI for good OCR quality
        const int dpi = 200;

        // Get page size using Index (new API)
        var pageSize = Conversion.GetPageSize(pdfBytes, new Index(pageIndex));
        int width = (int)(pageSize.Width * dpi / 72.0);  // PDF uses 72 points per inch
        int height = (int)(pageSize.Height * dpi / 72.0);

        // PDFtoImage ToImage - use RenderOptions for size
        var options = new RenderOptions(Dpi: dpi);

        // PDFtoImage returns SKBitmap, convert to System.Drawing.Bitmap
        using SKBitmap skBitmap = Conversion.ToImage(pdfBytes, new Index(pageIndex), options: options);

        // Convert SKBitmap to System.Drawing.Bitmap
        using var skImage = SKImage.FromBitmap(skBitmap);
        using var skData = skImage.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(skData.ToArray());
        var bmp = new Bitmap(ms);

        // Set DPI metadata
        bmp.SetResolution(dpi, dpi);

        return (bmp, totalPages);
    }

    /// <summary>Result of one extraction pass — useful for the status bar.</summary>
    public readonly record struct ExtractionStats(
        int WordsRecognised,
        int ReferenceDesignatorsFound,
        int NetLabelsFound,
        int TracedNets,
        int Connections,
        long ElapsedMs);

    /// <summary>Full extraction output, including the raw OCR words and the
    /// classified subsets — the UI uses these to draw debug overlays on the
    /// schematic image so the user can see exactly what Tesseract picked up.</summary>
    public sealed class ExtractionResult
    {
        public required ExtractionStats Stats { get; init; }
        public required IReadOnlyList<OcrEngine.Word> AllWords { get; init; }
        public required IReadOnlyDictionary<string, OcrEngine.Word> Designators { get; init; }
        public required IReadOnlyDictionary<string, OcrEngine.Word> NetLabels { get; init; }
        /// <summary>Designator text → detected symbol bbox (the largest non-letter
        /// CC near the text). Missing key = no symbol found. Used by the UI to
        /// draw a blue rectangle around each detected component symbol.</summary>
        public required IReadOnlyDictionary<string, Rectangle> SymbolBoxes { get; init; }
        /// <summary>Raw YOLO detections for debug overlay (magenta boxes).</summary>
        public required IReadOnlyList<SymbolDetector.SymbolHit> YoloHits { get; init; }
        /// <summary>Detected pins where wires connect to symbol edges.</summary>
        public required IReadOnlyList<WireTracer.DetectedPin> Pins { get; init; }
        /// <summary>Wire segments with their bounding boxes.</summary>
        public required IReadOnlyList<WireTracer.WireSegment> Wires { get; init; }
        /// <summary>Traced wire paths from pin to pin or junction.</summary>
        public required IReadOnlyList<WireTracer.TracedWire> TracedWires { get; init; }
        /// <summary>Wire junctions where multiple wires meet.</summary>
        public required IReadOnlyList<WireTracer.WireJunction> Junctions { get; init; }
    }

    // -----------------------------------------------------------------------
    //  Phase 1 Result: OCR + YOLO detection (for multi-step workflow)
    // -----------------------------------------------------------------------
    /// <summary>Result of Phase 1: OCR + YOLO detection.
    /// Allows manual editing of symbol boxes before proceeding to pin detection.</summary>
    public sealed class Phase1ExtractionResult
    {
        public required IReadOnlyList<OcrEngine.Word> AllWords { get; init; }
        public required IReadOnlyDictionary<string, OcrEngine.Word> Designators { get; init; }
        public required IReadOnlyDictionary<string, OcrEngine.Word> NetLabels { get; init; }
        public required IReadOnlyList<WireTracer.TextBoxInfo> TextBoxes { get; init; }
        public required WireTracer.Phase1Result TracePhase1 { get; init; }
        public required long ElapsedMs { get; init; }
    }

    // -----------------------------------------------------------------------
    //  Phase 2 Result: Pin detection (for multi-step workflow)
    // -----------------------------------------------------------------------
    /// <summary>Result of Phase 2: Pin detection.
    /// Allows manual editing of pins before proceeding to wire tracing.</summary>
    public sealed class Phase2ExtractionResult
    {
        public required Phase1ExtractionResult Phase1 { get; init; }
        public required WireTracer.Phase2Result TracePhase2 { get; init; }
        public required long ElapsedMs { get; init; }
    }

    /// <summary>
    /// Run OCR over <paramref name="processed"/> (typically the user's
    /// adjusted/binarised image), classify the recognised words into
    /// designators and net labels, then run a wire-tracing pass to build
    /// best-guess connectivity. The result replaces <paramref name="netlist"/>'s
    /// Components and Nets; the caller is expected to re-apply manual edits
    /// after extraction.
    /// </summary>
    public static ExtractionResult ExtractFromBitmap(Bitmap processed, Netlist netlist)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var words = OcrEngine.RecognizeWords(processed);

        // ---- Classify OCR words into designators and net labels ----
        var refs = new Dictionary<string, OcrEngine.Word>(StringComparer.Ordinal);
        var nets = new Dictionary<string, OcrEngine.Word>(StringComparer.Ordinal);

        foreach (var w in words)
        {
            string cleaned = w.Text.Trim().Trim(':', ',', '.', ';');
            string upper = cleaned.ToUpperInvariant();

            if (OcrEngine.ReferenceDesignatorRegex.IsMatch(upper))
            {
                if (!refs.ContainsKey(upper))
                    refs[upper] = w with { Text = upper };
            }
            else if (OcrEngine.IsLikelyNetLabel(upper))
            {
                if (!nets.ContainsKey(upper))
                    nets[upper] = w with { Text = upper };
            }
        }

        // ---- Build the input list for the wire tracer ----
        var textBoxes = new List<WireTracer.TextBoxInfo>(refs.Count + nets.Count);
        foreach (var kv in refs)
            textBoxes.Add(new WireTracer.TextBoxInfo(
                kv.Key, kv.Value.Bounds, WireTracer.TextKind.Designator));
        foreach (var kv in nets)
            textBoxes.Add(new WireTracer.TextBoxInfo(
                kv.Key, kv.Value.Bounds, WireTracer.TextKind.NetLabel));

        // ---- Trace wires ----
        var traceResult = WireTracer.Trace(processed, textBoxes);
        var tracedGroups = traceResult.Nets;
        var traceStats = traceResult.Stats;

        // Merge groups by name — a schematic typically has many "GND" labels
        // that are all logically the same net, even if the tracer's CC pass
        // saw them as separate groups (they're separate ground stubs on the
        // page, but logically one net).
        var membersByName = new Dictionary<string, List<WireTracer.TextBoxInfo>>(StringComparer.Ordinal);
        foreach (var grp in tracedGroups)
        {
            if (!membersByName.TryGetValue(grp.Name, out var list))
            {
                list = new List<WireTracer.TextBoxInfo>();
                membersByName[grp.Name] = list;
            }
            list.AddRange(grp.Members);
        }

        // ---- Build components, sorted by prefix+number ----
        netlist.Components.Clear();
        var componentByRef = new Dictionary<string, NetlistComponent>(StringComparer.Ordinal);

        var orderedRefs = refs.Values
            .OrderBy(w => SplitPrefix(w.Text).prefix, StringComparer.Ordinal)
            .ThenBy(w => SplitPrefix(w.Text).number)
            .ToList();
        foreach (var w in orderedRefs)
        {
            var c = new NetlistComponent { Reference = w.Text };
            componentByRef[w.Text] = c;
            netlist.Components.Add(c);
        }

        // ---- Build nets, power rails first, then alphabetical ----
        netlist.Nets.Clear();
        int connections = 0;
        var orderedNetNames = membersByName.Keys
            .OrderBy(n => PowerNetRank(n))
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var netName in orderedNetNames)
        {
            netlist.Nets.Add(new NetlistNet { Name = netName });

            // For each designator the tracer associated with this net, add
            // ONE pin to the component pointing at this net. Sequential pin
            // numbers — true pin numbers need symbol detection (future step).
            var memberRefs = membersByName[netName]
                .Where(m => m.Kind == WireTracer.TextKind.Designator)
                .Select(m => m.Text)
                .Distinct(StringComparer.Ordinal);

            foreach (var refName in memberRefs)
            {
                if (!componentByRef.TryGetValue(refName, out var comp)) continue;
                int pinNumber = comp.Pins.Count + 1;
                comp.Pins.Add(new NetlistPin
                {
                    Number = pinNumber.ToString(),
                    Net = netName,
                });
                connections++;
            }
        }

        sw.Stop();

        // ---- Refresh diagnostic notes ----
        netlist.Notes.RemoveAll(n => n.StartsWith("OCR:", StringComparison.Ordinal)
                                  || n.StartsWith("Trace:", StringComparison.Ordinal)
                                  || n.StartsWith("Click \"Extract", StringComparison.Ordinal));
        netlist.Notes.Add($"OCR: recognised {words.Count} word(s).");
        netlist.Notes.Add($"OCR: {refs.Count} reference designator(s), {nets.Count} net label(s).");
        string yoloStatus = traceStats.YoloLoaded
            ? $"YOLO: {traceStats.YoloRawDetections} raw detections"
            : "YOLO: not loaded";
        string symbolNote = traceStats.SymbolsViaYolo > 0
            ? $"{traceStats.SymbolsFound} symbol(s) located ({traceStats.SymbolsViaYolo} YOLO, {traceStats.SymbolsViaGeometric} geometric)"
            : $"{traceStats.SymbolsFound} symbol(s) located ({yoloStatus})";
        netlist.Notes.Add($"Trace: {traceStats.ConnectedComponents} CC(s), " +
                          $"{symbolNote}, " +
                          $"{traceStats.Nets} traced group(s) → {membersByName.Count} named net(s), " +
                          $"{traceStats.IsolatedTextBoxes} isolated text box(es), " +
                          $"{connections} pin↔net connection(s), " +
                          $"trace took {traceStats.ElapsedMs} ms.");
        if (!string.IsNullOrEmpty(traceStats.YoloDebugInfo))
            netlist.Notes.Add($"YOLO debug: {traceStats.YoloDebugInfo}");
        if (words.Count > 0)
        {
            var lowConf = words.OrderBy(w => w.Confidence).Take(10).ToList();
            netlist.Notes.Add($"OCR: lowest-confidence sample → " +
                string.Join(", ", lowConf.Select(w => $"\"{w.Text}\"@{w.Confidence:F2}")));
        }

        var stats = new ExtractionStats(
            words.Count, refs.Count, nets.Count,
            membersByName.Count, connections,
            sw.ElapsedMilliseconds);

        return new ExtractionResult
        {
            Stats = stats,
            AllWords = words,
            Designators = refs,
            NetLabels = nets,
            SymbolBoxes = traceResult.SymbolBoxes,
            YoloHits = traceResult.YoloHits,
            Pins = traceResult.Pins,
            Wires = traceResult.Wires,
            TracedWires = traceResult.TracedWires,
            Junctions = traceResult.Junctions,
        };
    }

    // -----------------------------------------------------------------------
    //  PHASE 1: OCR + YOLO detection (multi-step workflow)
    // -----------------------------------------------------------------------
    /// <summary>Phase 1: Run OCR and YOLO detection.
    /// Returns intermediate result for manual symbol box editing.</summary>
    public static Phase1ExtractionResult ExtractPhase1(Bitmap processed)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var words = OcrEngine.RecognizeWords(processed);

        // Classify OCR words into designators and net labels
        var refs = new Dictionary<string, OcrEngine.Word>(StringComparer.Ordinal);
        var nets = new Dictionary<string, OcrEngine.Word>(StringComparer.Ordinal);

        foreach (var w in words)
        {
            string cleaned = w.Text.Trim().Trim(':', ',', '.', ';');
            string upper = cleaned.ToUpperInvariant();

            if (OcrEngine.ReferenceDesignatorRegex.IsMatch(upper))
            {
                if (!refs.ContainsKey(upper))
                    refs[upper] = w with { Text = upper };
            }
            else if (OcrEngine.IsLikelyNetLabel(upper))
            {
                if (!nets.ContainsKey(upper))
                    nets[upper] = w with { Text = upper };
            }
        }

        // Build the input list for the wire tracer
        var textBoxes = new List<WireTracer.TextBoxInfo>(refs.Count + nets.Count);
        foreach (var kv in refs)
            textBoxes.Add(new WireTracer.TextBoxInfo(
                kv.Key, kv.Value.Bounds, WireTracer.TextKind.Designator));
        foreach (var kv in nets)
            textBoxes.Add(new WireTracer.TextBoxInfo(
                kv.Key, kv.Value.Bounds, WireTracer.TextKind.NetLabel));

        // Run Phase 1 of wire tracer
        var tracePhase1 = WireTracer.TracePhase1(processed, textBoxes);

        sw.Stop();
        return new Phase1ExtractionResult
        {
            AllWords = words,
            Designators = refs,
            NetLabels = nets,
            TextBoxes = textBoxes,
            TracePhase1 = tracePhase1,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    // -----------------------------------------------------------------------
    //  PHASE 2: Pin detection (multi-step workflow)
    // -----------------------------------------------------------------------
    /// <summary>Phase 2: Run pin detection.
    /// Takes Phase 1 result plus any manually added symbol boxes.
    /// Returns intermediate result for manual pin editing.</summary>
    public static Phase2ExtractionResult ExtractPhase2(
        Phase1ExtractionResult phase1,
        IReadOnlyList<Rectangle>? manualSymbolBoxes = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var tracePhase2 = WireTracer.TracePhase2(phase1.TracePhase1, manualSymbolBoxes);

        sw.Stop();
        return new Phase2ExtractionResult
        {
            Phase1 = phase1,
            TracePhase2 = tracePhase2,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    // -----------------------------------------------------------------------
    //  PHASE 3: Wire tracing (multi-step workflow)
    // -----------------------------------------------------------------------
    /// <summary>Phase 3: Run wire tracing and build netlist.
    /// Takes Phase 2 result plus any manually added pins and remaining detected pins.
    /// If remainingDetectedPins is provided, it replaces the detected pins from phase2.
    /// Returns the final ExtractionResult.</summary>
    public static ExtractionResult ExtractPhase3(
        Phase2ExtractionResult phase2,
        Netlist netlist,
        IReadOnlyList<WireTracer.DetectedPin>? manualPins = null,
        IReadOnlyList<WireTracer.DetectedPin>? remainingDetectedPins = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var phase1 = phase2.Phase1;

        // Run Phase 3 of wire tracer, passing remaining detected pins if available
        var traceResult = WireTracer.TracePhase3(phase2.TracePhase2, manualPins, remainingDetectedPins: remainingDetectedPins);
        var tracedGroups = traceResult.Nets;

        // Merge groups by name
        var membersByName = new Dictionary<string, List<WireTracer.TextBoxInfo>>(StringComparer.Ordinal);
        foreach (var grp in tracedGroups)
        {
            if (!membersByName.TryGetValue(grp.Name, out var list))
            {
                list = new List<WireTracer.TextBoxInfo>();
                membersByName[grp.Name] = list;
            }
            list.AddRange(grp.Members);
        }

        // Build components, sorted by prefix+number
        netlist.Components.Clear();
        var componentByRef = new Dictionary<string, NetlistComponent>(StringComparer.Ordinal);

        var orderedRefs = phase1.Designators.Values
            .OrderBy(w => SplitPrefix(w.Text).prefix, StringComparer.Ordinal)
            .ThenBy(w => SplitPrefix(w.Text).number)
            .ToList();
        foreach (var w in orderedRefs)
        {
            var c = new NetlistComponent { Reference = w.Text };
            componentByRef[w.Text] = c;
            netlist.Components.Add(c);
        }

        // Build nets, power rails first, then alphabetical
        netlist.Nets.Clear();
        int connections = 0;
        var orderedNetNames = membersByName.Keys
            .OrderBy(n => PowerNetRank(n))
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var netName in orderedNetNames)
        {
            netlist.Nets.Add(new NetlistNet { Name = netName });

            var memberRefs = membersByName[netName]
                .Where(m => m.Kind == WireTracer.TextKind.Designator)
                .Select(m => m.Text)
                .Distinct(StringComparer.Ordinal);

            foreach (var refName in memberRefs)
            {
                if (!componentByRef.TryGetValue(refName, out var comp)) continue;
                int pinNumber = comp.Pins.Count + 1;
                comp.Pins.Add(new NetlistPin
                {
                    Number = pinNumber.ToString(),
                    Net = netName,
                });
                connections++;
            }
        }

        sw.Stop();
        long totalElapsed = phase1.ElapsedMs + phase2.ElapsedMs + sw.ElapsedMilliseconds;

        // Refresh diagnostic notes
        netlist.Notes.RemoveAll(n => n.StartsWith("OCR:", StringComparison.Ordinal)
                                  || n.StartsWith("Trace:", StringComparison.Ordinal)
                                  || n.StartsWith("Click \"Extract", StringComparison.Ordinal)
                                  || n.StartsWith("Step ", StringComparison.Ordinal));
        netlist.Notes.Add($"OCR: recognised {phase1.AllWords.Count} word(s).");
        netlist.Notes.Add($"OCR: {phase1.Designators.Count} reference designator(s), {phase1.NetLabels.Count} net label(s).");
        var traceStats = traceResult.Stats;
        string yoloStatus = traceStats.YoloLoaded
            ? $"YOLO: {traceStats.YoloRawDetections} raw detections"
            : "YOLO: not loaded";
        string symbolNote = traceStats.SymbolsViaYolo > 0
            ? $"{traceStats.SymbolsFound} symbol(s) located ({traceStats.SymbolsViaYolo} YOLO, {traceStats.SymbolsViaGeometric} geometric)"
            : $"{traceStats.SymbolsFound} symbol(s) located ({yoloStatus})";
        netlist.Notes.Add($"Trace: {traceStats.ConnectedComponents} CC(s), " +
                          $"{symbolNote}, " +
                          $"{traceStats.Nets} traced group(s) → {membersByName.Count} named net(s), " +
                          $"{traceStats.IsolatedTextBoxes} isolated text box(es), " +
                          $"{connections} pin↔net connection(s), " +
                          $"total time {totalElapsed} ms.");

        var stats = new ExtractionStats(
            phase1.AllWords.Count, phase1.Designators.Count, phase1.NetLabels.Count,
            membersByName.Count, connections,
            totalElapsed);

        return new ExtractionResult
        {
            Stats = stats,
            AllWords = phase1.AllWords,
            Designators = phase1.Designators,
            NetLabels = phase1.NetLabels,
            SymbolBoxes = traceResult.SymbolBoxes,
            YoloHits = traceResult.YoloHits,
            Pins = traceResult.Pins,
            Wires = traceResult.Wires,
            TracedWires = traceResult.TracedWires,
            Junctions = traceResult.Junctions,
        };
    }

    /// <summary>Sort rank for a net name — lower comes first. Returns 0 for
    /// canonical power nets, 1 for anything else. Keeps GND/VCC/3V3 etc. at
    /// the top of the netlist regardless of alphabetical order.</summary>
    private static int PowerNetRank(string netName) => netName switch
    {
        "GND" or "AGND" or "DGND" or "GNDA" or "GNDD" or "PGND" or "SGND" => 0,
        "VCC" or "VDD" or "VSS" or "VBUS" or "VBAT" or "VIN" or "VOUT"    => 0,
        "3V3" or "+3V3" or "+3.3V" or "+5V" or "+12V" or "-12V" or "+1V8" => 0,
        _ => 1,
    };

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
