using System.Text.RegularExpressions;
using Tesseract;

namespace BoardviewBuilder;

/// <summary>
/// Thin wrapper around Tesseract.NET for schematic OCR.
///
/// We use it for one job right now: recognise text fragments in a processed
/// schematic <see cref="Bitmap"/> and return them with bounding boxes and
/// confidence scores. The caller (<see cref="SchematicImageLoader"/>) filters
/// those fragments down to reference designators (R1, U3, …) and turns them
/// into <see cref="NetlistComponent"/> entries.
///
/// Tessdata: we look for <c>tessdata/eng.traineddata</c> next to the
/// executable. The csproj copies the repo's <c>BoardviewBuilder/tessdata/</c>
/// folder to the build output on every build.
/// </summary>
public static class OcrEngine
{
    /// <summary>One recognised word with its image-space bounding box.</summary>
    public readonly record struct Word(string Text, Rectangle Bounds, float Confidence);

    /// <summary>Regex for a reference designator: one to three uppercase
    /// letters followed by digits (R1, C12, U3, J1, Q5, IC42, …).</summary>
    public static readonly Regex ReferenceDesignatorRegex =
        new(@"^[A-Z]{1,3}\d{1,4}$", RegexOptions.Compiled);

    /// <summary>Locate the tessdata folder relative to the running executable.
    /// Throws a helpful error if the eng.traineddata file is missing.</summary>
    public static string ResolveTessdataPath()
    {
        // AppContext.BaseDirectory points at the folder containing the .exe.
        string baseDir = AppContext.BaseDirectory;
        string tessdata = Path.Combine(baseDir, "tessdata");
        string engFile = Path.Combine(tessdata, "eng.traineddata");
        if (!File.Exists(engFile))
        {
            throw new FileNotFoundException(
                "Tesseract trained data not found at " + engFile +
                ". Download eng.traineddata from " +
                "https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata " +
                "and place it in a 'tessdata' folder next to the executable.",
                engFile);
        }
        return tessdata;
    }

    /// <summary>Run Tesseract over the given bitmap and return every word it
    /// recognised. The returned bounding boxes are in the bitmap's pixel
    /// coordinate system (top-left origin).</summary>
    public static List<Word> RecognizeWords(Bitmap bitmap)
    {
        string tessdata = ResolveTessdataPath();
        var results = new List<Word>();

        // EngineMode.Default = LSTM if available, falls back to legacy.
        using var engine = new TesseractEngine(tessdata, "eng", EngineMode.Default);

        // Schematics are mostly isolated labels scattered around the page,
        // which fits Tesseract's "sparse text" page segmentation mode best.
        engine.DefaultPageSegMode = PageSegMode.SparseText;

        using var pix = BitmapToPix(bitmap);
        using var page = engine.Process(pix);
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var rect))
            {
                string? word = iter.GetText(PageIteratorLevel.Word);
                if (!string.IsNullOrWhiteSpace(word))
                {
                    word = word.Trim();
                    float conf = iter.GetConfidence(PageIteratorLevel.Word);
                    results.Add(new Word(
                        word,
                        new Rectangle(rect.X1, rect.Y1, rect.Width, rect.Height),
                        conf));
                }
            }
        }
        while (iter.Next(PageIteratorLevel.Word));

        return results;
    }

    /// <summary>Convert a System.Drawing.Bitmap to a Tesseract Pix via an
    /// in-memory PNG round-trip. Slower than direct pixel copy but avoids
    /// fiddling with raw buffer layouts and works for any input format.</summary>
    private static Pix BitmapToPix(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        return Pix.LoadFromMemory(ms.ToArray());
    }
}
