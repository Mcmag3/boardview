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

    /// <summary>Per-trace diagnostics for the status bar / notes.</summary>
    public readonly record struct TraceStats(
        int ConnectedComponents,
        int Nets,
        int IsolatedTextBoxes,
        int SymbolsFound,
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
    }

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

        // 4+5) Per-text CC assignment.
        var textCC = new int[texts.Count];
        var symbolBoxes = new Dictionary<string, Rectangle>(StringComparer.Ordinal);
        int symbolsFound = 0;

        for (int i = 0; i < texts.Count; i++)
        {
            if (texts[i].Kind == TextKind.NetLabel)
            {
                // Net label: dominant CC in the ring just outside the bbox.
                textCC[i] = FindDominantCCInRing(labels, w, h, texts[i].Bounds, netLabelRingMargin);
            }
            else
            {
                // Designator: largest non-forbidden CC in an expanded search box.
                int symCC = FindSymbolCC(labels, w, h, texts[i].Bounds, forbidden);
                textCC[i] = symCC;
                if (symCC != 0)
                {
                    symbolBoxes[texts[i].Text] = ccBoxes[symCC];
                    symbolsFound++;
                }
            }
        }

        // 6) Group by CC label.
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

        // 7) Name each group.
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
        var stats = new TraceStats(ccCount, byCC.Count, isolated, symbolsFound, sw.ElapsedMilliseconds);
        return new TraceResult
        {
            Nets = byCC.Values.ToList(),
            Stats = stats,
            SymbolBoxes = symbolBoxes,
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
}
