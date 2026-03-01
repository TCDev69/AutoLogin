using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System.Threading;

namespace AutoLogin;

public partial class App : Application
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string RunValueName = "AutoLogin";

    private static Mutex? _singleInstanceMutex;
    private Window? _mainWindow;

    public App()
    {
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        EnsurePerUserAutoStart();

        var createdNew = true;
        _singleInstanceMutex ??= new Mutex(true, "AutoLogin.SingleInstance", out createdNew);
        if (!createdNew)
        {
            Exit();
            return;
        }

        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }

    private static void EnsurePerUserAutoStart()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            var runValue = $"\"{exePath}\"";
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                               ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (runKey is null)
            {
                return;
            }

            var existingValue = runKey.GetValue(RunValueName)?.ToString();
            if (!string.Equals(existingValue, runValue, StringComparison.OrdinalIgnoreCase))
            {
                runKey.SetValue(RunValueName, runValue, RegistryValueKind.String);
            }
        }
        catch
        {
        }
    }
}
