using System.Text;

namespace BoardviewBuilder;

public sealed class MainForm : Form
{
    private readonly TextBox _folderBox;
    private readonly NumericUpDown _widthBox;
    private readonly NumericUpDown _heightBox;
    private readonly TextBox _preview;
    private readonly Label _status;
    private readonly Button _saveBtn;

    private BoardModel? _board;
    private string _brdText = "";

    public MainForm()
    {
        Text = "Boardview Builder — CSV → .brd (FlexBV BRD2)";
        Width = 1000;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // ---- Top: inputs ----
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
        _folderBox = new TextBox { Dock = DockStyle.Fill };
        top.Controls.Add(_folderBox, 1, 0);
        var browseBtn = new Button { Text = "Browse…", AutoSize = true };
        browseBtn.Click += (_, _) => BrowseFolder();
        top.Controls.Add(browseBtn, 2, 0);

        var sizePanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        sizePanel.Controls.Add(new Label { Text = "Board W×H (used only if no outline.csv):", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        _widthBox = new NumericUpDown { Minimum = 1, Maximum = 10_000_000, Value = 2000, Width = 90 };
        _heightBox = new NumericUpDown { Minimum = 1, Maximum = 10_000_000, Value = 1500, Width = 90 };
        sizePanel.Controls.Add(_widthBox);
        sizePanel.Controls.Add(new Label { Text = "×", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        sizePanel.Controls.Add(_heightBox);
        top.Controls.Add(sizePanel, 1, 1);

        var loadBtn = new Button { Text = "Load && Preview", AutoSize = true };
        loadBtn.Click += (_, _) => LoadAndPreview();
        top.Controls.Add(loadBtn, 3, 1);

        _saveBtn = new Button { Text = "Save .brd…", AutoSize = true, Enabled = false };
        _saveBtn.Click += (_, _) => SaveBrd();
        top.Controls.Add(_saveBtn, 4, 1);

        root.Controls.Add(top, 0, 0);

        // ---- Middle: preview ----
        _preview = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            ReadOnly = true,
            Font = new Font("Consolas", 9.5f),
        };
        root.Controls.Add(_preview, 0, 1);

        // ---- Bottom: status ----
        _status = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Pick a folder containing nets.csv, parts.csv, pins.csv (see samples/).",
        };
        root.Controls.Add(_status, 0, 2);
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select folder with nets.csv / parts.csv / pins.csv" };
        if (Directory.Exists(_folderBox.Text)) dlg.SelectedPath = _folderBox.Text;
        if (dlg.ShowDialog() == DialogResult.OK)
            _folderBox.Text = dlg.SelectedPath;
    }

    private void LoadAndPreview()
    {
        try
        {
            string folder = _folderBox.Text.Trim();
            if (!Directory.Exists(folder))
            {
                Warn("Folder does not exist.");
                return;
            }

            _board = CsvLoader.Load(folder, (int)_widthBox.Value, (int)_heightBox.Value);
            _brdText = BrdGenerator.Generate(_board);
            _preview.Text = _brdText.Replace("\n", Environment.NewLine);

            int pinCount = _board.Parts.Sum(p => p.Pins.Count);
            _saveBtn.Enabled = true;
            Ok($"Loaded: {_board.Parts.Count} parts, {pinCount} pins, " +
               $"{_board.Nets.Count} nets, {_board.Nails.Count} nails, " +
               $"{_board.Outline.Count} outline points. Review, then Save .brd.");
        }
        catch (Exception ex)
        {
            _board = null;
            _saveBtn.Enabled = false;
            _preview.Text = "";
            Warn(ex.Message);
        }
    }

    private void SaveBrd()
    {
        if (_board is null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "Boardview (*.brd)|*.brd|All files (*.*)|*.*",
            FileName = "board.brd",
            InitialDirectory = Directory.Exists(_folderBox.Text) ? _folderBox.Text : null,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, _brdText, new UTF8Encoding(false));
        Ok($"Saved {dlg.FileName} — open it in FlexBV.");
    }

    private void Ok(string msg)
    {
        _status.ForeColor = Color.DarkGreen;
        _status.Text = msg;
    }

    private void Warn(string msg)
    {
        _status.ForeColor = Color.Firebrick;
        _status.Text = "Error: " + msg;
    }
}
