using System;
using System.Collections.Generic;
using System.IO;

static class StagingFolder
{
    public static readonly string Location = Path.Combine(Path.GetTempPath(), "RDPClip");

    // Every copy gets a folder of its own. Files from the last paste may still be open - an
    // attachment being uploaded, a document the user opened - so an old batch cannot always be
    // removed, and a fresh folder keeps its leftovers from mixing into the new copy.
    public static string StartNewBatch()
    {
        ClearPreviousBatches();
        string batchFolder = FreePathFor(Path.Combine(Location, DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(batchFolder);
        return batchFolder;
    }

    // Whatever is locked stays until a later copy.
    public static void ClearPreviousBatches()
    {
        Directory.CreateDirectory(Location);
        foreach (string file in Directory.GetFiles(Location)) TryDeleteFile(file);
        foreach (string folder in Directory.GetDirectories(Location)) TryDeleteFolder(folder);
    }

    public static void RemoveEverything()
    {
        if (!Directory.Exists(Location)) return;
        ClearPreviousBatches();
        TryDeleteFolder(Location);
    }

    public static void TryDeleteFile(string path)
    {
        if (path == null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void TryDeleteFolder(string path)
    {
        try { Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // The names come from the remote machine and keep the path below a copied folder
    // ("folder\file.txt"). That structure is kept, but every part is cleaned on its own, so nothing
    // in a name can lead out of the batch folder: a part cannot contain a separator or a drive
    // colon, and ".." does not survive the trimming. Dots and spaces are trimmed together because
    // Windows drops both from the end of a name - ".. ." would otherwise still arrive as "..".
    public static string ToLocalRelativePath(string remoteName)
    {
        List<string> parts = new List<string>();
        foreach (string remotePart in remoteName.Split('\\', '/'))
        {
            string part = remotePart;
            foreach (char invalid in Path.GetInvalidFileNameChars()) part = part.Replace(invalid, '_');
            part = part.Trim().TrimEnd('.', ' ');
            if (part.Length > 0) parts.Add(part);
        }
        return string.Join("\\", parts.ToArray());
    }

    // The second lock on the same door: whatever a remote name turns into after cleaning, and however
    // Windows reads the result, nothing is written anywhere but below the batch folder.
    public static bool IsInside(string folder, string path)
    {
        string prefix = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // Two remote names can become the same local one once they are cleaned.
    public static string FreePathFor(string path)
    {
        string folder = Path.GetDirectoryName(path);
        string baseName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int copy = 2; File.Exists(path) || Directory.Exists(path); copy++)
        {
            path = Path.Combine(folder, baseName + " (" + copy + ")" + extension);
        }
        return path;
    }
}
