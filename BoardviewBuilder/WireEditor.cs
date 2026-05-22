using System.Runtime.InteropServices;

namespace BoardviewBuilder;

/// <summary>
/// Modal editor for editing wire connections between pins.
/// User can delete wire sections and add new connections manually.
/// </summary>
public sealed class WireEditor : Form
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

    /// <summary>A wire connection between two pins.</summary>
    public readonly record struct WireConnection(string Pin1, Point Location1, string Pin2, Point Location2);

    private readonly Bitmap _image;
    private readonly List<Rectangle> _symbolBoxes;
    private readonly List<WireTracer.DetectedPin> _pins;
    private List<WireConnection> _connections;
    private readonly HashSet<int> _deletedConnections = new();

    // UI
    private readonly Label _statusLabel;
    private readonly FocusOnHoverPanel _scroll;
    private readonly DoubleBufferedPictureBox _canvas;
    private readonly TrackBar _zoom;
    private readonly Label _zoomLabel;
    private readonly Label _countLabel;

    // Interaction state
    private bool _panning;
    private Point _panStartCursor;
    private Point _panStartScroll;
    private int _selectedConnection = -1;
    private int _selectedPinForWire = -1;  // Pin index when drawing a new wire
    private bool _drawingWire;
    private Point _wireEndPoint;

    // Context menu
    private readonly ContextMenuStrip _contextMenu;

    /// <summary>Get the final connections after editing.</summary>
    public IReadOnlyList<WireConnection> FinalConnections =>
        _connections.Where((_, i) => !_deletedConnections.Contains(i)).ToList();

    public WireEditor(
        Bitmap image,
        IReadOnlyList<Rectangle> symbolBoxes,
        IReadOnlyList<WireTracer.DetectedPin> pins,
        IReadOnlyList<WireTracer.TracedWire> tracedWires)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _symbolBoxes = symbolBoxes.ToList();
        _pins = pins.ToList();

        // Convert traced wires to MST connections (not all-pairs)
        _connections = ConvertToMSTConnections(tracedWires);

        Text = "Edit Wire Connections";
        Width = 1100;
        Height = 800;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        KeyPreview = true;

        // Dark theme
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(220, 220, 220);

        // Enable dark title bar
        HandleCreated += (_, _) => EnableDarkTitleBar(this);

        // Context menu for wire deletion
        _contextMenu = new ContextMenuStrip();
        _contextMenu.BackColor = Color.FromArgb(45, 45, 45);
        _contextMenu.ForeColor = Color.FromArgb(220, 220, 220);
        var deleteItem = new ToolStripMenuItem("Delete Wire");
        deleteItem.Click += (_, _) => DeleteSelectedWire();
        _contextMenu.Items.Add(deleteItem);

        // Layout
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.FromArgb(30, 30, 30),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // Top bar: zoom + count
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.FromArgb(45, 45, 45),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var zoomLbl = new Label { Text = "Zoom:", AutoSize = true, Margin = new Padding(8, 10, 3, 3), ForeColor = Color.FromArgb(220, 220, 220) };
        top.Controls.Add(zoomLbl, 0, 0);

        _zoom = new TrackBar { Minimum = 10, Maximum = 400, Value = 100, TickFrequency = 50, Width = 150, AutoSize = false, Height = 30 };
        _zoom.ValueChanged += (_, _) => { _zoomLabel.Text = _zoom.Value + "%"; ApplyZoom(); };
        top.Controls.Add(_zoom, 1, 0);

        _zoomLabel = new Label { Text = "100%", AutoSize = true, Margin = new Padding(3, 10, 8, 3), ForeColor = Color.FromArgb(220, 220, 220) };
        top.Controls.Add(_zoomLabel, 2, 0);

        _countLabel = new Label
        {
            Text = $"Connections: {_connections.Count}",
            AutoSize = true,
            Margin = new Padding(8, 10, 8, 3),
            ForeColor = Color.FromArgb(180, 180, 180),
        };
        top.Controls.Add(_countLabel, 4, 0);

        root.Controls.Add(top, 0, 0);

        // Middle: scrollable canvas
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

        // Bottom: status + buttons
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.FromArgb(45, 45, 45),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Left-click pin to start wire - Right-click wire to delete - Wheel=zoom - Middle-drag=pan",
            ForeColor = Color.FromArgb(180, 180, 180),
        };
        bottom.Controls.Add(_statusLabel, 0, 0);

        var okBtn = CreateDarkButton("OK - Build Netlist");
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
        KeyDown += OnKeyDown;

        ApplyZoom();
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
        FlatAppearance = { BorderColor = Color.FromArgb(80, 80, 80) },
    };

    private void UpdateStatus()
    {
        int activeCount = _connections.Count - _deletedConnections.Count;
        _countLabel.Text = $"Connections: {activeCount}";
    }

    private void ApplyZoom()
    {
        float scale = _zoom.Value / 100f;
        _canvas.Size = new Size((int)(_image.Width * scale), (int)(_image.Height * scale));
        _canvas.Invalidate();
    }

    private Point ScreenToImage(Point screenPt)
    {
        float scale = _zoom.Value / 100f;
        return new Point((int)(screenPt.X / scale), (int)(screenPt.Y / scale));
    }

    private Point ImageToScreen(Point imgPt)
    {
        float scale = _zoom.Value / 100f;
        return new Point((int)(imgPt.X * scale), (int)(imgPt.Y * scale));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            if (_drawingWire)
            {
                _drawingWire = false;
                _selectedPinForWire = -1;
                _canvas.Invalidate();
                _statusLabel.Text = "Wire drawing cancelled.";
            }
            else
            {
                _selectedConnection = -1;
                _canvas.Invalidate();
            }
        }
        else if (e.KeyCode == Keys.Delete && _selectedConnection >= 0)
        {
            DeleteSelectedWire();
        }
    }

    private void DeleteSelectedWire()
    {
        if (_selectedConnection >= 0 && !_deletedConnections.Contains(_selectedConnection))
        {
            _deletedConnections.Add(_selectedConnection);
            _selectedConnection = -1;
            _canvas.Invalidate();
            UpdateStatus();
            _statusLabel.Text = "Wire deleted.";
        }
    }

    private int FindPinAt(Point imgPt, int radius = 15)
    {
        for (int i = 0; i < _pins.Count; i++)
        {
            var pin = _pins[i];
            int dx = imgPt.X - pin.Location.X;
            int dy = imgPt.Y - pin.Location.Y;
            if (dx * dx + dy * dy <= radius * radius)
                return i;
        }
        return -1;
    }

    private int FindConnectionAt(Point imgPt, int threshold = 10)
    {
        for (int i = 0; i < _connections.Count; i++)
        {
            if (_deletedConnections.Contains(i)) continue;

            var conn = _connections[i];
            // Check if point is near the line segment
            double dist = PointToLineDistance(imgPt, conn.Location1, conn.Location2);
            if (dist <= threshold)
                return i;
        }
        return -1;
    }

    private static double PointToLineDistance(Point p, Point lineStart, Point lineEnd)
    {
        double dx = lineEnd.X - lineStart.X;
        double dy = lineEnd.Y - lineStart.Y;
        double lengthSq = dx * dx + dy * dy;

        if (lengthSq == 0)
            return Math.Sqrt((p.X - lineStart.X) * (p.X - lineStart.X) + (p.Y - lineStart.Y) * (p.Y - lineStart.Y));

        double t = Math.Max(0, Math.Min(1, ((p.X - lineStart.X) * dx + (p.Y - lineStart.Y) * dy) / lengthSq));
        double projX = lineStart.X + t * dx;
        double projY = lineStart.Y + t * dy;

        return Math.Sqrt((p.X - projX) * (p.X - projX) + (p.Y - projY) * (p.Y - projY));
    }

    private void OnCanvasMouseDown(object? sender, MouseEventArgs e)
    {
        var imgPt = ScreenToImage(e.Location);

        if (e.Button == MouseButtons.Left)
        {
            // Check if clicking on a pin
            int pinIdx = FindPinAt(imgPt);
            if (pinIdx >= 0)
            {
                if (_drawingWire && _selectedPinForWire >= 0 && _selectedPinForWire != pinIdx)
                {
                    // Complete the wire
                    var startPin = _pins[_selectedPinForWire];
                    var endPin = _pins[pinIdx];
                    var newConn = new WireConnection(
                        $"{startPin.Designator}.{startPin.Side[0]}",
                        startPin.Location,
                        $"{endPin.Designator}.{endPin.Side[0]}",
                        endPin.Location);
                    _connections.Add(newConn);
                    _drawingWire = false;
                    _selectedPinForWire = -1;
                    _canvas.Invalidate();
                    UpdateStatus();
                    _statusLabel.Text = "Wire added.";
                }
                else
                {
                    // Start drawing a new wire
                    _selectedPinForWire = pinIdx;
                    _drawingWire = true;
                    _wireEndPoint = imgPt;
                    _selectedConnection = -1;
                    _canvas.Invalidate();
                    _statusLabel.Text = $"Drawing wire from {_pins[pinIdx].Designator}.{_pins[pinIdx].Side[0]} - Click another pin to connect";
                }
                return;
            }

            // Check if clicking on a connection
            int connIdx = FindConnectionAt(imgPt);
            if (connIdx >= 0)
            {
                _selectedConnection = connIdx;
                _drawingWire = false;
                _selectedPinForWire = -1;
                _canvas.Invalidate();
                var conn = _connections[connIdx];
                _statusLabel.Text = $"Selected wire: {conn.Pin1} - {conn.Pin2} (Press Delete or right-click to remove)";
                return;
            }

            // Clicked on empty space - deselect
            _selectedConnection = -1;
            if (_drawingWire)
            {
                _drawingWire = false;
                _selectedPinForWire = -1;
                _statusLabel.Text = "Wire drawing cancelled.";
            }
            _canvas.Invalidate();
        }
        else if (e.Button == MouseButtons.Right)
        {
            // Right-click on a connection to show context menu
            int connIdx = FindConnectionAt(imgPt);
            if (connIdx >= 0)
            {
                _selectedConnection = connIdx;
                _canvas.Invalidate();
                _contextMenu.Show(_canvas, e.Location);
            }
            else if (_drawingWire)
            {
                _drawingWire = false;
                _selectedPinForWire = -1;
                _canvas.Invalidate();
                _statusLabel.Text = "Wire drawing cancelled.";
            }
        }
        else if (e.Button == MouseButtons.Middle)
        {
            _panning = true;
            _panStartCursor = Cursor.Position;
            _panStartScroll = new Point(-_scroll.AutoScrollPosition.X, -_scroll.AutoScrollPosition.Y);
            _canvas.Cursor = Cursors.SizeAll;
        }
    }

    private void OnCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (_drawingWire)
        {
            _wireEndPoint = ScreenToImage(e.Location);
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
        if (_panning && e.Button == MouseButtons.Middle)
        {
            _panning = false;
            _canvas.Cursor = Cursors.Cross;
        }
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        int step = e.Delta > 0 ? +10 : -10;
        int newVal = Math.Clamp(_zoom.Value + step, _zoom.Minimum, _zoom.Maximum);
        if (newVal != _zoom.Value)
        {
            _zoom.Value = newVal;
        }
    }

    private void OnCanvasPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(25, 25, 25));

        float scale = _zoom.Value / 100f;

        // Draw the image
        g.DrawImage(_image, 0, 0, _canvas.Width, _canvas.Height);

        // Scale line thicknesses based on image size
        float diag = (float)Math.Sqrt(_image.Width * _image.Width + _image.Height * _image.Height);
        float thinLine = Math.Max(1f, diag / 1500f) * scale;
        float normalLine = Math.Max(2f, diag / 1000f) * scale;
        float thickLine = Math.Max(3f, diag / 700f) * scale;
        int pinRadius = Math.Max(4, (int)(diag / 400 * scale));

        // Draw symbol boxes (magenta, thin)
        using var penBox = new Pen(Color.Magenta, thinLine);
        foreach (var box in _symbolBoxes)
        {
            var sr = new Rectangle(
                (int)(box.X * scale), (int)(box.Y * scale),
                (int)(box.Width * scale), (int)(box.Height * scale));
            g.DrawRectangle(penBox, sr);
        }

        // Draw connections (wires)
        using var penWire = new Pen(Color.DodgerBlue, normalLine);
        using var penWireSelected = new Pen(Color.Yellow, thickLine);
        using var penWireDeleted = new Pen(Color.FromArgb(80, 80, 80), thinLine);

        for (int i = 0; i < _connections.Count; i++)
        {
            var conn = _connections[i];
            var p1 = ImageToScreen(conn.Location1);
            var p2 = ImageToScreen(conn.Location2);

            Pen pen;
            if (_deletedConnections.Contains(i))
                continue; // Don't draw deleted wires
            else if (i == _selectedConnection)
                pen = penWireSelected;
            else
                pen = penWire;

            g.DrawLine(pen, p1, p2);
        }

        // Draw component pins (orange, lime when selected)
        using var brushPin = new SolidBrush(Color.OrangeRed);
        using var brushPinSelected = new SolidBrush(Color.Lime);
        using var penPin = new Pen(Color.DarkOrange, thinLine);
        using var penPinSelected = new Pen(Color.Green, normalLine);

        for (int i = 0; i < _pins.Count; i++)
        {
            var pin = _pins[i];
            var sp = ImageToScreen(pin.Location);

            bool isSelected = (i == _selectedPinForWire);
            var brush = isSelected ? brushPinSelected : brushPin;
            var pen = isSelected ? penPinSelected : penPin;

            g.FillEllipse(brush, sp.X - pinRadius, sp.Y - pinRadius, pinRadius * 2, pinRadius * 2);
            g.DrawEllipse(pen, sp.X - pinRadius, sp.Y - pinRadius, pinRadius * 2, pinRadius * 2);
        }

        // Draw wire being drawn
        if (_drawingWire && _selectedPinForWire >= 0)
        {
            var startPin = _pins[_selectedPinForWire];
            var p1 = ImageToScreen(startPin.Location);
            var p2 = ImageToScreen(_wireEndPoint);

            using var penDrawing = new Pen(Color.Lime, normalLine) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawLine(penDrawing, p1, p2);
        }
    }

    /// <summary>Convert traced wires to MST connections (one wire per edge, not all-pairs).</summary>
    private static List<WireConnection> ConvertToMSTConnections(IReadOnlyList<WireTracer.TracedWire> tracedWires)
    {
        var result = new List<WireConnection>();

        // Collect all unique pins with their locations
        var pinData = new Dictionary<string, Point>();
        foreach (var wire in tracedWires)
        {
            if (wire.StartPin != null && wire.Path.Count >= 1)
                pinData[wire.StartPin] = wire.Path[0];
            if (wire.EndPin != null && wire.Path.Count >= 2)
                pinData[wire.EndPin] = wire.Path[wire.Path.Count - 1];
        }

        var allPins = pinData.Keys.ToList();
        if (allPins.Count < 2) return result;

        // Build pin index
        var pinIndex = new Dictionary<string, int>();
        for (int i = 0; i < allPins.Count; i++)
            pinIndex[allPins[i]] = i;

        // Union-Find to group connected pins
        var parent = Enumerable.Range(0, allPins.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        // Union all connected pins from traced wires
        foreach (var wire in tracedWires)
        {
            if (wire.StartPin != null && wire.EndPin != null)
            {
                Union(pinIndex[wire.StartPin], pinIndex[wire.EndPin]);
            }
        }

        // Group pins by net
        var nets = new Dictionary<int, List<string>>();
        for (int i = 0; i < allPins.Count; i++)
        {
            int root = Find(i);
            if (!nets.ContainsKey(root))
                nets[root] = new List<string>();
            nets[root].Add(allPins[i]);
        }

        // For each net, compute MST and create connections
        foreach (var net in nets.Values)
        {
            if (net.Count < 2) continue;

            // Get pin locations for this net
            var points = net.Select(p => pinData[p]).ToList();
            var pinNames = net.ToList();

            // Compute MST using Prim's algorithm
            var mstEdges = ComputeMST(points);

            // Convert MST edges to connections
            foreach (var (idx1, idx2) in mstEdges)
            {
                result.Add(new WireConnection(
                    pinNames[idx1], points[idx1],
                    pinNames[idx2], points[idx2]));
            }
        }

        return result;
    }

    /// <summary>Compute MST returning indices of connected points.</summary>
    private static List<(int, int)> ComputeMST(List<Point> points)
    {
        var edges = new List<(int, int)>();
        if (points.Count < 2) return edges;

        var inTree = new HashSet<int> { 0 };

        while (inTree.Count < points.Count)
        {
            int bestFrom = -1, bestTo = -1;
            double bestDist = double.MaxValue;

            foreach (int from in inTree)
            {
                for (int to = 0; to < points.Count; to++)
                {
                    if (inTree.Contains(to)) continue;

                    double dist = Math.Sqrt(
                        Math.Pow(points[from].X - points[to].X, 2) +
                        Math.Pow(points[from].Y - points[to].Y, 2));

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestFrom = from;
                        bestTo = to;
                    }
                }
            }

            if (bestTo >= 0)
            {
                inTree.Add(bestTo);
                edges.Add((bestFrom, bestTo));
            }
            else break;
        }

        return edges;
    }
}
