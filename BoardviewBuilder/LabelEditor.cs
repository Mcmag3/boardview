using System.Text;

namespace BoardviewBuilder;

/// <summary>
/// Modal labelling editor used to build a YOLO-format training dataset for
/// the learned symbol detector (<see cref="SymbolDetectorYolo"/>, added in
/// a follow-up commit).
///
/// Workflow:
///   1. User loads a schematic in the main form and (optionally) extracts —
///      so any auto-detected resistor bboxes are pre-filled here.
///   2. User clicks "Label for training…" → this form opens with the
///      processed bitmap.
///   3. Click-drag with the LEFT mouse button to create a new bbox.
///   4. The class for new boxes comes from the class combo at the top.
///   5. Click an existing box to select it; press Del to delete; change its
///      class via the combo while it's selected.
///   6. Mouse wheel zooms; middle/right-drag pans.
///   7. "Save" writes the image + label .txt into the dataset folder.
///
/// Output layout (always relative to the project root):
///   dataset/
///     classes.txt                ← "R\nC\nD\nQ\nU\nL\nIC\nOTHER"
///     images/&lt;stem&gt;.png    ← the EXACT processed bitmap shown here
///     labels/&lt;stem&gt;.txt    ← YOLO format: "cls cx cy w h" per line,
///                                  all normalised to 0..1 of the image size.
/// </summary>
public sealed class LabelEditor : Form
{
    public static readonly string[] DefaultClasses =
        new[] { "R", "C", "D", "Q", "U", "L", "IC", "OTHER" };

    private readonly Bitmap _image;             // the bitmap we're labelling (NOT disposed by us)
    private readonly string _stem;              // suggested filename stem (no extension)

    private readonly List<LabelBox> _boxes = new();
    private int _selected = -1;

    // ---- UI ----
    private readonly ComboBox _classCombo;
    private readonly Label _statusLabel;
    private readonly FocusOnHoverPanel _scroll;
    private readonly PictureBox _canvas;
    private readonly TrackBar _zoom;
    private readonly Label _zoomLabel;
    private readonly Label _countLabel;

    // ---- Interaction state ----
    private bool _drawing;
    private Point _dragStartImg;       // image-space coords
    private Point _dragCurImg;
    private bool _panning;
    private Point _panStartCursor;
    private Point _panStartScroll;

    public LabelEditor(Bitmap image, string stem,
                       IEnumerable<LabelBox>? prefill = null)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _stem = string.IsNullOrWhiteSpace(stem) ? "schematic" : stem;

        Text = "Label symbols for training — " + _stem;
        Width = 1100;
        Height = 800;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        KeyPreview = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // ---- Top row: class picker + zoom + actions ----
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 1, AutoSize = true,
        };
        for (int i = 0; i < 8; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        top.Controls.Add(new Label { Text = "Class:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        _classCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120,
            Margin = new Padding(3),
        };
        _classCombo.Items.AddRange(DefaultClasses);
        _classCombo.SelectedIndex = 0;
        _classCombo.SelectedIndexChanged += (_, _) =>
        {
            // Change the selected box's class on the fly.
            if (_selected >= 0 && _selected < _boxes.Count)
            {
                _boxes[_selected].ClassIndex = _classCombo.SelectedIndex;
                _canvas!.Invalidate();
                UpdateStatus();
            }
        };
        top.Controls.Add(_classCombo, 1, 0);

        var delBtn = new Button { Text = "Delete (Del)", AutoSize = true, Margin = new Padding(3) };
        delBtn.Click += (_, _) => DeleteSelected();
        top.Controls.Add(delBtn, 2, 0);

        var clearBtn = new Button { Text = "Clear all", AutoSize = true, Margin = new Padding(3) };
        clearBtn.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Remove ALL labels on this image?", "Clear",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _boxes.Clear();
                _selected = -1;
                _canvas!.Invalidate();
                UpdateStatus();
            }
        };
        top.Controls.Add(clearBtn, 3, 0);

        top.Controls.Add(new Label { Text = "Zoom:", AutoSize = true, Margin = new Padding(12, 8, 3, 3) }, 4, 0);
        _zoom = new TrackBar
        {
            Minimum = 10, Maximum = 800, TickFrequency = 50, Value = 100,
            Width = 200, AutoSize = false, Height = 30,
        };
        top.Controls.Add(_zoom, 5, 0);
        _zoomLabel = new Label { Text = "100%", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(45, 0) };
        top.Controls.Add(_zoomLabel, 6, 0);

        _countLabel = new Label { Text = "0 boxes", AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
        top.Controls.Add(_countLabel, 7, 0);

        root.Controls.Add(top, 0, 0);

        // ---- Middle: scrollable, zoomable canvas ----
        _scroll = new FocusOnHoverPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.DimGray,
        };
        _canvas = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Normal,    // we paint ourselves so we get pixel-perfect coords
            BackColor = Color.DimGray,
            Location = new Point(0, 0),
            Size = new Size(_image.Width, _image.Height),
            Cursor = Cursors.Cross,
        };
        _scroll.Controls.Add(_canvas);
        root.Controls.Add(_scroll, 0, 1);

        // ---- Bottom: status + save/cancel ----
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, AutoSize = true,
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Drag-LEFT to add a box · Click a box to select · Del to delete · Wheel = zoom · Middle/right-drag = pan",
        };
        bottom.Controls.Add(_statusLabel, 0, 0);

        var saveBtn = new Button { Text = "Save labels", AutoSize = true, Margin = new Padding(3) };
        saveBtn.Click += (_, _) => SaveDataset();
        bottom.Controls.Add(saveBtn, 1, 0);

        var cancelBtn = new Button { Text = "Close", AutoSize = true, Margin = new Padding(3) };
        cancelBtn.Click += (_, _) => Close();
        bottom.Controls.Add(cancelBtn, 2, 0);

        root.Controls.Add(bottom, 0, 2);

        // ---- Events ----
        _canvas.Paint += OnCanvasPaint;
        _canvas.MouseDown += OnCanvasMouseDown;
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseUp += OnCanvasMouseUp;
        _canvas.MouseWheel += OnWheelZoom;
        _scroll.MouseWheel += OnWheelZoom;
        _zoom.ValueChanged += (_, _) =>
        {
            _zoomLabel.Text = _zoom.Value + "%";
            ResizeCanvasToZoom();
            _canvas.Invalidate();
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { _selected = -1; _canvas.Invalidate(); e.Handled = true; }
        };

        // ---- Prefill from existing detector hits, if provided ----
        if (prefill != null)
        {
            foreach (var b in prefill)
                _boxes.Add(b);
        }
        UpdateStatus();
        ResizeCanvasToZoom();
    }

    // -----------------------------------------------------------------------
    //  A single labelled box. Stored in IMAGE coordinates so zoom/pan don't
    //  affect the data we persist.
    // -----------------------------------------------------------------------
    public sealed class LabelBox
    {
        public Rectangle Bounds;       // image-space, top-left origin
        public int ClassIndex;         // index into DefaultClasses

        public LabelBox(Rectangle r, int cls) { Bounds = r; ClassIndex = cls; }
    }

    // -----------------------------------------------------------------------
    //  Painting
    // -----------------------------------------------------------------------
    private void OnCanvasPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        // Draw the image scaled to the current canvas size.
        g.DrawImage(_image, new Rectangle(0, 0, _canvas.Width, _canvas.Height));

        float scale = _zoom.Value / 100f;
        using var font = new Font(FontFamily.GenericSansSerif, 9f, FontStyle.Bold);

        for (int i = 0; i < _boxes.Count; i++)
        {
            var b = _boxes[i];
            var r = new Rectangle(
                (int)(b.Bounds.X * scale),
                (int)(b.Bounds.Y * scale),
                (int)(b.Bounds.Width * scale),
                (int)(b.Bounds.Height * scale));

            Color col = ColorForClass(b.ClassIndex);
            using var pen = new Pen(col, i == _selected ? 3f : 2f);
            g.DrawRectangle(pen, r);
            using var brush = new SolidBrush(col);
            string text = ClassNameFor(b.ClassIndex);
            g.DrawString(text, font, brush, r.X + 2, r.Y + 2);
        }

        // In-progress rubber-band.
        if (_drawing)
        {
            int x = Math.Min(_dragStartImg.X, _dragCurImg.X);
            int y = Math.Min(_dragStartImg.Y, _dragCurImg.Y);
            int w = Math.Abs(_dragCurImg.X - _dragStartImg.X);
            int h = Math.Abs(_dragCurImg.Y - _dragStartImg.Y);
            var r = new Rectangle((int)(x * scale), (int)(y * scale),
                                  (int)(w * scale), (int)(h * scale));
            using var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 2f);
            pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
            g.DrawRectangle(pen, r);
        }
    }

    private static Color ColorForClass(int idx) => idx switch
    {
        0 => Color.OrangeRed,       // R
        1 => Color.DodgerBlue,      // C
        2 => Color.Gold,            // D
        3 => Color.MediumPurple,    // Q
        4 => Color.LimeGreen,       // U
        5 => Color.Cyan,            // L
        6 => Color.HotPink,         // IC
        _ => Color.LightGray,       // OTHER
    };

    private static string ClassNameFor(int idx)
        => idx >= 0 && idx < DefaultClasses.Length ? DefaultClasses[idx] : "?";

    // -----------------------------------------------------------------------
    //  Mouse: left = draw / select, middle or right = pan, wheel = zoom
    // -----------------------------------------------------------------------
    private void OnCanvasMouseDown(object? sender, MouseEventArgs e)
    {
        _canvas.Focus();

        if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
        {
            _panning = true;
            _panStartCursor = Cursor.Position;
            _panStartScroll = new Point(-_scroll.AutoScrollPosition.X, -_scroll.AutoScrollPosition.Y);
            _canvas.Cursor = Cursors.SizeAll;
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        var imgPt = CanvasToImage(e.Location);

        // Hit-test existing boxes: pick the SMALLEST that contains the point.
        // (Small boxes are usually the ones the user actually wants.)
        int hit = -1;
        long bestArea = long.MaxValue;
        for (int i = 0; i < _boxes.Count; i++)
        {
            if (_boxes[i].Bounds.Contains(imgPt))
            {
                long a = (long)_boxes[i].Bounds.Width * _boxes[i].Bounds.Height;
                if (a < bestArea) { bestArea = a; hit = i; }
            }
        }
        if (hit >= 0)
        {
            _selected = hit;
            _classCombo.SelectedIndex = _boxes[hit].ClassIndex;
            _canvas.Invalidate();
            UpdateStatus();
            return;
        }

        // Otherwise start drawing a new box.
        _drawing = true;
        _dragStartImg = imgPt;
        _dragCurImg = imgPt;
        _selected = -1;
        _canvas.Invalidate();
    }

    private void OnCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (_panning)
        {
            Point cur = Cursor.Position;
            int dx = cur.X - _panStartCursor.X;
            int dy = cur.Y - _panStartCursor.Y;
            int nx = Math.Max(0, _panStartScroll.X - dx);
            int ny = Math.Max(0, _panStartScroll.Y - dy);
            _scroll.AutoScrollPosition = new Point(nx, ny);
            return;
        }
        if (_drawing)
        {
            _dragCurImg = CanvasToImage(e.Location);
            _canvas.Invalidate();
        }
    }

    private void OnCanvasMouseUp(object? sender, MouseEventArgs e)
    {
        if (_panning && (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right))
        {
            _panning = false;
            _canvas.Cursor = Cursors.Cross;
            return;
        }
        if (!_drawing || e.Button != MouseButtons.Left) return;
        _drawing = false;

        int x = Math.Min(_dragStartImg.X, _dragCurImg.X);
        int y = Math.Min(_dragStartImg.Y, _dragCurImg.Y);
        int w = Math.Abs(_dragCurImg.X - _dragStartImg.X);
        int h = Math.Abs(_dragCurImg.Y - _dragStartImg.Y);
        // Ignore accidental tiny boxes.
        if (w < 4 || h < 4) { _canvas.Invalidate(); return; }

        // Clamp inside image.
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        if (x + w > _image.Width) w = _image.Width - x;
        if (y + h > _image.Height) h = _image.Height - y;

        var box = new LabelBox(new Rectangle(x, y, w, h), _classCombo.SelectedIndex);
        _boxes.Add(box);
        _selected = _boxes.Count - 1;
        _canvas.Invalidate();
        UpdateStatus();
    }

    private void OnWheelZoom(object? sender, MouseEventArgs e)
    {
        Point clientPt = _scroll.PointToClient(Cursor.Position);
        int scrollX = -_scroll.AutoScrollPosition.X;
        int scrollY = -_scroll.AutoScrollPosition.Y;
        float oldScale = _zoom.Value / 100f;
        float imgX = (scrollX + clientPt.X) / oldScale;
        float imgY = (scrollY + clientPt.Y) / oldScale;

        int step = e.Delta > 0 ? +Math.Max(1, _zoom.Value / 8) : -Math.Max(1, _zoom.Value / 9);
        int newZoom = Math.Clamp(_zoom.Value + step, _zoom.Minimum, _zoom.Maximum);
        if (newZoom == _zoom.Value) return;
        _zoom.Value = newZoom;
        _zoomLabel.Text = newZoom + "%";
        ResizeCanvasToZoom();

        float newScale = newZoom / 100f;
        int newScrollX = (int)(imgX * newScale - clientPt.X);
        int newScrollY = (int)(imgY * newScale - clientPt.Y);
        _scroll.AutoScrollPosition = new Point(Math.Max(0, newScrollX), Math.Max(0, newScrollY));
        _canvas.Invalidate();
    }

    private void DeleteSelected()
    {
        if (_selected < 0 || _selected >= _boxes.Count) return;
        _boxes.RemoveAt(_selected);
        _selected = -1;
        _canvas.Invalidate();
        UpdateStatus();
    }

    private Point CanvasToImage(Point canvasPt)
    {
        float scale = _zoom.Value / 100f;
        return new Point((int)(canvasPt.X / scale), (int)(canvasPt.Y / scale));
    }

    private void ResizeCanvasToZoom()
    {
        float scale = _zoom.Value / 100f;
        int w = Math.Max(1, (int)(_image.Width * scale));
        int h = Math.Max(1, (int)(_image.Height * scale));
        _canvas.Size = new Size(w, h);
    }

    private void UpdateStatus()
    {
        var counts = new Dictionary<int, int>();
        foreach (var b in _boxes)
        {
            counts.TryGetValue(b.ClassIndex, out int c);
            counts[b.ClassIndex] = c + 1;
        }
        var parts = new List<string>();
        for (int i = 0; i < DefaultClasses.Length; i++)
            if (counts.TryGetValue(i, out int c) && c > 0)
                parts.Add($"{DefaultClasses[i]}={c}");
        _countLabel.Text = $"{_boxes.Count} boxes" + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : "");
    }

    // -----------------------------------------------------------------------
    //  Save: write image + YOLO-format labels into ./dataset/{images,labels}.
    // -----------------------------------------------------------------------
    private void SaveDataset()
    {
        try
        {
            // Walk up from the current working dir to find a sensible project
            // root: the dataset/ folder lives next to the .csproj.
            string projectRoot = FindProjectRoot();
            string datasetDir = Path.Combine(projectRoot, "dataset");
            string imgDir = Path.Combine(datasetDir, "images");
            string lblDir = Path.Combine(datasetDir, "labels");
            Directory.CreateDirectory(imgDir);
            Directory.CreateDirectory(lblDir);

            // Always (re)write classes.txt so it stays in sync with DefaultClasses.
            File.WriteAllLines(Path.Combine(datasetDir, "classes.txt"),
                               DefaultClasses, new UTF8Encoding(false));

            // Pick a unique stem so multiple labelling passes on the same image
            // don't clobber each other.
            string stem = _stem;
            string imgPath = Path.Combine(imgDir, stem + ".png");
            string lblPath = Path.Combine(lblDir, stem + ".txt");
            int suffix = 1;
            while (File.Exists(imgPath) || File.Exists(lblPath))
            {
                stem = _stem + "_" + suffix;
                imgPath = Path.Combine(imgDir, stem + ".png");
                lblPath = Path.Combine(lblDir, stem + ".txt");
                suffix++;
            }

            // Save the EXACT bitmap the user labelled — not the original on disk,
            // because the user may have applied adjustments (grayscale, threshold,
            // rotate). The labels are in this bitmap's coordinate system.
            _image.Save(imgPath, System.Drawing.Imaging.ImageFormat.Png);

            // YOLO format: one line per box, "class cx cy w h" all in [0..1].
            var sb = new StringBuilder();
            float W = _image.Width;
            float H = _image.Height;
            foreach (var b in _boxes)
            {
                float cx = (b.Bounds.X + b.Bounds.Width  * 0.5f) / W;
                float cy = (b.Bounds.Y + b.Bounds.Height * 0.5f) / H;
                float bw = b.Bounds.Width  / W;
                float bh = b.Bounds.Height / H;
                sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0} {1:0.######} {2:0.######} {3:0.######} {4:0.######}",
                    b.ClassIndex, cx, cy, bw, bh));
            }
            File.WriteAllText(lblPath, sb.ToString(), new UTF8Encoding(false));

            _statusLabel.ForeColor = Color.DarkGreen;
            _statusLabel.Text = $"Saved {_boxes.Count} boxes → {Path.GetRelativePath(projectRoot, imgPath)}";
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Save failed: " + ex.Message;
        }
    }

    /// <summary>Walks up the directory tree from the current working directory
    /// until it finds a folder containing a `.csproj` or `.sln`; that's the
    /// project root. If nothing matches we fall back to cwd.</summary>
    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (dir.GetFiles("*.csproj").Length > 0 ||
                dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            // Also accept the "boardview" repo root: contains a BoardviewBuilder/ folder.
            if (dir.GetDirectories("BoardviewBuilder").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}