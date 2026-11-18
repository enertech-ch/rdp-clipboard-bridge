using System;
using System.Collections.Generic;

// Decides whether a copy is converted at all. It is taken as a whole or left to Remote Desktop as a
// whole: converting only some of its files would make the others vanish from what gets pasted.
static class CopyFilter
{
    public static LeftAloneReason ReasonToLeaveAlone(List<VirtualFile> files)
    {
        List<FileTypeGroup> leftAlone = FileTypes.NotConverted();
        bool convertFolders = !leftAlone.Contains(FileTypes.Folders);
        long totalSize = 0;
        foreach (VirtualFile file in files)
        {
            // judged by the name the file gets here, which is what ends up on this machine
            string localPath = StagingFolder.ToLocalRelativePath(file.RemoteName);

            // The apps this is for cannot take folders anyway, and Remote Desktop's own handling
            // pastes them into Explorer without first downloading the whole tree.
            if (!convertFolders && (file.IsFolder || localPath.IndexOf('\\') >= 0)) return LeftAloneReason.FileType;
            if (!file.IsFolder && localPath.Length > 0 && leftAlone.Contains(FileTypes.Of(localPath))) return LeftAloneReason.FileType;
            if (file.KnownSize > 0) totalSize += file.KnownSize;
        }
        return totalSize > SizeLimit.Bytes ? LeftAloneReason.SizeLimit : LeftAloneReason.None;
    }
}

enum LeftAloneReason
{
    None,
    MeteredConnection,
    FileType,
    SizeLimit
}

// The largest copy that is still converted. The tool exists for mail attachments, and about 25 MB
// is where mail stops taking them - a larger copy would only be downloaded for nothing.
static class SizeLimit
{
    public const int MinMegabytes = 1;
    public const int MaxMegabytes = 999;
    const int DefaultMegabytes = 25;
    const long BytesPerMegabyte = 1024 * 1024;

    // a hand-edited value outside the range must not switch the limit off or block every copy
    public static int Megabytes
    {
        get { return Clamp(Settings.GetNumber(Settings.SizeLimitMB, DefaultMegabytes)); }
    }

    public static long Bytes
    {
        get { return Megabytes * BytesPerMegabyte; }
    }

    public static void Set(int megabytes)
    {
        Settings.SetNumber(Settings.SizeLimitMB, Clamp(megabytes));
    }

    static int Clamp(int megabytes)
    {
        return Math.Max(MinMegabytes, Math.Min(MaxMegabytes, megabytes));
    }
}
