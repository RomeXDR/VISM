using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VISM;

sealed class NativeWindowsHost : IDesktopHost
{
    const string HostLibrary = "VISM.WindowsHost.dll";
    readonly SaveManager controller;
    readonly ReceiveMessage receive;

    NativeWindowsHost(bool test)
    {
        controller = new SaveManager(this, test, null);
        receive = Receive;
    }

    public static int Start(bool test)
    {
        try
        {
            var host = new NativeWindowsHost(test);
            Directory.CreateDirectory(host.controller.ProfileDirectory);
            string root = AppContext.BaseDirectory;
            int result = vism_run(host.receive, host.controller.ProfileDirectory, Path.Combine(root, "ui"),
                test ? "VI Save Manager (test profile)" : "VI Save Manager", Path.Combine(root, "vism.ico"));
            GC.KeepAlive(host);
            return result;
        }
        catch (Exception error)
        {
            MessageBoxW(IntPtr.Zero, error.Message, "VI Save Manager", 0x10);
            return 1;
        }
    }

    void Receive(IntPtr message)
    {
        try
        {
            if (Marshal.PtrToStringUni(message) is string json) controller.OnMessage(json);
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); }
    }

    public void Post(string json) => vism_post(json);
    public string? ChooseSaveFolder(string? current) => null;

    public string? ChooseExport(string filename) => ChooseFiles(true, filename,
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop)).FirstOrDefault();

    public string[] ChooseImports() => ChooseFiles(false, "", null);

    public void RevealExport(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    static unsafe string[] ChooseFiles(bool save, string filename, string? initial)
    {
        var buffer = new char[32_768];
        filename.AsSpan().CopyTo(buffer);
        fixed (char* address = buffer)
            if (vism_choose_files(save ? 1 : 0, address, (uint)buffer.Length, initial) == 0) return [];
        var files = new List<string>();
        int offset = 0;
        while (offset < buffer.Length && buffer[offset] != '\0')
        {
            int end = Array.IndexOf(buffer, '\0', offset);
            if (end < 0) return [];
            files.Add(new string(buffer, offset, end - offset));
            offset = end + 1;
        }
        return files.Count > 1 ? files.Skip(1).Select(file => Path.Combine(files[0], file)).ToArray() : files.ToArray();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ReceiveMessage(IntPtr message);
    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    [DllImport(HostLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int vism_run(ReceiveMessage receive, string userData, string uiFolder, string title, string iconPath);
    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    [DllImport(HostLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern void vism_post(string json);
    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    [DllImport(HostLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern unsafe int vism_choose_files(int save, char* buffer, uint capacity, string? initial);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
