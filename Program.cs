// VI Save Manager: keep, load, share and import saves for VIctory builds.
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VISM;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        bool test = args.Contains("--test", StringComparer.OrdinalIgnoreCase);
#if VISM_NATIVE_WINDOWS
        return NativeWindowsHost.Start(test);
#elif WINDOWS
        ApplicationConfiguration.Initialize();
        Application.Run(new WindowsHost(test));
        return 0;
#else
        string? saveDirectory = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--test", StringComparison.OrdinalIgnoreCase)) continue;
            if (args[i] == "--save-dir" && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                saveDirectory = args[++i];
                continue;
            }
            if (args[i] == "--help" || args[i] == "-h")
            {
                Console.WriteLine("Usage: VISaveManager [--test | --save-dir PATH]");
                return 0;
            }
            Console.Error.WriteLine($"Unrecognized or incomplete argument: {args[i]}. See --help.");
            return 2;
        }
        if (test && saveDirectory != null)
        {
            Console.Error.WriteLine("--test uses its own save folder. Run it without --save-dir.");
            return 2;
        }
        try { return new LinuxHost(test, saveDirectory).Run(); }
        catch (Exception error)
        {
            Console.Error.WriteLine("VI Save Manager: " + error.Message);
            return 1;
        }
#endif
    }
}

interface IDesktopHost
{
    void Post(string json);
    string? ChooseExport(string filename);
    string[] ChooseImports();
    string? ChooseSaveFolder(string? current);
    void RevealExport(string path);
}

sealed class SaveManager(IDesktopHost host, bool test, string? saveDirectory, string? assetsDirectory = null)
{
    const string TitleId = SaveLocations.TitleId;
    readonly SaveLocations locations = new(test, saveDirectory);
    public string ProfileDirectory => locations.ProfileDirectory;
    string SnapsRoot => Path.Combine(ProfileDirectory, "saves");
    string SkippedVersionFile => Path.Combine(ProfileDirectory, "skipped-version.txt");
    string TeamRoot => Path.Combine(assetsDirectory ?? AppContext.BaseDirectory, "team-saves");

    // ---- updates: the only thing this app asks the internet. One anonymous request to GitHub's
    //      public API for the newest release of this repo; nothing about the user is sent. ----
    const string Repo = "RomeXDR/VISM";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    async Task CheckUpdateAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("VISaveManager");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return;   // no release yet, offline, or a private repo
            var j = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            var tag = ((string?)j?["tag_name"] ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest)) return;
            if (File.Exists(SkippedVersionFile) && File.ReadAllText(SkippedVersionFile) == latest.ToString()) return;
            var mine = typeof(SaveManager).Assembly.GetName().Version ?? new Version(0, 0);
            if (latest <= new Version(mine.Major, mine.Minor, Math.Max(0, mine.Build))) return;
            var url = (string?)j?["html_url"] ?? $"https://github.com/{Repo}/releases/latest";
            var notes = ((string?)j?["body"] ?? "").Trim();
            if (notes.Length > 400) notes = notes[..400] + "…";
            Send(new UpdateMessage("update", latest.ToString(), url, notes));
        }
        catch { }
    }
    // only this repo's pages open, in the browser
    static void OpenRelease(string? url)
    {
        if (url == null || !url.StartsWith($"https://github.com/{Repo}/", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // messages to the page: types known at build time, so Native AOT needs no reflection
    void Send(UpdateMessage value) => host.Post(JsonSerializer.Serialize(value, VismJson.Default.UpdateMessage));
    void Send(SavesMessage value) => host.Post(JsonSerializer.Serialize(value, VismJson.Default.SavesMessage));

    public void OnMessage(string raw)
    {
        JsonObject m;
        try { m = JsonNode.Parse(raw)!.AsObject(); } catch { return; }
        var name = (string?)m["name"];
        switch ((string?)m["cmd"])
        {
            case "list": SendSaves(); break;
            case "checkUpdate": if (!test) _ = CheckUpdateAsync(); break;
            case "skipUpdate":
                if (Version.TryParse((string?)m["version"], out var version))
                {
                    try
                    {
                        Directory.CreateDirectory(ProfileDirectory);
                        File.WriteAllText(SkippedVersionFile, version.ToString());
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                break;
            case "chooseSaveFolder": ChooseSaveFolder(); break;
            case "openRelease": OpenRelease((string?)m["url"]); break;
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
    void ChooseSaveFolder()
    {
        if (test || !OperatingSystem.IsLinux()) return;
        try
        {
            var folder = host.ChooseSaveFolder(SavesDir());
            if (folder == null) { SendSaves(); return; }
            locations.SetLocation(folder);
            SendSaves("Game save folder selected.");
        }
        catch (Exception error) { SendSaves(error.Message, false); }
    }

    string? SavesDir() => locations.GetSaveDirectory();
    static readonly EnumerationOptions SaveTree = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
    static long DirSize(string d) { try { return Directory.EnumerateFiles(d, "*", SaveTree).Sum(f => new FileInfo(f).Length); } catch { return 0; } }
    static DateTime DirNewest(string d)
    {
        try { return Directory.EnumerateFiles(d, "*", SaveTree).Where(f => Path.GetFileName(f) != "about.txt").Select(f => File.GetLastWriteTime(f)).DefaultIfEmpty(DateTime.MinValue).Max(); }
        catch { return DateTime.MinValue; }
    }
    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The save contains a link at {entry.Name}.");
            var target = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo) CopyDir(entry.FullName, target);
            else File.Copy(entry.FullName, target, true);
        }
    }
    static void DeleteDir(string d)
    {
        if (!Directory.Exists(d)) return;
        if (OperatingSystem.IsWindows() && (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
            foreach (var f in Directory.EnumerateFiles(d, "*", SaveTree)) { try { File.SetAttributes(f, FileAttributes.Normal); } catch { } }
        Directory.Delete(d, true);
    }
    static string SafeName(string? n)
    {
        const string windowsInvalid = "<>:\\|?*/\"";
        var s = string.Concat((n ?? "").Trim().Select(c => c < ' ' || windowsInvalid.Contains(c) ? '-' : c)).Trim('.', ' ');
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
    bool GameRunning()
    {
        if (test) return false;
        if (OperatingSystem.IsLinux())
        {
            static bool IsVictory(string value)
            {
                var name = Path.GetFileName(value.Replace('\\', '/'));
                return name.Equals("victory", StringComparison.OrdinalIgnoreCase) || name.Equals("victory.exe", StringComparison.OrdinalIgnoreCase);
            }
            foreach (var pid in Directory.EnumerateDirectories("/proc"))
            {
                if (!char.IsAsciiDigit(Path.GetFileName(pid)[0])) continue;
                try
                {
                    if (IsVictory(File.ReadAllText(Path.Combine(pid, "comm")).TrimEnd())) return true;
                    var args = File.ReadAllText(Path.Combine(pid, "cmdline")).Split('\0', 3);
                    if (IsVictory(args[0]) || (args.Length > 1 && Path.GetFileName(args[0]).StartsWith("wine", StringComparison.Ordinal) && IsVictory(args[1]))) return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return false;
        }
        try { return Process.GetProcessesByName("victory").Length > 0; } catch { return false; }
    }
    static void Open(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }

    // ---- the list the page shows ----
    void SendSaves(string? note = null, bool ok = true)
    {
        var dir = SavesDir();
        SaveItem Item(string d) => new(Path.GetFileName(d), DirSize(d), DirNewest(d).ToString("yyyy-MM-dd HH:mm"), KeptTime(d).ToString("yyyy-MM-dd HH:mm"), Note(d), Path.GetFileName(d).StartsWith("auto "));
        var spots = (Directory.Exists(SnapsRoot) ? Directory.GetDirectories(SnapsRoot) : Array.Empty<string>()).OrderByDescending(KeptTime).Select(Item).ToList();
        var team = (Directory.Exists(TeamRoot) ? Directory.GetDirectories(TeamRoot) : Array.Empty<string>()).OrderBy(d => d).Select(Item).ToList();
        Send(new SavesMessage(
            "saves", note, ok, test, OperatingSystem.IsLinux() && !test,
            OperatingSystem.IsLinux() ? dir : null,
            dir != null && DirSize(dir) > 0,
            dir != null ? DirSize(dir) : 0,
            dir != null ? DirNewest(dir).ToString("yyyy-MM-dd HH:mm") : "",
            GameRunning(), spots, team));
    }

    static DateTime KeptTime(string directory) => OperatingSystem.IsWindows()
        ? Directory.GetCreationTime(directory) : Directory.GetLastWriteTime(directory);

    static void MarkKept(string directory)
    {
        if (OperatingSystem.IsWindows()) Directory.SetCreationTime(directory, DateTime.Now);
        else Directory.SetLastWriteTime(directory, DateTime.Now);
    }

    void SaveSnap(string? name)
    {
        try
        {
            var n = SafeName(name);
            var dir = SavesDir();
            if (n.Length == 0) { SendSaves("Give the save a name first.", false); return; }
            if (dir == null || !Directory.Exists(dir)) { SendSaves("No game save found yet.", false); return; }
            var target = Path.Combine(SnapsRoot, n);
            if (Directory.Exists(target)) { SendSaves($"A save called \"{n}\" already exists. Pick another name or delete it first.", false); return; }
            CopyDir(dir, target);
            MarkKept(target);
            SendSaves($"Saved the current game as \"{n}\".");
        }
        catch (Exception e) { SendSaves("Couldn't save: " + e.Message, false); }
    }

    // keeps the current save as "auto <time>" (the 5 newest of those stay) before anything replaces it
    string? Backup()
    {
        var dir = SavesDir();
        if (dir == null || !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any()) return null;
        var backup = Path.Combine(SnapsRoot, Unique(SnapsRoot, "auto " + DateTime.Now.ToString("yyyy-MM-dd HHmmss")));
        CopyDir(dir, backup);
        MarkKept(backup);
        foreach (var old in Directory.GetDirectories(SnapsRoot).Where(d => Path.GetFileName(d).StartsWith("auto "))
                     .OrderByDescending(KeptTime).Skip(5)) DeleteDir(old);
        return Path.GetFileName(backup);
    }

    void SaveLoad(string? name, bool fromTeam)
    {
        try
        {
            if (GameRunning()) { SendSaves("Close the game first: it would overwrite the save when it autosaves.", false); return; }
            var label = SafeName(name);
            if (label.Length == 0) { SendSaves("Select a save to load.", false); return; }
            var src = Path.Combine(fromTeam ? TeamRoot : SnapsRoot, label);
            if (!Directory.Exists(src)) { SendSaves("That save is gone.", false); return; }
            var dir = locations.RequireWritableLocation();
            if (Path.GetFullPath(src).Equals(Path.GetFullPath(dir), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Choose the game's saves folder outside the kept saves library.");
            string? backup = null;
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            var staged = dir + ".vism-new-" + Guid.NewGuid().ToString("N");
            var previous = dir + ".vism-old-" + Guid.NewGuid().ToString("N");
            try
            {
                CopyDir(src, staged);
                File.Delete(Path.Combine(staged, "about.txt"));
                backup = Backup();
                if (GameRunning()) throw new IOException("Close the game before replacing its save.");
                if (Directory.Exists(dir)) Directory.Move(dir, previous);
                try { Directory.Move(staged, dir); }
                catch
                {
                    if (Directory.Exists(previous)) Directory.Move(previous, dir);
                    throw;
                }
                DeleteDir(previous);
            }
            finally { DeleteDir(staged); }
            SendSaves($"Loaded \"{SafeName(name)}\".{(backup != null ? $" Your previous save was kept as \"{backup}\"." : "")} Start the game and pick Continue.");
        }
        catch (Exception e) { SendSaves("Couldn't load: " + e.Message, false); }
    }

    void SaveDelete(string? name)
    {
        try
        {
            var label = SafeName(name);
            if (label.Length == 0) { SendSaves("Select a save to delete.", false); return; }
            DeleteDir(Path.Combine(SnapsRoot, label));
            SendSaves($"Deleted \"{label}\".");
        }
        catch (Exception e) { SendSaves("Couldn't delete: " + e.Message, false); }
    }

    // a new story: the current save is kept, the story slots are cleared, the settings slot stays
    void SaveFresh()
    {
        try
        {
            if (GameRunning()) { SendSaves("Close the game first.", false); return; }
            var dir = SavesDir();
            if (dir == null || !Directory.Exists(dir)) { SendSaves("No save yet: the game starts a new story anyway."); return; }
            dir = locations.RequireWritableLocation();
            var backup = Backup();
            if (GameRunning()) { SendSaves("Close the game before clearing the story.", false); return; }
            var cleared = 0;
            foreach (var blob in Directory.GetDirectories(dir, "*~*", SaveTree))
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
            else { label = SafeName(name); src = label.Length == 0 ? "" : Path.Combine(kind == "team" ? TeamRoot : SnapsRoot, label); }
            if (src.Length == 0 || !Directory.Exists(src)) { SendSaves("Nothing to share.", false); return; }
            var filename = host.ChooseExport($"GTA VI save - {label}.zip");
            if (filename == null) { SendSaves(); return; }
            using (var zip = new ZipArchive(File.Create(filename), ZipArchiveMode.Create))
            {
                var info = new SaveInfo(label, Note(src), "GTA VI (Xbox Series), title " + TitleId, DirNewest(src).ToString("yyyy-MM-dd HH:mm"), "VI Save Manager");
                var e = zip.CreateEntry("save-info.json");
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(JsonSerializer.Serialize(info, VismJsonIndented.Default.SaveInfo));
                foreach (var f in Directory.EnumerateFiles(src, "*", SaveTree))
                {
                    if (Path.GetFileName(f) == "about.txt") continue;
                    zip.CreateEntryFromFile(f, "saves/" + Path.GetRelativePath(src, f).Replace('\\', '/'));
                }
            }
            host.RevealExport(filename);
            SendSaves($"Made {Path.GetFileName(filename)}: send it to anyone, they click Import.");
        }
        catch (Exception e) { SendSaves("Couldn't make the zip: " + e.Message, false); }
    }

    // ---- import: a zip from Share, or any zip with a game save folder in it ----
    static readonly Regex UserFolder = new("^[0-9A-Fa-f]{16}$");
    void Import()
    {
        var files = host.ChooseImports();
        if (files.Length == 0) { SendSaves(); return; }
        var done = new List<string>(); var failed = new List<string>();
        foreach (var file in files)
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
                MarkKept(target);
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

// ---- the JSON this app writes, generated at build time (Native AOT has no reflection) ----
record UpdateMessage(string Type, string Version, string Url, string Notes);
record SaveItem(string Name, long Size, string GameTime, string Kept, string Note, bool Auto);
record SavesMessage(string Type, string? Note, bool Ok, bool Test, bool CanChooseSaveFolder, string? SaveDirectory,
    bool Found, long Size, string Newest, bool Running, List<SaveItem> Spots, List<SaveItem> Team);
// save-info.json inside a shared zip
record SaveInfo(string Name, string Note, string Game, string SavedAt, string MadeWith);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateMessage))]
[JsonSerializable(typeof(SavesMessage))]
partial class VismJson : JsonSerializerContext { }

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SaveInfo))]
partial class VismJsonIndented : JsonSerializerContext { }
