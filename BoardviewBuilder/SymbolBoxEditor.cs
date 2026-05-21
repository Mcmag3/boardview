namespace BoardviewBuilder;

/// <summary>
/// Modal editor for adding/editing symbol bounding boxes.
/// Used in the multi-step extraction workflow after Phase 1 (OCR + YOLO).
/// User can add boxes for symbols that YOLO missed.
///
/// Workflow:
///   1. Phase 1 runs OCR + YOLO, detects symbol boxes automatically
///   2. User sees current detections (magenta YOLO boxes, blue symbol boxes)
///   3. Drag-LEFT to add a new symbol box for a missed symbol
///   4. Click existing manual box to select, press Del to delete
///   5. Click OK to proceed to Phase 2 (pin detection)
/// </summary>
public sealed class SymbolBoxEditor : Form
{
    private readonly Bitmap _image;
    private readonly List<Rectangle> _yoloBoxes;  // read-only, from YOLO
    private readonly List<Rectangle> _manualBoxes = new();  // user-added boxes

    // UI
    private readonly Label _statusLabel;
    private readonly FocusOnHoverPanel _scroll;
    private readonly DoubleBufferedPictureBox _canvas;
    private readonly TrackBar _zoom;
    private readonly Label _zoomLabel;
    private readonly Label _countLabel;

    // Interaction state
    private bool _drawing;
    private Point _dragStartImg;
    private Point _dragCurImg;
    private bool _panning;
    private Point _panStartCursor;
    private Point _panStartScroll;
    private int _selectedManual = -1;

    /// <summary>Get the manually added symbol boxes.</summary>
    public IReadOnlyList<Rectangle> ManualBoxes => _manualBoxes;

    public SymbolBoxEditor(Bitmap image, IReadOnlyList<SymbolDetector.SymbolHit> yoloHits)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _yoloBoxes = yoloHits.Select(h => h.Bounds).ToList();

        Text = "Edit Symbol Boxes - Add missing symbols";
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

        // Top row: zoom + actions
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, AutoSize = true,
        };
        for (int i = 0; i < 6; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var delBtn = new Button { Text = "Delete Selected (Del)", AutoSize = true, Margin = new Padding(3) };
        delBtn.Click += (_, _) => DeleteSelected();
        top.Controls.Add(delBtn, 0, 0);

        var clearBtn = new Button { Text = "Clear Manual Boxes", AutoSize = true, Margin = new Padding(3) };
        clearBtn.Click += (_, _) =>
        {
            if (_manualBoxes.Count == 0) return;
            if (MessageBox.Show(this, "Remove all manually added boxes?", "Clear",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _manualBoxes.Clear();
                _selectedManual = -1;
                _canvas!.Invalidate();
                UpdateStatus();
            }
        };
        top.Controls.Add(clearBtn, 1, 0);

        top.Controls.Add(new Label { Text = "Zoom:", AutoSize = true, Margin = new Padding(12, 8, 3, 3) }, 2, 0);
        _zoom = new TrackBar
        {
            Minimum = 10, Maximum = 800, TickFrequency = 50, Value = 100,
            Width = 200, AutoSize = false, Height = 30,
        };
        top.Controls.Add(_zoom, 3, 0);
        _zoomLabel = new Label { Text = "100%", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(45, 0) };
        top.Controls.Add(_zoomLabel, 4, 0);

        _countLabel = new Label { Text = "0 manual boxes", AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
        top.Controls.Add(_countLabel, 5, 0);

        root.Controls.Add(top, 0, 0);

        // Middle: scrollable, zoomable canvas
        _scroll = new FocusOnHoverPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.DimGray,
        };
        _canvas = new DoubleBufferedPictureBox
        {
            SizeMode = PictureBoxSizeMode.Normal,
            BackColor = Color.DimGray,
            Location = new Point(0, 0),
            Size = new Size(_image.Width, _image.Height),
            Cursor = Cursors.Cross,
        };
        _scroll.Controls.Add(_canvas);
        root.Controls.Add(_scroll, 0, 1);

        // Bottom: status + OK/cancel
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
            Text = "Drag-LEFT to add a box for missed symbols - Magenta=YOLO, Cyan=Manual - Wheel=zoom - Middle/right-drag=pan",
        };
        bottom.Controls.Add(_statusLabel, 0, 0);

        var okBtn = new Button { Text = "OK - Proceed to Pin Detection", AutoSize = true, Margin = new Padding(3) };
        okBtn.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        bottom.Controls.Add(okBtn, 1, 0);

        var cancelBtn = new Button { Text = "Cancel", AutoSize = true, Margin = new Padding(3) };
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        bottom.Controls.Add(cancelBtn, 2, 0);

        root.Controls.Add(bottom, 0, 2);

        // Events
        _canvas.Paint += OnCanvasPaint;
        _canvas.MouseDown += OnCanvasMouseDown;
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseUp += OnCanvasMouseUp;
        _scroll.MouseWheel += OnMouseWheel;
        _zoom.ValueChanged += (_, _) =>
        {
            _zoomLabel.Text = _zoom.Value + "%";
            ResizeCanvas();
        };
        KeyDown += OnKeyDown;

        UpdateStatus();
    }

    private void ResizeCanvas()
    {
        float scale = _zoom.Value / 100f;
        int w = Math.Max(1, (int)(_image.Width * scale));
        int h = Math.Max(1, (int)(_image.Height * scale));
        _canvas.Size = new Size(w, h);
        _canvas.Invalidate();
    }

    private void UpdateStatus()
    {
        _countLabel.Text = $"{_manualBoxes.Count} manual box(es), {_yoloBoxes.Count} YOLO";
    }

    private void DeleteSelected()
    {
        if (_selectedManual >= 0 && _selectedManual < _manualBoxes.Count)
        {
            _manualBoxes.RemoveAt(_selectedManual);
            _selectedManual = -1;
            _canvas.Invalidate();
            UpdateStatus();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            _selectedManual = -1;
            _canvas.Invalidate();
        }
    }

    private Point ScreenToImage(Point screen)
    {
        float scale = _zoom.Value / 100f;
        int imgX = (int)((screen.X) / scale);
        int imgY = (int)((screen.Y) / scale);
        return new Point(
            Math.Clamp(imgX, 0, _image.Width - 1),
            Math.Clamp(imgY, 0, _image.Height - 1));
    }

    private Rectangle ImageToScreen(Rectangle r)
    {
        float scale = _zoom.Value / 100f;
        return new Rectangle(
            (int)(r.X * scale), (int)(r.Y * scale),
            (int)(r.Width * scale), (int)(r.Height * scale));
    }

    private void OnCanvasMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var imgPt = ScreenToImage(e.Location);

            // Check if clicked on an existing manual box
            for (int i = _manualBoxes.Count - 1; i >= 0; i--)
            {
                if (_manualBoxes[i].Contains(imgPt))
                {
                    _selectedManual = i;
                    _canvas.Invalidate();
                    return;
                }
            }

            // Start drawing a new box
            _selectedManual = -1;
            _drawing = true;
            _dragStartImg = imgPt;
            _dragCurImg = imgPt;
            _canvas.Invalidate();
        }
        else if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
        {
            _panning = true;
            _panStartCursor = Cursor.Position;
            _panStartScroll = new Point(-_scroll.AutoScrollPosition.X, -_scroll.AutoScrollPosition.Y);
            _canvas.Cursor = Cursors.SizeAll;
        }
    }

    private void OnCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (_drawing)
        {
            _dragCurImg = ScreenToImage(e.Location);
            _canvas.Invalidate();
        }
        else if (_panning)
        {
            var cur = Cursor.Position;
            int dx = cur.X - _panStartCursor.X;
            int dy = cur.Y - _panStartCursor.Y;
            int nx = Math.Max(0, _panStartScroll.X - dx);
            int ny = Math.Max(0, _panStartScroll.Y - dy);
            _scroll.AutoScrollPosition = new Point(nx, ny);
        }
    }

    private void OnCanvasMouseUp(object? sender, MouseEventArgs e)
    {
        if (_drawing && e.Button == MouseButtons.Left)
        {
            _drawing = false;
            _dragCurImg = ScreenToImage(e.Location);

            // Create box if large enough
            int x0 = Math.Min(_dragStartImg.X, _dragCurImg.X);
            int y0 = Math.Min(_dragStartImg.Y, _dragCurImg.Y);
            int x1 = Math.Max(_dragStartImg.X, _dragCurImg.X);
            int y1 = Math.Max(_dragStartImg.Y, _dragCurImg.Y);

            if (x1 - x0 >= 10 && y1 - y0 >= 10)
            {
                var box = new Rectangle(x0, y0, x1 - x0, y1 - y0);
                _manualBoxes.Add(box);
                _selectedManual = _manualBoxes.Count - 1;
                UpdateStatus();
            }
            _canvas.Invalidate();
        }
        else if (_panning && (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right))
        {
            _panning = false;
            _canvas.Cursor = Cursors.Cross;
        }
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        // Zoom anchored to cursor
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
        ResizeCanvas();

        float newScale = newZoom / 100f;
        int newScrollX = (int)(imgX * newScale - clientPt.X);
        int newScrollY = (int)(imgY * newScale - clientPt.Y);
        _scroll.AutoScrollPosition = new Point(Math.Max(0, newScrollX), Math.Max(0, newScrollY));
    }

    private void OnCanvasPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        float scale = _zoom.Value / 100f;

        // Draw image
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.DrawImage(_image, 0, 0, (int)(_image.Width * scale), (int)(_image.Height * scale));

        // Draw YOLO boxes (magenta, thin)
        using var penYolo = new Pen(Color.Magenta, 1f);
        foreach (var box in _yoloBoxes)
        {
            var sr = ImageToScreen(box);
            if (sr.Width > 0 && sr.Height > 0)
                g.DrawRectangle(penYolo, sr);
        }

        // Draw manual boxes (cyan, thicker)
        using var penManual = new Pen(Color.Cyan, 2f);
        using var penSelected = new Pen(Color.Yellow, 3f);
        for (int i = 0; i < _manualBoxes.Count; i++)
        {
            var sr = ImageToScreen(_manualBoxes[i]);
            if (sr.Width > 0 && sr.Height > 0)
            {
                var pen = (i == _selectedManual) ? penSelected : penManual;
                g.DrawRectangle(pen, sr);
            }
        }

        // Draw rubber-band if drawing
        if (_drawing)
        {
            int x0 = Math.Min(_dragStartImg.X, _dragCurImg.X);
            int y0 = Math.Min(_dragStartImg.Y, _dragCurImg.Y);
            int x1 = Math.Max(_dragStartImg.X, _dragCurImg.X);
            int y1 = Math.Max(_dragStartImg.Y, _dragCurImg.Y);
            var rect = new Rectangle(x0, y0, x1 - x0, y1 - y0);
            var sr = ImageToScreen(rect);
            using var penDraw = new Pen(Color.Lime, 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(penDraw, sr);
        }
    }
}
