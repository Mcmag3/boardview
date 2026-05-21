using System.Runtime.InteropServices;

namespace BoardviewBuilder;

/// <summary>
/// Modal editor for adding/editing symbol bounding boxes.
/// Used in the multi-step extraction workflow after Phase 1 (OCR + YOLO).
/// User can add boxes for symbols that YOLO missed.
/// </summary>
public sealed class SymbolBoxEditor : Form
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

    public static readonly string[] SymbolTypes = { "R", "C", "D", "Q", "U", "L", "IC", "OTHER" };

    /// <summary>A manual symbol box with its type.</summary>
    public readonly record struct ManualBox(Rectangle Bounds, string SymbolType);

    private readonly Bitmap _image;
    private readonly List<Rectangle> _yoloBoxes;
    private readonly List<Rectangle> _ocrBoxes;
    private readonly List<ManualBox> _manualBoxes = new();

    // UI
    private readonly Label _statusLabel;
    private readonly FocusOnHoverPanel _scroll;
    private readonly DoubleBufferedPictureBox _canvas;
    private readonly TrackBar _zoom;
    private readonly Label _zoomLabel;
    private readonly Label _countLabel;
    private readonly ComboBox _symbolTypeCombo;
    private readonly CheckBox _showOcrCheckbox;

    // Interaction state
    private bool _drawing;
    private Point _dragStartImg;
    private Point _dragCurImg;
    private bool _panning;
    private Point _panStartCursor;
    private Point _panStartScroll;
    private int _selectedManual = -1;

    /// <summary>Get the manually added symbol boxes.</summary>
    public IReadOnlyList<ManualBox> ManualBoxesWithType => _manualBoxes;

    /// <summary>Get just the rectangles (for backward compat).</summary>
    public IReadOnlyList<Rectangle> ManualBoxes => _manualBoxes.Select(b => b.Bounds).ToList();

    public SymbolBoxEditor(
        Bitmap image,
        IReadOnlyList<SymbolDetector.SymbolHit> yoloHits,
        IReadOnlyList<Rectangle>? ocrBoxes = null)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _yoloBoxes = yoloHits.Select(h => h.Bounds).ToList();
        _ocrBoxes = ocrBoxes?.ToList() ?? new List<Rectangle>();

        Text = "Edit Symbol Boxes - Add missing symbols";
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

        // Top row: symbol type, OCR toggle, zoom, actions
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 10, RowCount = 1, AutoSize = true,
            BackColor = Color.FromArgb(45, 45, 45),
        };
        for (int i = 0; i < 10; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        top.Controls.Add(new Label { Text = "Symbol Type:", AutoSize = true, Margin = new Padding(3, 8, 3, 3), ForeColor = Color.FromArgb(220, 220, 220) }, 0, 0);
        _symbolTypeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 80,
            Margin = new Padding(3),
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.FromArgb(220, 220, 220),
            FlatStyle = FlatStyle.Flat,
        };
        _symbolTypeCombo.Items.AddRange(SymbolTypes);
        _symbolTypeCombo.SelectedIndex = 0;
        top.Controls.Add(_symbolTypeCombo, 1, 0);

        _showOcrCheckbox = new CheckBox
        {
            Text = "Show OCR boxes",
            AutoSize = true,
            Margin = new Padding(12, 6, 3, 3),
            Checked = true,
            ForeColor = Color.FromArgb(220, 220, 220),
        };
        _showOcrCheckbox.CheckedChanged += (_, _) => _canvas.Invalidate();
        top.Controls.Add(_showOcrCheckbox, 2, 0);

        var delBtn = CreateDarkButton("Delete (Del)");
        delBtn.Click += (_, _) => DeleteSelected();
        top.Controls.Add(delBtn, 3, 0);

        var clearBtn = CreateDarkButton("Clear Manual");
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
        top.Controls.Add(clearBtn, 4, 0);

        top.Controls.Add(new Label { Text = "Zoom:", AutoSize = true, Margin = new Padding(12, 8, 3, 3), ForeColor = Color.FromArgb(220, 220, 220) }, 5, 0);
        _zoom = new TrackBar
        {
            Minimum = 10, Maximum = 800, TickFrequency = 50, Value = 100,
            Width = 150, AutoSize = false, Height = 30,
            BackColor = Color.FromArgb(45, 45, 45),
        };
        top.Controls.Add(_zoom, 6, 0);
        _zoomLabel = new Label { Text = "100%", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(45, 0), ForeColor = Color.FromArgb(220, 220, 220) };
        top.Controls.Add(_zoomLabel, 7, 0);

        _countLabel = new Label { Text = "0 manual boxes", AutoSize = true, Margin = new Padding(12, 8, 3, 3), ForeColor = Color.FromArgb(220, 220, 220) };
        top.Controls.Add(_countLabel, 8, 0);

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
            Text = "Select symbol type, then drag to add box - Magenta=YOLO, Red=OCR, Cyan=Manual - Wheel=zoom",
            ForeColor = Color.FromArgb(180, 180, 180),
        };
        bottom.Controls.Add(_statusLabel, 0, 0);

        var okBtn = CreateDarkButton("OK - Proceed to Pin Detection");
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
        _countLabel.Text = $"{_manualBoxes.Count} manual, {_yoloBoxes.Count} YOLO";
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
                if (_manualBoxes[i].Bounds.Contains(imgPt))
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
                string symType = _symbolTypeCombo.SelectedItem?.ToString() ?? "OTHER";
                _manualBoxes.Add(new ManualBox(box, symType));
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

        // Scale line thickness based on image size
        float lineScale = GetLineScaleFactor();
        float thinLine = 1f * lineScale;
        float normalLine = 2f * lineScale;
        float thickLine = 3f * lineScale;
        float fontSize = 8f * Math.Min(lineScale, 2.5f);

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

        // Draw manual boxes (cyan, thin like others) with type label
        using var penManual = new Pen(Color.Cyan, thinLine);
        using var penSelected = new Pen(Color.Yellow, thickLine);
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold);
        using var brush = new SolidBrush(Color.Cyan);
        for (int i = 0; i < _manualBoxes.Count; i++)
        {
            var sr = ImageToScreen(_manualBoxes[i].Bounds);
            if (sr.Width > 0 && sr.Height > 0)
            {
                var pen = (i == _selectedManual) ? penSelected : penManual;
                g.DrawRectangle(pen, sr);
                // Draw type label
                g.DrawString(_manualBoxes[i].SymbolType, font, brush, sr.X + 2, sr.Y + 2);
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
            using var penDraw = new Pen(Color.Lime, thinLine) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(penDraw, sr);
        }
    }
}
