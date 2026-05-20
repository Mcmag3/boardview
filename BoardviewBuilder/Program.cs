namespace BoardviewBuilder;

static class Program
{
    /// <summary>
    /// Entry point. With no arguments: launches the GUI.
    /// Headless CLI:  BoardviewBuilder &lt;csvFolder&gt; &lt;output.brd&gt; [width height]
    /// (width/height only used if the folder has no outline.csv; default 2000x1500)
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2)
            return RunCli(args);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
        {
            MessageBox.Show($"Thread Exception:\n\n{e.Exception}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            MessageBox.Show($"Unhandled Exception:\n\n{e.ExceptionObject}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static int RunCli(string[] args)
    {
        try
        {
            string folder = args[0];
            string output = args[1];
            int w = args.Length >= 3 ? int.Parse(args[2]) : 2000;
            int h = args.Length >= 4 ? int.Parse(args[3]) : 1500;

            var board = CsvLoader.Load(folder, w, h);
            BrdGenerator.WriteFile(board, output);

            int pins = board.Parts.Sum(p => p.Pins.Count);
            Console.WriteLine(
                $"Wrote {output}: {board.Parts.Count} parts, {pins} pins, " +
                $"{board.Nets.Count} nets, {board.Nails.Count} nails.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }
}
