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
    private readonly CheckBox _hideOcrBoxes;
    private readonly CheckBox _showPins;
    private readonly CheckBox _showWires;

    private SchematicImageLoader.LoadResult? _schematic;
    private Bitmap? _displayBitmap;          // processed image currently shown
    private readonly ImageAdjustments _adjustments = new();

    // Last OCR pass — kept so we can re-draw bounding-box overlays whenever the
    // image is reprocessed (slider changes, threshold toggle, etc.).
    // Null until the user clicks "Extract from image" at least once.
    private SchematicImageLoader.ExtractionResult? _lastOcr;

    // Multi-step extraction state
    private SchematicImageLoader.Phase1ExtractionResult? _phase1Result;
    private SchematicImageLoader.Phase2ExtractionResult? _phase2Result;
    private List<Rectangle> _manualSymbolBoxes = new();
    private List<WireTracer.DetectedPin> _manualPins = new();

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
         _adjBrightnessVal, _adjContrastVal, _adjThresholdVal,
         _hideOcrBoxes, _showPins, _showWires) = BuildSchematicTab(schemTab);
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
             Label brightVal, Label contrastVal, Label threshVal,
             CheckBox hideOcrBoxes, CheckBox showPins, CheckBox showWires) BuildSchematicTab(TabPage tab)
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
            Dock = DockStyle.Fill, ColumnCount = 15, RowCount = 1, AutoSize = true,
        };
        for (int i = 0; i < 15; i++) adjLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        adj.Controls.Add(adjLayout);

        var cbGray = new CheckBox { Text = "Grayscale", AutoSize = true, Margin = new Padding(3, 8, 8, 3) };
        var cbInv  = new CheckBox { Text = "Invert",    AutoSize = true, Margin = new Padding(3, 8, 8, 3) };
        var cbHideOcr = new CheckBox { Text = "Hide OCR boxes", AutoSize = true, Margin = new Padding(3, 8, 8, 3) };
        var cbShowPins = new CheckBox { Text = "Show Pins", AutoSize = true, Margin = new Padding(3, 8, 8, 3), Checked = true };
        var cbShowWires = new CheckBox { Text = "Show Wires", AutoSize = true, Margin = new Padding(3, 8, 8, 3), Checked = true };
        adjLayout.Controls.Add(cbGray, 0, 0);
        adjLayout.Controls.Add(cbInv,  1, 0);
        adjLayout.Controls.Add(cbHideOcr, 2, 0);
        adjLayout.Controls.Add(cbShowPins, 3, 0);
        adjLayout.Controls.Add(cbShowWires, 4, 0);

        adjLayout.Controls.Add(new Label { Text = "Brightness:", AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, 5, 0);
        var tbBright = new TrackBar { Minimum = -100, Maximum = 100, Value = 0, TickFrequency = 25, Width = 120, AutoSize = false, Height = 30 };
        adjLayout.Controls.Add(tbBright, 6, 0);
        var lblBright = new Label { Text = "0", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(28, 0) };
        adjLayout.Controls.Add(lblBright, 7, 0);

        adjLayout.Controls.Add(new Label { Text = "Contrast:", AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, 8, 0);
        var tbCont = new TrackBar { Minimum = -100, Maximum = 100, Value = 0, TickFrequency = 25, Width = 120, AutoSize = false, Height = 30 };
        adjLayout.Controls.Add(tbCont, 9, 0);
        var lblCont = new Label { Text = "0", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(28, 0) };
        adjLayout.Controls.Add(lblCont, 10, 0);

        var cbThresh = new CheckBox { Text = "Threshold", AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
        adjLayout.Controls.Add(cbThresh, 11, 0);
        var tbThresh = new TrackBar { Minimum = 0, Maximum = 255, Value = 160, TickFrequency = 32, Width = 120, AutoSize = false, Height = 30, Enabled = false };
        adjLayout.Controls.Add(tbThresh, 12, 0);
        var lblThresh = new Label { Text = "160", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(32, 0) };
        adjLayout.Controls.Add(lblThresh, 13, 0);

        var rotateBtn = new Button { Text = "Rotate 90°", AutoSize = true, Margin = new Padding(8, 4, 3, 4) };
        var resetBtn  = new Button { Text = "Reset",      AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        var btnFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        btnFlow.Controls.Add(rotateBtn);
        btnFlow.Controls.Add(resetBtn);
        adjLayout.Controls.Add(btnFlow, 14, 0);

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
        // Multi-step extraction buttons
        var step1Btn      = new Button { Text = "1: OCR+Symbols", AutoSize = true, Margin = new Padding(3), Enabled = false };
        var step2Btn      = new Button { Text = "2: Detect Pins", AutoSize = true, Margin = new Padding(3), Enabled = false };
        var step3Btn      = new Button { Text = "3: Trace Wires", AutoSize = true, Margin = new Padding(3), Enabled = false };
        var extractBtn    = new Button { Text = "Extract (all)", AutoSize = true, Margin = new Padding(3), Enabled = false };
        var labelBtn      = new Button { Text = "Label…", AutoSize = true, Margin = new Padding(3) };
        bottom.Controls.Add(applyEditsBtn, 1, 0);
        bottom.Controls.Add(loadNetBtn,    2, 0);
        bottom.Controls.Add(saveNetBtn,    3, 0);
        bottom.Controls.Add(step1Btn,      4, 0);
        bottom.Controls.Add(step2Btn,      5, 0);
        bottom.Controls.Add(step3Btn,      6, 0);
        bottom.Controls.Add(extractBtn,    7, 0);
        bottom.ColumnCount = 9;
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(labelBtn, 8, 0);
        labelBtn.Click += (_, _) => OpenLabelEditor(status);

        // Step 1: OCR + Symbol detection
        step1Btn.Click += (_, _) => RunStep1(netlistText, status, step2Btn);
        // Step 2: Pin detection
        step2Btn.Click += (_, _) => RunStep2(netlistText, status, step3Btn);
        // Step 3: Wire tracing
        step3Btn.Click += (_, _) => RunStep3(netlistText, status);


        root.Controls.Add(bottom, 0, 3);

        // ---- Wire up events ------------------------------------------------
        browseBtn.Click += (_, _) => BrowseSchematic(pathBox);
        loadBtn.Click   += (_, _) => LoadSchematic(pathBox, img, zoom, zoomLabel, netlistText, status, extractBtn, step1Btn, step2Btn, step3Btn);

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
        cbHideOcr.CheckedChanged += (_, _) => RefreshProcessedImage(img, zoom);
        cbShowPins.CheckedChanged += (_, _) => RefreshProcessedImage(img, zoom);
        cbShowWires.CheckedChanged += (_, _) => RefreshProcessedImage(img, zoom);

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
                lblBright, lblCont, lblThresh,
                cbHideOcr, cbShowPins, cbShowWires);
    }

    // ---- Image loading -------------------------------------------------------

    private static void BrowseSchematic(TextBox pathBox)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Open schematic image or PDF",
            Filter = "Schematic files (*.jpg;*.jpeg;*.png;*.bmp;*.pdf)|*.jpg;*.jpeg;*.png;*.bmp;*.pdf|" +
                     "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|" +
                     "PDF files (*.pdf)|*.pdf|" +
                     "All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (File.Exists(pathBox.Text))
            dlg.InitialDirectory = Path.GetDirectoryName(pathBox.Text);
        if (dlg.ShowDialog() == DialogResult.OK)
            pathBox.Text = dlg.FileName;
    }

    private void LoadSchematic(TextBox pathBox, PictureBox img, TrackBar zoom, Label zoomLabel,
                               TextBox netlistText, Label status,
                               Button extractBtn, Button step1Btn, Button step2Btn, Button step3Btn)
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

            // Reset multi-step state
            _phase1Result = null;
            _phase2Result = null;
            _manualSymbolBoxes.Clear();
            _manualPins.Clear();

            // Check if PDF with multiple pages
            int pageIndex = 0;
            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                int pageCount = SchematicImageLoader.GetPdfPageCount(path);
                if (pageCount > 1)
                {
                    pageIndex = ShowPdfPageSelector(path, pageCount);
                    if (pageIndex < 0) return; // User cancelled
                }
            }

            _schematic = SchematicImageLoader.Load(path, pageIndex);
            _adjustments.Reset();
            _lastOcr = null;   // new image → clear overlay
            RefreshProcessedImage(img, zoom);
            zoom.Value = 100;
            zoomLabel.Text = "100%";
            ResizePictureBoxToZoom(img, zoom);

            netlistText.Text = NetlistTextFormat.Format(_schematic.Netlist)
                                                .Replace("\n", Environment.NewLine);
            extractBtn.Enabled = true;
            step1Btn.Enabled = true;
            step2Btn.Enabled = false;
            step3Btn.Enabled = false;

            SchemOk(status,
                $"Loaded {Path.GetFileName(path)} — " +
                $"{_schematic.Image.Width}×{_schematic.Image.Height} px. " +
                "Click '1: OCR+Symbols' to start extraction.");
        }
        catch (Exception ex)
        {
            _schematic?.Dispose();
            _schematic = null;
            _displayBitmap?.Dispose();
            _displayBitmap = null;
            img.Image = null;
            extractBtn.Enabled = false;
            step1Btn.Enabled = false;
            step2Btn.Enabled = false;
            step3Btn.Enabled = false;
            netlistText.Text = "(load failed)";
            SchemWarn(status, ex.Message);
        }
    }

    /// <summary>Show a dialog to select which page of a multi-page PDF to load.</summary>
    private static int ShowPdfPageSelector(string path, int pageCount)
    {
        using var dlg = new Form
        {
            Text = "Select PDF Page",
            Width = 350,
            Height = 180,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };

        var label = new Label
        {
            Text = $"This PDF has {pageCount} pages.\nSelect which page to load:",
            Location = new Point(15, 15),
            AutoSize = true,
        };
        dlg.Controls.Add(label);

        var combo = new ComboBox
        {
            Location = new Point(15, 55),
            Width = 300,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        for (int i = 0; i < pageCount; i++)
            combo.Items.Add($"Page {i + 1}");
        combo.SelectedIndex = 0;
        dlg.Controls.Add(combo);

        var okBtn = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(150, 100),
            Width = 80,
        };
        dlg.Controls.Add(okBtn);

        var cancelBtn = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(235, 100),
            Width = 80,
        };
        dlg.Controls.Add(cancelBtn);

        dlg.AcceptButton = okBtn;
        dlg.CancelButton = cancelBtn;

        return dlg.ShowDialog() == DialogResult.OK ? combo.SelectedIndex : -1;
    }

    /// <summary>Recompute the processed display bitmap from the original image
    /// and the current adjustments, paint any OCR debug overlay on top, then
    /// assign it to the PictureBox.</summary>
    private void RefreshProcessedImage(PictureBox img, TrackBar zoom)
    {
        if (_schematic is null) return;
        var newBmp = _adjustments.Apply(_schematic.Image);

        // Paint OCR bounding boxes if we have results from the last extraction.
        // Bboxes are in the processed-image coordinate system at extraction time;
        // they stay valid as long as the user doesn't rotate after extracting.
        if (_lastOcr != null)
            DrawOcrOverlay(newBmp, _lastOcr,
                hideOcrText: _hideOcrBoxes.Checked,
                showPins: _showPins.Checked,
                showWires: _showWires.Checked);

        var oldImage = img.Image;
        img.Image = newBmp;
        oldImage?.Dispose();
        if (_displayBitmap != null && !ReferenceEquals(_displayBitmap, oldImage))
            _displayBitmap.Dispose();
        _displayBitmap = newBmp;
        ResizePictureBoxToZoom(img, zoom);
    }

    /// <summary>Draw classified OCR bounding boxes onto <paramref name="bmp"/>.
    ///   * magenta = YOLO raw detections (always drawn)
    ///   * red    = reference designators (matched the designator regex)
    ///   * green  = net labels             (matched the net-label regex)
    ///   * yellow = other recognised words (didn't match either — useful for
    ///              tuning thresholds and spotting OCR noise)
    ///   * blue   = detected component SYMBOL for a designator (the largest
    ///              non-text connected component the tracer found near the
    ///              designator text — i.e. where the wires actually attach).
    ///   * cyan   = traced wire paths (lines following ink pixels)
    ///   * orange circles = detected pins where wires connect to symbols
    ///   * yellow filled circles = wire junctions where multiple wires meet
    /// Each text box is labelled with its recognised text; each symbol box
    /// is labelled with the designator it belongs to.
    /// When <paramref name="hideOcrText"/> is true, only YOLO boxes are drawn.</summary>
    private static void DrawOcrOverlay(Bitmap bmp, SchematicImageLoader.ExtractionResult ocr,
        bool hideOcrText = false, bool showPins = true, bool showWires = true)
    {
        var designators = new HashSet<string>(ocr.Designators.Keys, StringComparer.Ordinal);
        var netLabels   = new HashSet<string>(ocr.NetLabels.Keys,   StringComparer.Ordinal);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        using var penRef     = new Pen(Color.Red,    2f);
        using var penNet     = new Pen(Color.LimeGreen, 2f);
        using var penOther   = new Pen(Color.FromArgb(180, 200, 160, 0), 1f); // dim yellow
        using var penSymbol  = new Pen(Color.DodgerBlue, 3f);                  // bold blue
        using var penYolo    = new Pen(Color.Magenta, 2f);                     // YOLO detections
        using var penPin     = new Pen(Color.OrangeRed, 2f);                   // pin circles
        using var brushRef   = new SolidBrush(Color.Red);
        using var brushNet   = new SolidBrush(Color.LimeGreen);
        using var brushOther = new SolidBrush(Color.FromArgb(200, 200, 160, 0));
        using var brushSym   = new SolidBrush(Color.DodgerBlue);
        using var brushYolo  = new SolidBrush(Color.Magenta);
        using var brushPin   = new SolidBrush(Color.OrangeRed);
        using var labelFont  = new Font(FontFamily.GenericSansSerif, 9f, FontStyle.Bold);
        using var symFont    = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold);
        using var pinFont    = new Font(FontFamily.GenericSansSerif, 7f, FontStyle.Regular);

        // ---- Pass 0: YOLO raw detections (magenta, drawn FIRST so everything else overlays) ----
        // Filter to only show highest confidence box when multiple overlap significantly
        var filteredYolo = FilterOverlappingYoloHits(ocr.YoloHits);
        foreach (var hit in filteredYolo)
        {
            var r = hit.Bounds;
            if (r.Width <= 0 || r.Height <= 0) continue;
            g.DrawRectangle(penYolo, r);
            // Label with class name and confidence
            string label = $"{hit.Kind} {hit.Score:P0}";
            g.DrawString(label, labelFont, brushYolo, r.X + 2, r.Bottom + 2);
        }

        // ---- Pass 1 & 2: OCR boxes (skip if hideOcrText is true) ----
        if (!hideOcrText)
        {
            // ---- Pass 1: symbol bboxes (blue) ----
            foreach (var kv in ocr.SymbolBoxes)
            {
                var r = kv.Value;
                if (r.Width <= 0 || r.Height <= 0) continue;
                g.DrawRectangle(penSymbol, r);
                // Label the symbol bbox with the designator name in the top-left corner.
                g.DrawString(kv.Key, symFont, brushSym, r.X + 2, r.Y + 2);
            }

            // ---- Pass 2: text bboxes (red/green/yellow) ----
            foreach (var w in ocr.AllWords)
            {
                string cleaned = w.Text.Trim().Trim(':', ',', '.', ';');
                string upper = cleaned.ToUpperInvariant();

                Pen pen;
                Brush brush;
                if (designators.Contains(upper))     { pen = penRef;   brush = brushRef; }
                else if (netLabels.Contains(upper))  { pen = penNet;   brush = brushNet; }
                else                                  { pen = penOther; brush = brushOther; }

                var r = w.Bounds;
                if (r.Width <= 0 || r.Height <= 0) continue;
                g.DrawRectangle(pen, r);

                int textY = r.Y - 14;
                if (textY < 0) textY = r.Bottom + 1;
                g.DrawString(w.Text, labelFont, brush, r.X, textY);
            }
        }

        // ---- Pass 3: Pins (orange circles at YOLO box edges where wires cross) ----
        if (showPins)
        {
            const int pinRadius = 5;
            foreach (var pin in ocr.Pins)
            {
                int cx = pin.Location.X;
                int cy = pin.Location.Y;

                // Draw filled circle for pin
                g.FillEllipse(brushPin, cx - pinRadius, cy - pinRadius, pinRadius * 2, pinRadius * 2);
                g.DrawEllipse(penPin, cx - pinRadius, cy - pinRadius, pinRadius * 2, pinRadius * 2);

                // Draw small label showing designator.side
                string pinLabel = $"{pin.Designator}.{pin.Side[0]}";
                g.DrawString(pinLabel, pinFont, brushPin, cx + pinRadius + 2, cy - 5);
            }
        }

        // ---- Pass 4: Traced wires (blue lines pin-to-pin, ratsnest style) ----
        if (showWires)
        {
            using var penWire = new Pen(Color.DodgerBlue, 2f);

            // Draw straight lines between connected pins
            foreach (var wire in ocr.TracedWires)
            {
                if (wire.Path.Count >= 2)
                {
                    g.DrawLine(penWire, wire.Path[0], wire.Path[wire.Path.Count - 1]);
                }
            }
        }
    }

    /// <summary>Filter YOLO hits to only keep highest confidence when boxes overlap significantly (IoU > 0.3).</summary>
    private static List<SymbolDetector.SymbolHit> FilterOverlappingYoloHits(IReadOnlyList<SymbolDetector.SymbolHit> hits)
    {
        if (hits.Count <= 1) return hits.ToList();

        // Sort by confidence descending
        var sorted = hits.OrderByDescending(h => h.Score).ToList();
        var kept = new List<SymbolDetector.SymbolHit>();
        var suppressed = new bool[sorted.Count];

        for (int i = 0; i < sorted.Count; i++)
        {
            if (suppressed[i]) continue;
            kept.Add(sorted[i]);

            // Suppress any lower-confidence boxes that overlap significantly
            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (suppressed[j]) continue;
                float iou = ComputeIoU(sorted[i].Bounds, sorted[j].Bounds);
                if (iou > 0.3f)
                    suppressed[j] = true;
            }
        }
        return kept;
    }

    /// <summary>Compute Intersection over Union for two rectangles.</summary>
    private static float ComputeIoU(Rectangle a, Rectangle b)
    {
        int x1 = Math.Max(a.Left, b.Left);
        int y1 = Math.Max(a.Top, b.Top);
        int x2 = Math.Min(a.Right, b.Right);
        int y2 = Math.Min(a.Bottom, b.Bottom);

        if (x2 <= x1 || y2 <= y1) return 0f;

        float intersection = (x2 - x1) * (y2 - y1);
        float areaA = a.Width * a.Height;
        float areaB = b.Width * b.Height;
        float union = areaA + areaB - intersection;

        return union > 0 ? intersection / union : 0f;
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
            var result = SchematicImageLoader.ExtractFromBitmap(_displayBitmap, _schematic.Netlist);
            _lastOcr = result;

            // Re-render the image so the new overlay is drawn on top.
            // Re-uses RefreshProcessedImage, which now consults _lastOcr.
            // We need to look up the controls we created earlier — they were
            // captured into fields by BuildSchematicTab.
            RefreshProcessedImage(_imageBox, _zoomBar);

            netlistText.Text = NetlistTextFormat.Format(_schematic.Netlist)
                                                .Replace("\n", Environment.NewLine);

            var s = result.Stats;
            SchemOk(status,
                $"OCR+Trace: {s.WordsRecognised} word(s) → " +
                $"{s.ReferenceDesignatorsFound} designator(s), " +
                $"{s.NetLabelsFound} net label(s), " +
                $"{result.SymbolBoxes.Count} symbol(s), " +
                $"{s.TracedNets} net(s), " +
                $"{s.Connections} pin↔net connection(s) in {s.ElapsedMs} ms. " +
                $"Boxes: red=designator, green=net label, blue=symbol, yellow=other.");
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

    // ---- Multi-step extraction -----------------------------------------------

    /// <summary>Step 1: Run OCR + YOLO detection, then open symbol box editor.</summary>
    private void RunStep1(TextBox netlistText, Label status, Button step2Btn)
    {
        if (_schematic is null || _displayBitmap is null)
        {
            SchemWarn(status, "Load a schematic image first.");
            return;
        }

        Cursor.Current = Cursors.WaitCursor;
        try
        {
            // Run Phase 1
            _phase1Result = SchematicImageLoader.ExtractPhase1(_displayBitmap);
            _manualSymbolBoxes.Clear();

            // Create a temporary ExtractionResult for overlay display
            UpdateOverlayFromPhase1();
            RefreshProcessedImage(_imageBox, _zoomBar);

            SchemOk(status,
                $"Step 1: {_phase1Result.AllWords.Count} words, " +
                $"{_phase1Result.Designators.Count} designators, " +
                $"{_phase1Result.TracePhase1.YoloHits.Count} YOLO detections. " +
                $"Opening symbol box editor...");

            // Open symbol box editor
            using var editor = new SymbolBoxEditor(_displayBitmap, _phase1Result.TracePhase1.YoloHits);
            if (editor.ShowDialog(this) == DialogResult.OK)
            {
                _manualSymbolBoxes = editor.ManualBoxes.ToList();
                step2Btn.Enabled = true;
                SchemOk(status,
                    $"Step 1 complete: {_phase1Result.TracePhase1.YoloHits.Count} YOLO + " +
                    $"{_manualSymbolBoxes.Count} manual symbol boxes. Click '2: Detect Pins'.");
            }
            else
            {
                SchemOk(status, "Symbol box editing cancelled.");
            }

            // Update overlay with any manual boxes
            UpdateOverlayFromPhase1();
            RefreshProcessedImage(_imageBox, _zoomBar);
        }
        catch (Exception ex)
        {
            SchemWarn(status, "Step 1 failed: " + ex.Message);
        }
        finally
        {
            Cursor.Current = Cursors.Default;
        }
    }

    /// <summary>Step 2: Run pin detection, then open pin editor.</summary>
    private void RunStep2(TextBox netlistText, Label status, Button step3Btn)
    {
        if (_phase1Result is null || _displayBitmap is null)
        {
            SchemWarn(status, "Run Step 1 first.");
            return;
        }

        Cursor.Current = Cursors.WaitCursor;
        try
        {
            // Run Phase 2
            _phase2Result = SchematicImageLoader.ExtractPhase2(_phase1Result, _manualSymbolBoxes);
            _manualPins.Clear();

            // Update overlay to show detected pins
            UpdateOverlayFromPhase2();
            RefreshProcessedImage(_imageBox, _zoomBar);

            SchemOk(status,
                $"Step 2: {_phase2Result.TracePhase2.Pins.Count} pins detected. " +
                $"Opening pin editor...");

            // Open pin editor
            using var editor = new PinEditor(
                _displayBitmap,
                _phase1Result.TracePhase1.YoloHits,
                _manualSymbolBoxes,
                _phase2Result.TracePhase2.Pins);
            if (editor.ShowDialog(this) == DialogResult.OK)
            {
                _manualPins = editor.ManualPins.ToList();
                step3Btn.Enabled = true;
                SchemOk(status,
                    $"Step 2 complete: {_phase2Result.TracePhase2.Pins.Count} detected + " +
                    $"{_manualPins.Count} manual pins. Click '3: Trace Wires'.");
            }
            else
            {
                SchemOk(status, "Pin editing cancelled.");
            }

            // Update overlay with any manual pins
            UpdateOverlayFromPhase2();
            RefreshProcessedImage(_imageBox, _zoomBar);
        }
        catch (Exception ex)
        {
            SchemWarn(status, "Step 2 failed: " + ex.Message);
        }
        finally
        {
            Cursor.Current = Cursors.Default;
        }
    }

    /// <summary>Step 3: Run wire tracing and build final netlist.</summary>
    private void RunStep3(TextBox netlistText, Label status)
    {
        if (_phase2Result is null || _schematic is null)
        {
            SchemWarn(status, "Run Steps 1 and 2 first.");
            return;
        }

        Cursor.Current = Cursors.WaitCursor;
        try
        {
            // Run Phase 3
            var result = SchematicImageLoader.ExtractPhase3(_phase2Result, _schematic.Netlist, _manualPins);
            _lastOcr = result;

            RefreshProcessedImage(_imageBox, _zoomBar);

            netlistText.Text = NetlistTextFormat.Format(_schematic.Netlist)
                                                .Replace("\n", Environment.NewLine);

            var s = result.Stats;
            SchemOk(status,
                $"Step 3 complete: {s.TracedNets} nets, {s.Connections} connections. " +
                $"Total time {s.ElapsedMs} ms.");
        }
        catch (Exception ex)
        {
            SchemWarn(status, "Step 3 failed: " + ex.Message);
        }
        finally
        {
            Cursor.Current = Cursors.Default;
        }
    }

    /// <summary>Create an overlay ExtractionResult from Phase 1 for display.</summary>
    private void UpdateOverlayFromPhase1()
    {
        if (_phase1Result is null) return;

        // Create a minimal ExtractionResult just for the overlay
        _lastOcr = new SchematicImageLoader.ExtractionResult
        {
            Stats = new SchematicImageLoader.ExtractionStats(
                _phase1Result.AllWords.Count,
                _phase1Result.Designators.Count,
                _phase1Result.NetLabels.Count,
                0, 0, _phase1Result.ElapsedMs),
            AllWords = _phase1Result.AllWords,
            Designators = _phase1Result.Designators,
            NetLabels = _phase1Result.NetLabels,
            SymbolBoxes = _phase1Result.TracePhase1.SymbolBoxes,
            YoloHits = _phase1Result.TracePhase1.YoloHits,
            Pins = Array.Empty<WireTracer.DetectedPin>(),
            Wires = Array.Empty<WireTracer.WireSegment>(),
            TracedWires = Array.Empty<WireTracer.TracedWire>(),
            Junctions = Array.Empty<WireTracer.WireJunction>(),
        };
    }

    /// <summary>Create an overlay ExtractionResult from Phase 2 for display.</summary>
    private void UpdateOverlayFromPhase2()
    {
        if (_phase2Result is null) return;
        var phase1 = _phase2Result.Phase1;

        // Combine detected and manual pins
        var allPins = _phase2Result.TracePhase2.Pins.Concat(_manualPins).ToList();

        _lastOcr = new SchematicImageLoader.ExtractionResult
        {
            Stats = new SchematicImageLoader.ExtractionStats(
                phase1.AllWords.Count,
                phase1.Designators.Count,
                phase1.NetLabels.Count,
                0, 0, phase1.ElapsedMs + _phase2Result.ElapsedMs),
            AllWords = phase1.AllWords,
            Designators = phase1.Designators,
            NetLabels = phase1.NetLabels,
            SymbolBoxes = phase1.TracePhase1.SymbolBoxes,
            YoloHits = phase1.TracePhase1.YoloHits,
            Pins = allPins,
            Wires = Array.Empty<WireTracer.WireSegment>(),
            TracedWires = Array.Empty<WireTracer.TracedWire>(),
            Junctions = Array.Empty<WireTracer.WireJunction>(),
        };
    }

    /// <summary>Open the YOLO-format labelling editor on the currently
    /// displayed processed bitmap. If the user has already run "Extract from
    /// image" we pre-fill the editor with the auto-detected symbol bboxes
    /// (currently just resistors) classified as "R" — the user only has to
    /// correct/add the missing ones (caps, diodes, etc.).</summary>
    private void OpenLabelEditor(Label status)
    {
        if (_displayBitmap is null)
        {
            SchemWarn(status, "Load a schematic image first.");
            return;
        }

        // Suggested filename stem = source image filename, no extension.
        string stem = _schematic?.SourcePath is { Length: > 0 } p
                    ? Path.GetFileNameWithoutExtension(p)
                    : "schematic";

        // Pre-fill: every symbol bbox we already detected. We assume the
        // existing geometric detector only fires for R designators today; if
        // that changes later we can dispatch on the designator's first letter.
        var prefill = new List<LabelEditor.LabelBox>();
        if (_lastOcr != null)
        {
            int classR = Array.IndexOf(LabelEditor.DefaultClasses, "R");
            int classC = Array.IndexOf(LabelEditor.DefaultClasses, "C");
            int classD = Array.IndexOf(LabelEditor.DefaultClasses, "D");
            int classQ = Array.IndexOf(LabelEditor.DefaultClasses, "Q");
            int classU = Array.IndexOf(LabelEditor.DefaultClasses, "U");
            int classL = Array.IndexOf(LabelEditor.DefaultClasses, "L");
            int classOther = Array.IndexOf(LabelEditor.DefaultClasses, "OTHER");

            foreach (var kv in _lastOcr.SymbolBoxes)
            {
                if (kv.Value.Width <= 0 || kv.Value.Height <= 0) continue;
                char first = kv.Key.Length > 0 ? char.ToUpperInvariant(kv.Key[0]) : '?';
                int cls = first switch
                {
                    'R' => classR,
                    'C' => classC,
                    'D' => classD,
                    'Q' => classQ,
                    'U' => classU,
                    'L' => classL,
                    _ => classOther,
                };
                prefill.Add(new LabelEditor.LabelBox(kv.Value, cls));
            }
        }

        // Use the displayed bitmap directly — the editor doesn't dispose it,
        // and labels are saved in this bitmap's coordinate system.
        using var editor = new LabelEditor(_displayBitmap, stem, prefill);
        editor.ShowDialog(this);
        SchemOk(status, "Label editor closed.");
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
