namespace UwModManager.Core;

public static class Files
{
    /// <summary>Writes through a temporary file and a rename, so a crash leaves the old file or the new one, never half of one.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".uwmm-tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            f.Write(data, 0, data.Length);
            f.Flush(true);
        }
        File.Move(tmp, path, true);
    }

    public static string Sha(byte[]? data) => UltimateEditor.Modding.Hashing.Sha256(data);
}
