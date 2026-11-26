using System.Text.RegularExpressions;

namespace VISM;

sealed class SaveLocations
{
    public const string TitleId = "7952987A";
    static readonly Regex SteamLibraryPath = new(@"^\s*""path""\s*""([^""]+)""", RegexOptions.Multiline);
    readonly bool test;
    readonly string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    readonly string? windowsData;
    readonly string selectionFile;
    string? selected;
    string[] detected = [];
    DateTime lastScan;

    public string ProfileDirectory { get; }

    public SaveLocations(bool test, string? overridePath)
    {
        this.test = test;
        string localData = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : GetLinuxDataHome();
        ProfileDirectory = Path.Combine(localData, test ? "VISaveManager-test" : "VISaveManager");
        selectionFile = Path.Combine(ProfileDirectory, "game-save-directory.txt");

        if (test) windowsData = Path.Combine(ProfileDirectory, "victory-data");
        else if (OperatingSystem.IsWindows()) windowsData = Path.Combine(localData, "victory", "data");
        else if (overridePath != null) selected = ValidateSelection(overridePath);
        else if (File.Exists(selectionFile))
        {
            string saved = File.ReadAllText(selectionFile).Trim();
            if (Path.IsPathFullyQualified(saved)) selected = saved;
        }
    }

    string GetLinuxDataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return xdg != null && Path.IsPathFullyQualified(xdg) ? xdg : Path.Combine(home, ".local", "share");
    }

    public string? GetSaveDirectory()
    {
        if (windowsData != null)
        {
            string preferred = Path.Combine(windowsData, TitleId, "saves");
            if (Directory.Exists(preferred)) return preferred;
            return Directories(windowsData).Select(d => Path.Combine(d, "saves")).FirstOrDefault(Directory.Exists);
        }
        if (selected != null) return selected;
        if (DateTime.UtcNow - lastScan >= TimeSpan.FromSeconds(5))
        {
            detected = Detect().Distinct(StringComparer.Ordinal).ToArray();
            lastScan = DateTime.UtcNow;
        }
        return detected.Length == 1 ? detected[0] : null;
    }

    public string RequireWritableLocation()
    {
        var directory = GetSaveDirectory();
        if (windowsData != null) return directory ?? Path.Combine(windowsData, TitleId, "saves");
        if (directory == null) throw new InvalidOperationException("Choose the VIctory save folder before loading a save.");
        return ValidateSelection(directory);
    }

    public void SetLocation(string path)
    {
        string folder = ValidateSelection(path);
        Directory.CreateDirectory(ProfileDirectory);
        File.WriteAllText(selectionFile, folder);
        selected = folder;
    }

    string ValidateSelection(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Path.GetFileName(fullPath).Equals("saves", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.GetDirectoryName(fullPath)))
        {
            throw new ArgumentException("Select VIctory's saves folder, or pass its path under an existing game directory with --save-dir.");
        }
        var profile = Path.TrimEndingDirectorySeparator(ProfileDirectory);
        if (fullPath.Equals(profile, StringComparison.Ordinal) || fullPath.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Choose the game's saves directory outside the kept saves library.");
        if (Directory.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Choose the actual saves folder instead of a symbolic link.");
        return fullPath;
    }

    IEnumerable<string> Detect()
    {
        // Explicit runner environment takes priority over the default prefixes.
        var prefixes = new List<string>();
        var wine = Environment.GetEnvironmentVariable("WINEPREFIX");
        if (!string.IsNullOrWhiteSpace(wine)) prefixes.Add(wine);
        var proton = Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH");
        if (!string.IsNullOrWhiteSpace(proton)) prefixes.Add(Path.Combine(proton, "pfx"));
        var runnerSaves = prefixes.SelectMany(PrefixSaves).Distinct(StringComparer.Ordinal).ToArray();
        if (runnerSaves.Length > 0) return runnerSaves;

        var linuxData = Child(GetLinuxDataHome(), "victory");
        var nativeSaves = TitleSaves(linuxData);
        var libraries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var steam in new[] { Path.Combine(home, ".local/share/Steam"), Path.Combine(home, ".steam/steam"),
                     Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam") })
        {
            if (!Directory.Exists(steam)) continue;
            libraries.Add(CanonicalDirectory(steam));
            try
            {
                string vdf = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
                foreach (Match match in SteamLibraryPath.Matches(vdf))
                    if (Directory.Exists(match.Groups[1].Value)) libraries.Add(CanonicalDirectory(match.Groups[1].Value));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        prefixes.Clear();
        prefixes.Add(Path.Combine(home, ".wine"));
        foreach (var library in libraries)
            prefixes.AddRange(Directories(Path.Combine(library, "steamapps", "compatdata")).Select(d => Path.Combine(d, "pfx")));
        return nativeSaves.Concat(prefixes.SelectMany(PrefixSaves));
    }

    static IEnumerable<string> PrefixSaves(string prefix)
    {
        foreach (var user in Directories(Path.Combine(prefix, "drive_c", "users")))
        {
            string? appData = Child(user, "AppData");
            string? local = appData == null ? null : Child(appData, "Local");
            foreach (var saves in TitleSaves(local == null ? null : Child(local, "victory"))) yield return saves;
        }
    }

    static IEnumerable<string> TitleSaves(string? victory)
    {
        string? data = victory == null ? null : Child(victory, "data");
        string? title = data == null ? null : Child(data, TitleId);
        if (title != null) yield return Path.Combine(CanonicalDirectory(title), Path.GetFileName(Child(title, "saves") ?? "saves"));
    }

    static string CanonicalDirectory(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        return directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
    }

    static string? Child(string parent, string name)
    {
        string exact = Path.Combine(parent, name);
        return Directory.Exists(exact) ? exact
            : Directories(parent).FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    static string[] Directories(string parent)
    {
        try { return Directory.GetDirectories(parent); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
