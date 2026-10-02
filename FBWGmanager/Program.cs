using System;
using System.Windows.Forms;

namespace FbwgModManager
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--scan") >= 0)
                return CliScan.Run();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}