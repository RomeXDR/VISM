using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VISM;

static class WindowsBundle
{
    const string ResourceName = "VISM.WindowsBundle.zip";
    const uint SearchDllDirectory = 0x100;
    const uint SearchSystem32 = 0x800;
    static readonly string[] Libraries = ["libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll"];

    public static string Unpack(string profileDirectory)
    {
        using var payload = typeof(WindowsBundle).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The executable has no embedded Windows assets.");
        string revision = Convert.ToHexString(SHA256.HashData(payload));
        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        string cache = Path.Combine(profileDirectory, "runtime");
        string root = Path.Combine(cache, revision);

        if (!Directory.Exists(root))
            Install(archive, cache, root);

        foreach (string library in Libraries)
        {
            string path = Path.Combine(root, library);
            using var embedded = archive.GetEntry(library)!.Open();

            // Keep the verified file open while the loader maps it, so it cannot be replaced.
            using var extracted = File.OpenRead(path);
            if (!SHA256.HashData(embedded).AsSpan().SequenceEqual(SHA256.HashData(extracted)))
                throw new InvalidDataException($"The cached {library} is damaged. Remove the runtime cache at {root} and restart.");

            if (LoadLibraryExW(path, IntPtr.Zero, SearchDllDirectory | SearchSystem32) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not load {library}.");
        }
        return root;
    }

    static void Install(ZipArchive archive, string cache, string root)
    {
        Directory.CreateDirectory(cache);
        string staging = Path.Combine(cache, Guid.NewGuid().ToString("N"));
        try
        {
            archive.ExtractToDirectory(staging);
            try { Directory.Move(staging, root); }
            catch (IOException) when (Directory.Exists(root)) { } // Another launch installed the same bundle.
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);
}
