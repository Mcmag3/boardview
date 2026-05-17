using OpenCvSharp;
using OpenCvSharp.Extensions;
using DrawingRectangle = System.Drawing.Rectangle;
using CvRect = OpenCvSharp.Rect;

namespace BoardviewBuilder;

/// <summary>
/// Shape-aware schematic symbol detector, B3 strategy:
///   the designator letter tells us what shape to look for, and we run a
///   targeted geometric matcher per letter in a small region around the
///   designator text. Each matcher returns the bbox of the best candidate
///   shape in IMAGE COORDINATES (not crop-local), or null if nothing fits.
///
/// Current coverage:
///   * R / RN  → resistor: rectangle (IEC) OR zig-zag (US).
///   * C       → capacitor: a pair of short PARALLEL line segments separated
///               by a small gap (the two plates). Works for both non-polarised
///               (two straight lines) and polarised (straight + curved — the
///               curved plate registers as a short segment too, since we don't
///               demand both are perfectly straight).
///   * (others will be added incrementally — diode, BJT, IC.)
///
/// Implementation notes:
///   * We use OpenCvSharp4 because it gives us contour finding, polygon
///     approximation, and Hough lines for free. The native runtime ships
///     in OpenCvSharp4.runtime.win and gets copied next to the .exe.
///   * The TEXT bounding boxes of all OCR words are MASKED OUT of the
///     binarised crop before contour finding. Otherwise the letters of
///     \"R1\" themselves form 4-corner contours and get picked.
/// </summary>
public static class SymbolDetector
{
    /// <summary>Result of a single symbol detection.</summary>
    public sealed class SymbolHit
    {
        /// <summary>Bounding box of the detected shape in IMAGE coords.</summary>
        public DrawingRectangle Bounds { get; init; }
        /// <summary>What kind of shape matched (\"resistor-rect\", \"resistor-zigzag\", …).</summary>
        public string Kind { get; init; } = "";
        /// <summary>0..1 confidence — higher = better fit.</summary>
        public float Score { get; init; }
    }

    /// <summary>Try to find a resistor near <paramref name="textBbox"/>. Returns
    /// the best candidate, preferring rectangle (IEC) → zig-zag (US). The
    /// search region is auto-sized to ~4× the text dimensions (min 60 px).</summary>
    public static SymbolHit? FindResistorNear(
        Bitmap processed,
        DrawingRectangle textBbox,
        IReadOnlyList<DrawingRectangle> allTextBoxes,
        int binaryThreshold = 160)
    {
        int marginX = Math.Max(60, textBbox.Width  * 4);
        int marginY = Math.Max(60, textBbox.Height * 4);

        int sx = Math.Max(0, textBbox.X      - marginX);
        int sy = Math.Max(0, textBbox.Y      - marginY);
        int sw = Math.Min(processed.Width,  textBbox.Right  + marginX) - sx;
        int sh = Math.Min(processed.Height, textBbox.Bottom + marginY) - sy;
        if (sw <= 0 || sh <= 0) return null;

        var cropRect = new CvRect(sx, sy, sw, sh);

        using var full = OpenCvSharp.Extensions.BitmapConverter.ToMat(processed);   // BGR or BGRA
        using var crop = full.SubMat(cropRect).Clone();

        // To grayscale + binary inverse (symbols/wires/text become WHITE on BLACK).
        using var gray = new Mat();
        if (crop.Channels() == 1) crop.CopyTo(gray);
        else Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);

        using var bin = new Mat();
        Cv2.Threshold(gray, bin, binaryThreshold, 255, ThresholdTypes.BinaryInv);

        // Mask out every text bbox that overlaps the crop — paint those
        // regions black so OCR'd letters can't be picked as the symbol.
        var fullCrop = new CvRect(0, 0, sw, sh);
        foreach (var tb in allTextBoxes)
        {
            int tx = tb.X - sx;
            int ty = tb.Y - sy;
            // 2-px pad to also kill anti-aliased letter edges.
            var local = new CvRect(tx - 2, ty - 2, tb.Width + 4, tb.Height + 4)
                            .Intersect(fullCrop);
            if (local.Width <= 0 || local.Height <= 0) continue;
            Cv2.Rectangle(bin, local, Scalar.Black, thickness: -1);
        }

        // ---- Attempt 1: IEC-style rectangle resistor -------------------------
        var rectHit = FindResistorRectangle(bin, textBbox, sx, sy);
        if (rectHit != null) return rectHit;

        // ---- Attempt 2: US-style zig-zag resistor ---------------------------
        var zigHit = FindResistorZigZag(bin, textBbox, sx, sy);
        if (zigHit != null) return zigHit;

        return null;
    }

    /// <summary>Try to find a capacitor near <paramref name="textBbox"/>. A
    /// capacitor is two short PARALLEL line segments (the two plates) separated
    /// by a small perpendicular gap — looks like `||` (vertical orientation)
    /// or `=` (horizontal orientation) on the page. We crop, mask out all
    /// OCR text, run HoughLinesP, then search for the best pair of segments
    /// that satisfy the geometric constraints.</summary>
    public static SymbolHit? FindCapacitorNear(
        Bitmap processed,
        DrawingRectangle textBbox,
        IReadOnlyList<DrawingRectangle> allTextBoxes,
        int binaryThreshold = 160)
    {
        int marginX = Math.Max(60, textBbox.Width  * 4);
        int marginY = Math.Max(60, textBbox.Height * 4);

        int sx = Math.Max(0, textBbox.X      - marginX);
        int sy = Math.Max(0, textBbox.Y      - marginY);
        int sw = Math.Min(processed.Width,  textBbox.Right  + marginX) - sx;
        int sh = Math.Min(processed.Height, textBbox.Bottom + marginY) - sy;
        if (sw <= 0 || sh <= 0) return null;

        var cropRect = new CvRect(sx, sy, sw, sh);

        using var full = OpenCvSharp.Extensions.BitmapConverter.ToMat(processed);
        using var crop = full.SubMat(cropRect).Clone();

        using var gray = new Mat();
        if (crop.Channels() == 1) crop.CopyTo(gray);
        else Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);

        using var bin = new Mat();
        Cv2.Threshold(gray, bin, binaryThreshold, 255, ThresholdTypes.BinaryInv);

        var fullCrop = new CvRect(0, 0, sw, sh);
        foreach (var tb in allTextBoxes)
        {
            int tx = tb.X - sx;
            int ty = tb.Y - sy;
            var local = new CvRect(tx - 2, ty - 2, tb.Width + 4, tb.Height + 4)
                            .Intersect(fullCrop);
            if (local.Width <= 0 || local.Height <= 0) continue;
            Cv2.Rectangle(bin, local, Scalar.Black, thickness: -1);
        }

        return FindCapacitorPlates(bin, textBbox, sx, sy);
    }

    /// <summary>Look for two short parallel line segments separated by a small
    /// gap — the two plates of a capacitor. Parameters are sized relative to
    /// the OCR text height because that's our only on-page scale reference.
    /// </summary>
    private static SymbolHit? FindCapacitorPlates(Mat bin, DrawingRectangle textBbox, int cropOriginX, int cropOriginY)
    {
        float textH = Math.Max(8, textBbox.Height);

        // Plate length: roughly 0.8 - 3.5 × text height.
        float minPlateLen = textH * 0.8f;
        float maxPlateLen = textH * 3.5f;
        // Gap between the two plates (perpendicular distance):
        // typically a small fraction of the plate length, but not zero.
        float minGap = Math.Max(2f, textH * 0.15f);
        float maxGap = textH * 1.5f;
        // Angle tolerance between the two plates (degrees).
        float angleTol = 15f;
        // Length similarity: plates should be within 60% of each other.
        float lenSimilarity = 0.6f;
        // Lateral offset (along the plate direction) of the two midpoints,
        // expressed as a fraction of the average plate length: 0 = perfectly
        // aligned, 1 = end-to-end. We allow up to 0.6.
        float maxLateralFrac = 0.6f;

        int minLineLen = Math.Max(4, (int)(minPlateLen * 0.6f));
        int maxLineGap = Math.Max(2, (int)(textH * 0.25f));

        var segments = Cv2.HoughLinesP(bin, rho: 1, theta: Math.PI / 180,
                                       threshold: 15, minLineLength: minLineLen,
                                       maxLineGap: maxLineGap);
        if (segments == null || segments.Length < 2) return null;

        // Convert to a friendly struct and prefilter by length.
        var segs = new List<(float cx, float cy, float angle, float len, float dx, float dy)>();
        foreach (var s in segments)
        {
            float dx = s.P2.X - s.P1.X;
            float dy = s.P2.Y - s.P1.Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < minPlateLen || len > maxPlateLen) continue;
            float a = MathF.Atan2(dy, dx) * (180f / MathF.PI);
            if (a > 90) a -= 180;
            else if (a < -90) a += 180;
            float cx = (s.P1.X + s.P2.X) * 0.5f;
            float cy = (s.P1.Y + s.P2.Y) * 0.5f;
            // Unit-vector components for projection math (along the line).
            float ux = dx / Math.Max(1e-6f, len);
            float uy = dy / Math.Max(1e-6f, len);
            segs.Add((cx, cy, a, len, ux, uy));
        }
        if (segs.Count < 2) return null;

        SymbolHit? best = null;
        float bestScore = 0f;

        for (int i = 0; i < segs.Count; i++)
        for (int j = i + 1; j < segs.Count; j++)
        {
            var a = segs[i];
            var b = segs[j];

            // Same orientation (within angleTol)?
            float angDiff = Math.Abs(a.angle - b.angle);
            if (angDiff > 90) angDiff = 180 - angDiff;
            if (angDiff > angleTol) continue;

            // Similar length?
            float lenRatio = Math.Min(a.len, b.len) / Math.Max(a.len, b.len);
            if (lenRatio < lenSimilarity) continue;

            // Perpendicular gap between the two plates: project the midpoint
            // delta onto the perpendicular of plate A's direction.
            float dxm = b.cx - a.cx;
            float dym = b.cy - a.cy;
            // Perpendicular to (a.dx, a.dy) is (-a.dy, a.dx) — but we stored
            // unit components, so this is (-a.dy_unit, a.dx_unit) = (-a.uy, a.ux)
            // wait we stored dx as unit-vector component (ux, uy). Rename mental:
            // a.dx = ux, a.dy = uy in our tuple. Perpendicular unit = (-uy, ux).
            float perp = MathF.Abs(-a.dy * dxm + a.dx * dym);
            if (perp < minGap || perp > maxGap) continue;

            // Lateral offset along the plate direction.
            float lateral = MathF.Abs(a.dx * dxm + a.dy * dym);
            float avgLen = (a.len + b.len) * 0.5f;
            if (lateral > maxLateralFrac * avgLen) continue;

            // Score: prefer (i) plates of similar length, (ii) small gap
            // relative to plate length, (iii) small lateral offset.
            float lenScore = lenRatio;
            float gapScore = 1f - Math.Min(1f, perp / (avgLen * 0.8f));
            float latScore = 1f - Math.Min(1f, lateral / (avgLen * maxLateralFrac));
            float score = 0.4f * lenScore + 0.35f * gapScore + 0.25f * latScore;

            if (score > bestScore)
            {
                // Bounding box = union of both segments.
                float halfA = a.len * 0.5f;
                float halfB = b.len * 0.5f;
                float ax1 = a.cx - a.dx * halfA, ay1 = a.cy - a.dy * halfA;
                float ax2 = a.cx + a.dx * halfA, ay2 = a.cy + a.dy * halfA;
                float bx1 = b.cx - b.dx * halfB, by1 = b.cy - b.dy * halfB;
                float bx2 = b.cx + b.dx * halfB, by2 = b.cy + b.dy * halfB;
                float minX = MathF.Min(MathF.Min(ax1, ax2), MathF.Min(bx1, bx2));
                float minY = MathF.Min(MathF.Min(ay1, ay2), MathF.Min(by1, by2));
                float maxX = MathF.Max(MathF.Max(ax1, ax2), MathF.Max(bx1, bx2));
                float maxY = MathF.Max(MathF.Max(ay1, ay2), MathF.Max(by1, by2));
                int bx = Math.Max(0, (int)MathF.Floor(minX));
                int by = Math.Max(0, (int)MathF.Floor(minY));
                int bw = Math.Max(1, (int)MathF.Ceiling(maxX - minX));
                int bh = Math.Max(1, (int)MathF.Ceiling(maxY - minY));

                bestScore = score;
                best = new SymbolHit
                {
                    Bounds = new DrawingRectangle(bx + cropOriginX, by + cropOriginY, bw, bh),
                    Kind = "capacitor",
                    Score = score,
                };
            }
        }

        return bestScore >= 0.45f ? best : null;
    }

    /// <summary>Look for a small rectangular contour inside the masked crop.
    /// Filters: 4 vertices after polygon approx, aspect ratio 1.5–5:1
    /// (either orientation), area within a sensible range relative to the
    /// text height (a resistor body is typically 2-4× the designator text
    /// height in its long dimension), not touching the crop boundary (that
    /// would be a wire mesh, not a body).</summary>
    private static SymbolHit? FindResistorRectangle(Mat bin, DrawingRectangle textBbox, int cropOriginX, int cropOriginY)
    {
        Cv2.FindContours(bin, out OpenCvSharp.Point[][] contours, out _,
                         RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        float textH = Math.Max(1, textBbox.Height);
        // Acceptable resistor long-side length, in pixels.
        float minLong = textH * 1.5f;
        float maxLong = textH * 8.0f;

        SymbolHit? best = null;
        float bestScore = 0f;

        foreach (var contour in contours)
        {
            if (contour.Length < 4) continue;

            double peri = Cv2.ArcLength(contour, closed: true);
            var approx = Cv2.ApproxPolyDP(contour, peri * 0.04, closed: true);
            if (approx.Length != 4) continue;

            var br = Cv2.BoundingRect(approx);
            if (br.Width < 4 || br.Height < 4) continue;
            // Reject contours touching the crop boundary — they're wires or page edge.
            if (br.X <= 1 || br.Y <= 1
             || br.Right >= bin.Width - 1 || br.Bottom >= bin.Height - 1) continue;

            float longSide = Math.Max(br.Width, br.Height);
            float shortSide = Math.Max(1, Math.Min(br.Width, br.Height));
            float aspect = longSide / shortSide;

            if (aspect < 1.5f || aspect > 5.0f) continue;
            if (longSide < minLong || longSide > maxLong) continue;

            // Score: prefer aspect close to 3:1, prefer reasonable fill ratio.
            float aspectScore = 1f - Math.Min(1f, Math.Abs(aspect - 3f) / 2f);
            double contourArea = Cv2.ContourArea(approx);
            double rectArea = br.Width * (double)br.Height;
            float fillRatio = (float)(contourArea / Math.Max(1, rectArea));
            float fillScore = fillRatio;  // closer to 1 = more rectangular
            float score = 0.6f * aspectScore + 0.4f * fillScore;

            if (score > bestScore)
            {
                bestScore = score;
                best = new SymbolHit
                {
                    Bounds = new DrawingRectangle(br.X + cropOriginX, br.Y + cropOriginY,
                                                   br.Width, br.Height),
                    Kind = "resistor-rect",
                    Score = score,
                };
            }
        }

        // Demand a reasonable score so we don't accept junk.
        return bestScore >= 0.5f ? best : null;
    }

    /// <summary>Look for a US-style zig-zag resistor: a cluster of short line
    /// segments with alternating ±~60° slopes, all packed within a small bbox.
    /// We use HoughLinesP on the masked binary, then group segments by
    /// proximity and accept any group with ≥5 segments and a mix of two
    /// distinct slopes.</summary>
    private static SymbolHit? FindResistorZigZag(Mat bin, DrawingRectangle textBbox, int cropOriginX, int cropOriginY)
    {
        float textH = Math.Max(8, textBbox.Height);
        int minLineLen = Math.Max(4, (int)(textH * 0.4f));
        int maxLineGap = Math.Max(2, (int)(textH * 0.3f));

        var segments = Cv2.HoughLinesP(bin, rho: 1, theta: Math.PI / 180,
                                        threshold: 12, minLineLength: minLineLen,
                                        maxLineGap: maxLineGap);
        if (segments == null || segments.Length < 5) return null;

        // Convert to (cx, cy, angle°, length).
        var segs = new List<(float cx, float cy, float angle, float len)>(segments.Length);
        foreach (var s in segments)
        {
            float dx = s.P2.X - s.P1.X;
            float dy = s.P2.Y - s.P1.Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            float a = MathF.Atan2(dy, dx) * (180f / MathF.PI);   // -180..180
            // Normalise to -90..90 (line direction is symmetric).
            if (a > 90) a -= 180;
            else if (a < -90) a += 180;
            float cx = (s.P1.X + s.P2.X) * 0.5f;
            float cy = (s.P1.Y + s.P2.Y) * 0.5f;
            segs.Add((cx, cy, a, len));
        }

        // Greedy cluster: pick any seed, gather segments within `gather` px.
        float gather = textH * 3.0f;
        var visited = new bool[segs.Count];
        SymbolHit? best = null;
        float bestScore = 0f;

        for (int i = 0; i < segs.Count; i++)
        {
            if (visited[i]) continue;
            var seed = segs[i];

            var group = new List<int> { i };
            visited[i] = true;
            for (int j = i + 1; j < segs.Count; j++)
            {
                if (visited[j]) continue;
                var s = segs[j];
                if (MathF.Abs(s.cx - seed.cx) <= gather &&
                    MathF.Abs(s.cy - seed.cy) <= gather)
                {
                    group.Add(j);
                    visited[j] = true;
                }
            }
            if (group.Count < 5) continue;

            // Check the angle distribution — a resistor zig-zag has TWO
            // distinct slopes (~+60° and ~-60°), each represented several times.
            int posSlope = 0, negSlope = 0;
            foreach (var k in group)
            {
                float a = segs[k].angle;
                if (a > 20f) posSlope++;
                else if (a < -20f) negSlope++;
            }
            if (posSlope < 2 || negSlope < 2) continue;

            // Compute group bbox.
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (var k in group)
            {
                var s = segs[k];
                float half = s.len * 0.5f;
                if (s.cx - half < minX) minX = s.cx - half;
                if (s.cy - half < minY) minY = s.cy - half;
                if (s.cx + half > maxX) maxX = s.cx + half;
                if (s.cy + half > maxY) maxY = s.cy + half;
            }
            int bx = Math.Max(0, (int)minX);
            int by = Math.Max(0, (int)minY);
            int bw = Math.Max(1, (int)(maxX - minX));
            int bh = Math.Max(1, (int)(maxY - minY));

            float score = 0.5f + 0.05f * group.Count;  // more segments = stronger hit
            score = Math.Min(1f, score);
            if (score > bestScore)
            {
                bestScore = score;
                best = new SymbolHit
                {
                    Bounds = new DrawingRectangle(bx + cropOriginX, by + cropOriginY, bw, bh),
                    Kind = "resistor-zigzag",
                    Score = score,
                };
            }
        }
        return best;
    }
}