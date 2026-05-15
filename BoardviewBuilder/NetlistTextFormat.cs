using System.Text;

namespace BoardviewBuilder;

/// <summary>
/// Text encoding for a <see cref="Netlist"/> that is both human-readable AND
/// round-trippable. Used as the editable representation in the schematic tab —
/// the user edits the text in the netlist textbox and clicks "Apply edits"
/// to re-parse it back into a Netlist instance, overriding whatever the
/// extractor produced (or supplying the netlist manually if the extractor
/// is wrong or not yet implemented).
///
/// Grammar (case-insensitive section headers, indentation ignored — used only
/// as a visual hint):
///
///   # comment line (ignored)
///   # Source: ...      ← any '# ' line is metadata and discarded on parse
///
///   NETS
///     GND
///     VCC
///     SDA
///
///   COMPONENTS
///     R1 = 10k
///       1 -&gt; VCC
///       2 -&gt; SDA
///
///     U1 = MCP23017
///       1 (SDA)   -&gt; SDA
///       2 (SCL)   -&gt; SCL
///       3         -&gt; GND        (empty net = NC)
///
/// Pin line       : "&lt;number&gt;[ (&lt;name&gt;)] -&gt; &lt;net&gt;"   (presence of "-&gt;" makes it a pin)
/// Component line : "&lt;reference&gt;[ = &lt;value&gt;]"          (no "-&gt;")
/// Order of nets and components is preserved.
/// </summary>
public static class NetlistTextFormat
{
    public static string Format(Netlist n)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Schematic Netlist — edit, then click \"Apply edits\" to update.");
        sb.AppendLine("# Lines starting with '#' are comments and are not parsed.");
        if (!string.IsNullOrEmpty(n.Source))
            sb.Append("# Source: ").AppendLine(n.Source);
        foreach (var note in n.Notes)
            sb.Append("# Note: ").AppendLine(note);
        sb.AppendLine();

        sb.AppendLine("NETS");
        foreach (var net in n.Nets)
            sb.Append("  ").AppendLine(net.Name);
        if (n.Nets.Count == 0)
            sb.AppendLine("  # (no nets — add one per line, e.g. GND)");
        sb.AppendLine();

        sb.AppendLine("COMPONENTS");
        if (n.Components.Count == 0)
            sb.AppendLine("  # (no components — e.g. \"R1 = 10k\" then pins indented)");
        foreach (var c in n.Components)
        {
            sb.Append("  ").Append(c.Reference);
            if (!string.IsNullOrEmpty(c.Value)) sb.Append(" = ").Append(c.Value);
            sb.AppendLine();
            foreach (var p in c.Pins)
            {
                sb.Append("    ").Append(p.Number);
                if (!string.IsNullOrEmpty(p.Name)) sb.Append(" (").Append(p.Name).Append(')');
                sb.Append(" -> ").AppendLine(p.Net ?? "");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Parse the text representation. Returns true if there were no
    /// errors; <paramref name="netlist"/> is always populated as far as parsing
    /// could go, and <paramref name="errors"/> lists any problems with line
    /// numbers.</summary>
    public static bool TryParse(string text, out Netlist netlist, out List<string> errors)
    {
        errors = new List<string>();
        netlist = new Netlist();

        string section = "";
        NetlistComponent? currentComponent = null;
        int lineNo = 0;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("#")) continue;

            // Section header?
            string upper = trimmed.ToUpperInvariant();
            if (upper == "NETS" || upper == "COMPONENTS")
            {
                section = upper;
                currentComponent = null;
                continue;
            }

            if (section == "NETS")
            {
                netlist.Nets.Add(new NetlistNet { Name = trimmed });
            }
            else if (section == "COMPONENTS")
            {
                int arrowIdx = trimmed.IndexOf("->", StringComparison.Ordinal);
                if (arrowIdx >= 0)
                {
                    // ----- Pin line -----
                    if (currentComponent is null)
                    {
                        errors.Add($"Line {lineNo}: pin line before any component definition.");
                        continue;
                    }
                    string left = trimmed.Substring(0, arrowIdx).Trim();
                    string netName = trimmed.Substring(arrowIdx + 2).Trim();

                    string pinNum;
                    string? pinName = null;
                    int parenOpen = left.IndexOf('(');
                    int parenClose = left.IndexOf(')');
                    if (parenOpen > 0 && parenClose > parenOpen)
                    {
                        pinNum = left.Substring(0, parenOpen).Trim();
                        pinName = left.Substring(parenOpen + 1, parenClose - parenOpen - 1).Trim();
                        if (pinName.Length == 0) pinName = null;
                    }
                    else
                    {
                        pinNum = left;
                    }

                    if (pinNum.Length == 0)
                    {
                        errors.Add($"Line {lineNo}: pin number missing before '->'.");
                        continue;
                    }
                    currentComponent.Pins.Add(new NetlistPin
                    {
                        Number = pinNum,
                        Name = pinName,
                        Net = netName.Length > 0 ? netName : null,
                    });
                }
                else
                {
                    // ----- Component header -----
                    string reference;
                    string? value = null;
                    int eqIdx = trimmed.IndexOf('=');
                    if (eqIdx >= 0)
                    {
                        reference = trimmed.Substring(0, eqIdx).Trim();
                        var v = trimmed.Substring(eqIdx + 1).Trim();
                        value = v.Length > 0 ? v : null;
                    }
                    else
                    {
                        reference = trimmed;
                    }
                    if (reference.Length == 0)
                    {
                        errors.Add($"Line {lineNo}: empty reference designator.");
                        continue;
                    }
                    currentComponent = new NetlistComponent { Reference = reference, Value = value };
                    netlist.Components.Add(currentComponent);
                }
            }
            else
            {
                errors.Add($"Line {lineNo}: data outside any section: \"{trimmed}\"");
            }
        }

        return errors.Count == 0;
    }
}
