namespace BoardviewBuilder;

/// <summary>
/// Builds a <see cref="BoardModel"/> from a folder of CSV files:
///
///   nets.csv     one net name per line (order = id). '#' lines / blanks ignored.
///   parts.csv    header: name,x1,y1,x2,y2,side
///   pins.csv     header: part,pin,x,y,net      (net blank = no-connect)
///   nails.csv    header: probe,x,y,net,side    (optional)
///   outline.csv  header: x,y                   (optional; else a rectangle
///                                               is built from width/height)
///
/// A future PdfLoader can implement the same "produce a BoardModel" contract
/// and the rest of the app (preview, generate, save) works unchanged.
/// </summary>
public static class CsvLoader
{
    public static BoardModel Load(string folder, int rectWidth, int rectHeight)
    {
        var board = new BoardModel();

        // --- nets ---
        string netsPath = Path.Combine(folder, "nets.csv");
        if (!File.Exists(netsPath))
            throw new FileNotFoundException($"Required file missing: {netsPath}");
        foreach (var line in DataLines(netsPath))
            board.Nets.Add(new Net { Name = line.Trim() });

        // --- parts ---
        string partsPath = Path.Combine(folder, "parts.csv");
        if (!File.Exists(partsPath))
            throw new FileNotFoundException($"Required file missing: {partsPath}");
        var partsByName = new Dictionary<string, Part>(StringComparer.Ordinal);
        foreach (var row in Rows(partsPath, skipHeader: true))
        {
            // name,x1,y1,x2,y2,side
            var part = new Part
            {
                Name = row[0].Trim(),
                X1 = BrdGenerator.Int(row[1]),
                Y1 = BrdGenerator.Int(row[2]),
                X2 = BrdGenerator.Int(row[3]),
                Y2 = BrdGenerator.Int(row[4]),
                Side = row.Length > 5 && row[5].Trim().Length > 0
                    ? BrdGenerator.Int(row[5]) : 1,
            };
            board.Parts.Add(part);
            partsByName[part.Name] = part;
        }

        // --- pins ---
        string pinsPath = Path.Combine(folder, "pins.csv");
        if (!File.Exists(pinsPath))
            throw new FileNotFoundException($"Required file missing: {pinsPath}");
        foreach (var row in Rows(pinsPath, skipHeader: true))
        {
            // part,pin,x,y,net
            string partName = row[0].Trim();
            if (!partsByName.TryGetValue(partName, out var part))
                throw new InvalidDataException(
                    $"pins.csv references unknown part '{partName}'. " +
                    "Every pin's part must exist in parts.csv.");
            part.Pins.Add(new Pin
            {
                Number = BrdGenerator.Int(row[1]),
                X = BrdGenerator.Int(row[2]),
                Y = BrdGenerator.Int(row[3]),
                Net = row.Length > 4 ? row[4].Trim() : null,
            });
        }

        // --- nails (optional) ---
        string nailsPath = Path.Combine(folder, "nails.csv");
        if (File.Exists(nailsPath))
        {
            foreach (var row in Rows(nailsPath, skipHeader: true))
            {
                // probe,x,y,net,side
                board.Nails.Add(new Nail
                {
                    Probe = BrdGenerator.Int(row[0]),
                    X = BrdGenerator.Int(row[1]),
                    Y = BrdGenerator.Int(row[2]),
                    Net = row.Length > 3 ? row[3].Trim() : null,
                    Side = row.Length > 4 && row[4].Trim().Length > 0
                        ? BrdGenerator.Int(row[4]) : 1,
                });
            }
        }

        // --- outline ---
        string outlinePath = Path.Combine(folder, "outline.csv");
        if (File.Exists(outlinePath))
        {
            foreach (var row in Rows(outlinePath, skipHeader: true))
                board.Outline.Add((BrdGenerator.Int(row[0]), BrdGenerator.Int(row[1])));
        }
        else
        {
            // Rectangle from supplied width/height.
            board.Outline.Add((0, 0));
            board.Outline.Add((rectWidth, 0));
            board.Outline.Add((rectWidth, rectHeight));
            board.Outline.Add((0, rectHeight));
        }

        return board;
    }

    private static IEnumerable<string> DataLines(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            yield return line;
        }
    }

    private static IEnumerable<string[]> Rows(string path, bool skipHeader)
    {
        bool first = true;
        foreach (var line in DataLines(path))
        {
            if (first && skipHeader) { first = false; continue; }
            first = false;
            yield return line.Split(',');
        }
    }
}
