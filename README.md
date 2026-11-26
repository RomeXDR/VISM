# VI Save Manager

A small desktop app to keep, load and share GTA VI saves for VIctory-based builds.

![VI Save Manager](vism-512.png)

## What it does

- **Keep current save**: give where you are a name ("boat") and come back to it later.
- **Load**: switch to a kept save. The save it replaces is backed up first.
- **Share**: turn any save into a zip you can send to someone.
- **Import a save…**: add one or more zips, including ones made on the other platform.
- **Start a new game**: clear story saves, keep settings, and back up the previous save.
- **Team saves**: load any of the saves bundled with the app.

Close the game before loading a save or clearing the story. The app updates its list while open. It checks GitHub for new versions at launch and every six hours.

## Linux

The Linux desktop application uses GTK3 and WebKitGTK 4.1 (version 2.40 or newer). Install the native libraries for your distribution:

```sh
sudo dnf install gtk3 webkit2gtk4.1     # Nobara / Fedora
sudo apt install libgtk-3-0 libwebkit2gtk-4.1-0  # Debian / Ubuntu
```

Build with the .NET 8 SDK or newer, then launch from the output directory. The default build uses the installed .NET runtime. For a NativeAOT archive, install the .NET 10 SDK, Clang, and the zlib development headers (`clang zlib-devel` on Fedora or `clang zlib1g-dev` on Ubuntu), then run `./build.sh --release`. Extract the archive into its own folder and run `./VISaveManager`.

```sh
./build.sh
./dist/linux-x64/VISaveManager          # use linux-arm64 on ARM64
```

On Linux, VISM looks for the `victory/data/7952987A/saves` path under the current Wine/Proton launch environment, the default Wine prefix, or known Steam libraries and their Proton prefixes. A single match is selected automatically. For custom prefixes or several matches, click **Choose save folder…** and select the game's `saves` directory. A typical path is `drive_c/users/<user>/AppData/Local/victory/data/7952987A/saves` inside the Wine prefix. The selected folder is displayed in the app and remembered across launches.

To override detection for one launch, pass `--save-dir /absolute/path/to/saves`. The containing directory must already exist. `--test` uses a separate library and a disposable game directory without checking for updates.

Kept saves and backups are in `~/.local/share/VISaveManager/saves` (or under `$XDG_DATA_HOME` when set).

## Windows

Download the Windows zip from **Releases**, extract it, and run `VISaveManager.exe`. Windows 10 or later and the Microsoft Edge WebView2 Runtime are required. The game folder is `%LOCALAPPDATA%\victory\data\7952987A\saves`. Kept saves and backups are in `%LOCALAPPDATA%\VISaveManager\saves`.

To build a Windows NativeAOT ZIP from source, install the .NET 10 SDK and Visual Studio Build Tools with **Desktop development with C++**. Run `release.ps1` on Windows x64. The NativeAOT build uses the Majorsilence.Forms compatibility layer with its Avalonia backend. The ZIP contains only `VISaveManager.exe`. On first launch it unpacks the embedded UI, team saves, native libraries, and third-party notices into a versioned cache under `%LOCALAPPDATA%\VISaveManager\runtime`. The EXE can run from any folder.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1     # build and add shortcuts
powershell -ExecutionPolicy Bypass -File release.ps1   # make the zip to share
```

## Choosing the build target

`dotnet build -c Release` defaults to the host OS. You can cross-publish a self-contained build from the same checkout. NativeAOT releases use a native build host for each operating system. Self-contained cross publishing requires the matching SDK/runtime packs, which `dotnet publish` restores from NuGet:

```sh
dotnet publish VISaveManager.csproj -c Release -r linux-x64 --self-contained true -p:PublishAot=false -o dist/linux-x64-self-contained
dotnet publish VISaveManager.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=false -o dist/win-x64-self-contained
```

## Automated builds

The **Build VISM** workflow compiles both platforms on pull requests into `main` and on pushes to `main`. You can also run it manually from the Actions tab. Open a successful run and download the artifact for your OS:

- `VISaveManager-linux-x64` contains `VISaveManager-linux-x64.tar.gz`. Extract it to a folder and run `./VISaveManager`. GTK3 and WebKitGTK are required.
- `VISaveManager-win-x64` contains `VISaveManager-win-x64.zip`. Extract it to a folder and run `VISaveManager.exe`. The WebView2 Runtime is required.

Both archives include the application interface and team saves. Neither requires a .NET installation.
