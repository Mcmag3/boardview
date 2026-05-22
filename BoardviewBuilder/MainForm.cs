using System.Runtime.InteropServices;
using System.Text;

namespace BoardviewBuilder;

/// <summary>
/// Main window for Boardview Builder.
/// Tab 1 "Schematic → Netlist" : load a raster schematic (JPG/PNG/BMP/PDF),
///                               adjust it for OCR, edit the netlist
///                               textually, save/load netlist files.
/// Tab 2 "Future" : placeholder for future functionality.
/// </summary>
public sealed class MainForm : Form
{
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
    private List<WireTracer.DetectedPin> _remainingDetectedPins = new();

    // Pan/zoom state
    private bool _panning;
    private Point _panStartCursor;
    private Point _panStartScroll;

    // Dark theme colors
    private static readonly Color DarkBackground = Color.FromArgb(30, 30, 30);
    private static readonly Color DarkPanel = Color.FromArgb(45, 45, 45);
    private static readonly Color DarkControl = Color.FromArgb(60, 60, 60);
    private static readonly Color DarkBorder = Color.FromArgb(70, 70, 70);
    private static readonly Color DarkText = Color.FromArgb(220, 220, 220);
    private static readonly Color DarkTextDim = Color.FromArgb(160, 160, 160);

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

    /// <summary>Apply dark scrollbars to a control using Windows dark theme.</summary>
    private static void EnableDarkScrollbars(Control control)
    {
        try
        {
            SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        catch { /* Ignore on older Windows versions */ }
    }

    public MainForm()
    {
        Text = "Boardview Builder";
        Width = 1200;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        // Use owner-drawn TabControl for dark theme
        var tabs = new DarkTabControl { Dock = DockStyle.Fill };
        Controls.Add(tabs);

        var schemTab = new TabPage("Schematic → Netlist") { Padding = new Padding(8) };
        var futureTab = new TabPage("Future") { Padding = new Padding(8) };
        tabs.TabPages.Add(schemTab);
        tabs.TabPages.Add(futureTab);

        // ===== Schematic tab =====
        (_imagePathBox, _imageBox, _imageScroll, _zoomBar, _zoomLabel,
         _netlistText, _schemStatus,
         _applyEditsBtn, _saveNetlistBtn, _loadNetlistBtn, _extractBtn,
         _adjGrayscale, _adjInvert, _adjBrightness, _adjContrast,
         _adjThresholdEnabled, _adjThreshold,
         _adjRotateBtn, _adjResetBtn,
         _adjBrightnessVal, _adjContrastVal, _adjThresholdVal,
         _hideOcrBoxes, _showPins, _showWires) = BuildSchematicTab(schemTab);

        // ===== Future tab (placeholder) =====
        BuildFutureTab(futureTab);

        // Apply dark theme to entire form
        ApplyDarkTheme(this);

        // Enable dark title bar (must be done after handle is created)
        HandleCreated += (_, _) => EnableDarkTitleBar(this);
    }

    /// <summary>Recursively apply dark theme to all controls.</summary>
    private static void ApplyDarkTheme(Control control)
    {
        // Set form-level colors
        if (control is Form form)
        {
            form.BackColor = DarkBackground;
            form.ForeColor = DarkText;
        }

        // Apply to each control based on type
        foreach (Control c in control.Controls)
        {
            ApplyDarkThemeToControl(c);
            // Recurse into child controls
            if (c.HasChildren)
                ApplyDarkTheme(c);
        }
    }

    private static void ApplyDarkThemeToControl(Control c)
    {
        switch (c)
        {
            case TabControl tab:
                tab.BackColor = DarkBackground;
                tab.ForeColor = DarkText;
                // DrawMode for custom tab painting would require more work
                break;

            case TabPage page:
                page.BackColor = DarkPanel;
                page.ForeColor = DarkText;
                break;

            case TextBox txt:
                txt.BackColor = DarkControl;
                txt.ForeColor = DarkText;
                txt.BorderStyle = BorderStyle.FixedSingle;
                // Apply dark scrollbars when handle is created
                if (txt.Multiline && txt.ScrollBars != ScrollBars.None)
                    txt.HandleCreated += (s, _) => EnableDarkScrollbars((Control)s!);
                break;

            case Button btn:
                btn.BackColor = DarkControl;
                btn.ForeColor = DarkText;
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderColor = DarkBorder;
                break;

            case Label lbl:
                lbl.BackColor = Color.Transparent;
                lbl.ForeColor = DarkText;
                break;

            case CheckBox chk:
                chk.BackColor = Color.Transparent;
                chk.ForeColor = DarkText;
                break;

            case NumericUpDown num:
                num.BackColor = DarkControl;
                num.ForeColor = DarkText;
                break;

            case ComboBox combo:
                combo.BackColor = DarkControl;
                combo.ForeColor = DarkText;
                combo.FlatStyle = FlatStyle.Flat;
                break;

            case TrackBar:
                // TrackBar doesn't support BackColor well on Windows
                break;

            case GroupBox grp:
                grp.BackColor = DarkPanel;
                grp.ForeColor = DarkText;
                break;

            case TableLayoutPanel tlp:
                tlp.BackColor = DarkPanel;
                break;

            case FlowLayoutPanel flp:
                flp.BackColor = Color.Transparent;
                break;

            case FocusOnHoverPanel fohp:
                // Apply dark scrollbars to scroll panels
                fohp.HandleCreated += (s, _) => EnableDarkScrollbars((Control)s!);
                break;

            case Panel panel:
                panel.BackColor = DarkPanel;
                if (panel.AutoScroll)
                    panel.HandleCreated += (s, _) => EnableDarkScrollbars((Control)s!);
                break;

            case SplitContainer split:
                split.BackColor = DarkPanel;
                split.Panel1.BackColor = DarkPanel;
                split.Panel2.BackColor = DarkPanel;
                break;

            case PictureBox:
                // Keep as-is for image display
                break;
        }
    }

    // ---------------------------------------------------------------------
    //  Future tab (placeholder)
    // ---------------------------------------------------------------------
    private static void BuildFutureTab(TabPage tab)
    {
        var label = new Label
        {
            Text = "This tab is reserved for future functionality.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(160, 160, 160),
        };
        tab.Controls.Add(label);
    }

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
            Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, AutoSize = true,
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        top.Controls.Add(new Label { Text = "Image:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        var pathBox = new TextBox { Dock = DockStyle.Fill };
        top.Controls.Add(pathBox, 1, 0);

        var browseBtn = new Button { Text = "Browse…", AutoSize = true };
        top.Controls.Add(browseBtn, 2, 0);

        top.Controls.Add(new Label { Text = "Zoom:", AutoSize = true, Margin = new Padding(12, 8, 3, 3) }, 3, 0);
        var zoom = new TrackBar
        {
            Minimum = 10, Maximum = 800, TickFrequency = 50, Value = 100,
            Width = 200, AutoSize = false, Height = 30,
        };
        top.Controls.Add(zoom, 4, 0);
        var zoomLabel = new Label { Text = "100%", AutoSize = true, Margin = new Padding(3, 8, 3, 3), MinimumSize = new Size(45, 0) };
        top.Controls.Add(zoomLabel, 5, 0);

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
        browseBtn.Click += (_, _) => BrowseAndLoadSchematic(pathBox, img, zoom, zoomLabel, netlistText, status, extractBtn, step1Btn, step2Btn, step3Btn);

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

    private void BrowseAndLoadSchematic(TextBox pathBox, PictureBox img, TrackBar zoom, Label zoomLabel,
                               TextBox netlistText, Label status,
                               Button extractBtn, Button step1Btn, Button step2Btn, Button step3Btn)
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
        {
            pathBox.Text = dlg.FileName;
            LoadSchematic(pathBox, img, zoom, zoomLabel, netlistText, status, extractBtn, step1Btn, step2Btn, step3Btn);
        }
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
        {
            // Use remaining detected pins + manual pins for display after pin editing
            var displayPins = (_remainingDetectedPins.Count > 0 || _manualPins.Count > 0)
                ? _remainingDetectedPins.Concat(_manualPins).ToList()
                : null;
            DrawOcrOverlay(newBmp, _lastOcr,
                hideOcrText: _hideOcrBoxes.Checked,
                showPins: _showPins.Checked,
                showWires: _showWires.Checked,
                overridePins: displayPins);
        }

        var oldImage = img.Image;
        img.Image = newBmp;
        oldImage?.Dispose();
        if (_displayBitmap != null && !ReferenceEquals(_displayBitmap, oldImage))
            _displayBitmap.Dispose();
        _displayBitmap = newBmp;
        ResizePictureBoxToZoom(img, zoom);
    }

    /// <summary>Calculate a scale factor for line thickness based on image size.
    /// For a 1000px diagonal image, returns 1.0. Larger images get proportionally thicker lines.</summary>
    private static float GetLineScaleFactor(int width, int height)
    {
        double diagonal = Math.Sqrt(width * width + height * height);
        // Base on 1000px diagonal = 1.0 scale, with minimum of 1.0 and maximum of 5.0
        return Math.Clamp((float)(diagonal / 1000.0), 1.0f, 5.0f);
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
        bool hideOcrText = false, bool showPins = true, bool showWires = true,
        IReadOnlyList<WireTracer.DetectedPin>? overridePins = null)
    {
        var designators = new HashSet<string>(ocr.Designators.Keys, StringComparer.Ordinal);
        var netLabels   = new HashSet<string>(ocr.NetLabels.Keys,   StringComparer.Ordinal);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        // Scale line thickness and sizes based on image dimensions
        float scale = GetLineScaleFactor(bmp.Width, bmp.Height);
        float thinLine = 1f * scale;
        float normalLine = 2f * scale;
        float thickLine = 3f * scale;
        int pinRadius = Math.Max(3, (int)(3 * scale));   // Reduced pin size
        float baseFontSize = 9f * Math.Min(scale, 2.5f);  // Don't scale fonts too much
        float smallFontSize = 7f * Math.Min(scale, 2.5f);

        using var penRef     = new Pen(Color.Red, normalLine);
        using var penNet     = new Pen(Color.LimeGreen, normalLine);
        using var penOther   = new Pen(Color.FromArgb(180, 200, 160, 0), thinLine); // dim yellow
        using var penSymbol  = new Pen(Color.DodgerBlue, thickLine);                 // bold blue
        using var penYolo    = new Pen(Color.Magenta, thinLine);                     // YOLO detections (thin like OCR)
        using var penPin     = new Pen(Color.OrangeRed, thinLine);                   // pin circles
        using var brushRef   = new SolidBrush(Color.Red);
        using var brushNet   = new SolidBrush(Color.LimeGreen);
        using var brushOther = new SolidBrush(Color.FromArgb(200, 200, 160, 0));
        using var brushSym   = new SolidBrush(Color.DodgerBlue);
        using var brushYolo  = new SolidBrush(Color.Magenta);
        using var brushPin   = new SolidBrush(Color.OrangeRed);
        using var labelFont  = new Font(FontFamily.GenericSansSerif, baseFontSize, FontStyle.Bold);
        using var symFont    = new Font(FontFamily.GenericSansSerif, baseFontSize + 1, FontStyle.Bold);
        using var pinFont    = new Font(FontFamily.GenericSansSerif, smallFontSize, FontStyle.Regular);

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

                int textY = r.Y - (int)(14 * scale);
                if (textY < 0) textY = r.Bottom + 1;
                g.DrawString(w.Text, labelFont, brush, r.X, textY);
            }
        }

        // ---- Pass 3: Pins (orange circles at YOLO box edges where wires cross) ----
        if (showPins)
        {
            var pinsToDisplay = overridePins ?? ocr.Pins;
            foreach (var pin in pinsToDisplay)
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

        // ---- Pass 4: Traced wires (ratsnest style with MST - like KiCad PCB editor) ----
        if (showWires)
        {
            using var penWire = new Pen(Color.DodgerBlue, normalLine);

            // Use Union-Find to group pins that are connected via wires
            var allPoints = new List<Point>();
            var pointIndex = new Dictionary<Point, int>();

            // Collect all unique points
            foreach (var wire in ocr.TracedWires)
            {
                if (wire.Path.Count < 2) continue;
                var p1 = wire.Path[0];
                var p2 = wire.Path[wire.Path.Count - 1];

                if (!pointIndex.ContainsKey(p1))
                {
                    pointIndex[p1] = allPoints.Count;
                    allPoints.Add(p1);
                }
                if (!pointIndex.ContainsKey(p2))
                {
                    pointIndex[p2] = allPoints.Count;
                    allPoints.Add(p2);
                }
            }

            if (allPoints.Count < 2) return;

            // Union-Find: parent array
            var parent = Enumerable.Range(0, allPoints.Count).ToArray();
            int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
            void Union(int a, int b) => parent[Find(a)] = Find(b);

            // Union all connected pins
            foreach (var wire in ocr.TracedWires)
            {
                if (wire.Path.Count < 2) continue;
                var p1 = wire.Path[0];
                var p2 = wire.Path[wire.Path.Count - 1];
                Union(pointIndex[p1], pointIndex[p2]);
            }

            // Group points by their root
            var nets = new Dictionary<int, List<Point>>();
            for (int i = 0; i < allPoints.Count; i++)
            {
                int root = Find(i);
                if (!nets.ContainsKey(root))
                    nets[root] = new List<Point>();
                nets[root].Add(allPoints[i]);
            }

            // For each net, compute and draw Minimum Spanning Tree
            foreach (var net in nets.Values)
            {
                if (net.Count < 2) continue;

                var mstEdges = ComputeMinimumSpanningTree(net);
                foreach (var (p1, p2) in mstEdges)
                {
                    g.DrawLine(penWire, p1, p2);
                }
            }
        }
    }

    /// <summary>Compute Minimum Spanning Tree using Prim's algorithm.</summary>
    private static List<(Point, Point)> ComputeMinimumSpanningTree(List<Point> points)
    {
        if (points.Count < 2) return new List<(Point, Point)>();

        var edges = new List<(Point, Point)>();
        var inTree = new HashSet<int> { 0 }; // Start with first point

        while (inTree.Count < points.Count)
        {
            int bestFrom = -1, bestTo = -1;
            double bestDist = double.MaxValue;

            // Find shortest edge from tree to non-tree point
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
                edges.Add((points[bestFrom], points[bestTo]));
            }
            else break;
        }

        return edges;
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

            // Collect OCR boxes for the editor
            var ocrBoxes = _phase1Result.AllWords.Select(w => w.Bounds).ToList();

            // Create a clean image (without overlay) for the editor
            using var cleanImage = _adjustments.Apply(_schematic.Image);

            // Open symbol box editor
            using var editor = new SymbolBoxEditor(cleanImage, _phase1Result.TracePhase1.YoloHits, ocrBoxes);
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
            UpdateOverlayFromPhase1WithManualBoxes();
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

            // Collect OCR boxes for the editor
            var ocrBoxes = _phase1Result.AllWords.Select(w => w.Bounds).ToList();

            // Create a clean image (without overlay) for the editor
            using var cleanImage = _adjustments.Apply(_schematic.Image);

            // Open pin editor
            using var editor = new PinEditor(
                cleanImage,
                _phase1Result.TracePhase1.YoloHits,
                _manualSymbolBoxes,
                _phase2Result.TracePhase2.Pins,
                ocrBoxes);
            if (editor.ShowDialog(this) == DialogResult.OK)
            {
                _manualPins = editor.ManualPins.ToList();
                _remainingDetectedPins = editor.RemainingDetectedPins.ToList();
                step3Btn.Enabled = true;
                int totalPins = _remainingDetectedPins.Count + _manualPins.Count;
                SchemOk(status,
                    $"Step 2 complete: {_remainingDetectedPins.Count} detected + " +
                    $"{_manualPins.Count} manual = {totalPins} total pins. Click '3: Trace Wires'.");
            }
            else
            {
                // Keep original detected pins if cancelled
                _remainingDetectedPins = _phase2Result.TracePhase2.Pins.ToList();
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
            // Run Phase 3 with remaining detected pins and manual pins
            var result = SchematicImageLoader.ExtractPhase3(
                _phase2Result,
                _schematic.Netlist,
                _manualPins,
                _remainingDetectedPins);
            _lastOcr = result;

            RefreshProcessedImage(_imageBox, _zoomBar);

            // Build netlist from traced wires
            netlistText.Text = BuildNetlistFromTracedWires(result.TracedWires)
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

    /// <summary>Build a netlist text from traced wires using Union-Find to group connected pins.</summary>
    private static string BuildNetlistFromTracedWires(IReadOnlyList<WireTracer.TracedWire> tracedWires)
    {
        // Collect all unique pins
        var allPins = new List<string>();
        var pinIndex = new Dictionary<string, int>();

        foreach (var wire in tracedWires)
        {
            if (wire.StartPin != null && !pinIndex.ContainsKey(wire.StartPin))
            {
                pinIndex[wire.StartPin] = allPins.Count;
                allPins.Add(wire.StartPin);
            }
            if (wire.EndPin != null && !pinIndex.ContainsKey(wire.EndPin))
            {
                pinIndex[wire.EndPin] = allPins.Count;
                allPins.Add(wire.EndPin);
            }
        }

        if (allPins.Count == 0)
            return "# No nets found\n";

        // Union-Find
        var parent = Enumerable.Range(0, allPins.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        // Union connected pins
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

        // Build output
        var sb = new StringBuilder();
        sb.AppendLine("# Traced Nets");
        sb.AppendLine();

        int netNum = 1;
        foreach (var net in nets.Values.OrderByDescending(n => n.Count))
        {
            if (net.Count < 2) continue; // Skip single-pin "nets"

            sb.AppendLine($"NET N${netNum}");
            foreach (var pin in net.OrderBy(p => p))
            {
                sb.AppendLine($"  {pin}");
            }
            sb.AppendLine();
            netNum++;
        }

        return sb.ToString();
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

    /// <summary>Create an overlay ExtractionResult from Phase 1 including manual boxes.</summary>
    private void UpdateOverlayFromPhase1WithManualBoxes()
    {
        if (_phase1Result is null) return;

        // Combine YOLO hits with manual boxes for display
        var combinedYoloHits = new List<SymbolDetector.SymbolHit>(_phase1Result.TracePhase1.YoloHits);
        foreach (var box in _manualSymbolBoxes)
        {
            combinedYoloHits.Add(new SymbolDetector.SymbolHit { Kind = "Manual", Bounds = box, Score = 1.0f });
        }

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
            YoloHits = combinedYoloHits,
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

    private static void SchemOk(Label status, string msg)   { status.ForeColor = Color.LimeGreen; status.Text = msg; }
    private static void SchemWarn(Label status, string msg) { status.ForeColor = Color.Tomato;     status.Text = "Error: " + msg; }


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

/// <summary>
/// Owner-drawn TabControl with dark theme support.
/// </summary>
internal sealed class DarkTabControl : TabControl
{
    private static readonly Color DarkBackground = Color.FromArgb(30, 30, 30);
    private static readonly Color DarkPanel = Color.FromArgb(45, 45, 45);
    private static readonly Color DarkTabSelected = Color.FromArgb(60, 60, 60);
    private static readonly Color DarkText = Color.FromArgb(220, 220, 220);
    private static readonly Color DarkTextDim = Color.FromArgb(140, 140, 140);

    public DarkTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.DoubleBuffer, true);
        DrawMode = TabDrawMode.OwnerDrawFixed;
        Padding = new Point(12, 4);
        ItemSize = new Size(120, 28);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(DarkBackground);

        // Draw tab strip background
        var tabStripRect = new Rectangle(0, 0, Width, ItemSize.Height + 4);
        using var tabStripBrush = new SolidBrush(DarkPanel);
        g.FillRectangle(tabStripBrush, tabStripRect);

        // Draw each tab
        for (int i = 0; i < TabCount; i++)
        {
            var tabRect = GetTabRect(i);
            bool isSelected = (SelectedIndex == i);

            // Tab background
            using var tabBrush = new SolidBrush(isSelected ? DarkTabSelected : DarkPanel);
            g.FillRectangle(tabBrush, tabRect);

            // Tab text
            var textColor = isSelected ? DarkText : DarkTextDim;
            using var textBrush = new SolidBrush(textColor);
            using var font = new Font(Font.FontFamily, Font.Size, isSelected ? FontStyle.Bold : FontStyle.Regular);
            var textFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(TabPages[i].Text, font, textBrush, tabRect, textFormat);

            // Bottom border for selected tab (accent)
            if (isSelected)
            {
                using var accentPen = new Pen(Color.FromArgb(80, 160, 220), 2);
                g.DrawLine(accentPen, tabRect.Left + 2, tabRect.Bottom - 1, tabRect.Right - 2, tabRect.Bottom - 1);
            }
        }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        // Handled in OnPaint
    }
}
