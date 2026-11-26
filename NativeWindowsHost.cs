using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Majorsilence.Forms;
using Majorsilence.Forms.Backends;

namespace VISM;

// The compatibility form and its Skia-rendered chrome use the Avalonia backend;
// WebView2 owns the content surface inside the form.
sealed class NativeWindowsHost : Form, IDesktopHost
{
    readonly Uri page;
    readonly NativeWebView view = new();
    readonly SaveManager controller;
    bool closing;
    bool handlingMessage;

    NativeWindowsHost(bool test, string assetsDirectory)
    {
        page = new Uri(Path.Combine(assetsDirectory, "ui", "index.html"));
        controller = new SaveManager(this, test, null, assetsDirectory);
        Directory.CreateDirectory(controller.ProfileDirectory);

        Text = test ? "VI Save Manager (test profile)" : "VI Save Manager";
        ClientSize = new(980, 820);
        MinimumSize = new(720, 560);
        StartPosition = FormStartPosition.CenterScreen;

        view.EnvironmentRequested += (_, e) =>
        {
            e.EnableDevTools = test;
            if (e is WindowsWebView2EnvironmentRequestedEventArgs webView)
            {
                webView.UserDataFolder = controller.ProfileDirectory;
                if (test) webView.AdditionalBrowserArguments = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
            }
            if (test) Console.Error.WriteLine("WebView environment: " + e.GetType().Name);
        };
        view.NavigationStarted += (_, e) =>
        {
            e.Cancel = e.Request != page;
            if (test) Console.Error.WriteLine($"WebView navigation: {e.Request} (cancelled: {e.Cancel})");
        };
        view.NewWindowRequested += (_, e) => e.Handled = true;
        view.NavigationCompleted += (_, e) =>
        {
            if (test) Console.Error.WriteLine($"WebView navigation complete: {e.Request} (success: {e.IsSuccess})");
            if (e.IsSuccess && e.Request == page) PostCurrentSaves();
        };
        view.WebMessageReceived += (_, e) => Dispatcher.UIThread.Post(() => Receive(e.Body));
        Controls.Add(new Majorsilence.Forms.NativeControlHost { Dock = DockStyle.Fill, NativeControl = view });
        view.Source = page;
        if (test)
        {
            Shown += (_, _) => Console.Error.WriteLine("Compatibility form shown");
            view.AdapterCreated += (_, _) => Console.Error.WriteLine("WebView adapter created");
        }
        FormClosed += (_, _) => closing = true;
    }

    public static int Start(bool test)
    {
        try
        {
            if (test) Console.Error.WriteLine("Starting the compatibility host");
            string assetsDirectory = WindowsBundle.Unpack(new SaveLocations(test, null).ProfileDirectory);
            Platform.Backend = new AvaloniaPlatformBackend();
            Platform.Backend.Initialize();
            if (!WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2).IsSupported)
                throw new InvalidOperationException("Install the Microsoft Edge WebView2 Runtime to start VI Save Manager.");
            using var host = new NativeWindowsHost(test, assetsDirectory);
            Majorsilence.Forms.Application.Run(host);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (!test) MessageBoxW(IntPtr.Zero, error.Message, "VI Save Manager", 0x10);
            return 1;
        }
    }

    void PostCurrentSaves() => Dispatcher.UIThread.Post(() => Receive("""{"cmd":"list"}"""));

    void Receive(string? json)
    {
        if (closing || handlingMessage || view.Source != page || json is null) return;
        handlingMessage = true;
        try { controller.OnMessage(json); }
        catch (Exception error) { Console.Error.WriteLine(error.Message); }
        finally { handlingMessage = false; }
    }

    public void Post(string json) => Dispatcher.UIThread.Post(async () =>
    {
        if (closing || view.Source != page) return;
        try { await view.InvokeScript($"window.vismReceive?.({json});"); }
        catch (Exception error) { Console.Error.WriteLine(error.Message); }
    });

    public string? ChooseSaveFolder(string? current) => null;

    public string? ChooseExport(string filename)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Share this save as a zip", Filter = "GTA VI save (*.zip)|*.zip",
            FileName = filename, DefaultExt = "zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    public string[] ChooseImports()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import GTA VI saves", Filter = "Save zips (*.zip)|*.zip", Multiselect = true,
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileNames : [];
    }

    public void RevealExport(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
