// VI Save Manager: keep, load, share and import GTA VI saves for the VIctory-based builds.
// Every VIctory build keeps the game's saves in %LOCALAPPDATA%\victory\data\<title id>\saves.
// This app copies that folder in and out; it never changes a save's contents.
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VISM;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        MainForm.TestProfile = args.Any(a => a.Equals("--test", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm());
    }
}

sealed class MainForm : Form
{
    // `VISaveManager.exe --test`: its own spots and a sandbox in place of the real game save folder
    public static bool TestProfile;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    const string TitleId = "7952987A";
    readonly WebView2 ui = new();
    readonly string root = AppContext.BaseDirectory;

    static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string ProfileDir => Path.Combine(LocalData, TestProfile ? "VISaveManager-test" : "VISaveManager");
    static string VictoryData => TestProfile ? Path.Combine(ProfileDir, "victory-data") : Path.Combine(LocalData, "victory", "data");
    static string SnapsRoot => Path.Combine(ProfileDir, "saves");
    string TeamRoot => Path.Combine(root, "team-saves");

    public MainForm()
    {
        Text = TestProfile ? "VI Save Manager (test profile)" : "VI Save Manager";
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
        Directory.CreateDirectory(ProfileDir);
        var env = await CoreWebView2Environment.CreateAsync(null, ProfileDir);
        await ui.EnsureCoreWebView2Async(env);
        var core = ui.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("app.vism", Path.Combine(root, "ui"), CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://app.vism/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        core.Navigate("https://app.vism/index.html");
    }

    void Send(object o)
    {
        var json = JsonSerializer.Serialize(o, Json);
        void Post() { try { ui.CoreWebView2?.PostWebMessageAsJson(json); } catch { } }
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(Post); else Post();
    }

    void OnMessage(string raw)
    {
        JsonObject m;
        try { m = JsonNode.Parse(raw)!.AsObject(); } catch { return; }
        var name = (string?)m["name"];
        switch ((string?)m["cmd"])
        {
            case "list": SendSaves(); break;
            case "snap": SaveSnap(name); break;
            case "load": SaveLoad(name, (string?)m["kind"] == "team"); break;
            case "delete": SaveDelete(name); break;
            case "fresh": SaveFresh(); break;
            case "share": Share(name, (string?)m["kind"] ?? "spot"); break;
            case "import": Import(); break;
            case "openFolder": Directory.CreateDirectory(SnapsRoot); Open(SnapsRoot); break;
        }
    }

    // ---- files ----
    static string? SavesDir()
    {
        if (!Directory.Exists(VictoryData)) return null;
        var own = Path.Combine(VictoryData, TitleId, "saves");
        if (Directory.Exists(own)) return own;
        return Directory.GetDirectories(VictoryData).Select(d => Path.Combine(d, "saves")).FirstOrDefault(Directory.Exists);
    }
    static long DirSize(string d) { try { return Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); } catch { return 0; } }
    static DateTime DirNewest(string d)
    {
        try { return Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "about.txt").Select(f => File.GetLastWriteTime(f)).DefaultIfEmpty(DateTime.MinValue).Max(); }
        catch { return DateTime.MinValue; }
    }
    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, true);
        }
    }
    static void DeleteDir(string d)
    {
        if (!Directory.Exists(d)) return;
        foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)) { try { File.SetAttributes(f, FileAttributes.Normal); } catch { } }
        Directory.Delete(d, true);
    }
    static string SafeName(string? n)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = string.Concat((n ?? "").Trim().Select(c => bad.Contains(c) ? '-' : c)).Trim('.', ' ');
        return s.Length > 60 ? s[..60] : s;
    }
    static string Note(string dir) { var a = Path.Combine(dir, "about.txt"); try { return File.Exists(a) ? File.ReadAllText(a).Trim() : ""; } catch { return ""; } }
    static string Unique(string parent, string name)
    {
        var n = name; var i = 2;
        while (Directory.Exists(Path.Combine(parent, n))) n = $"{name} ({i++})";
        return n;
    }
    // a VIctory build that is running would write over a save loaded under it
    static bool GameRunning()
    {
        try { return Process.GetProcessesByName("victory").Length > 0; } catch { return false; }
    }
    static void Open(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }

    // ---- the list the page shows ----
    void SendSaves(string? note = null, bool ok = true)
    {
        var dir = SavesDir();
        object Item(string d) => new { name = Path.GetFileName(d), size = DirSize(d), gameTime = DirNewest(d).ToString("yyyy-MM-dd HH:mm"), kept = Directory.GetCreationTime(d).ToString("yyyy-MM-dd HH:mm"), note = Note(d), auto = Path.GetFileName(d).StartsWith("auto ") };
        var spots = (Directory.Exists(SnapsRoot) ? Directory.GetDirectories(SnapsRoot) : Array.Empty<string>()).OrderByDescending(Directory.GetCreationTime).Select(Item).ToList();
        var team = (Directory.Exists(TeamRoot) ? Directory.GetDirectories(TeamRoot) : Array.Empty<string>()).OrderBy(d => d).Select(Item).ToList();
        Send(new
        {
            type = "saves", note, ok, test = TestProfile,
            found = dir != null && DirSize(dir) > 0,
            size = dir != null ? DirSize(dir) : 0,
            newest = dir != null ? DirNewest(dir).ToString("yyyy-MM-dd HH:mm") : "",
            running = GameRunning(), spots, team,
        });
    }

    void SaveSnap(string? name)
    {
        try
        {
            var n = SafeName(name);
            var dir = SavesDir();
            if (n.Length == 0) { SendSaves("Give the save a name first.", false); return; }
            if (dir == null) { SendSaves("No game save found yet.", false); return; }
            var target = Path.Combine(SnapsRoot, n);
            if (Directory.Exists(target)) { SendSaves($"A save called \"{n}\" already exists. Pick another name or delete it first.", false); return; }
            CopyDir(dir, target);
            Directory.SetCreationTime(target, DateTime.Now);
            SendSaves($"Saved the current game as \"{n}\".");
        }
        catch (Exception e) { SendSaves("Couldn't save: " + e.Message, false); }
    }

    // keeps the current save as "auto <time>" (the 5 newest of those stay) before anything replaces it
    string? Backup()
    {
        var dir = SavesDir();
        if (dir == null || DirSize(dir) == 0) return null;
        var backup = Path.Combine(SnapsRoot, "auto " + DateTime.Now.ToString("yyyy-MM-dd HHmmss"));
        CopyDir(dir, backup);
        Directory.SetCreationTime(backup, DateTime.Now);
        foreach (var old in Directory.GetDirectories(SnapsRoot).Where(d => Path.GetFileName(d).StartsWith("auto "))
                     .OrderByDescending(Directory.GetCreationTime).Skip(5)) DeleteDir(old);
        return Path.GetFileName(backup);
    }

    void SaveLoad(string? name, bool fromTeam)
    {
        try
        {
            if (GameRunning()) { SendSaves("Close the game first: it would overwrite the save when it autosaves.", false); return; }
            var src = Path.Combine(fromTeam ? TeamRoot : SnapsRoot, SafeName(name));
            if (!Directory.Exists(src)) { SendSaves("That save is gone.", false); return; }
            var dir = SavesDir() ?? Path.Combine(VictoryData, TitleId, "saves");
            var backup = Backup();
            DeleteDir(dir);
            CopyDir(src, dir);
            var about = Path.Combine(dir, "about.txt");
            if (File.Exists(about)) File.Delete(about);
            SendSaves($"Loaded \"{SafeName(name)}\".{(backup != null ? $" Your previous save was kept as \"{backup}\"." : "")} Start the game and pick Continue.");
        }
        catch (Exception e) { SendSaves("Couldn't load: " + e.Message, false); }
    }

    void SaveDelete(string? name)
    {
        try { DeleteDir(Path.Combine(SnapsRoot, SafeName(name))); SendSaves($"Deleted \"{SafeName(name)}\"."); }
        catch (Exception e) { SendSaves("Couldn't delete: " + e.Message, false); }
    }

    // a new story: the current save is kept, the story slots are cleared, the settings slot stays
    void SaveFresh()
    {
        try
        {
            if (GameRunning()) { SendSaves("Close the game first.", false); return; }
            var dir = SavesDir();
            if (dir == null) { SendSaves("No save yet: the game starts a new story anyway."); return; }
            var backup = Backup();
            var cleared = 0;
            foreach (var blob in Directory.GetDirectories(dir, "*~*", SearchOption.AllDirectories).ToList())
            {
                if (Path.GetFileName(blob).StartsWith("SettingsProfileSettings", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(blob)) continue;
                DeleteDir(blob); cleared++;
            }
            SendSaves($"Story cleared ({cleared} save slots).{(backup != null ? $" Your previous save was kept as \"{backup}\"." : "")} The next run starts a new game.");
        }
        catch (Exception e) { SendSaves("Couldn't clear the save: " + e.Message, false); }
    }

    // ---- share: one save as a zip (save-info.json + saves\...) ----
    void Share(string? name, string kind)
    {
        try
        {
            string src, label;
            if (kind == "current") { src = SavesDir() ?? ""; label = "Current save " + DateTime.Now.ToString("yyyy-MM-dd HHmm"); }
            else { label = SafeName(name); src = Path.Combine(kind == "team" ? TeamRoot : SnapsRoot, label); }
            if (src.Length == 0 || !Directory.Exists(src)) { SendSaves("Nothing to share.", false); return; }
            using var dlg = new SaveFileDialog
            {
                Title = "Share this save as a zip", Filter = "GTA VI save (*.zip)|*.zip",
                FileName = $"GTA VI save - {label}.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            using (var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create))
            {
                var info = new { name = label, note = Note(src), game = "GTA VI (Xbox Series), title " + TitleId, savedAt = DirNewest(src).ToString("yyyy-MM-dd HH:mm"), madeWith = "VI Save Manager" };
                var e = zip.CreateEntry("save-info.json");
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
                foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(f) == "about.txt") continue;
                    zip.CreateEntryFromFile(f, "saves/" + Path.GetRelativePath(src, f).Replace('\\', '/'));
                }
            }
            try { Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\""); } catch { }
            SendSaves($"Made {Path.GetFileName(dlg.FileName)}: send it to anyone, they click Import.");
        }
        catch (Exception e) { SendSaves("Couldn't make the zip: " + e.Message, false); }
    }

    // ---- import: a zip from Share, or any zip with a game save folder in it ----
    static readonly Regex UserFolder = new("^[0-9A-Fa-f]{16}$");
    void Import()
    {
        using var dlg = new OpenFileDialog { Title = "Import GTA VI saves", Filter = "Save zips (*.zip)|*.zip", Multiselect = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var done = new List<string>(); var failed = new List<string>();
        foreach (var file in dlg.FileNames)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "vism-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (new FileInfo(file).Length > 200L * 1024 * 1024) throw new Exception("over 200 MB, not a save");
                ZipFile.ExtractToDirectory(file, tmp);   // refuses entries that point outside tmp
                // the save root is the folder that holds the user folder (16 hex digits, e.g. 0009000000000001)
                var root = new[] { tmp }.Concat(Directory.GetDirectories(tmp, "*", SearchOption.AllDirectories))
                    .FirstOrDefault(d => Directory.GetDirectories(d).Any(c => UserFolder.IsMatch(Path.GetFileName(c))))
                    ?? throw new Exception("no GTA VI save inside");
                string name = Path.GetFileNameWithoutExtension(file).Replace("GTA VI save - ", ""), note = "";
                var infoPath = Path.Combine(tmp, "save-info.json");
                if (File.Exists(infoPath))
                {
                    var info = JsonNode.Parse(File.ReadAllText(infoPath));
                    name = (string?)info?["name"] ?? name; note = (string?)info?["note"] ?? "";
                }
                name = Unique(SnapsRoot, SafeName(name).Length > 0 ? SafeName(name) : "Imported save");
                var target = Path.Combine(SnapsRoot, name);
                foreach (var user in Directory.GetDirectories(root).Where(c => UserFolder.IsMatch(Path.GetFileName(c))))
                    CopyDir(user, Path.Combine(target, Path.GetFileName(user)));
                File.WriteAllText(Path.Combine(target, "about.txt"), note.Length > 0 ? note : "Imported from " + Path.GetFileName(file));
                Directory.SetCreationTime(target, DateTime.Now);
                done.Add(name);
            }
            catch (Exception e) { failed.Add($"{Path.GetFileName(file)} ({e.Message})"); }
            finally { try { DeleteDir(tmp); } catch { } }
        }
        var msg = (done.Count > 0 ? $"Imported: {string.Join(", ", done)}. Press Load on it to play from there." : "")
                + (failed.Count > 0 ? $" Couldn't import: {string.Join("; ", failed)}." : "");
        SendSaves(msg.Trim(), failed.Count == 0);
    }
}
