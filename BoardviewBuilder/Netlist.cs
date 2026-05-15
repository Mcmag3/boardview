namespace BoardviewBuilder;

/// <summary>
/// Geometry-free electrical graph extracted from a schematic source
/// (image, PDF, KiCad netlist, …). This is the FIRST stage of the
/// new two-stage pipeline:
///
///   schematic source ──► <see cref="Netlist"/>      (this file)
///   layout source    ──► physical (x,y) for parts/pins
///   Netlist + Layout ──► <see cref="BoardModel"/>   ──► .brd via BrdGenerator
///
/// A Netlist intentionally has NO coordinates. It only describes:
///   * What components (reference designators) exist on the board.
///   * Which pins each component has.
///   * Which net each pin is connected to.
///
/// Different input sources (JPEG schematic, PDF schematic, manually
/// authored CSV) all converge on a Netlist before we worry about
/// physical placement.
/// </summary>
public sealed class Netlist
{
    /// <summary>All distinct nets in the design. Order is meaningful only
    /// in that it's the order in which they'll appear in the final .brd
    /// when this netlist is later combined with a layout.</summary>
    public List<NetlistNet> Nets { get; } = new();

    /// <summary>All components (reference designators) in the design.</summary>
    public List<NetlistComponent> Components { get; } = new();

    /// <summary>Free-form provenance string — which file / extractor produced
    /// this netlist. Useful for debugging the extraction pipeline.</summary>
    public string? Source { get; set; }

    /// <summary>Diagnostic notes from the extractor (e.g. "found 42 text
    /// fragments, recognised 12 reference designators"). Shown in the UI.</summary>
    public List<string> Notes { get; } = new();
}

public sealed class NetlistNet
{
    public required string Name { get; init; }
}

public sealed class NetlistComponent
{
    /// <summary>Reference designator: R1, C12, U3, J1, Q5, …</summary>
    public required string Reference { get; init; }

    /// <summary>Optional value / part number (e.g. "10k", "MCP23017", "100nF").</summary>
    public string? Value { get; set; }

    /// <summary>Pins in pin-number order.</summary>
    public List<NetlistPin> Pins { get; } = new();
}

public sealed class NetlistPin
{
    /// <summary>Pin number as printed on the symbol (1, 2, … or "A1", "B2"
    /// for BGA — we keep it as a string to be safe).</summary>
    public required string Number { get; init; }

    /// <summary>Optional pin name from the symbol ("VCC", "SDA", "RESET").</summary>
    public string? Name { get; set; }

    /// <summary>Connected net name. Null/empty = unconnected (no-connect).</summary>
    public string? Net { get; set; }
}
