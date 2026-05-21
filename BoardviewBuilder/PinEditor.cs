using System.Runtime.InteropServices;

namespace BoardviewBuilder;

/// <summary>
/// Modal editor for adding/editing pins on symbol edges.
/// Used in the multi-step extraction workflow after Phase 2 (pin detection).
/// User can add pins that were missed by automatic detection.
/// </summary>
public sealed class PinEditor : Form
{
    // Windows API for dark title bar and scrollbars
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string? pszSubIdList);

    private static void EnableDarkTitleBar(Form form)
    {
        try
        {
            int value = 1;
            DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch { /* Ignore on older Windows versions */ }
    }

    private static void EnableDarkScrollbars(Control control)
    {
        try
        {
            SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        catch { /* Ignore on older Windows versions */ }
    }

    private readonly Bitmap _image;
    private readonly List<Rectangle> _yoloBoxes;
    private readonly List<Rectangle> _manualBoxes;
    private readonly List<Rectangle> _ocrBoxes;
    private readonly List<WireTracer.DetectedPin> _detectedPins;  // can be modified (deletions)
    private readonly List<WireTracer.DetectedPin> _manualPins = new();
    private readonly HashSet<int> _deletedDetectedPins = new();  // indices of deleted detected pins

    // UI
    private readonly Label _statusLabel;
    private readonly FocusOnHoverPanel _scroll;
    private readonly DoubleBufferedPictureBox _canvas;
    private readonly TrackBar _zoom;
    private readonly Label _zoomLabel;
    private readonly Label _countLabel;
    private readonly CheckBox _showOcrCheckbox;

    // Interaction state
    private bool _panning;
    private Point _panStartCursor;
    private Point _panStartScroll;
    private int _selectedManual = -1;
    private int _selectedDetected = -1;  // for selecting detected pins to delete

    // Box selection for multiple pins
    private bool _boxSelecting;
    private Point _boxSelectStart;
    private Point _boxSelectEnd;
    private readonly HashSet<int> _boxSelectedDetected = new();  // indices selected by box

    /// <summary>Get the manually added pins.</summary>
    public IReadOnlyList<WireTracer.DetectedPin> ManualPins => _manualPins;

    /// <summary>Get the remaining detected pins (after deletions).</summary>
    public IReadOnlyList<WireTracer.DetectedPin> RemainingDetectedPins =>
        _detectedPins.Where((_, i) => !_deletedDetectedPins.Contains(i)).ToList();

    public PinEditor(
        Bitmap image,
        IReadOnlyList<SymbolDetector.SymbolHit> yoloHits,
        IReadOnlyList<Rectangle> manualBoxes,
        IReadOnlyList<WireTracer.DetectedPin> detectedPins,
        IReadOnlyList<Rectangle>? ocrBoxes = null)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _yoloBoxes = yoloHits.Select(h => h.Bounds).ToList();
        _manualBoxes = manualBoxes.ToList();
        _detectedPins = detectedPins.ToList();
        _ocrBoxes = ocrBoxes?.ToList() ?? new List<Rectangle>();

        Text = "Edit Pins - Add missing pin locations";
        Width = 1100;
        Height = 800;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        KeyPreview = true;

        // Dark theme
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(220, 220, 220);

        // Enable dark title bar (must be done after handle is created)
        HandleCreated += (_, _) => EnableDarkTitleBar(this);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 3,
            BackColor = Color.FromArgb(30, 30, 30),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // Top row: OCR toggle, zoom, actions
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 10, RowCount = 1, AutoSize = true,
            BackColor = Color.FromArgb(45, 45, 45),
        };
        for (int i = 0; i < 10; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _showOcrCheckbox = new CheckBox
        {
            Text = "Show OCR",
            AutoSize = true,
            Margin = new Padding(3, 6, 3, 3),
            Checked = true,
            ForeColor = Color.FromArgb(220, 220, 220),
        };
        _showOcrCheckbox.CheckedChanged += (_, _) => _canvas!.Invalidate();
        top.Controls.Add(_showOcrCheckbox, 0, 0);

        var delManualBtn = CreateDarkButton("Delete Manual (Del)");
        delManualBtn.Click += (_, _) => DeleteSelectedManual();
        top.Controls.Add(delManualBtn, 1, 0);

        var delDetectedBtn = CreateDarkButton("Delete Selected Pins");
        delDetectedBtn.Click += (_, _) => DeleteBoxSelectedDetected();
        top.Controls.Add(delDetectedBtn, 2, 0);

        var clearManualBtn = CreateDarkButton("Clear Manual");
        clearManualBtn.Click += (_, _) =>
        {
            if (_manualPins.Count == 0) return;
            if (MessageBox.Show(this, "Remove all manually added pins?", "Clear",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _manualPins.Clear();
                _selectedManual = -1;
                _canvas!.Invalidate();
                UpdateStatus();
            }
        };
        top.Controls.Add(clearManualBtn, 3, 0);

        top.Controls.Add(new Label { Text = "Zoom:", AutoSize = true, Margin = new Padding(12, 8, 3, 3), ForeColor = Color.FromArgb(220, 220, 220) }, 4, 0);
        _zoom = new TrackBar
        {
            Minimum = 10, Maximum = 800, TickFrequency = 50, Value = 100,
            Width = 150, AutoSize = false, Height = 30,
            BackColor = Color.FromArgb(45, 45, 45),
        };
        top.Controls.Add(_zoom, 5, 0);
        _zoomLabel = new Label { Text = "100%", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(45, 0), ForeColor = Color.FromArgb(220, 220, 220) };
        top.Controls.Add(_zoomLabel, 6, 0);

        _countLabel = new Label { Text = "", AutoSize = true, Margin = new Padding(12, 8, 3, 3), ForeColor = Color.FromArgb(220, 220, 220) };
        top.Controls.Add(_countLabel, 7, 0);

        root.Controls.Add(top, 0, 0);

        // Middle: scrollable, zoomable canvas
        _scroll = new FocusOnHoverPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.FromArgb(25, 25, 25),
        };
        _scroll.HandleCreated += (s, _) => EnableDarkScrollbars((Control)s!);
        _canvas = new DoubleBufferedPictureBox
        {
            SizeMode = PictureBoxSizeMode.Normal,
            BackColor = Color.FromArgb(25, 25, 25),
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
            BackColor = Color.FromArgb(45, 45, 45),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Left-drag to box-select pins - Right-click to add pin - Wheel=zoom - Middle-drag=pan",
            ForeColor = Color.FromArgb(180, 180, 180),
        };
        bottom.Controls.Add(_statusLabel, 0, 0);

        var okBtn = CreateDarkButton("OK - Proceed to Wire Tracing");
        okBtn.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        bottom.Controls.Add(okBtn, 1, 0);

        var cancelBtn = CreateDarkButton("Cancel");
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

    private static Button CreateDarkButton(string text) => new Button
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(3),
        BackColor = Color.FromArgb(60, 60, 60),
        ForeColor = Color.FromArgb(220, 220, 220),
        FlatStyle = FlatStyle.Flat,
    };

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
        int activeDetected = _detectedPins.Count - _deletedDetectedPins.Count;
        _countLabel.Text = $"{_manualPins.Count} manual, {activeDetected} detected";
    }

    private void DeleteSelectedManual()
    {
        if (_selectedManual >= 0 && _selectedManual < _manualPins.Count)
        {
            _manualPins.RemoveAt(_selectedManual);
            _selectedManual = -1;
            _canvas.Invalidate();
            UpdateStatus();
        }
    }

    private void DeleteSelectedDetected()
    {
        if (_selectedDetected >= 0 && _selectedDetected < _detectedPins.Count && !_deletedDetectedPins.Contains(_selectedDetected))
        {
            _deletedDetectedPins.Add(_selectedDetected);
            _selectedDetected = -1;
            _canvas.Invalidate();
            UpdateStatus();
        }
    }

    private void DeleteBoxSelectedDetected()
    {
        // Delete all pins selected by box selection
        if (_boxSelectedDetected.Count > 0)
        {
            foreach (var idx in _boxSelectedDetected)
            {
                _deletedDetectedPins.Add(idx);
            }
            _boxSelectedDetected.Clear();
            _canvas.Invalidate();
            UpdateStatus();
        }
        // Also delete single selected if any
        else if (_selectedDetected >= 0)
        {
            DeleteSelectedDetected();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
        {
            if (_selectedManual >= 0)
                DeleteSelectedManual();
            else if (_selectedDetected >= 0)
                DeleteSelectedDetected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            _selectedManual = -1;
            _selectedDetected = -1;
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

    private Point ImageToScreen(Point p)
    {
        float scale = _zoom.Value / 100f;
        return new Point((int)(p.X * scale), (int)(p.Y * scale));
    }

    private Rectangle ImageToScreen(Rectangle r)
    {
        float scale = _zoom.Value / 100f;
        return new Rectangle(
            (int)(r.X * scale), (int)(r.Y * scale),
            (int)(r.Width * scale), (int)(r.Height * scale));
    }

    /// <summary>Get all symbol boxes (YOLO + manual).</summary>
    private IEnumerable<Rectangle> AllSymbolBoxes => _yoloBoxes.Concat(_manualBoxes);

    private (Rectangle box, string side)? FindNearestSymbolEdge(Point imgPt)
    {
        const int maxDist = 30;
        Rectangle? bestBox = null;
        string bestSide = "";
        int bestDist = int.MaxValue;

        foreach (var box in AllSymbolBoxes)
        {
            // Top edge
            if (imgPt.X >= box.X && imgPt.X < box.Right)
            {
                int dist = Math.Abs(imgPt.Y - box.Y);
                if (dist < bestDist && dist < maxDist)
                {
                    bestDist = dist;
                    bestBox = box;
                    bestSide = "Top";
                }
            }
            // Bottom edge
            if (imgPt.X >= box.X && imgPt.X < box.Right)
            {
                int dist = Math.Abs(imgPt.Y - (box.Bottom - 1));
                if (dist < bestDist && dist < maxDist)
                {
                    bestDist = dist;
                    bestBox = box;
                    bestSide = "Bottom";
                }
            }
            // Left edge
            if (imgPt.Y >= box.Y && imgPt.Y < box.Bottom)
            {
                int dist = Math.Abs(imgPt.X - box.X);
                if (dist < bestDist && dist < maxDist)
                {
                    bestDist = dist;
                    bestBox = box;
                    bestSide = "Left";
                }
            }
            // Right edge
            if (imgPt.Y >= box.Y && imgPt.Y < box.Bottom)
            {
                int dist = Math.Abs(imgPt.X - (box.Right - 1));
                if (dist < bestDist && dist < maxDist)
                {
                    bestDist = dist;
                    bestBox = box;
                    bestSide = "Right";
                }
            }
        }

        if (bestBox.HasValue)
            return (bestBox.Value, bestSide);
        return null;
    }

    private Point SnapToEdge(Point imgPt, Rectangle box, string side)
    {
        return side switch
        {
            "Top" => new Point(Math.Clamp(imgPt.X, box.X, box.Right - 1), box.Y),
            "Bottom" => new Point(Math.Clamp(imgPt.X, box.X, box.Right - 1), box.Bottom - 1),
            "Left" => new Point(box.X, Math.Clamp(imgPt.Y, box.Y, box.Bottom - 1)),
            "Right" => new Point(box.Right - 1, Math.Clamp(imgPt.Y, box.Y, box.Bottom - 1)),
            _ => imgPt
        };
    }

    private void OnCanvasMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var imgPt = ScreenToImage(e.Location);

            // Start box selection with left-click drag
            _boxSelecting = true;
            _boxSelectStart = imgPt;
            _boxSelectEnd = imgPt;
            _boxSelectedDetected.Clear();
            _selectedManual = -1;
            _selectedDetected = -1;
            _canvas.Invalidate();
        }
        else if (e.Button == MouseButtons.Right)
        {
            // Right-click to add a pin near symbol edge
            var imgPt = ScreenToImage(e.Location);
            var edge = FindNearestSymbolEdge(imgPt);
            if (edge.HasValue)
            {
                var snapped = SnapToEdge(imgPt, edge.Value.box, edge.Value.side);
                var pin = new WireTracer.DetectedPin("Manual", snapped, 0, edge.Value.side);
                _manualPins.Add(pin);
                _selectedManual = _manualPins.Count - 1;
                _selectedDetected = -1;
                _boxSelectedDetected.Clear();
                _canvas.Invalidate();
                UpdateStatus();
            }
        }
        else if (e.Button == MouseButtons.Middle)
        {
            // Middle-click for panning
            _panning = true;
            _panStartCursor = Cursor.Position;
            _panStartScroll = new Point(-_scroll.AutoScrollPosition.X, -_scroll.AutoScrollPosition.Y);
            _canvas.Cursor = Cursors.SizeAll;
        }
    }

    private void OnCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (_boxSelecting)
        {
            _boxSelectEnd = ScreenToImage(e.Location);
            // Update selected pins within the box
            UpdateBoxSelection();
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

    private void UpdateBoxSelection()
    {
        _boxSelectedDetected.Clear();
        int x0 = Math.Min(_boxSelectStart.X, _boxSelectEnd.X);
        int y0 = Math.Min(_boxSelectStart.Y, _boxSelectEnd.Y);
        int x1 = Math.Max(_boxSelectStart.X, _boxSelectEnd.X);
        int y1 = Math.Max(_boxSelectStart.Y, _boxSelectEnd.Y);
        var selRect = new Rectangle(x0, y0, x1 - x0, y1 - y0);

        for (int i = 0; i < _detectedPins.Count; i++)
        {
            if (_deletedDetectedPins.Contains(i)) continue;
            var loc = _detectedPins[i].Location;
            if (selRect.Contains(loc))
            {
                _boxSelectedDetected.Add(i);
            }
        }
    }

    private void OnCanvasMouseUp(object? sender, MouseEventArgs e)
    {
        if (_boxSelecting && e.Button == MouseButtons.Left)
        {
            _boxSelecting = false;
            _boxSelectEnd = ScreenToImage(e.Location);
            UpdateBoxSelection();
            _canvas.Invalidate();
            if (_boxSelectedDetected.Count > 0)
            {
                _statusLabel.Text = $"{_boxSelectedDetected.Count} pins selected - Click 'Delete Selected Pins' to remove";
            }
        }
        else if (_panning && e.Button == MouseButtons.Middle)
        {
            _panning = false;
            _canvas.Cursor = Cursors.Cross;
        }
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
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
        ResizeCanvas();

        float newScale = newZoom / 100f;
        int newScrollX = (int)(imgX * newScale - clientPt.X);
        int newScrollY = (int)(imgY * newScale - clientPt.Y);
        _scroll.AutoScrollPosition = new Point(Math.Max(0, newScrollX), Math.Max(0, newScrollY));
    }

    /// <summary>Calculate a scale factor for line thickness based on image size.</summary>
    private float GetLineScaleFactor()
    {
        double diagonal = Math.Sqrt(_image.Width * _image.Width + _image.Height * _image.Height);
        return Math.Clamp((float)(diagonal / 1000.0), 1.0f, 5.0f);
    }

    private void OnCanvasPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        float zoomScale = _zoom.Value / 100f;

        // Scale line thickness and pin radius based on image size
        float lineScale = GetLineScaleFactor();
        float thinLine = 1f * lineScale;
        float normalLine = 2f * lineScale;
        float thickLine = 3f * lineScale;
        int basePinRadius = Math.Max(3, (int)(3 * lineScale));  // Reduced pin size

        // Draw image
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.DrawImage(_image, 0, 0, (int)(_image.Width * zoomScale), (int)(_image.Height * zoomScale));

        // Draw OCR boxes if enabled (red, thin)
        if (_showOcrCheckbox.Checked)
        {
            using var penOcr = new Pen(Color.Red, thinLine);
            foreach (var box in _ocrBoxes)
            {
                var sr = ImageToScreen(box);
                if (sr.Width > 0 && sr.Height > 0)
                    g.DrawRectangle(penOcr, sr);
            }
        }

        // Draw YOLO boxes (magenta, thin like OCR)
        using var penYolo = new Pen(Color.Magenta, thinLine);
        foreach (var box in _yoloBoxes)
        {
            var sr = ImageToScreen(box);
            if (sr.Width > 0 && sr.Height > 0)
                g.DrawRectangle(penYolo, sr);
        }

        // Draw manual symbol boxes (cyan)
        using var penManualBox = new Pen(Color.Cyan, normalLine);
        foreach (var box in _manualBoxes)
        {
            var sr = ImageToScreen(box);
            if (sr.Width > 0 && sr.Height > 0)
                g.DrawRectangle(penManualBox, sr);
        }

        // Draw detected pins (orange, yellow if selected, cyan if box-selected)
        using var brushDetected = new SolidBrush(Color.OrangeRed);
        using var brushBoxSelected = new SolidBrush(Color.Cyan);
        using var penDetected = new Pen(Color.DarkOrange, thinLine);
        using var penDetectedSelected = new Pen(Color.Yellow, normalLine);
        using var penBoxSelected = new Pen(Color.DarkCyan, thickLine);
        for (int i = 0; i < _detectedPins.Count; i++)
        {
            if (_deletedDetectedPins.Contains(i)) continue;
            var pin = _detectedPins[i];
            var sp = ImageToScreen(pin.Location);
            int sr = (int)(basePinRadius * zoomScale);
            sr = Math.Max(3, sr);

            bool isBoxSelected = _boxSelectedDetected.Contains(i);
            var brush = isBoxSelected ? brushBoxSelected : brushDetected;
            g.FillEllipse(brush, sp.X - sr, sp.Y - sr, sr * 2, sr * 2);

            Pen pen;
            if (isBoxSelected)
                pen = penBoxSelected;
            else if (i == _selectedDetected)
                pen = penDetectedSelected;
            else
                pen = penDetected;
            g.DrawEllipse(pen, sp.X - sr, sp.Y - sr, sr * 2, sr * 2);
        }

        // Draw manual pins (lime green)
        using var brushManual = new SolidBrush(Color.Lime);
        using var penManual = new Pen(Color.DarkGreen, thinLine);
        using var penSelected = new Pen(Color.Yellow, normalLine);
        for (int i = 0; i < _manualPins.Count; i++)
        {
            var pin = _manualPins[i];
            var sp = ImageToScreen(pin.Location);
            int sr = (int)(basePinRadius * zoomScale);
            sr = Math.Max(3, sr);
            g.FillEllipse(brushManual, sp.X - sr, sp.Y - sr, sr * 2, sr * 2);
            var pen = (i == _selectedManual) ? penSelected : penManual;
            g.DrawEllipse(pen, sp.X - sr, sp.Y - sr, sr * 2, sr * 2);
        }

        // Draw box selection rectangle if active
        if (_boxSelecting)
        {
            int x0 = Math.Min(_boxSelectStart.X, _boxSelectEnd.X);
            int y0 = Math.Min(_boxSelectStart.Y, _boxSelectEnd.Y);
            int x1 = Math.Max(_boxSelectStart.X, _boxSelectEnd.X);
            int y1 = Math.Max(_boxSelectStart.Y, _boxSelectEnd.Y);
            var imgRect = new Rectangle(x0, y0, x1 - x0, y1 - y0);
            var screenRect = ImageToScreen(imgRect);
            using var penBox = new Pen(Color.Cyan, normalLine) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(penBox, screenRect);
        }
    }
}
