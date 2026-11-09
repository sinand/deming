namespace Deming;

/// <summary>
/// Writes a file that only its owner may read: on Unix a new file gets mode 0600 (and a new directory 0700);
/// on Windows the user profile's ACLs already keep it private. The write is atomic: a temporary file beside
/// the target is renamed over it, so a reader never sees half a file.
/// </summary>
internal static class PrivateFile
{
    public static void Write(string path, Action<Stream> write)
    {
        CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, options))
                write(stream);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static void WriteAllBytes(string path, byte[] bytes) => Write(path, s => s.Write(bytes));

    /// <summary>Creates the directory if missing; on Unix a new one is owner-only (0700).</summary>
    public static void CreateDirectory(string directory)
    {
        if (Directory.Exists(directory))
            return;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Unix: narrows an existing file to owner read/write (a copied or restored file may be wider). No-op on Windows.</summary>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        var wanted = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (File.GetUnixFileMode(path) != wanted)
            File.SetUnixFileMode(path, wanted);
    }
}
