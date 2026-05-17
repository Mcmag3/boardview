using System.Drawing.Imaging;

namespace BoardviewBuilder;

/// <summary>
/// Naïve wire tracer for schematic raster images.
///
/// Idea (v1 — small step, no symbol detection yet):
///   1. Binarise the processed bitmap (dark pixels = "ink": wires + text).
///   2. Run two-pass connected-component (CC) labelling on the ink.
///   3. For each OCR text bounding box (designator or net label), look at a
///      "ring" of pixels just OUTSIDE the bbox and find the dominant non-zero
///      CC label there. That CC is the wire physically nearest the label.
///   4. Group text boxes that share a CC → those are one electrical net.
///   5. Prefer the group's net-label name (e.g. "GND") for the net name; if
///      none, generate "N$1", "N$2", …  Groups with the same name are merged
///      (a schematic typically has many "GND" labels that are all one net).
///
/// Limitations (intentional for this step):
///   * Each designator can only end up on ONE net — we don't yet detect the
///     component symbol or its multiple pins, just the label text.
///   * Letters of the OCR'd text are themselves dark pixels and become part
///     of a CC. The "ring" lookup is what bridges from the letter-CC to the
///     wire-CC that actually carries the signal.
///   * No diagonal connectivity (4-connectivity only) — schematic wires are
///     axis-aligned, so this is the right choice.
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
        long ElapsedMs);

    /// <summary>Trace wire connectivity. <paramref name="processed"/> can be
    /// any pixel format — it'll be internally rebound to 24bpp RGB for the
    /// binarisation pass.</summary>
    public static (List<TracedNet> nets, TraceStats stats) Trace(
        Bitmap processed,
        IReadOnlyList<TextBoxInfo> texts,
        int expansionMargin = 12,
        int binaryThreshold = 160)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int w = processed.Width;
        int h = processed.Height;

        // 1) Binarise: dark pixels become foreground.
        bool[] ink = Binarise(processed, binaryThreshold);

        // 2) Two-pass CC labelling with union-find.
        int[] labels = ConnectedComponents(ink, w, h, out int ccCount);

        // 3) For each text box, find the dominant CC label in the ring just
        //    outside it. That CC is the wire we believe the text is touching.
        var textCC = new int[texts.Count];
        for (int i = 0; i < texts.Count; i++)
            textCC[i] = FindDominantCC(labels, w, h, texts[i].Bounds, expansionMargin);

        // 4) Group text boxes by their CC label.
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

        // 5) Name each group: prefer a net-label member, otherwise auto-name.
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
        var stats = new TraceStats(ccCount, byCC.Count, isolated, sw.ElapsedMilliseconds);
        return (byCC.Values.ToList(), stats);
    }

    // -----------------------------------------------------------------------
    //  Binarisation
    // -----------------------------------------------------------------------
    private static bool[] Binarise(Bitmap src, int threshold)
    {
        int w = src.Width;
        int h = src.Height;
        var mask = new bool[w * h];

        // LockBits with a fixed format auto-converts other source formats.
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
                        // Quick luma = (r + g + b) / 3.
                        int luma = (r + g + b) / 3;
                        mask[rowStart + x] = luma < threshold;
                    }
                }
            }
        }
        finally
        {
            src.UnlockBits(data);
        }
        return mask;
    }

    // -----------------------------------------------------------------------
    //  Connected-components labelling (4-connectivity, two-pass union-find)
    // -----------------------------------------------------------------------
    private static int[] ConnectedComponents(bool[] mask, int w, int h, out int finalCount)
    {
        int[] labels = new int[w * h];
        var parent = new List<int> { 0 }; // index 0 unused (background)

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]]; // path compression
                x = parent[x];
            }
            return x;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            // Union by min-id keeps roots stable for debugging.
            if (ra < rb) parent[rb] = ra;
            else         parent[ra] = rb;
        }

        // Pass 1: provisional labels.
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
                else if (left != 0 && up == 0)
                {
                    labels[idx] = left;
                }
                else if (left == 0 && up != 0)
                {
                    labels[idx] = up;
                }
                else
                {
                    int min = Math.Min(left, up);
                    labels[idx] = min;
                    if (left != up) Union(left, up);
                }
            }
        }

        // Pass 2: resolve to roots and compact IDs to 1..N.
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
    //  Find the dominant non-zero CC label in the ring just outside a bbox.
    //  Ring = (bbox expanded by margin) minus (bbox itself).
    // -----------------------------------------------------------------------
    private static int FindDominantCC(int[] labels, int w, int h, Rectangle bbox, int margin)
    {
        int x0 = Math.Max(0, bbox.X - margin);
        int y0 = Math.Max(0, bbox.Y - margin);
        int x1 = Math.Min(w, bbox.Right + margin);
        int y1 = Math.Min(h, bbox.Bottom + margin);

        var counts = new Dictionary<int, int>();
        for (int y = y0; y < y1; y++)
        {
            int rowStart = y * w;
            for (int x = x0; x < x1; x++)
            {
                // Skip pixels inside the original bbox — we only want the
                // ring AROUND the text so we find the wire, not the letters.
                if (x >= bbox.X && x < bbox.Right && y >= bbox.Y && y < bbox.Bottom)
                    continue;
                int lbl = labels[rowStart + x];
                if (lbl == 0) continue;
                counts.TryGetValue(lbl, out int c);
                counts[lbl] = c + 1;
            }
        }
        if (counts.Count == 0) return 0;

        int best = 0, bestCount = 0;
        foreach (var kv in counts)
        {
            if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
        }
        return best;
    }
}
