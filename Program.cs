using System.Windows.Forms;

namespace PaykitTotem;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}