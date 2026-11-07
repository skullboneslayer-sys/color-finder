namespace ColorFinder;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var store = new SavedColorStore(System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "palette-colors.txt"));
        using var main = new MainForm(store);
        main.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Application.Run(main);
    }
}
