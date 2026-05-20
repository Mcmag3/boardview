using System.Drawing.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BoardviewBuilder;

/// <summary>
/// YOLOv8 ONNX inference for schematic symbol detection.
///
/// This replaces the geometric <see cref="SymbolDetector"/> with a learned
/// model that handles all symbol classes (R, C, D, Q, U, L, IC) at once.
///
/// Model path: BoardviewBuilder/models/symbols.onnx
/// Classes:    BoardviewBuilder/models/symbols.classes.txt
///
/// Usage:
///   var detector = SymbolDetectorYolo.TryLoad("models/symbols.onnx", "models/symbols.classes.txt");
///   if (detector != null)
///   {
///       var hits = detector.Detect(bitmap);
///       // hits is List&lt;SymbolDetector.SymbolHit&gt;
///   }
/// </summary>
public sealed class SymbolDetectorYolo : IDisposable
{
    private const int InputSize = 640;

    private readonly InferenceSession _session;
    private readonly string[] _classNames;
    private readonly string _inputName;
    private bool _disposed;

    /// <summary>Debug info from last detection run.</summary>
    public string LastDebugInfo { get; private set; } = "";

    private SymbolDetectorYolo(InferenceSession session, string[] classNames, string inputName)
    {
        _session = session;
        _classNames = classNames;
        _inputName = inputName;
    }

    /// <summary>
    /// Attempt to load the YOLO model. Returns null if the model file doesn't
    /// exist or fails to load (so the caller can fall back to geometric detection).
    /// </summary>
    public static SymbolDetectorYolo? TryLoad(string modelPath, string classesPath)
    {
        // Try multiple locations for the model:
        // 1. Relative to executable (bin/Debug/.../models/)
        // 2. Relative to current working directory (for dotnet run)
        // 3. Walk up to find BoardviewBuilder/models/ (source tree)
        string? fullModelPath = FindFile(modelPath);
        string? fullClassesPath = FindFile(classesPath);

        if (fullModelPath == null || !File.Exists(fullModelPath))
            return null;

        string[] classNames;
        if (fullClassesPath != null && File.Exists(fullClassesPath))
        {
            classNames = File.ReadAllLines(fullClassesPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
        }
        else
        {
            // Default class names matching LabelEditor.DefaultClasses
            classNames = new[] { "R", "C", "D", "Q", "U", "L", "IC", "OTHER" };
        }

        try
        {
            var options = new SessionOptions();
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            var session = new InferenceSession(fullModelPath, options);

            // Get input name from the model metadata
            string inputName = session.InputNames.FirstOrDefault() ?? "images";

            return new SymbolDetectorYolo(session, classNames, inputName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Run detection on the full bitmap. Returns all detected symbols above
    /// the confidence threshold, after NMS.
    /// </summary>
    public List<SymbolDetector.SymbolHit> Detect(Bitmap image, float confThreshold = 0.25f, float iouThreshold = 0.5f)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SymbolDetectorYolo));

        // Preprocess: letterbox resize to 640x640, BGR->RGB, /255, NCHW
        var (tensor, scale, padX, padY) = Preprocess(image);

        // Run inference
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, tensor)
        };

        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();

        // Debug: log output shape and max values
        var dims = output.Dimensions.ToArray();
        float maxScore = 0f;
        int numClasses = dims[1] - 4;
        int numAnchors = dims[2];
        for (int i = 0; i < numAnchors; i++)
            for (int c = 0; c < numClasses; c++)
                maxScore = Math.Max(maxScore, output[0, 4 + c, i]);

        // Postprocess: decode YOLOv8 output, threshold, NMS
        var detections = Postprocess(output, image.Width, image.Height, scale, padX, padY, confThreshold);

        // Class-wise NMS
        var finalDetections = ApplyNMS(detections, iouThreshold);

        LastDebugInfo = $"shape=[{string.Join(",", dims)}], maxConf={maxScore:F4}, thresh={confThreshold:F4}, preNMS={detections.Count}, postNMS={finalDetections.Count}";

        return finalDetections;
    }

    /// <summary>
    /// Preprocess the image: letterbox to 640x640, normalize to [0,1], NCHW format.
    /// Returns the tensor and scaling/padding info to map coordinates back.
    /// </summary>
    private static (DenseTensor<float> tensor, float scale, float padX, float padY) Preprocess(Bitmap image)
    {
        int origW = image.Width;
        int origH = image.Height;

        // Compute letterbox scale and padding
        float scale = Math.Min((float)InputSize / origW, (float)InputSize / origH);
        int newW = (int)(origW * scale);
        int newH = (int)(origH * scale);
        float padX = (InputSize - newW) / 2f;
        float padY = (InputSize - newH) / 2f;

        // Create padded image (gray background = 114)
        using var resized = new Bitmap(InputSize, InputSize, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.Clear(Color.FromArgb(114, 114, 114));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(image, (int)padX, (int)padY, newW, newH);
        }

        // Convert to NCHW tensor (RGB, normalized to 0-1)
        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        var data = resized.LockBits(new Rectangle(0, 0, InputSize, InputSize),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                byte* p = (byte*)data.Scan0;
                int stride = data.Stride;

                for (int y = 0; y < InputSize; y++)
                {
                    byte* row = p + y * stride;
                    for (int x = 0; x < InputSize; x++)
                    {
                        // BGR -> RGB, normalize to [0,1]
                        byte b = row[x * 3 + 0];
                        byte g = row[x * 3 + 1];
                        byte r = row[x * 3 + 2];

                        tensor[0, 0, y, x] = r / 255f;
                        tensor[0, 1, y, x] = g / 255f;
                        tensor[0, 2, y, x] = b / 255f;
                    }
                }
            }
        }
        finally
        {
            resized.UnlockBits(data);
        }

        return (tensor, scale, padX, padY);
    }

    /// <summary>
    /// Postprocess YOLOv8 output. YOLOv8 outputs shape [1, 4+nc, 8400] where
    /// each column is a detection: [cx, cy, w, h, class_scores...].
    /// </summary>
    private List<SymbolDetector.SymbolHit> Postprocess(
        Tensor<float> output, int origW, int origH,
        float scale, float padX, float padY, float confThreshold)
    {
        var detections = new List<SymbolDetector.SymbolHit>();

        // Output shape: [1, 4+nc, 8400]
        var dims = output.Dimensions.ToArray();
        int numClasses = dims[1] - 4;
        int numAnchors = dims[2];

        for (int i = 0; i < numAnchors; i++)
        {
            // Find best class
            int bestClass = -1;
            float bestScore = confThreshold;

            for (int c = 0; c < numClasses; c++)
            {
                float score = output[0, 4 + c, i];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            if (bestClass < 0) continue;

            // Get box coordinates (in model input space: 640x640 with letterbox)
            float cx = output[0, 0, i];
            float cy = output[0, 1, i];
            float w = output[0, 2, i];
            float h = output[0, 3, i];

            // Convert from letterboxed space back to original image coordinates
            float x1 = (cx - w / 2 - padX) / scale;
            float y1 = (cy - h / 2 - padY) / scale;
            float x2 = (cx + w / 2 - padX) / scale;
            float y2 = (cy + h / 2 - padY) / scale;

            // Clamp to image bounds
            x1 = Math.Max(0, Math.Min(origW, x1));
            y1 = Math.Max(0, Math.Min(origH, y1));
            x2 = Math.Max(0, Math.Min(origW, x2));
            y2 = Math.Max(0, Math.Min(origH, y2));

            int bx = (int)x1;
            int by = (int)y1;
            int bw = Math.Max(1, (int)(x2 - x1));
            int bh = Math.Max(1, (int)(y2 - y1));

            string kind = bestClass < _classNames.Length ? _classNames[bestClass] : $"class{bestClass}";

            detections.Add(new SymbolDetector.SymbolHit
            {
                Bounds = new Rectangle(bx, by, bw, bh),
                Kind = kind,
                Score = bestScore,
            });
        }

        return detections;
    }

    /// <summary>
    /// Apply class-wise Non-Maximum Suppression to remove overlapping detections.
    /// </summary>
    private static List<SymbolDetector.SymbolHit> ApplyNMS(List<SymbolDetector.SymbolHit> detections, float iouThreshold)
    {
        // Group by class
        var byClass = detections.GroupBy(d => d.Kind);
        var result = new List<SymbolDetector.SymbolHit>();

        foreach (var group in byClass)
        {
            var sorted = group.OrderByDescending(d => d.Score).ToList();
            var keep = new List<SymbolDetector.SymbolHit>();

            while (sorted.Count > 0)
            {
                var best = sorted[0];
                keep.Add(best);
                sorted.RemoveAt(0);

                // Remove overlapping boxes
                sorted.RemoveAll(d => ComputeIoU(best.Bounds, d.Bounds) > iouThreshold);
            }

            result.AddRange(keep);
        }

        return result;
    }

    /// <summary>Compute Intersection over Union of two rectangles.</summary>
    private static float ComputeIoU(Rectangle a, Rectangle b)
    {
        int x1 = Math.Max(a.X, b.X);
        int y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.Right, b.Right);
        int y2 = Math.Min(a.Bottom, b.Bottom);

        int interW = Math.Max(0, x2 - x1);
        int interH = Math.Max(0, y2 - y1);
        int interArea = interW * interH;

        int areaA = a.Width * a.Height;
        int areaB = b.Width * b.Height;
        int unionArea = areaA + areaB - interArea;

        return unionArea > 0 ? (float)interArea / unionArea : 0f;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _session.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Find a file by checking multiple locations:
    /// 1. Relative to executable (AppContext.BaseDirectory)
    /// 2. Relative to current working directory
    /// 3. Walk up directory tree to find BoardviewBuilder/models/
    /// </summary>
    private static string? FindFile(string relativePath)
    {
        System.Diagnostics.Debug.WriteLine($"[YOLO] Looking for: {relativePath}");
        System.Diagnostics.Debug.WriteLine($"[YOLO] AppContext.BaseDirectory: {AppContext.BaseDirectory}");
        System.Diagnostics.Debug.WriteLine($"[YOLO] CurrentDirectory: {Directory.GetCurrentDirectory()}");

        // 1. Relative to executable
        string path1 = Path.Combine(AppContext.BaseDirectory, relativePath);
        System.Diagnostics.Debug.WriteLine($"[YOLO] Checking: {path1} -> {File.Exists(path1)}");
        if (File.Exists(path1))
            return path1;

        // 2. Relative to current working directory
        string path2 = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
        System.Diagnostics.Debug.WriteLine($"[YOLO] Checking: {path2} -> {File.Exists(path2)}");
        if (File.Exists(path2))
            return path2;

        // 3. Walk up to find BoardviewBuilder folder (handles running from bin/)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            // Check if this is the BoardviewBuilder project folder
            string path3 = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(path3))
            {
                System.Diagnostics.Debug.WriteLine($"[YOLO] Found at: {path3}");
                return path3;
            }

            // Check if there's a BoardviewBuilder subfolder
            string path4 = Path.Combine(dir.FullName, "BoardviewBuilder", relativePath);
            if (File.Exists(path4))
            {
                System.Diagnostics.Debug.WriteLine($"[YOLO] Found at: {path4}");
                return path4;
            }

            dir = dir.Parent;
        }

        System.Diagnostics.Debug.WriteLine($"[YOLO] Not found: {relativePath}");
        return null;
    }
}
