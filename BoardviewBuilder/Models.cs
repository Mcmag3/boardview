namespace BoardviewBuilder;

/// <summary>
/// In-memory representation of a board. Any input source (CSV today, a PDF
/// parser later) only needs to populate one of these; <see cref="BrdGenerator"/>
/// turns it into a FlexBV-loadable .brd file.
/// </summary>
public sealed class BoardModel
{
    /// <summary>Board outline polygon points, in order. Units are arbitrary
    /// but must be consistent (we use mils in the samples).</summary>
    public List<(int X, int Y)> Outline { get; } = new();

    /// <summary>Nets in order. The 1-based position in this list is the id
    /// that pins reference (FlexBV ignores any explicit id column and uses
    /// row order). Index 0 / unknown net = no-connect.</summary>
    public List<Net> Nets { get; } = new();

    /// <summary>Parts in placement order.</summary>
    public List<Part> Parts { get; } = new();

    /// <summary>Optional test points.</summary>
    public List<Nail> Nails { get; } = new();

    /// <summary>Resolve a net name to its 1-based id. Empty/unknown =&gt; 0 (NC).</summary>
    public int NetId(string? netName)
    {
        if (string.IsNullOrWhiteSpace(netName)) return 0;
        for (int i = 0; i < Nets.Count; i++)
            if (string.Equals(Nets[i].Name, netName, StringComparison.Ordinal))
                return i + 1; // 1-based
        return 0;
    }
}

public sealed class Net
{
    public required string Name { get; init; }
}

public sealed class Part
{
    public required string Name { get; init; }

    /// <summary>Bounding box corners (x1,y1)-(x2,y2).</summary>
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }

    /// <summary>1 = top side, 2 = bottom side.</summary>
    public int Side { get; set; } = 1;

    /// <summary>Pins belonging to this part, ordered by pin number.</summary>
    public List<Pin> Pins { get; } = new();
}

public sealed class Pin
{
    /// <summary>Pin number as printed on the schematic/datasheet. Used only
    /// for ordering within a part; the .brd format has no explicit pin number.</summary>
    public int Number { get; set; }

    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>Net name; resolved to a 1-based id at generation time.</summary>
    public string? Net { get; set; }
}

public sealed class Nail
{
    public int Probe { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string? Net { get; set; }
    public int Side { get; set; } = 1;
}
