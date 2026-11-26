using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VISM;

sealed class WindowsHost : Form, IDesktopHost
{
    readonly WebView2 ui = new();
    readonly string root = AppContext.BaseDirectory;
    readonly SaveManager controller;

    public WindowsHost(bool test)
    {
        controller = new SaveManager(this, test, null);
        Text = test ? "VI Save Manager (test profile)" : "VI Save Manager";
        try { Icon = new Icon(Path.Combine(root, "vism.ico")); } catch { }
        BackColor = Color.FromArgb(0x12, 0x0B, 0x1F);
        ClientSize = new Size(980, 820);
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterScreen;
        ui.Dock = DockStyle.Fill;
        ui.DefaultBackgroundColor = BackColor;
        Controls.Add(ui);
        Load += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        Directory.CreateDirectory(controller.ProfileDirectory);
        var env = await CoreWebView2Environment.CreateAsync(null, controller.ProfileDirectory);
        await ui.EnsureCoreWebView2Async(env);
        var core = ui.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("app.vism", Path.Combine(root, "ui"), CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += (_, e) => controller.OnMessage(e.WebMessageAsJson);
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://app.vism/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        core.Navigate("https://app.vism/index.html");
    }

    public void Post(string json)
    {
        if (IsDisposed || !IsHandleCreated) return;
        void PostMessage() { ui.CoreWebView2?.PostWebMessageAsJson(json); }
        if (InvokeRequired) BeginInvoke(PostMessage); else PostMessage();
    }

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

    public string? ChooseSaveFolder(string? current) => null;

    public void RevealExport(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }
}
