using System.Globalization;
using System.Text;

namespace BoardviewBuilder;

/// <summary>
/// Emits the BRD2 ASCII format that FlexBV / OpenBoardView's BRD2 parser reads.
/// Format rules were reverse-engineered against FlexBV v1165 + the
/// OpenBoardView BRD2File.cpp parser. The non-obvious ones, encoded here:
///
///   * Sections: BRDOUT / NETS / PARTS / PINS / NAILS, each "HEADER: count ...".
///   * NETS: each line is "&lt;id&gt; &lt;name&gt;". The id column is decorative —
///     the parser uses ROW ORDER. A pin's net id is the 1-based row position.
///     Net id 0 = no-connect.
///   * PARTS: "name x1 y1 x2 y2 end_of_pins side". Despite the name,
///     end_of_pins is the index of the part's FIRST pin, NOT its last.
///     The parser starts a running pin index at 0 and, for part i, assigns
///     pins up to (but excluding) parts[i+1].end_of_pins. parts[0].end_of_pins
///     is never read. Setting every part's value to the 0-based index of its
///     first pin in the global pin list is correct for all parts.
///   * PINS: "x y netid side". One global list; a part owns a contiguous
///     slice of it (see above). Pin order within a part defines pin numbering.
///   * side: 1 = top, 2 = bottom.
/// </summary>
public static class BrdGenerator
{
    public static string Generate(BoardModel board)
    {
        var sb = new StringBuilder();

        // ---- BRDOUT ----
        int maxX = 0, maxY = 0;
        foreach (var (x, y) in board.Outline)
        {
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
        sb.Append("BRDOUT: ")
          .Append(board.Outline.Count).Append(' ')
          .Append(maxX).Append(' ')
          .Append(maxY).Append('\n');
        foreach (var (x, y) in board.Outline)
            sb.Append(x).Append(' ').Append(y).Append('\n');
        sb.Append('\n');

        // ---- NETS ---- (id column decorative; row order is what counts)
        sb.Append("NETS: ").Append(board.Nets.Count).Append('\n');
        for (int i = 0; i < board.Nets.Count; i++)
            sb.Append(i + 1).Append(' ').Append(board.Nets[i].Name).Append('\n');
        sb.Append('\n');

        // Flatten pins in part order; within a part, ordered by pin number.
        // Record each part's first-pin index (0-based) = its end_of_pins value.
        var flatPins = new List<(Pin Pin, int Side)>();
        var firstPinIndex = new int[board.Parts.Count];
        for (int p = 0; p < board.Parts.Count; p++)
        {
            firstPinIndex[p] = flatPins.Count; // 0-based start of this part's pins
            var part = board.Parts[p];
            foreach (var pin in part.Pins.OrderBy(pn => pn.Number))
                flatPins.Add((pin, part.Side));
        }

        // ---- PARTS ----
        sb.Append("PARTS: ").Append(board.Parts.Count).Append('\n');
        for (int p = 0; p < board.Parts.Count; p++)
        {
            var part = board.Parts[p];
            sb.Append(part.Name).Append(' ')
              .Append(part.X1).Append(' ').Append(part.Y1).Append(' ')
              .Append(part.X2).Append(' ').Append(part.Y2).Append(' ')
              .Append(firstPinIndex[p]).Append(' ')   // "end_of_pins" = first pin idx
              .Append(part.Side).Append('\n');
        }
        sb.Append('\n');

        // ---- PINS ----
        sb.Append("PINS: ").Append(flatPins.Count).Append('\n');
        foreach (var (pin, side) in flatPins)
        {
            sb.Append(pin.X).Append(' ')
              .Append(pin.Y).Append(' ')
              .Append(board.NetId(pin.Net)).Append(' ')
              .Append(side).Append('\n');
        }
        sb.Append('\n');

        // ---- NAILS ----
        sb.Append("NAILS: ").Append(board.Nails.Count).Append('\n');
        foreach (var nail in board.Nails)
        {
            sb.Append(nail.Probe).Append(' ')
              .Append(nail.X).Append(' ').Append(nail.Y).Append(' ')
              .Append(board.NetId(nail.Net)).Append(' ')
              .Append(nail.Side).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Convenience: generate and write to disk (LF line endings,
    /// which FlexBV accepts and keeps the file diff-clean).</summary>
    public static void WriteFile(BoardModel board, string path)
    {
        File.WriteAllText(path, Generate(board), new UTF8Encoding(false));
    }

    // Small helper so callers can parse ints from CSV uniformly.
    internal static int Int(string s) =>
        int.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
}
