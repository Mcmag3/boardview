using System.Drawing.Imaging;

namespace BoardviewBuilder;

/// <summary>
/// Wire tracer for schematic raster images, v2 — now with symbol detection.
///
/// Pipeline:
///   1. Binarise the processed bitmap (dark pixels = "ink": wires + text + symbols).
///   2. Run two-pass connected-component (CC) labelling on the ink.
///   3. Compute bounding boxes for every CC, and record which CCs are
///      "letter CCs" (intersect any OCR text bbox). The union of those is
///      the FORBIDDEN set when looking for a symbol.
///   4. For each DESIGNATOR text box: find the largest non-forbidden CC
///      whose pixels fall inside an expanded search region around the text.
///      That CC is the component symbol (resistor zig-zag, capacitor plates,
///      transistor circle, IC rectangle, …). Record its bbox.
///   5. For each NET LABEL: keep the bbox-ring lookup — net labels typically
///      sit next to a wire stub, so the wire is in the ring around the text.
///   6. Group text boxes that share a CC label → same electrical net.
///   7. Name each net from the first net-label member; auto-name "N$&lt;n&gt;"
///      otherwise. Multiple groups with the same name get merged downstream
///      by the caller (many "GND" labels = one net).
///
/// Known limitation (to be fixed by Option D in the next step):
///   In the binarised image the SYMBOL BODY connects its wires into one
///   single CC — so a resistor between GND and VCC has its symbol CC =
///   {GND wires ∪ resistor body ∪ VCC wires}, and we end up treating the
///   two electrically-distinct nets as one. Option D will mask the symbol
///   body out before tracing, leaving the two wire stubs as separate CCs.
/// </summary>
public static class WireTracer
{
    public enum TextKind { Designator, NetLabel }

    /// <summary>A single OCR text box fed into the tracer.</summary>
    public readonly record struct TextBoxInfo(string Text, Rectangle Bounds, TextKind Kind);

    /// <summary>A group of text boxes that the tracer believes are on the
    /// same electrical net. <see cref="Name"/> comes from the first net-label
    /// member, or is auto-generated as "N$&lt;n&gt;" otherwise.</summary>
    public sealed class TracedNet
    {
        public string Name { get; set; } = "";
        public List<TextBoxInfo> Members { get; } = new();
    }

    /// <summary>A detected pin where a wire connects to a symbol.</summary>
    public readonly record struct DetectedPin(
        string Designator,      // e.g. "R1", "C2"
        Point Location,         // center point of the pin
        int WireCC,             // CC label of the wire connected to this pin
        string Side);           // "Top", "Bottom", "Left", "Right"

    /// <summary>A traced wire path - a list of points from one pin/junction to another.</summary>
    public sealed class TracedWire
    {
        public List<Point> Path { get; } = new();
        public string? StartPin { get; set; }  // e.g. "R1.L" or null if junction
        public string? EndPin { get; set; }    // e.g. "R2.R" or null if junction
        public Rectangle Bounds { get; set; }
    }

    /// <summary>A wire junction where multiple wires meet.</summary>
    public readonly record struct WireJunction(Point Location, int ConnectionCount);

    /// <summary>A wire segment (bounding box of a single wire CC) - kept for compatibility.</summary>
    public readonly record struct WireSegment(
        int CCLabel,
        Rectangle Bounds,
        List<string> ConnectedPins);  // pins that connect to this wire

    /// <summary>Per-trace diagnostics for the status bar / notes.</summary>
    public readonly record struct TraceStats(
        int ConnectedComponents,
        int Nets,
        int IsolatedTextBoxes,
        int SymbolsFound,
        int SymbolsViaYolo,
        int SymbolsViaGeometric,
        bool YoloLoaded,
        int YoloRawDetections,
        string YoloDebugInfo,
        int PinsDetected,
        int WireSegments,
        long ElapsedMs);

    /// <summary>Full tracer output — nets + diagnostics + per-designator
    /// symbol bounding boxes (used by the UI to render the blue overlay).</summary>
    public sealed class TraceResult
    {
        public required List<TracedNet> Nets { get; init; }
        public required TraceStats Stats { get; init; }
        /// <summary>Designator text → bbox of the detected symbol CC. Missing
        /// keys = no symbol found near that designator (isolated label).</summary>
        public required IReadOnlyDictionary<string, Rectangle> SymbolBoxes { get; init; }
        /// <summary>Raw YOLO detections (for debug overlay).</summary>
        public required IReadOnlyList<SymbolDetector.SymbolHit> YoloHits { get; init; }
        /// <summary>Detected pins where wires connect to symbols.</summary>
        public required IReadOnlyList<DetectedPin> Pins { get; init; }
        /// <summary>Wire segments with their bounding boxes.</summary>
        public required IReadOnlyList<WireSegment> Wires { get; init; }
        /// <summary>Traced wire paths.</summary>
        public required IReadOnlyList<TracedWire> TracedWires { get; init; }
        /// <summary>Wire junctions where multiple wires meet.</summary>
        public required IReadOnlyList<WireJunction> Junctions { get; init; }
    }

    // -----------------------------------------------------------------------
    //  Phase 1 Result: OCR + YOLO detection (before pin detection)
    // -----------------------------------------------------------------------
    /// <summary>Result of Phase 1: binarization, CC labeling, YOLO detection.
    /// This intermediate result allows manual editing of symbol boxes before
    /// running pin detection in Phase 2.</summary>
    public sealed class Phase1Result
    {
        /// <summary>Binarized ink mask (dark pixels = true).</summary>
        public required bool[] Ink { get; init; }
        /// <summary>Image width.</summary>
        public required int Width { get; init; }
        /// <summary>Image height.</summary>
        public required int Height { get; init; }
        /// <summary>CC labels for each pixel.</summary>
        public required int[] Labels { get; init; }
        /// <summary>Number of connected components.</summary>
        public required int CCCount { get; init; }
        /// <summary>Bounding boxes per CC.</summary>
        public required Rectangle[] CCBoxes { get; init; }
        /// <summary>CC labels that are forbidden (text letters).</summary>
        public required HashSet<int> Forbidden { get; init; }
        /// <summary>All text bounding boxes.</summary>
        public required List<Rectangle> AllTextBoxes { get; init; }
        /// <summary>Raw YOLO detections.</summary>
        public required List<SymbolDetector.SymbolHit> YoloHits { get; init; }
        /// <summary>Designator text → detected symbol bbox.</summary>
        public required Dictionary<string, Rectangle> SymbolBoxes { get; init; }
        /// <summary>Per-text CC assignment.</summary>
        public required int[] TextCC { get; init; }
        /// <summary>Original texts input.</summary>
        public required IReadOnlyList<TextBoxInfo> Texts { get; init; }
        /// <summary>Stats from phase 1.</summary>
        public required int SymbolsFound { get; init; }
        public required int SymbolsViaYolo { get; init; }
        public required int SymbolsViaGeometric { get; init; }
        public required int YoloRawCount { get; init; }
        public required string YoloDebug { get; init; }
        /// <summary>Binary threshold used.</summary>
        public required int BinaryThreshold { get; init; }
    }

    // -----------------------------------------------------------------------
    //  Phase 2 Result: Pin detection (before wire tracing)
    // -----------------------------------------------------------------------
    /// <summary>Result of Phase 2: detected pins at symbol edges.
    /// This intermediate result allows manual editing of pins before
    /// running wire tracing in Phase 3.</summary>
    public sealed class Phase2Result
    {
        /// <summary>Phase 1 result (preserved for Phase 3).</summary>
        public required Phase1Result Phase1 { get; init; }
        /// <summary>Detected pins where wires connect to symbols.</summary>
        public required List<DetectedPin> Pins { get; init; }
        /// <summary>Ink mask with text erased (for wire tracing).</summary>
        public required bool[] InkForWires { get; init; }
    }

    // Singleton YOLO detector — loaded once on first use, cached for the session.
    private static SymbolDetectorYolo? _yoloDetector;
    private static bool _yoloLoadAttempted;

    /// <summary>Trace wire connectivity. <paramref name="processed"/> can be
    /// any pixel format — it'll be internally rebound to 24bpp RGB for the
    /// binarisation pass.</summary>
    public static TraceResult Trace(
        Bitmap processed,
        IReadOnlyList<TextBoxInfo> texts,
        int netLabelRingMargin = 12,
        int binaryThreshold = 160)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int w = processed.Width;
        int h = processed.Height;

        // 1) Binarise.
        bool[] ink = Binarise(processed, binaryThreshold);

        // 2) CC labelling.
        int[] labels = ConnectedComponents(ink, w, h, out int ccCount);

        // 3) Per-CC bboxes + forbidden (letter) CC set.
        var ccBoxes = ComputeCCBoxes(labels, w, h, ccCount);
        var forbidden = new HashSet<int>();
        for (int i = 0; i < texts.Count; i++)
            foreach (var lbl in CollectCCsInside(labels, w, h, texts[i].Bounds))
                forbidden.Add(lbl);

        // Collect ALL text bboxes once for the SymbolDetector mask step.
        var allTextBoxes = texts.Select(t => t.Bounds).ToList();

        // Try to load YOLO detector (once per session).
        if (!_yoloLoadAttempted)
        {
            _yoloLoadAttempted = true;
            _yoloDetector = SymbolDetectorYolo.TryLoad("models/symbols.onnx", "models/symbols.classes.txt");
        }

        // Run YOLO detection ONCE on the full image if available.
        List<SymbolDetector.SymbolHit>? yoloHits = null;
        Dictionary<char, List<SymbolDetector.SymbolHit>>? yoloByClass = null;
        int yoloRawCount = 0;
        string yoloDebug = "";
        if (_yoloDetector != null)
        {
            // Use very low confidence threshold (0.05) since we have limited training data
            yoloHits = _yoloDetector.Detect(processed, confThreshold: 0.05f);
            yoloRawCount = yoloHits.Count;
            yoloDebug = _yoloDetector.LastDebugInfo;
            // Build spatial lookup by first letter of class name
            yoloByClass = new Dictionary<char, List<SymbolDetector.SymbolHit>>();
            foreach (var hit in yoloHits)
            {
                char key = hit.Kind.Length > 0 ? char.ToUpperInvariant(hit.Kind[0]) : '?';
                if (!yoloByClass.TryGetValue(key, out var list))
                {
                    list = new List<SymbolDetector.SymbolHit>();
                    yoloByClass[key] = list;
                }
                list.Add(hit);
            }
        }

        // 4+5) Per-text CC assignment.
        var textCC = new int[texts.Count];
        var symbolBoxes = new Dictionary<string, Rectangle>(StringComparer.Ordinal);
        int symbolsFound = 0;
        int symbolsViaYolo = 0;
        int symbolsViaGeometric = 0;

        for (int i = 0; i < texts.Count; i++)
        {
            if (texts[i].Kind == TextKind.NetLabel)
            {
                // Net label: dominant CC in the ring just outside the bbox.
                textCC[i] = FindDominantCCInRing(labels, w, h, texts[i].Bounds, netLabelRingMargin);
            }
            else
            {
                // Designator: try YOLO first, then geometric fallback.
                string desig = texts[i].Text;
                char letter = desig.Length > 0 ? char.ToUpperInvariant(desig[0]) : '?';
                var textBbox = texts[i].Bounds;

                Rectangle? shapeBbox = null;
                bool usedYolo = false;

                // Try YOLO: find the closest detection of matching class
                // whose centre is within ~6× text size of the designator.
                if (yoloByClass != null && yoloByClass.TryGetValue(letter, out var candidates))
                {
                    float textCx = textBbox.X + textBbox.Width * 0.5f;
                    float textCy = textBbox.Y + textBbox.Height * 0.5f;
                    float maxDist = Math.Max(textBbox.Width, textBbox.Height) * 6f;

                    SymbolDetector.SymbolHit? bestHit = null;
                    float bestDist = float.MaxValue;

                    foreach (var hit in candidates)
                    {
                        float hitCx = hit.Bounds.X + hit.Bounds.Width * 0.5f;
                        float hitCy = hit.Bounds.Y + hit.Bounds.Height * 0.5f;
                        float dist = MathF.Sqrt((hitCx - textCx) * (hitCx - textCx) +
                                                (hitCy - textCy) * (hitCy - textCy));
                        if (dist < maxDist && dist < bestDist)
                        {
                            bestDist = dist;
                            bestHit = hit;
                        }
                    }

                    if (bestHit != null)
                    {
                        shapeBbox = bestHit.Bounds;
                        usedYolo = true;
                    }
                }

                // Geometric fallback if YOLO didn't find anything
                if (!shapeBbox.HasValue)
                {
                    if (letter == 'R')
                    {
                        var hit = SymbolDetector.FindResistorNear(
                            processed, textBbox, allTextBoxes, binaryThreshold);
                        if (hit != null) shapeBbox = hit.Bounds;
                    }
                    else if (letter == 'C')
                    {
                        var hit = SymbolDetector.FindCapacitorNear(
                            processed, textBbox, allTextBoxes, binaryThreshold);
                        if (hit != null) shapeBbox = hit.Bounds;
                    }
                }

                if (shapeBbox.HasValue)
                {
                    // Shape detector found a real symbol. Look up which CC
                    // lives inside that bbox so connectivity tracing still
                    // works — picks the largest non-letter CC that overlaps.
                    int symCC = FindDominantCCInside(labels, w, h, shapeBbox.Value, forbidden);
                    textCC[i] = symCC;
                    symbolBoxes[desig] = shapeBbox.Value;
                    symbolsFound++;
                    if (usedYolo) symbolsViaYolo++;
                    else symbolsViaGeometric++;
                }
                else
                {
                    // Fallback: largest non-forbidden CC in an expanded search box.
                    int symCC = FindSymbolCC(labels, w, h, textBbox, forbidden);
                    textCC[i] = symCC;
                    if (symCC != 0)
                    {
                        symbolBoxes[desig] = ccBoxes[symCC];
                        symbolsFound++;
                        symbolsViaGeometric++;
                    }
                }
            }
        }

        // 6) NEW: Pin detection - find where wires cross YOLO box edges
        //    Create a copy of ink mask with text erased, but KEEP ink that connects outside the text box
        //    (this preserves wires that pass through text areas like "+" signs)
        var inkForPins = (bool[])ink.Clone();
        foreach (var tb in allTextBoxes)
        {
            int margin = 2;
            int x0 = Math.Max(0, tb.X - margin);
            int y0 = Math.Max(0, tb.Y - margin);
            int x1 = Math.Min(w, tb.Right + margin);
            int y1 = Math.Min(h, tb.Bottom + margin);

            // Find all ink pixels inside this text box that connect to ink OUTSIDE the box
            // These are wires passing through - we keep them
            var keepPixels = new HashSet<int>();

            // BFS from each edge pixel that has ink, to find connected ink inside the box
            var visited = new bool[w * h];
            var queue = new Queue<Point>();

            // Seed from all edge pixels of the text box that have ink AND connect to ink outside
            // Top edge
            for (int x = x0; x < x1; x++)
            {
                if (y0 > 0 && ink[y0 * w + x] && ink[(y0 - 1) * w + x])
                    queue.Enqueue(new Point(x, y0));
            }
            // Bottom edge
            for (int x = x0; x < x1; x++)
            {
                if (y1 - 1 < h - 1 && ink[(y1 - 1) * w + x] && ink[y1 * w + x])
                    queue.Enqueue(new Point(x, y1 - 1));
            }
            // Left edge
            for (int y = y0; y < y1; y++)
            {
                if (x0 > 0 && ink[y * w + x0] && ink[y * w + (x0 - 1)])
                    queue.Enqueue(new Point(x0, y));
            }
            // Right edge
            for (int y = y0; y < y1; y++)
            {
                if (x1 - 1 < w - 1 && ink[y * w + (x1 - 1)] && ink[y * w + x1])
                    queue.Enqueue(new Point(x1 - 1, y));
            }

            // BFS to find all connected ink inside the box that connects to outside
            while (queue.Count > 0)
            {
                var pt = queue.Dequeue();
                int idx = pt.Y * w + pt.X;

                if (pt.X < x0 || pt.X >= x1 || pt.Y < y0 || pt.Y >= y1) continue;
                if (visited[idx]) continue;
                if (!ink[idx]) continue;

                visited[idx] = true;
                keepPixels.Add(idx);

                // Add neighbors (4-connected)
                queue.Enqueue(new Point(pt.X - 1, pt.Y));
                queue.Enqueue(new Point(pt.X + 1, pt.Y));
                queue.Enqueue(new Point(pt.X, pt.Y - 1));
                queue.Enqueue(new Point(pt.X, pt.Y + 1));
            }

            // Erase all ink inside the text box EXCEPT pixels that connect to outside (wires)
            for (int y = y0; y < y1; y++)
            {
                int rs = y * w;
                for (int x = x0; x < x1; x++)
                {
                    int idx = rs + x;
                    if (!keepPixels.Contains(idx))
                        inkForPins[idx] = false;
                }
            }
        }

        var detectedPins = new List<DetectedPin>();
        if (yoloHits != null)
        {
            foreach (var hit in yoloHits)
            {
                // Skip YOLO boxes that ARE text (YOLO box similar size to OCR text box)
                // Don't skip large symbols that CONTAIN text labels (like ICs with designator inside)
                bool isTextDetection = false;
                int yoloArea = hit.Bounds.Width * hit.Bounds.Height;
                foreach (var tb in allTextBoxes)
                {
                    int textArea = tb.Width * tb.Height;
                    // Skip if YOLO box is similar size to text box (within 3x)
                    // This catches false YOLO detections on text, but keeps large symbols with text inside
                    if (textArea > 0 && yoloArea < textArea * 3)
                    {
                        // Calculate intersection
                        int ix0 = Math.Max(hit.Bounds.X, tb.X);
                        int iy0 = Math.Max(hit.Bounds.Y, tb.Y);
                        int ix1 = Math.Min(hit.Bounds.Right, tb.Right);
                        int iy1 = Math.Min(hit.Bounds.Bottom, tb.Bottom);
                        if (ix1 > ix0 && iy1 > iy0)
                        {
                            int intersection = (ix1 - ix0) * (iy1 - iy0);
                            if (intersection > textArea * 0.5)
                            {
                                isTextDetection = true;
                                break;
                            }
                        }
                    }
                }
                if (isTextDetection) continue;

                // Use the YOLO class as designator (e.g., "R", "C", "Q")
                // Try to find matching designator text nearby
                string desig = hit.Kind;

                // Find the closest designator text to this YOLO box
                float hitCx = hit.Bounds.X + hit.Bounds.Width * 0.5f;
                float hitCy = hit.Bounds.Y + hit.Bounds.Height * 0.5f;
                float maxDist = Math.Max(hit.Bounds.Width, hit.Bounds.Height) * 3f;

                foreach (var text in texts)
                {
                    if (text.Kind != TextKind.Designator) continue;
                    string textUpper = text.Text.ToUpperInvariant();
                    char textLetter = textUpper.Length > 0 ? textUpper[0] : '?';
                    char hitLetter = hit.Kind.Length > 0 ? char.ToUpperInvariant(hit.Kind[0]) : '?';
                    if (textLetter != hitLetter) continue;

                    float textCx = text.Bounds.X + text.Bounds.Width * 0.5f;
                    float textCy = text.Bounds.Y + text.Bounds.Height * 0.5f;
                    float dist = MathF.Sqrt((hitCx - textCx) * (hitCx - textCx) + (hitCy - textCy) * (hitCy - textCy));
                    if (dist < maxDist)
                    {
                        desig = textUpper;
                        break;
                    }
                }

                // Detect pins at YOLO box edges
                // Use inkForPins (text erased) for edge detection, but original ink for outside check
                var edgePins = DetectPinsAtYoloEdgesSimple(inkForPins, ink, w, h, hit.Bounds, desig);
                detectedPins.AddRange(edgePins);

                // Second pass: detect lines crossing the YOLO box boundary
                // Use ORIGINAL ink (before OCR erasure) to catch wires that pass through text areas
                var linePins = DetectPinsByLineCrossing(ink, w, h, hit.Bounds, desig, detectedPins);
                detectedPins.AddRange(linePins);
            }
        }

        // 7) Trace wires from pins - follow ink to find wire paths and junctions
        var (tracedWires, junctions) = TraceWiresFromPins(inkForPins, w, h, detectedPins, yoloHits);
        var wireSegments = new List<WireSegment>();

        // 8) Group by CC label (using original labels for net grouping).
        var byCC = new Dictionary<int, TracedNet>();
        int isolated = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            int cc = textCC[i];
            if (cc == 0) { isolated++; continue; }
            if (!byCC.TryGetValue(cc, out var grp))
            {
                grp = new TracedNet();
                byCC[cc] = grp;
            }
            grp.Members.Add(texts[i]);
        }

        // 9) Name each group.
        int autoIdx = 1;
        foreach (var grp in byCC.Values)
        {
            var netLabel = grp.Members
                .Where(m => m.Kind == TextKind.NetLabel)
                .Select(m => (string?)m.Text)
                .FirstOrDefault();
            grp.Name = netLabel ?? $"N${autoIdx++}";
        }

        sw.Stop();
        var stats = new TraceStats(ccCount, byCC.Count, isolated, symbolsFound,
                                   symbolsViaYolo, symbolsViaGeometric,
                                   _yoloDetector != null, yoloRawCount, yoloDebug,
                                   detectedPins.Count, tracedWires.Count, sw.ElapsedMilliseconds);
        return new TraceResult
        {
            Nets = byCC.Values.ToList(),
            Stats = stats,
            SymbolBoxes = symbolBoxes,
            YoloHits = yoloHits ?? new List<SymbolDetector.SymbolHit>(),
            Pins = detectedPins,
            Wires = wireSegments,
            TracedWires = tracedWires,
            Junctions = junctions,
        };
    }

    // -----------------------------------------------------------------------
    //  PHASE 1: Binarization, CC labeling, YOLO detection, symbol association
    // -----------------------------------------------------------------------
    /// <summary>Phase 1: Run binarization, CC labeling, and YOLO detection.
    /// Returns intermediate result that allows manual editing of symbol boxes
    /// before proceeding to pin detection.</summary>
    public static Phase1Result TracePhase1(
        Bitmap processed,
        IReadOnlyList<TextBoxInfo> texts,
        int netLabelRingMargin = 12,
        int binaryThreshold = 160)
    {
        int w = processed.Width;
        int h = processed.Height;

        // 1) Binarise.
        bool[] ink = Binarise(processed, binaryThreshold);

        // 2) CC labelling.
        int[] labels = ConnectedComponents(ink, w, h, out int ccCount);

        // 3) Per-CC bboxes + forbidden (letter) CC set.
        var ccBoxes = ComputeCCBoxes(labels, w, h, ccCount);
        var forbidden = new HashSet<int>();
        for (int i = 0; i < texts.Count; i++)
            foreach (var lbl in CollectCCsInside(labels, w, h, texts[i].Bounds))
                forbidden.Add(lbl);

        // Collect ALL text bboxes once for the SymbolDetector mask step.
        var allTextBoxes = texts.Select(t => t.Bounds).ToList();

        // Try to load YOLO detector (once per session).
        if (!_yoloLoadAttempted)
        {
            _yoloLoadAttempted = true;
            _yoloDetector = SymbolDetectorYolo.TryLoad("models/symbols.onnx", "models/symbols.classes.txt");
        }

        // Run YOLO detection ONCE on the full image if available.
        List<SymbolDetector.SymbolHit> yoloHits = new();
        Dictionary<char, List<SymbolDetector.SymbolHit>>? yoloByClass = null;
        int yoloRawCount = 0;
        string yoloDebug = "";
        if (_yoloDetector != null)
        {
            yoloHits = _yoloDetector.Detect(processed, confThreshold: 0.05f);
            yoloRawCount = yoloHits.Count;
            yoloDebug = _yoloDetector.LastDebugInfo;
            yoloByClass = new Dictionary<char, List<SymbolDetector.SymbolHit>>();
            foreach (var hit in yoloHits)
            {
                char key = hit.Kind.Length > 0 ? char.ToUpperInvariant(hit.Kind[0]) : '?';
                if (!yoloByClass.TryGetValue(key, out var list))
                {
                    list = new List<SymbolDetector.SymbolHit>();
                    yoloByClass[key] = list;
                }
                list.Add(hit);
            }
        }

        // 4+5) Per-text CC assignment.
        var textCC = new int[texts.Count];
        var symbolBoxes = new Dictionary<string, Rectangle>(StringComparer.Ordinal);
        int symbolsFound = 0;
        int symbolsViaYolo = 0;
        int symbolsViaGeometric = 0;

        for (int i = 0; i < texts.Count; i++)
        {
            if (texts[i].Kind == TextKind.NetLabel)
            {
                textCC[i] = FindDominantCCInRing(labels, w, h, texts[i].Bounds, netLabelRingMargin);
            }
            else
            {
                string desig = texts[i].Text;
                char letter = desig.Length > 0 ? char.ToUpperInvariant(desig[0]) : '?';
                var textBbox = texts[i].Bounds;

                Rectangle? shapeBbox = null;
                bool usedYolo = false;

                if (yoloByClass != null && yoloByClass.TryGetValue(letter, out var candidates))
                {
                    float textCx = textBbox.X + textBbox.Width * 0.5f;
                    float textCy = textBbox.Y + textBbox.Height * 0.5f;
                    float maxDist = Math.Max(textBbox.Width, textBbox.Height) * 6f;

                    SymbolDetector.SymbolHit? bestHit = null;
                    float bestDist = float.MaxValue;

                    foreach (var hit in candidates)
                    {
                        float hitCx = hit.Bounds.X + hit.Bounds.Width * 0.5f;
                        float hitCy = hit.Bounds.Y + hit.Bounds.Height * 0.5f;
                        float dist = MathF.Sqrt((hitCx - textCx) * (hitCx - textCx) +
                                                (hitCy - textCy) * (hitCy - textCy));
                        if (dist < maxDist && dist < bestDist)
                        {
                            bestDist = dist;
                            bestHit = hit;
                        }
                    }

                    if (bestHit != null)
                    {
                        shapeBbox = bestHit.Bounds;
                        usedYolo = true;
                    }
                }

                if (!shapeBbox.HasValue)
                {
                    if (letter == 'R')
                    {
                        var hit = SymbolDetector.FindResistorNear(
                            processed, textBbox, allTextBoxes, binaryThreshold);
                        if (hit != null) shapeBbox = hit.Bounds;
                    }
                    else if (letter == 'C')
                    {
                        var hit = SymbolDetector.FindCapacitorNear(
                            processed, textBbox, allTextBoxes, binaryThreshold);
                        if (hit != null) shapeBbox = hit.Bounds;
                    }
                }

                if (shapeBbox.HasValue)
                {
                    int symCC = FindDominantCCInside(labels, w, h, shapeBbox.Value, forbidden);
                    textCC[i] = symCC;
                    symbolBoxes[desig] = shapeBbox.Value;
                    symbolsFound++;
                    if (usedYolo) symbolsViaYolo++;
                    else symbolsViaGeometric++;
                }
                else
                {
                    int symCC = FindSymbolCC(labels, w, h, textBbox, forbidden);
                    textCC[i] = symCC;
                    if (symCC != 0)
                    {
                        symbolBoxes[desig] = ccBoxes[symCC];
                        symbolsFound++;
                        symbolsViaGeometric++;
                    }
                }
            }
        }

        return new Phase1Result
        {
            Ink = ink,
            Width = w,
            Height = h,
            Labels = labels,
            CCCount = ccCount,
            CCBoxes = ccBoxes,
            Forbidden = forbidden,
            AllTextBoxes = allTextBoxes,
            YoloHits = yoloHits,
            SymbolBoxes = symbolBoxes,
            TextCC = textCC,
            Texts = texts,
            SymbolsFound = symbolsFound,
            SymbolsViaYolo = symbolsViaYolo,
            SymbolsViaGeometric = symbolsViaGeometric,
            YoloRawCount = yoloRawCount,
            YoloDebug = yoloDebug,
            BinaryThreshold = binaryThreshold,
        };
    }

    // -----------------------------------------------------------------------
    //  PHASE 2: Pin detection at symbol edges
    // -----------------------------------------------------------------------
    /// <summary>Phase 2: Detect pins at symbol edges.
    /// Takes Phase 1 result plus any manually added symbol boxes.
    /// Returns intermediate result that allows manual editing of pins
    /// before proceeding to wire tracing.</summary>
    public static Phase2Result TracePhase2(
        Phase1Result phase1,
        IReadOnlyList<Rectangle>? manualSymbolBoxes = null)
    {
        int w = phase1.Width;
        int h = phase1.Height;
        var ink = phase1.Ink;
        var allTextBoxes = phase1.AllTextBoxes;
        var texts = phase1.Texts;

        // Combine YOLO hits with manual symbol boxes
        var allSymbolBoxes = new List<SymbolDetector.SymbolHit>(phase1.YoloHits);
        if (manualSymbolBoxes != null)
        {
            foreach (var box in manualSymbolBoxes)
            {
                // Create a synthetic YOLO hit for manual boxes
                allSymbolBoxes.Add(new SymbolDetector.SymbolHit { Kind = "Manual", Bounds = box, Score = 1.0f });
            }
        }

        // Create ink mask with text erased but preserving through-wires
        var inkForPins = (bool[])ink.Clone();
        foreach (var tb in allTextBoxes)
        {
            int margin = 2;
            int x0 = Math.Max(0, tb.X - margin);
            int y0 = Math.Max(0, tb.Y - margin);
            int x1 = Math.Min(w, tb.Right + margin);
            int y1 = Math.Min(h, tb.Bottom + margin);

            var keepPixels = new HashSet<int>();
            var visited = new bool[w * h];
            var queue = new Queue<Point>();

            // Top edge
            for (int x = x0; x < x1; x++)
            {
                if (y0 > 0 && ink[y0 * w + x] && ink[(y0 - 1) * w + x])
                    queue.Enqueue(new Point(x, y0));
            }
            // Bottom edge
            for (int x = x0; x < x1; x++)
            {
                if (y1 - 1 < h - 1 && ink[(y1 - 1) * w + x] && ink[y1 * w + x])
                    queue.Enqueue(new Point(x, y1 - 1));
            }
            // Left edge
            for (int y = y0; y < y1; y++)
            {
                if (x0 > 0 && ink[y * w + x0] && ink[y * w + (x0 - 1)])
                    queue.Enqueue(new Point(x0, y));
            }
            // Right edge
            for (int y = y0; y < y1; y++)
            {
                if (x1 - 1 < w - 1 && ink[y * w + (x1 - 1)] && ink[y * w + x1])
                    queue.Enqueue(new Point(x1 - 1, y));
            }

            while (queue.Count > 0)
            {
                var pt = queue.Dequeue();
                int idx = pt.Y * w + pt.X;

                if (pt.X < x0 || pt.X >= x1 || pt.Y < y0 || pt.Y >= y1) continue;
                if (visited[idx]) continue;
                if (!ink[idx]) continue;

                visited[idx] = true;
                keepPixels.Add(idx);

                queue.Enqueue(new Point(pt.X - 1, pt.Y));
                queue.Enqueue(new Point(pt.X + 1, pt.Y));
                queue.Enqueue(new Point(pt.X, pt.Y - 1));
                queue.Enqueue(new Point(pt.X, pt.Y + 1));
            }

            for (int y = y0; y < y1; y++)
            {
                int rs = y * w;
                for (int x = x0; x < x1; x++)
                {
                    int idx = rs + x;
                    if (!keepPixels.Contains(idx))
                        inkForPins[idx] = false;
                }
            }
        }

        // Detect pins at symbol edges
        var detectedPins = new List<DetectedPin>();
        foreach (var hit in allSymbolBoxes)
        {
            // Skip YOLO boxes that ARE text
            bool isTextDetection = false;
            int yoloArea = hit.Bounds.Width * hit.Bounds.Height;
            foreach (var tb in allTextBoxes)
            {
                int textArea = tb.Width * tb.Height;
                if (textArea > 0 && yoloArea < textArea * 3)
                {
                    int ix0 = Math.Max(hit.Bounds.X, tb.X);
                    int iy0 = Math.Max(hit.Bounds.Y, tb.Y);
                    int ix1 = Math.Min(hit.Bounds.Right, tb.Right);
                    int iy1 = Math.Min(hit.Bounds.Bottom, tb.Bottom);
                    if (ix1 > ix0 && iy1 > iy0)
                    {
                        int intersection = (ix1 - ix0) * (iy1 - iy0);
                        if (intersection > textArea * 0.5)
                        {
                            isTextDetection = true;
                            break;
                        }
                    }
                }
            }
            if (isTextDetection) continue;

            // Find designator for this symbol
            string desig = hit.Kind;
            float hitCx = hit.Bounds.X + hit.Bounds.Width * 0.5f;
            float hitCy = hit.Bounds.Y + hit.Bounds.Height * 0.5f;
            float maxDist = Math.Max(hit.Bounds.Width, hit.Bounds.Height) * 3f;

            foreach (var text in texts)
            {
                if (text.Kind != TextKind.Designator) continue;
                string textUpper = text.Text.ToUpperInvariant();
                char textLetter = textUpper.Length > 0 ? textUpper[0] : '?';
                char hitLetter = hit.Kind.Length > 0 ? char.ToUpperInvariant(hit.Kind[0]) : '?';
                if (textLetter != hitLetter && hit.Kind != "Manual") continue;

                float textCx = text.Bounds.X + text.Bounds.Width * 0.5f;
                float textCy = text.Bounds.Y + text.Bounds.Height * 0.5f;
                float dist = MathF.Sqrt((hitCx - textCx) * (hitCx - textCx) + (hitCy - textCy) * (hitCy - textCy));
                if (dist < maxDist)
                {
                    desig = textUpper;
                    break;
                }
            }

            var edgePins = DetectPinsAtYoloEdgesSimple(inkForPins, ink, w, h, hit.Bounds, desig);
            detectedPins.AddRange(edgePins);

            var linePins = DetectPinsByLineCrossing(ink, w, h, hit.Bounds, desig, detectedPins);
            detectedPins.AddRange(linePins);
        }

        return new Phase2Result
        {
            Phase1 = phase1,
            Pins = detectedPins,
            InkForWires = inkForPins,
        };
    }

    // -----------------------------------------------------------------------
    //  PHASE 3: Wire tracing and net grouping
    // -----------------------------------------------------------------------
    /// <summary>Phase 3: Trace wires and build nets.
    /// Takes Phase 2 result plus any manually added pins.
    /// Returns the final TraceResult.</summary>
    public static TraceResult TracePhase3(
        Phase2Result phase2,
        IReadOnlyList<DetectedPin>? manualPins = null,
        int netLabelRingMargin = 12)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var phase1 = phase2.Phase1;
        int w = phase1.Width;
        int h = phase1.Height;

        // Combine detected pins with manual pins
        var allPins = new List<DetectedPin>(phase2.Pins);
        if (manualPins != null)
        {
            allPins.AddRange(manualPins);
        }

        // Trace wires from pins
        var (tracedWires, junctions) = TraceWiresFromPins(phase2.InkForWires, w, h, allPins, phase1.YoloHits);
        var wireSegments = new List<WireSegment>();

        // Group by CC label
        var byCC = new Dictionary<int, TracedNet>();
        int isolated = 0;
        for (int i = 0; i < phase1.Texts.Count; i++)
        {
            int cc = phase1.TextCC[i];
            if (cc == 0) { isolated++; continue; }
            if (!byCC.TryGetValue(cc, out var grp))
            {
                grp = new TracedNet();
                byCC[cc] = grp;
            }
            grp.Members.Add(phase1.Texts[i]);
        }

        // Name each group
        int autoIdx = 1;
        foreach (var grp in byCC.Values)
        {
            var netLabel = grp.Members
                .Where(m => m.Kind == TextKind.NetLabel)
                .Select(m => (string?)m.Text)
                .FirstOrDefault();
            grp.Name = netLabel ?? $"N${autoIdx++}";
        }

        sw.Stop();
        var stats = new TraceStats(
            phase1.CCCount, byCC.Count, isolated, phase1.SymbolsFound,
            phase1.SymbolsViaYolo, phase1.SymbolsViaGeometric,
            _yoloDetector != null, phase1.YoloRawCount, phase1.YoloDebug,
            allPins.Count, tracedWires.Count, sw.ElapsedMilliseconds);

        return new TraceResult
        {
            Nets = byCC.Values.ToList(),
            Stats = stats,
            SymbolBoxes = phase1.SymbolBoxes,
            YoloHits = phase1.YoloHits,
            Pins = allPins,
            Wires = wireSegments,
            TracedWires = tracedWires,
            Junctions = junctions,
        };
    }

    // -----------------------------------------------------------------------
    //  Binarisation
    // -----------------------------------------------------------------------
    private static bool[] Binarise(Bitmap src, int threshold)
    {
        int w = src.Width;
        int h = src.Height;
        var mask = new bool[w * h];
        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* p0 = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * stride;
                    int rowStart = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 3 + 0];
                        byte g = row[x * 3 + 1];
                        byte r = row[x * 3 + 2];
                        int luma = (r + g + b) / 3;
                        mask[rowStart + x] = luma < threshold;
                    }
                }
            }
        }
        finally { src.UnlockBits(data); }
        return mask;
    }

    // -----------------------------------------------------------------------
    //  Connected-components labelling (4-connectivity, two-pass union-find)
    // -----------------------------------------------------------------------
    private static int[] ConnectedComponents(bool[] mask, int w, int h, out int finalCount)
    {
        int[] labels = new int[w * h];
        var parent = new List<int> { 0 };

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }

        for (int y = 0; y < h; y++)
        {
            int rowStart = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rowStart + x;
                if (!mask[idx]) continue;

                int left = (x > 0 && mask[idx - 1]) ? labels[idx - 1] : 0;
                int up   = (y > 0 && mask[idx - w]) ? labels[idx - w] : 0;

                if (left == 0 && up == 0)
                {
                    int newLabel = parent.Count;
                    parent.Add(newLabel);
                    labels[idx] = newLabel;
                }
                else if (left != 0 && up == 0) labels[idx] = left;
                else if (left == 0 && up != 0) labels[idx] = up;
                else
                {
                    int min = Math.Min(left, up);
                    labels[idx] = min;
                    if (left != up) Union(left, up);
                }
            }
        }

        var remap = new Dictionary<int, int>();
        int next = 1;
        for (int i = 0; i < labels.Length; i++)
        {
            int lbl = labels[i];
            if (lbl == 0) continue;
            int root = Find(lbl);
            if (!remap.TryGetValue(root, out int compact))
            {
                compact = next++;
                remap[root] = compact;
            }
            labels[i] = compact;
        }
        finalCount = next - 1;
        return labels;
    }

    // -----------------------------------------------------------------------
    //  Per-CC bounding box (single O(W*H) pass)
    // -----------------------------------------------------------------------
    private static Rectangle[] ComputeCCBoxes(int[] labels, int w, int h, int ccCount)
    {
        var minX = new int[ccCount + 1];
        var minY = new int[ccCount + 1];
        var maxX = new int[ccCount + 1];
        var maxY = new int[ccCount + 1];
        for (int i = 1; i <= ccCount; i++)
        {
            minX[i] = int.MaxValue; minY[i] = int.MaxValue;
            maxX[i] = -1;           maxY[i] = -1;
        }
        for (int y = 0; y < h; y++)
        {
            int rs = y * w;
            for (int x = 0; x < w; x++)
            {
                int lbl = labels[rs + x];
                if (lbl == 0) continue;
                if (x < minX[lbl]) minX[lbl] = x;
                if (y < minY[lbl]) minY[lbl] = y;
                if (x > maxX[lbl]) maxX[lbl] = x;
                if (y > maxY[lbl]) maxY[lbl] = y;
            }
        }
        var boxes = new Rectangle[ccCount + 1];
        for (int i = 1; i <= ccCount; i++)
        {
            if (maxX[i] < 0) continue;
            boxes[i] = new Rectangle(minX[i], minY[i],
                                     maxX[i] - minX[i] + 1, maxY[i] - minY[i] + 1);
        }
        return boxes;
    }

    /// <summary>All CC labels whose pixels intersect <paramref name="bbox"/>.</summary>
    private static HashSet<int> CollectCCsInside(int[] labels, int w, int h, Rectangle bbox)
    {
        var s = new HashSet<int>();
        int x0 = Math.Max(0, bbox.X);
        int y0 = Math.Max(0, bbox.Y);
        int x1 = Math.Min(w, bbox.Right);
        int y1 = Math.Min(h, bbox.Bottom);
        for (int y = y0; y < y1; y++)
        {
            int rs = y * w;
            for (int x = x0; x < x1; x++)
            {
                int lbl = labels[rs + x];
                if (lbl != 0) s.Add(lbl);
            }
        }
        return s;
    }

    // -----------------------------------------------------------------------
    //  Designator → symbol CC.  Search an expanded box around the text and
    //  return the largest CC there that is NOT a "letter" CC of any text.
    //  Search box is auto-sized to 3× the text dimensions (min 40 px) — most
    //  schematic layouts put the designator within 1-2 symbol-widths of its
    //  symbol, so 3× is generous but not so big it grabs a neighbour.
    // -----------------------------------------------------------------------
    private static int FindSymbolCC(int[] labels, int w, int h,
                                    Rectangle textBbox, HashSet<int> forbidden)
    {
        int marginX = Math.Max(40, textBbox.Width  * 3);
        int marginY = Math.Max(40, textBbox.Height * 3);

        int x0 = Math.Max(0, textBbox.X      - marginX);
        int y0 = Math.Max(0, textBbox.Y      - marginY);
        int x1 = Math.Min(w, textBbox.Right  + marginX);
        int y1 = Math.Min(h, textBbox.Bottom + marginY);

        var counts = new Dictionary<int, int>();
        for (int y = y0; y < y1; y++)
        {
            int rs = y * w;
            for (int x = x0; x < x1; x++)
            {
                int lbl = labels[rs + x];
                if (lbl == 0) continue;
                if (forbidden.Contains(lbl)) continue;   // skip text letters
                counts.TryGetValue(lbl, out int c);
                counts[lbl] = c + 1;
            }
        }
        if (counts.Count == 0) return 0;

        int best = 0, bestCount = 0;
        foreach (var kv in counts)
            if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
        return best;
    }

    /// <summary>Largest non-forbidden CC whose pixels fall INSIDE bbox.
    /// Used after the shape detector returns a symbol bbox — we still need
    /// a CC label for the grouping step.</summary>
    private static int FindDominantCCInside(int[] labels, int w, int h,
                                            Rectangle bbox, HashSet<int> forbidden)
    {
        int x0 = Math.Max(0, bbox.X);
        int y0 = Math.Max(0, bbox.Y);
        int x1 = Math.Min(w, bbox.Right);
        int y1 = Math.Min(h, bbox.Bottom);

        var counts = new Dictionary<int, int>();
        for (int y = y0; y < y1; y++)
        {
            int rs = y * w;
            for (int x = x0; x < x1; x++)
            {
                int lbl = labels[rs + x];
                if (lbl == 0) continue;
                if (forbidden.Contains(lbl)) continue;
                counts.TryGetValue(lbl, out int c);
                counts[lbl] = c + 1;
            }
        }
        if (counts.Count == 0) return 0;
        int best = 0, bestCount = 0;
        foreach (var kv in counts)
            if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
        return best;
    }

    // -----------------------------------------------------------------------
    //  Net label → dominant CC in the ring just outside the bbox.
    // -----------------------------------------------------------------------
    private static int FindDominantCCInRing(int[] labels, int w, int h, Rectangle bbox, int margin)
    {
        int x0 = Math.Max(0, bbox.X - margin);
        int y0 = Math.Max(0, bbox.Y - margin);
        int x1 = Math.Min(w, bbox.Right + margin);
        int y1 = Math.Min(h, bbox.Bottom + margin);

        var counts = new Dictionary<int, int>();
        for (int y = y0; y < y1; y++)
        {
            int rs = y * w;
            for (int x = x0; x < x1; x++)
            {
                if (x >= bbox.X && x < bbox.Right && y >= bbox.Y && y < bbox.Bottom)
                    continue;
                int lbl = labels[rs + x];
                if (lbl == 0) continue;
                counts.TryGetValue(lbl, out int c);
                counts[lbl] = c + 1;
            }
        }
        if (counts.Count == 0) return 0;
        int best = 0, bestCount = 0;
        foreach (var kv in counts)
            if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
        return best;
    }

    // -----------------------------------------------------------------------
    //  Pin detection - scan OUTSIDE the YOLO box for wires.
    //  For ICs with thick outlines, the outline touches the box edge and creates
    //  one long continuous run. Instead, we scan just outside the box to find
    //  the actual wires exiting the symbol.
    // -----------------------------------------------------------------------
    private static List<DetectedPin> DetectPinsAtYoloEdgesSimple(
        bool[] inkEdge, bool[] inkOriginal, int w, int h, Rectangle bbox, string designator)
    {
        var pins = new List<DetectedPin>();
        var ink = inkOriginal;

        // How far outside the box to look for wires
        int outsideMargin = 8;

        // TOP: scan horizontal strip just above the box for vertical wires
        {
            int y = bbox.Y - 1;
            if (y >= 0)
            {
                int runStart = -1;
                for (int x = Math.Max(0, bbox.X); x < Math.Min(w, bbox.Right); x++)
                {
                    // Check if there's ink in the strip above the box
                    bool hasWire = false;
                    for (int dy = 0; dy < outsideMargin && y - dy >= 0; dy++)
                    {
                        if (ink[(y - dy) * w + x])
                        {
                            hasWire = true;
                            break;
                        }
                    }

                    if (hasWire)
                    {
                        if (runStart < 0) runStart = x;
                    }
                    else if (runStart >= 0)
                    {
                        int cx = (runStart + x - 1) / 2;
                        bool duplicate = pins.Any(p => p.Side == "Top" && Math.Abs(p.Location.X - cx) < 10);
                        if (!duplicate)
                            pins.Add(new DetectedPin(designator, new Point(cx, bbox.Y), 0, "Top"));
                        runStart = -1;
                    }
                }
                if (runStart >= 0)
                {
                    int cx = (runStart + Math.Min(w, bbox.Right) - 1) / 2;
                    bool duplicate = pins.Any(p => p.Side == "Top" && Math.Abs(p.Location.X - cx) < 10);
                    if (!duplicate)
                        pins.Add(new DetectedPin(designator, new Point(cx, bbox.Y), 0, "Top"));
                }
            }
        }

        // BOTTOM: scan horizontal strip just below the box
        {
            int y = bbox.Bottom;
            if (y < h)
            {
                int runStart = -1;
                for (int x = Math.Max(0, bbox.X); x < Math.Min(w, bbox.Right); x++)
                {
                    bool hasWire = false;
                    for (int dy = 0; dy < outsideMargin && y + dy < h; dy++)
                    {
                        if (ink[(y + dy) * w + x])
                        {
                            hasWire = true;
                            break;
                        }
                    }

                    if (hasWire)
                    {
                        if (runStart < 0) runStart = x;
                    }
                    else if (runStart >= 0)
                    {
                        int cx = (runStart + x - 1) / 2;
                        bool duplicate = pins.Any(p => p.Side == "Bottom" && Math.Abs(p.Location.X - cx) < 10);
                        if (!duplicate)
                            pins.Add(new DetectedPin(designator, new Point(cx, bbox.Bottom - 1), 0, "Bottom"));
                        runStart = -1;
                    }
                }
                if (runStart >= 0)
                {
                    int cx = (runStart + Math.Min(w, bbox.Right) - 1) / 2;
                    bool duplicate = pins.Any(p => p.Side == "Bottom" && Math.Abs(p.Location.X - cx) < 10);
                    if (!duplicate)
                        pins.Add(new DetectedPin(designator, new Point(cx, bbox.Bottom - 1), 0, "Bottom"));
                }
            }
        }

        // LEFT: scan vertical strip just left of the box
        {
            int x = bbox.X - 1;
            if (x >= 0)
            {
                int runStart = -1;
                for (int y = Math.Max(0, bbox.Y); y < Math.Min(h, bbox.Bottom); y++)
                {
                    bool hasWire = false;
                    for (int dx = 0; dx < outsideMargin && x - dx >= 0; dx++)
                    {
                        if (ink[y * w + (x - dx)])
                        {
                            hasWire = true;
                            break;
                        }
                    }

                    if (hasWire)
                    {
                        if (runStart < 0) runStart = y;
                    }
                    else if (runStart >= 0)
                    {
                        int cy = (runStart + y - 1) / 2;
                        bool duplicate = pins.Any(p => p.Side == "Left" && Math.Abs(p.Location.Y - cy) < 10);
                        if (!duplicate)
                            pins.Add(new DetectedPin(designator, new Point(bbox.X, cy), 0, "Left"));
                        runStart = -1;
                    }
                }
                if (runStart >= 0)
                {
                    int cy = (runStart + Math.Min(h, bbox.Bottom) - 1) / 2;
                    bool duplicate = pins.Any(p => p.Side == "Left" && Math.Abs(p.Location.Y - cy) < 10);
                    if (!duplicate)
                        pins.Add(new DetectedPin(designator, new Point(bbox.X, cy), 0, "Left"));
                }
            }
        }

        // RIGHT: scan vertical strip just right of the box
        {
            int x = bbox.Right;
            if (x < w)
            {
                int runStart = -1;
                for (int y = Math.Max(0, bbox.Y); y < Math.Min(h, bbox.Bottom); y++)
                {
                    bool hasWire = false;
                    for (int dx = 0; dx < outsideMargin && x + dx < w; dx++)
                    {
                        if (ink[y * w + (x + dx)])
                        {
                            hasWire = true;
                            break;
                        }
                    }

                    if (hasWire)
                    {
                        if (runStart < 0) runStart = y;
                    }
                    else if (runStart >= 0)
                    {
                        int cy = (runStart + y - 1) / 2;
                        bool duplicate = pins.Any(p => p.Side == "Right" && Math.Abs(p.Location.Y - cy) < 10);
                        if (!duplicate)
                            pins.Add(new DetectedPin(designator, new Point(bbox.Right - 1, cy), 0, "Right"));
                        runStart = -1;
                    }
                }
                if (runStart >= 0)
                {
                    int cy = (runStart + Math.Min(h, bbox.Bottom) - 1) / 2;
                    bool duplicate = pins.Any(p => p.Side == "Right" && Math.Abs(p.Location.Y - cy) < 10);
                    if (!duplicate)
                        pins.Add(new DetectedPin(designator, new Point(bbox.Right - 1, cy), 0, "Right"));
                }
            }
        }

        return pins;
    }

    // -----------------------------------------------------------------------
    //  Second pass pin detection: find lines that cross the YOLO box boundary.
    //  Uses ORIGINAL ink (before OCR text erasure) to detect wires passing
    //  through text areas. Looks for vertical/horizontal lines that have ink
    //  on BOTH sides of the boundary.
    // -----------------------------------------------------------------------
    private static List<DetectedPin> DetectPinsByLineCrossing(
        bool[] ink, int w, int h, Rectangle bbox, string designator,
        IReadOnlyList<DetectedPin> existingPins)
    {
        var pins = new List<DetectedPin>();

        // How many pixels to check on each side of the boundary
        int checkDist = 12;

        // Helper: check if there's a vertical line crossing at (x, boundaryY)
        // Simply checks for ink ABOVE and BELOW the boundary point
        bool HasVerticalLineCrossing(int x, int boundaryY)
        {
            if (x < 0 || x >= w || boundaryY < 0 || boundaryY >= h) return false;

            int aboveCount = 0;
            int belowCount = 0;

            // Check pixels ABOVE the boundary
            for (int d = 1; d <= checkDist; d++)
            {
                int y = boundaryY - d;
                if (y < 0) break;
                if (ink[y * w + x]) aboveCount++;
            }

            // Check pixels BELOW the boundary
            for (int d = 1; d <= checkDist; d++)
            {
                int y = boundaryY + d;
                if (y >= h) break;
                if (ink[y * w + x]) belowCount++;
            }

            // Need ink on both sides to confirm a line crossing
            return aboveCount >= 2 && belowCount >= 2;
        }

        // Helper: check if there's a horizontal line crossing at (boundaryX, y)
        // Simply checks for ink LEFT and RIGHT of the boundary point
        bool HasHorizontalLineCrossing(int y, int boundaryX)
        {
            if (y < 0 || y >= h || boundaryX < 0 || boundaryX >= w) return false;

            int leftCount = 0;
            int rightCount = 0;

            // Check pixels LEFT of the boundary
            for (int d = 1; d <= checkDist; d++)
            {
                int x = boundaryX - d;
                if (x < 0) break;
                if (ink[y * w + x]) leftCount++;
            }

            // Check pixels RIGHT of the boundary
            for (int d = 1; d <= checkDist; d++)
            {
                int x = boundaryX + d;
                if (x >= w) break;
                if (ink[y * w + x]) rightCount++;
            }

            return leftCount >= 2 && rightCount >= 2;
        }

        // Check if a pin already exists nearby
        bool IsDuplicate(string side, int coord)
        {
            foreach (var p in existingPins)
            {
                if (p.Side != side) continue;
                int existingCoord = (side == "Top" || side == "Bottom") ? p.Location.X : p.Location.Y;
                if (Math.Abs(existingCoord - coord) < 10) return true;
            }
            foreach (var p in pins)
            {
                if (p.Side != side) continue;
                int existingCoord = (side == "Top" || side == "Bottom") ? p.Location.X : p.Location.Y;
                if (Math.Abs(existingCoord - coord) < 10) return true;
            }
            return false;
        }

        // Scan TOP edge for vertical lines crossing
        {
            int y = bbox.Y;
            for (int x = Math.Max(0, bbox.X); x < Math.Min(w, bbox.Right); x++)
            {
                if (HasVerticalLineCrossing(x, y))
                {
                    if (!IsDuplicate("Top", x))
                        pins.Add(new DetectedPin(designator, new Point(x, y), 0, "Top"));
                }
            }
        }

        // Scan BOTTOM edge for vertical lines crossing
        {
            int y = bbox.Bottom - 1;
            for (int x = Math.Max(0, bbox.X); x < Math.Min(w, bbox.Right); x++)
            {
                if (HasVerticalLineCrossing(x, y))
                {
                    if (!IsDuplicate("Bottom", x))
                        pins.Add(new DetectedPin(designator, new Point(x, y), 0, "Bottom"));
                }
            }
        }

        // Scan LEFT edge for horizontal lines crossing
        {
            int x = bbox.X;
            for (int y = Math.Max(0, bbox.Y); y < Math.Min(h, bbox.Bottom); y++)
            {
                if (HasHorizontalLineCrossing(y, x))
                {
                    if (!IsDuplicate("Left", y))
                        pins.Add(new DetectedPin(designator, new Point(x, y), 0, "Left"));
                }
            }
        }

        // Scan RIGHT edge for horizontal lines crossing
        {
            int x = bbox.Right - 1;
            for (int y = Math.Max(0, bbox.Y); y < Math.Min(h, bbox.Bottom); y++)
            {
                if (HasHorizontalLineCrossing(y, x))
                {
                    if (!IsDuplicate("Right", y))
                        pins.Add(new DetectedPin(designator, new Point(x, y), 0, "Right"));
                }
            }
        }

        return pins;
    }

    // -----------------------------------------------------------------------
    //  Pin detection at YOLO bounding box edges (OLD - kept for reference).
    //  Scans each edge for runs of ink pixels (wires) that cross the edge.
    //  Excludes any ink that falls inside OCR text boxes.
    //  A pin is detected when ink pixels cross the YOLO box boundary.
    // -----------------------------------------------------------------------
    private static List<DetectedPin> DetectPinsAtYoloEdges(
        bool[] ink, int w, int h, Rectangle bbox, string designator, List<Rectangle> textBoxes)
    {
        var pins = new List<DetectedPin>();

        // Helper: check if an edge overlaps with any text box
        bool EdgeOverlapsText(string side)
        {
            const int margin = 15; // Larger margin to catch text near edges
            foreach (var tb in textBoxes)
            {
                // Expand text box by margin
                var expanded = new Rectangle(tb.X - margin, tb.Y - margin,
                                            tb.Width + margin * 2, tb.Height + margin * 2);
                switch (side)
                {
                    case "Top":
                        if (bbox.Y >= expanded.Y && bbox.Y <= expanded.Bottom &&
                            bbox.X < expanded.Right && bbox.Right > expanded.X)
                            return true;
                        break;
                    case "Bottom":
                        int bottomY = bbox.Bottom - 1;
                        if (bottomY >= expanded.Y && bottomY <= expanded.Bottom &&
                            bbox.X < expanded.Right && bbox.Right > expanded.X)
                            return true;
                        break;
                    case "Left":
                        if (bbox.X >= expanded.X && bbox.X <= expanded.Right &&
                            bbox.Y < expanded.Bottom && bbox.Bottom > expanded.Y)
                            return true;
                        break;
                    case "Right":
                        int rightX = bbox.Right - 1;
                        if (rightX >= expanded.X && rightX <= expanded.Right &&
                            bbox.Y < expanded.Bottom && bbox.Bottom > expanded.Y)
                            return true;
                        break;
                }
            }
            return false;
        }

        // Helper: check if a point is inside or near any text box (with margin)
        bool IsInsideOrNearText(int px, int py, int margin = 15)
        {
            foreach (var tb in textBoxes)
            {
                // Expand text box by margin to catch nearby points
                if (px >= tb.X - margin && px < tb.Right + margin &&
                    py >= tb.Y - margin && py < tb.Bottom + margin)
                    return true;
            }
            return false;
        }

        // Helper: check if a point is inside any text box (exact)
        bool IsInsideText(int px, int py)
        {
            foreach (var tb in textBoxes)
            {
                if (px >= tb.X && px < tb.Right && py >= tb.Y && py < tb.Bottom)
                    return true;
            }
            return false;
        }

        // Helper: check if ink continues outside the bbox (confirming it's a wire, not just edge of symbol)
        bool HasInkOutside(int px, int py, string side, int checkDist = 8)
        {
            int dx = 0, dy = 0;
            switch (side)
            {
                case "Top": dy = -1; break;
                case "Bottom": dy = 1; break;
                case "Left": dx = -1; break;
                case "Right": dx = 1; break;
            }

            int inkCount = 0;
            for (int d = 1; d <= checkDist; d++)
            {
                int nx = px + dx * d;
                int ny = py + dy * d;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) break;
                if (IsInsideText(nx, ny)) return false; // Wire leads into text, not a real pin
                if (ink[ny * w + nx]) inkCount++;
            }
            // Need at least 3 ink pixels outside to confirm it's a wire
            return inkCount >= 3;
        }

        // Scan top edge (skip if edge overlaps text)
        if (!EdgeOverlapsText("Top"))
        {
            int y = bbox.Y;
            if (y >= 0 && y < h)
            {
                int runStart = -1;
                for (int x = bbox.X; x < bbox.Right && x < w; x++)
                {
                    if (x < 0) continue;
                    bool isInk = ink[y * w + x] && !IsInsideOrNearText(x, y);
                    if (isInk)
                    {
                        if (runStart < 0) runStart = x;
                    }
                    else if (runStart >= 0)
                    {
                        int cx = (runStart + x - 1) / 2;
                        if (!IsInsideOrNearText(cx, y) && HasInkOutside(cx, y, "Top"))
                            pins.Add(new DetectedPin(designator, new Point(cx, y), 0, "Top"));
                        runStart = -1;
                    }
                }
                if (runStart >= 0)
                {
                    int cx = (runStart + Math.Min(bbox.Right, w) - 1) / 2;
                    if (!IsInsideOrNearText(cx, y) && HasInkOutside(cx, y, "Top"))
                        pins.Add(new DetectedPin(designator, new Point(cx, y), 0, "Top"));
                }
            }
        }

        // Scan bottom edge (skip if edge overlaps text)
        if (!EdgeOverlapsText("Bottom"))
        {
            int y = bbox.Bottom - 1;
            if (y >= 0 && y < h)
            {
                int runStart = -1;
                for (int x = bbox.X; x < bbox.Right && x < w; x++)
                {
                    if (x < 0) continue;
                    bool isInk = ink[y * w + x] && !IsInsideOrNearText(x, y);
                    if (isInk)
                    {
                        if (runStart < 0) runStart = x;
                    }
                    else if (runStart >= 0)
                    {
                        int cx = (runStart + x - 1) / 2;
                        if (!IsInsideOrNearText(cx, y) && HasInkOutside(cx, y, "Bottom"))
                            pins.Add(new DetectedPin(designator, new Point(cx, y), 0, "Bottom"));
                        runStart = -1;
                    }
                }
                if (runStart >= 0)
                {
                    int cx = (runStart + Math.Min(bbox.Right, w) - 1) / 2;
                    if (!IsInsideOrNearText(cx, y) && HasInkOutside(cx, y, "Bottom"))
                        pins.Add(new DetectedPin(designator, new Point(cx, y), 0, "Bottom"));
                }
            }
        }

        // Scan left edge (skip if edge overlaps text)
        if (!EdgeOverlapsText("Left"))
        {
            int x = bbox.X;
            if (x >= 0 && x < w)
            {
                int runStart = -1;
                for (int y = bbox.Y; y < bbox.Bottom && y < h; y++)
                {
                    if (y < 0) continue;
                    bool isInk = ink[y * w + x] && !IsInsideOrNearText(x, y);
                    if (isInk)
                    {
                        if (runStart < 0) runStart = y;
                    }
                    else if (runStart >= 0)
                    {
                        int cy = (runStart + y - 1) / 2;
                        if (!IsInsideOrNearText(x, cy) && HasInkOutside(x, cy, "Left"))
                            pins.Add(new DetectedPin(designator, new Point(x, cy), 0, "Left"));
                        runStart = -1;
                    }
                }
                if (runStart >= 0)
                {
                    int cy = (runStart + Math.Min(bbox.Bottom, h) - 1) / 2;
                    if (!IsInsideOrNearText(x, cy) && HasInkOutside(x, cy, "Left"))
                        pins.Add(new DetectedPin(designator, new Point(x, cy), 0, "Left"));
                }
            }
        }

        // Scan right edge (skip if edge overlaps text)
        if (!EdgeOverlapsText("Right"))
        {
            int x = bbox.Right - 1;
            if (x >= 0 && x < w)
            {
                int runStart = -1;
                for (int y = bbox.Y; y < bbox.Bottom && y < h; y++)
                {
                    if (y < 0) continue;
                    bool isInk = ink[y * w + x] && !IsInsideOrNearText(x, y);
                    if (isInk)
                    {
                        if (runStart < 0) runStart = y;
                    }
                    else if (runStart >= 0)
                    {
                        int cy = (runStart + y - 1) / 2;
                        if (!IsInsideOrNearText(x, cy) && HasInkOutside(x, cy, "Right"))
                            pins.Add(new DetectedPin(designator, new Point(x, cy), 0, "Right"));
                        runStart = -1;
                    }
                }
                if (runStart >= 0)
                {
                    int cy = (runStart + Math.Min(bbox.Bottom, h) - 1) / 2;
                    if (!IsInsideOrNearText(x, cy) && HasInkOutside(x, cy, "Right"))
                        pins.Add(new DetectedPin(designator, new Point(x, cy), 0, "Right"));
                }
            }
        }

        return pins;
    }

    // -----------------------------------------------------------------------
    //  Wire tracing - find which pins are connected via ink (ratsnest style).
    //  Uses BFS flood-fill to find all pins reachable from each pin.
    // -----------------------------------------------------------------------
    private static (List<TracedWire> wires, List<WireJunction> junctions) TraceWiresFromPins(
        bool[] ink, int w, int h,
        IReadOnlyList<DetectedPin> pins,
        IReadOnlyList<SymbolDetector.SymbolHit>? yoloHits)
    {
        var tracedWires = new List<TracedWire>();
        var junctions = new List<WireJunction>(); // Empty - not used

        if (pins.Count < 2) return (tracedWires, junctions);

        // Create a set of YOLO bounding boxes - shrink slightly to allow pins at edges
        var symbolBoxes = new List<Rectangle>();
        if (yoloHits != null)
        {
            foreach (var hit in yoloHits)
            {
                // Shrink box by 2 pixels so pins at edges aren't considered "inside"
                var shrunk = Rectangle.Inflate(hit.Bounds, -2, -2);
                if (shrunk.Width > 0 && shrunk.Height > 0)
                    symbolBoxes.Add(shrunk);
            }
        }

        // Helper: check if point is deep inside any symbol box (not at edge)
        bool IsDeepInsideSymbol(int px, int py)
        {
            foreach (var box in symbolBoxes)
            {
                if (px >= box.X && px < box.Right && py >= box.Y && py < box.Bottom)
                    return true;
            }
            return false;
        }

        // Track which pin pairs we've already connected (to avoid duplicates)
        var connectedPairs = new HashSet<string>();

        // For each pin, flood-fill to find other pins it connects to
        foreach (var startPin in pins)
        {
            string startPinId = $"{startPin.Designator}.{startPin.Side[0]}";

            // BFS from this pin's location
            var visited = new bool[w * h];
            var queue = new Queue<Point>();

            // Start from pin location and a few pixels around it to ensure we catch the wire
            queue.Enqueue(startPin.Location);
            for (int dy = -3; dy <= 3; dy++)
            {
                for (int dx = -3; dx <= 3; dx++)
                {
                    int nx = startPin.Location.X + dx;
                    int ny = startPin.Location.Y + dy;
                    if (nx >= 0 && nx < w && ny >= 0 && ny < h)
                        queue.Enqueue(new Point(nx, ny));
                }
            }

            while (queue.Count > 0)
            {
                var pt = queue.Dequeue();
                int idx = pt.Y * w + pt.X;

                if (pt.X < 0 || pt.X >= w || pt.Y < 0 || pt.Y >= h) continue;
                if (visited[idx]) continue;
                if (!ink[idx]) continue;
                if (IsDeepInsideSymbol(pt.X, pt.Y)) continue;

                visited[idx] = true;

                // Check if we reached another pin
                foreach (var otherPin in pins)
                {
                    if (otherPin.Designator == startPin.Designator && otherPin.Side == startPin.Side) continue;

                    int distSq = (pt.X - otherPin.Location.X) * (pt.X - otherPin.Location.X) +
                                (pt.Y - otherPin.Location.Y) * (pt.Y - otherPin.Location.Y);
                    if (distSq < 225) // Within 15 pixels
                    {
                        string endPinId = $"{otherPin.Designator}.{otherPin.Side[0]}";

                        // Create unique pair key (sorted to avoid A-B and B-A duplicates)
                        string pairKey = string.CompareOrdinal(startPinId, endPinId) < 0
                            ? $"{startPinId}|{endPinId}"
                            : $"{endPinId}|{startPinId}";

                        if (!connectedPairs.Contains(pairKey))
                        {
                            connectedPairs.Add(pairKey);

                            // Create wire with just start and end points (ratsnest style)
                            var wire = new TracedWire
                            {
                                StartPin = startPinId,
                                EndPin = endPinId
                            };
                            wire.Path.Add(startPin.Location);
                            wire.Path.Add(otherPin.Location);
                            wire.Bounds = Rectangle.FromLTRB(
                                Math.Min(startPin.Location.X, otherPin.Location.X),
                                Math.Min(startPin.Location.Y, otherPin.Location.Y),
                                Math.Max(startPin.Location.X, otherPin.Location.X),
                                Math.Max(startPin.Location.Y, otherPin.Location.Y));

                            tracedWires.Add(wire);
                        }
                    }
                }

                // Add neighbors to queue (4-connected)
                queue.Enqueue(new Point(pt.X - 1, pt.Y));
                queue.Enqueue(new Point(pt.X + 1, pt.Y));
                queue.Enqueue(new Point(pt.X, pt.Y - 1));
                queue.Enqueue(new Point(pt.X, pt.Y + 1));
            }
        }

        return (tracedWires, junctions);
    }
}
