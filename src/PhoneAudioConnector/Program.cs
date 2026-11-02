// Phone Audio Connector: Bluetooth A2DP sink for Windows 10 2004+ / Windows 11.
// Plays phone audio on the PC's default speaker.
// Arguments:
//   --minimized  start hidden in the tray (used by "Start with Windows")
//   --verbose    turn verbose logging on for this run (all categories)

namespace PhoneAudioConnector;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Only one copy may run; two copies would fight over the same Bluetooth links.
        using var mutex = new Mutex(true, "PhoneAudioConnector_SingleInstance", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();

        Application.Run(new MainForm(Has("--minimized"), Has("--verbose")));
        return;

        bool Has(string flag)
        {
            return args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        }
    }
}