using System.Text;

namespace BoardviewBuilder;

/// <summary>
/// Main window. Hosts two independent pipelines as separate tabs:
///
///   Tab 1 "CSV → BRD"           : existing flow, unchanged behaviour.
///   Tab 2 "Schematic → Netlist" : load a raster schematic (JPG/PNG/BMP),
///                                 adjust it for OCR, edit the netlist
///                                 textually, save/load netlist files.
///
/// The two tabs share no state — each owns its own model and preview.
/// </summary>
public sealed class MainForm : Form
{
    // ---- CSV tab state ----
    private readonly TextBox _folderBox;
    private readonly NumericUpDown _widthBox;
    private readonly NumericUpDown _heightBox;
    private readonly TextBox _brdPreview;
    private readonly Label _csvStatus;
    private readonly Button _saveBtn;
    private BoardModel? _board;
    private string _brdText = "";

    // ---- Schematic tab state ----
    private readonly TextBox _imagePathBox;
    private readonly PictureBox _imageBox;
    private readonly FocusOnHoverPanel _imageScroll;
    private readonly TrackBar _zoomBar;
    private readonly Label _zoomLabel;
    private readonly TextBox _netlistText;
    private readonly Label _schemStatus;
    private readonly Button _applyEditsBtn;
    private readonly Button _saveNetlistBtn;
    private readonly Button _loadNetlistBtn;
    private readonly Button _extractBtn;

    // Adjustment controls
    private readonly CheckBox _adjGrayscale;
    private readonly CheckBox _adjInvert;
    private readonly TrackBar _adjBrightness;
    private readonly TrackBar _adjContrast;
    private readonly CheckBox _adjThresholdEnabled;
    private readonly TrackBar _adjThreshold;
    private readonly Button _adjRotateBtn;
    private readonly Button _adjResetBtn;
    private readonly Label _adjBrightnessVal;
    private readonly Label _adjContrastVal;
    private readonly Label _adjThresholdVal;

    private SchematicImageLoader.LoadResult? _schematic;
    private Bitmap? _displayBitmap;          // processed image currently shown
    private readonly ImageAdjustments _adjustments = new();

    // Pan/zoom state
    private bool _panning;
    private Point _panStartCursor;
    private Point _panStartScroll;

    public MainForm()
    {
        Text = "Boardview Builder";
        Width = 1200;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(tabs);

        var csvTab = new TabPage("CSV → BRD") { Padding = new Padding(8) };
        var schemTab = new TabPage("Schematic → Netlist") { Padding = new Padding(8) };
        tabs.TabPages.Add(csvTab);
        tabs.TabPages.Add(schemTab);

        // ===== CSV tab =====
        (_folderBox, _widthBox, _heightBox, _brdPreview, _csvStatus, _saveBtn) = BuildCsvTab(csvTab);

        // ===== Schematic tab =====
        (_imagePathBox, _imageBox, _imageScroll, _zoomBar, _zoomLabel,
         _netlistText, _schemStatus,
         _applyEditsBtn, _saveNetlistBtn, _loadNetlistBtn, _extractBtn,
         _adjGrayscale, _adjInvert, _adjBrightness, _adjContrast,
         _adjThresholdEnabled, _adjThreshold,
         _adjRotateBtn, _adjResetBtn,
         _adjBrightnessVal, _adjContrastVal, _adjThresholdVal) = BuildSchematicTab(schemTab);
    }

    // ---------------------------------------------------------------------
    //  CSV → BRD tab  (unchanged from before)
    // ---------------------------------------------------------------------
    private (TextBox folder, NumericUpDown w, NumericUpDown h, TextBox preview,
             Label status, Button save) BuildCsvTab(TabPage tab)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tab.Controls.Add(root);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 2,
            AutoSize = true,
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        top.Controls.Add(new Label { Text = "CSV folder:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        var folderBox = new TextBox { Dock = DockStyle.Fill };
        top.Controls.Add(folderBox, 1, 0);
        var browseBtn = new Button { Text = "Browse…", AutoSize = true };
        browseBtn.Click += (_, _) => BrowseCsvFolder(folderBox);
        top.Controls.Add(browseBtn, 2, 0);

        var sizePanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        sizePanel.Controls.Add(new Label { Text = "Board W×H (used only if no outline.csv):", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        var wBox = new NumericUpDown { Minimum = 1, Maximum = 10_000_000, Value = 2000, Width = 90 };
        var hBox = new NumericUpDown { Minimum = 1, Maximum = 10_000_000, Value = 1500, Width = 90 };
        sizePanel.Controls.Add(wBox);
        sizePanel.Controls.Add(new Label { Text = "×", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        sizePanel.Controls.Add(hBox);
        top.Controls.Add(sizePanel, 1, 1);

        var loadBtn = new Button { Text = "Load && Preview", AutoSize = true };
        top.Controls.Add(loadBtn, 3, 1);

        var saveBtn = new Button { Text = "Save .brd…", AutoSize = true, Enabled = false };
        top.Controls.Add(saveBtn, 4, 1);

        root.Controls.Add(top, 0, 0);

        var preview = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            ReadOnly = true,
            Font = new Font("Consolas", 9.5f),
        };
        root.Controls.Add(preview, 0, 1);

        var status = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Pick a folder containing nets.csv, parts.csv, pins.csv (see samples/).",
        };
        root.Controls.Add(status, 0, 2);

        loadBtn.Click += (_, _) => LoadCsvAndPreview(folderBox, wBox, hBox, preview, status, saveBtn);
        saveBtn.Click += (_, _) => SaveBrd(folderBox, status);

        return (folderBox, wBox, hBox, preview, status, saveBtn);
    }

    private static void BrowseCsvFolder(TextBox folderBox)
    {
        using var dlg = new FolderBrowserDialog { Description = "Select folder with nets.csv / parts.csv / pins.csv" };
        if (Directory.Exists(folderBox.Text)) dlg.SelectedPath = folderBox.Text;
        if (dlg.ShowDialog() == DialogResult.OK)
            folderBox.Text = dlg.SelectedPath;
    }

    private void LoadCsvAndPreview(TextBox folderBox, NumericUpDown wBox, NumericUpDown hBox,
                                   TextBox preview, Label status, Button saveBtn)
    {
        try
        {
            string folder = folderBox.Text.Trim();
            if (!Directory.Exists(folder))
            {
                CsvWarn(status, "Folder does not exist.");
                return;
            }

            _board = CsvLoader.Load(folder, (int)wBox.Value, (int)hBox.Value);
            _brdText = BrdGenerator.Generate(_board);
            preview.Text = _brdText.Replace("\n", Environment.NewLine);

            int pinCount = _board.Parts.Sum(p => p.Pins.Count);
            saveBtn.Enabled = true;
            CsvOk(status,
                $"Loaded: {_board.Parts.Count} parts, {pinCount} pins, " +
                $"{_board.Nets.Count} nets, {_board.Nails.Count} nails, " +
                $"{_board.Outline.Count} outline points. Review, then Save .brd.");
        }
        catch (Exception ex)
        {
            _board = null;
            saveBtn.Enabled = false;
            preview.Text = "";
            CsvWarn(status, ex.Message);
        }
    }

    private void SaveBrd(TextBox folderBox, Label status)
    {
        if (_board is null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "Boardview (*.brd)|*.brd|All files (*.*)|*.*",
            FileName = "board.brd",
            InitialDirectory = Directory.Exists(folderBox.Text) ? folderBox.Text : null,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, _brdText, new UTF8Encoding(false));
        CsvOk(status, $"Saved {dlg.FileName} — open it in FlexBV.");
    }

    private static void CsvOk(Label status, string msg)   { status.ForeColor = Color.DarkGreen; status.Text = msg; }
    private static void CsvWarn(Label status, string msg) { status.ForeColor = Color.Firebrick;  status.Text = "Error: " + msg; }

    // ---------------------------------------------------------------------
    //  Schematic → Netlist tab
    // ---------------------------------------------------------------------
    private (TextBox pathBox, PictureBox img, FocusOnHoverPanel scroll, TrackBar zoom, Label zoomLabel,
             TextBox netlistText, Label status,
             Button applyEdits, Button saveNetlist, Button loadNetlist, Button extract,
             CheckBox gray, CheckBox invert, TrackBar brightness, TrackBar contrast,
             CheckBox thresholdEnabled, TrackBar threshold,
             Button rotate, Button reset,
             Label brightVal, Label contrastVal, Label threshVal) BuildSchematicTab(TabPage tab)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // file row
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // adjustments row
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // viewer + netlist
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // status + buttons
        tab.Controls.Add(root);

        // ---- Row 0: file picker + zoom -------------------------------------
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, AutoSize = true,
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        top.Controls.Add(new Label { Text = "Image:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        var pathBox = new TextBox { Dock = DockStyle.Fill };
        top.Controls.Add(pathBox, 1, 0);

        var browseBtn = new Button { Text = "Browse…", AutoSize = true };
        top.Controls.Add(browseBtn, 2, 0);
        var loadBtn = new Button { Text = "Load image", AutoSize = true };
        top.Controls.Add(loadBtn, 3, 0);

        top.Controls.Add(new Label { Text = "Zoom:", AutoSize = true, Margin = new Padding(12, 8, 3, 3) }, 4, 0);
        var zoom = new TrackBar
        {
            Minimum = 10, Maximum = 800, TickFrequency = 50, Value = 100,
            Width = 200, AutoSize = false, Height = 30,
        };
        top.Controls.Add(zoom, 5, 0);
        var zoomLabel = new Label { Text = "100%", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(45, 0) };
        top.Controls.Add(zoomLabel, 6, 0);

        root.Controls.Add(top, 0, 0);

        // ---- Row 1: adjustments --------------------------------------------
        var adj = new GroupBox
        {
            Text = "Pre-OCR adjustments (applied to displayed image; original untouched)",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(6, 4, 6, 4),
        };
        var adjLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 12, RowCount = 1, AutoSize = true,
        };
        for (int i = 0; i < 12; i++) adjLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        adj.Controls.Add(adjLayout);

        var cbGray = new CheckBox { Text = "Grayscale", AutoSize = true, Margin = new Padding(3, 8, 8, 3) };
        var cbInv  = new CheckBox { Text = "Invert",    AutoSize = true, Margin = new Padding(3, 8, 8, 3) };
        adjLayout.Controls.Add(cbGray, 0, 0);
        adjLayout.Controls.Add(cbInv,  1, 0);

        adjLayout.Controls.Add(new Label { Text = "Brightness:", AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, 2, 0);
        var tbBright = new TrackBar { Minimum = -100, Maximum = 100, Value = 0, TickFrequency = 25, Width = 120, AutoSize = false, Height = 30 };
        adjLayout.Controls.Add(tbBright, 3, 0);
        var lblBright = new Label { Text = "0", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(28, 0) };
        adjLayout.Controls.Add(lblBright, 4, 0);

        adjLayout.Controls.Add(new Label { Text = "Contrast:", AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, 5, 0);
        var tbCont = new TrackBar { Minimum = -100, Maximum = 100, Value = 0, TickFrequency = 25, Width = 120, AutoSize = false, Height = 30 };
        adjLayout.Controls.Add(tbCont, 6, 0);
        var lblCont = new Label { Text = "0", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(28, 0) };
        adjLayout.Controls.Add(lblCont, 7, 0);

        var cbThresh = new CheckBox { Text = "Threshold", AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
        adjLayout.Controls.Add(cbThresh, 8, 0);
        var tbThresh = new TrackBar { Minimum = 0, Maximum = 255, Value = 160, TickFrequency = 32, Width = 120, AutoSize = false, Height = 30, Enabled = false };
        adjLayout.Controls.Add(tbThresh, 9, 0);
        var lblThresh = new Label { Text = "160", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(32, 0) };
        adjLayout.Controls.Add(lblThresh, 10, 0);

        var rotateBtn = new Button { Text = "Rotate 90°", AutoSize = true, Margin = new Padding(8, 4, 3, 4) };
        var resetBtn  = new Button { Text = "Reset",      AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        var btnFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        btnFlow.Controls.Add(rotateBtn);
        btnFlow.Controls.Add(resetBtn);
        adjLayout.Controls.Add(btnFlow, 11, 0);

        root.Controls.Add(adj, 0, 1);

        // ---- Row 2: split viewer / netlist ---------------------------------
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 720,
        };
        root.Controls.Add(split, 0, 2);

        var scroll = new FocusOnHoverPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.DimGray,
        };
        var img = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.DimGray,
            Location = new Point(0, 0),
            Size = new Size(0, 0),
            Cursor = Cursors.Hand,
        };
        scroll.Controls.Add(img);
        split.Panel1.Controls.Add(scroll);

        var netlistText = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            ReadOnly = false,
            AcceptsTab = true,
            AcceptsReturn = true,
            Font = new Font("Consolas", 9.5f),
            Text = "(no schematic loaded — load an image, or type a netlist here and click \"Apply edits\")",
        };
        split.Panel2.Controls.Add(netlistText);

        // ---- Row 3: status + buttons ---------------------------------------
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, AutoSize = true,
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var status = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Wheel = zoom · Left-drag = pan · Edit the netlist text and \"Apply edits\".",
        };
        bottom.Controls.Add(status, 0, 0);

        var applyEditsBtn = new Button { Text = "Apply edits",      AutoSize = true, Margin = new Padding(3) };
        var loadNetBtn    = new Button { Text = "Load netlist…",    AutoSize = true, Margin = new Padding(3) };
        var saveNetBtn    = new Button { Text = "Save netlist…",    AutoSize = true, Margin = new Padding(3) };
        var extractBtn    = new Button { Text = "Extract from image", AutoSize = true, Margin = new Padding(3), Enabled = false };
        bottom.Controls.Add(applyEditsBtn, 1, 0);
        bottom.Controls.Add(loadNetBtn,    2, 0);
        bottom.Controls.Add(saveNetBtn,    3, 0);
        bottom.Controls.Add(extractBtn,    4, 0);

        root.Controls.Add(bottom, 0, 3);

        // ---- Wire up events ------------------------------------------------
        browseBtn.Click += (_, _) => BrowseSchematic(pathBox);
        loadBtn.Click   += (_, _) => LoadSchematic(pathBox, img, zoom, zoomLabel, netlistText, status, extractBtn);

        zoom.ValueChanged += (_, _) =>
        {
            zoomLabel.Text = zoom.Value + "%";
            ResizePictureBoxToZoom(img, zoom);
        };

        // Mouse wheel zoom (anchored to cursor)
        scroll.MouseWheel += (s, e) => HandleWheelZoom(e, scroll, img, zoom, zoomLabel);
        img.MouseWheel    += (s, e) => HandleWheelZoom(e, scroll, img, zoom, zoomLabel);

        // Drag to pan (left mouse button on the image)
        img.MouseDown += (s, e) => StartPan(e, scroll, img);
        img.MouseMove += (s, e) => DoPan(e, scroll);
        img.MouseUp   += (s, e) => EndPan(img);

        // Adjustment events: re-process the image on every change
        void OnAdjChanged()
        {
            _adjustments.Grayscale       = cbGray.Checked;
            _adjustments.Invert          = cbInv.Checked;
            _adjustments.Brightness      = tbBright.Value;
            _adjustments.Contrast        = tbCont.Value;
            _adjustments.ThresholdEnabled = cbThresh.Checked;
            _adjustments.Threshold       = tbThresh.Value;
            tbThresh.Enabled = cbThresh.Checked;
            lblBright.Text = tbBright.Value.ToString();
            lblCont.Text   = tbCont.Value.ToString();
            lblThresh.Text = tbThresh.Value.ToString();
            RefreshProcessedImage(img, zoom);
        }
        cbGray.CheckedChanged   += (_, _) => OnAdjChanged();
        cbInv.CheckedChanged    += (_, _) => OnAdjChanged();
        cbThresh.CheckedChanged += (_, _) => OnAdjChanged();
        tbBright.ValueChanged   += (_, _) => OnAdjChanged();
        tbCont.ValueChanged     += (_, _) => OnAdjChanged();
        tbThresh.ValueChanged   += (_, _) => OnAdjChanged();

        rotateBtn.Click += (_, _) =>
        {
            _adjustments.RotationDegrees = (_adjustments.RotationDegrees + 90) % 360;
            RefreshProcessedImage(img, zoom);
            SchemOk(status, $"Rotation: {_adjustments.RotationDegrees}°");
        };
        resetBtn.Click += (_, _) =>
        {
            _adjustments.Reset();
            cbGray.Checked = cbInv.Checked = cbThresh.Checked = false;
            tbBright.Value = 0;
            tbCont.Value = 0;
            tbThresh.Value = 160;
            RefreshProcessedImage(img, zoom);
            SchemOk(status, "Adjustments reset.");
        };

        applyEditsBtn.Click += (_, _) => ApplyNetlistEdits(netlistText, status);
        saveNetBtn.Click    += (_, _) => SaveNetlistToFile(netlistText, status);
        loadNetBtn.Click    += (_, _) => LoadNetlistFromFile(netlistText, status);
        extractBtn.Click    += (_, _) => ReExtract(netlistText, status);

        return (pathBox, img, scroll, zoom, zoomLabel,
                netlistText, status,
                applyEditsBtn, saveNetBtn, loadNetBtn, extractBtn,
                cbGray, cbInv, tbBright, tbCont, cbThresh, tbThresh,
                rotateBtn, resetBtn,
                lblBright, lblCont, lblThresh);
    }

    // ---- Image loading -------------------------------------------------------

    private static void BrowseSchematic(TextBox pathBox)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Open schematic image",
            Filter = "Schematic images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (File.Exists(pathBox.Text))
            dlg.InitialDirectory = Path.GetDirectoryName(pathBox.Text);
        if (dlg.ShowDialog() == DialogResult.OK)
            pathBox.Text = dlg.FileName;
    }

    private void LoadSchematic(TextBox pathBox, PictureBox img, TrackBar zoom, Label zoomLabel,
                               TextBox netlistText, Label status, Button extractBtn)
    {
        try
        {
            string path = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                SchemWarn(status, "Pick an image file first.");
                return;
            }

            img.Image = null;
            _displayBitmap?.Dispose();
            _displayBitmap = null;
            _schematic?.Dispose();
            _schematic = null;

            _schematic = SchematicImageLoader.Load(path);
            _adjustments.Reset();
            RefreshProcessedImage(img, zoom);
            zoom.Value = 100;
            zoomLabel.Text = "100%";
            ResizePictureBoxToZoom(img, zoom);

            netlistText.Text = NetlistTextFormat.Format(_schematic.Netlist)
                                                .Replace("\n", Environment.NewLine);
            extractBtn.Enabled = true;

            SchemOk(status,
                $"Loaded {Path.GetFileName(path)} — " +
                $"{_schematic.Image.Width}×{_schematic.Image.Height} px. " +
                "Adjust the image, edit the netlist, then Apply edits.");
        }
        catch (Exception ex)
        {
            _schematic?.Dispose();
            _schematic = null;
            _displayBitmap?.Dispose();
            _displayBitmap = null;
            img.Image = null;
            extractBtn.Enabled = false;
            netlistText.Text = "(load failed)";
            SchemWarn(status, ex.Message);
        }
    }

    /// <summary>Recompute the processed display bitmap from the original image
    /// and the current adjustments, and assign it to the PictureBox.</summary>
    private void RefreshProcessedImage(PictureBox img, TrackBar zoom)
    {
        if (_schematic is null) return;
        var newBmp = _adjustments.Apply(_schematic.Image);
        var oldImage = img.Image;
        img.Image = newBmp;
        oldImage?.Dispose();
        if (_displayBitmap != null && !ReferenceEquals(_displayBitmap, oldImage))
            _displayBitmap.Dispose();
        _displayBitmap = newBmp;
        ResizePictureBoxToZoom(img, zoom);
    }

    private static void ResizePictureBoxToZoom(PictureBox img, TrackBar zoom)
    {
        if (img.Image is null)
        {
            img.Size = new Size(0, 0);
            return;
        }
        float scale = zoom.Value / 100f;
        int w = Math.Max(1, (int)(img.Image.Width * scale));
        int h = Math.Max(1, (int)(img.Image.Height * scale));
        img.Size = new Size(w, h);
    }

    // ---- Mouse wheel zoom (anchored to cursor) ------------------------------
    private static void HandleWheelZoom(MouseEventArgs e, Panel scroll, PictureBox img,
                                        TrackBar zoom, Label zoomLabel)
    {
        if (img.Image is null) return;

        // Point in image-space currently under the cursor.
        Point clientPt = scroll.PointToClient(Cursor.Position);
        int scrollX = -scroll.AutoScrollPosition.X;
        int scrollY = -scroll.AutoScrollPosition.Y;
        float oldScale = zoom.Value / 100f;
        float imgX = (scrollX + clientPt.X) / oldScale;
        float imgY = (scrollY + clientPt.Y) / oldScale;

        // Step zoom by ~12.5% per wheel notch (e.Delta is usually ±120).
        int step = e.Delta > 0 ? +Math.Max(1, zoom.Value / 8) : -Math.Max(1, zoom.Value / 9);
        int newZoom = Math.Clamp(zoom.Value + step, zoom.Minimum, zoom.Maximum);
        if (newZoom == zoom.Value) return;
        zoom.Value = newZoom;
        zoomLabel.Text = newZoom + "%";
        ResizePictureBoxToZoom(img, zoom);

        // Restore the same image-space point under the cursor.
        float newScale = newZoom / 100f;
        int newScrollX = (int)(imgX * newScale - clientPt.X);
        int newScrollY = (int)(imgY * newScale - clientPt.Y);
        // AutoScrollPosition takes positive offsets despite its negative getter.
        scroll.AutoScrollPosition = new Point(
            Math.Max(0, newScrollX),
            Math.Max(0, newScrollY));
    }

    // ---- Mouse drag pan -----------------------------------------------------
    private void StartPan(MouseEventArgs e, Panel scroll, PictureBox img)
    {
        if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Middle) return;
        _panning = true;
        _panStartCursor = Cursor.Position;
        _panStartScroll = new Point(-scroll.AutoScrollPosition.X, -scroll.AutoScrollPosition.Y);
        img.Cursor = Cursors.SizeAll;
    }
    private void DoPan(MouseEventArgs e, Panel scroll)
    {
        if (!_panning) return;
        Point cur = Cursor.Position;
        int dx = cur.X - _panStartCursor.X;
        int dy = cur.Y - _panStartCursor.Y;
        int nx = Math.Max(0, _panStartScroll.X - dx);
        int ny = Math.Max(0, _panStartScroll.Y - dy);
        scroll.AutoScrollPosition = new Point(nx, ny);
    }
    private void EndPan(PictureBox img)
    {
        if (!_panning) return;
        _panning = false;
        img.Cursor = Cursors.Hand;
    }

    // ---- Netlist editing ----------------------------------------------------
    private void ApplyNetlistEdits(TextBox netlistText, Label status)
    {
        var ok = NetlistTextFormat.TryParse(netlistText.Text, out var parsed, out var errors);
        if (!ok)
        {
            SchemWarn(status, $"Netlist parsed with {errors.Count} error(s): {errors[0]}" +
                              (errors.Count > 1 ? $" (+{errors.Count - 1} more)" : ""));
        }
        else
        {
            SchemOk(status,
                $"Applied: {parsed.Nets.Count} nets, {parsed.Components.Count} components, " +
                $"{parsed.Components.Sum(c => c.Pins.Count)} pins.");
        }

        // Preserve source/notes from existing schematic load if present.
        if (_schematic != null)
        {
            parsed.Source = _schematic.Netlist.Source;
            parsed.Notes.AddRange(_schematic.Netlist.Notes);
            // Swap in the new netlist while keeping the loaded image/result.
            _schematic = new SchematicImageLoader.LoadResult
            {
                Image = _schematic.Image,
                Netlist = parsed,
                SourcePath = _schematic.SourcePath,
            };
        }
        else
        {
            // No image loaded — keep the parsed netlist as a free-standing model
            // (no source). The user can still save it.
            _standaloneNetlist = parsed;
        }
    }

    private Netlist? _standaloneNetlist;

    private void SaveNetlistToFile(TextBox netlistText, Label status)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Netlist text (*.netlist.txt)|*.netlist.txt|Text (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = SuggestedNetlistFilename(),
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, netlistText.Text.Replace(Environment.NewLine, "\n"),
                              new UTF8Encoding(false));
            SchemOk(status, $"Saved netlist → {dlg.FileName}");
        }
        catch (Exception ex)
        {
            SchemWarn(status, ex.Message);
        }
    }

    private string SuggestedNetlistFilename()
    {
        if (_schematic != null && !string.IsNullOrEmpty(_schematic.SourcePath))
            return Path.GetFileNameWithoutExtension(_schematic.SourcePath) + ".netlist.txt";
        return "schematic.netlist.txt";
    }

    private void LoadNetlistFromFile(TextBox netlistText, Label status)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Netlist text (*.netlist.txt;*.txt)|*.netlist.txt;*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            netlistText.Text = File.ReadAllText(dlg.FileName).Replace("\n", Environment.NewLine);
            ApplyNetlistEdits(netlistText, status);
        }
        catch (Exception ex)
        {
            SchemWarn(status, ex.Message);
        }
    }

    private void ReExtract(TextBox netlistText, Label status)
    {
        if (_schematic is null)
        {
            SchemWarn(status, "Load a schematic image first.");
            return;
        }
        if (_displayBitmap is null)
        {
            SchemWarn(status, "Processed image not ready — try reloading.");
            return;
        }

        // Run OCR on the CURRENT processed bitmap so the user's threshold /
        // grayscale / contrast tweaks actually affect the result.
        Cursor.Current = Cursors.WaitCursor;
        try
        {
            var stats = SchematicImageLoader.ExtractFromBitmap(_displayBitmap, _schematic.Netlist);
            netlistText.Text = NetlistTextFormat.Format(_schematic.Netlist)
                                                .Replace("\n", Environment.NewLine);
            SchemOk(status,
                $"OCR: {stats.WordsRecognised} word(s) → {stats.ReferenceDesignatorsFound} " +
                $"designator(s), {stats.NetLabelsFound} net label(s) in {stats.ElapsedMs} ms.");
        }
        catch (Exception ex)
        {
            SchemWarn(status, "OCR failed: " + ex.Message);
        }
        finally
        {
            Cursor.Current = Cursors.Default;
        }
    }

    private static void SchemOk(Label status, string msg)   { status.ForeColor = Color.DarkGreen; status.Text = msg; }
    private static void SchemWarn(Label status, string msg) { status.ForeColor = Color.Firebrick;  status.Text = "Error: " + msg; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _displayBitmap?.Dispose();
            _displayBitmap = null;
            _schematic?.Dispose();
            _schematic = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A Panel that grabs focus whenever the mouse enters it. Needed for
/// MouseWheel events to be delivered without the user having to click first.
/// </summary>
internal sealed class FocusOnHoverPanel : Panel
{
    public FocusOnHoverPanel()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Focused) Focus();
    }
}
