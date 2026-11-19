using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ComTypes = System.Runtime.InteropServices.ComTypes;

// A copy that was downloaded into the staging folder.
class StagedCopy
{
    // names, sizes and dates of what the clipboard announced, to recognise the same copy again
    public readonly string CopiedFiles;
    // What goes on the clipboard: a copied folder is listed once, and whoever pastes it takes
    // everything below it along - listing its files as well would paste them twice.
    public readonly List<string> TopLevelPaths = new List<string>();
    public int FailedCount;
    // given up half way, because nobody was waiting for it any more
    public bool Abandoned;

    public StagedCopy(string copiedFiles)
    {
        CopiedFiles = copiedFiles;
    }

    // A copy whose files all failed is kept too: its warning is still owed once the user wants it.
    public bool IsWorthKeeping
    {
        get { return !Abandoned && (TopLevelPaths.Count > 0 || FailedCount > 0); }
    }
}

class VirtualFile
{
    public int DescriptorIndex;
    public string RemoteName;
    public bool IsFolder;
    public long KnownSize = -1;
    public DateTime? LastWriteTimeUtc;
}

// The clipboard's data object together with the file list it announces.
sealed class ClipboardFiles : IDisposable
{
    public readonly ComTypes.IDataObject Data;
    public readonly List<VirtualFile> Files;
    // names, sizes and dates of the files, to recognise the same copy when it is announced again
    public readonly string CopiedFiles;

    public ClipboardFiles(ComTypes.IDataObject data, List<VirtualFile> files)
    {
        Data = data;
        Files = files;
        CopiedFiles = Describe(files);
    }

    public void Dispose()
    {
        Marshal.ReleaseComObject(Data);
    }

    // Parsed fields rather than the raw descriptor, whose unused bytes need not be the same twice.
    // The date is part of it so that a file changed and copied again is not taken for the old one.
    static string Describe(List<VirtualFile> files)
    {
        StringBuilder description = new StringBuilder();
        foreach (VirtualFile file in files)
        {
            description.Append(file.DescriptorIndex).Append('|').Append(file.RemoteName).Append('|');
            description.Append(file.KnownSize).Append('|').Append(file.LastWriteTimeUtc.GetValueOrDefault().Ticks).Append('\n');
        }
        return description.ToString();
    }
}

sealed class TransferAbandonedException : Exception
{
}

static class VirtualFileClipboard
{
    const int CfHdrop = 15;
    static readonly int FileGroupDescriptorFormat = DataFormats.GetFormat("FileGroupDescriptorW").Id;
    static readonly int FileContentsFormat = DataFormats.GetFormat("FileContents").Id;

    // FILEGROUPDESCRIPTORW: a uint32 count, followed by that many FILEDESCRIPTORW records
    const int DescriptorCountSize = 4;
    const int FileDescriptorSize = 592;
    const int FlagsOffset = 0;
    const int AttributesOffset = 36;
    const int LastWriteTimeOffset = 56;
    const int FileSizeHighOffset = 64;
    const int FileSizeLowOffset = 68;
    const int FileNameOffset = 72;
    const int MaxFileNameChars = 260;
    const uint FdAttributes = 0x4;
    const uint FdWritesTime = 0x20;
    const uint FdFileSize = 0x40;
    const uint FileAttributeDirectory = 0x10;

    const int CopyChunkSize = 81920;
    const int WholeObject = -1;

    [DllImport("user32.dll")]
    static extern bool IsClipboardFormatAvailable(int format);

    [DllImport("ole32.dll")]
    static extern int OleGetClipboard(out ComTypes.IDataObject dataObject);

    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern UIntPtr GlobalSize(IntPtr memory);

    // A real file list on the clipboard already works everywhere, including our own replacement.
    public static bool HasVirtualFilesOnly()
    {
        return IsClipboardFormatAvailable(FileGroupDescriptorFormat) && !IsClipboardFormatAvailable(CfHdrop);
    }

    // Downloads what the clipboard announces into a new batch folder. Whether it should be is decided
    // before, from the same file list.
    public static StagedCopy Stage(ClipboardFiles clipboard, Func<string, bool> isAbandoned)
    {
        string copiedFiles = clipboard.CopiedFiles;
        StagedCopy copy = new StagedCopy(copiedFiles);
        if (clipboard.Files.Count == 0) return copy;

        Func<bool> abandoned = delegate { return isAbandoned(copiedFiles); };
        string batchFolder = StagingFolder.StartNewBatch();
        try
        {
            foreach (VirtualFile file in clipboard.Files)
            {
                string topLevelPath = TryExtract(clipboard.Data, file, batchFolder, abandoned);
                if (topLevelPath == null) copy.FailedCount++;
                else if (!ContainsPath(copy.TopLevelPaths, topLevelPath)) copy.TopLevelPaths.Add(topLevelPath);
            }
        }
        catch (TransferAbandonedException)
        {
            copy.Abandoned = true;
        }
        return copy;
    }

    // Compared by content, because whether reading the files moves the clipboard sequence number is
    // up to mstsc, and a wrong guess there would mean nothing is ever put on the clipboard.
    public static bool StillHolds(string copiedFiles)
    {
        if (!HasVirtualFilesOnly()) return false;
        using (ClipboardFiles clipboard = ReadClipboardFiles())
        {
            return clipboard.CopiedFiles == copiedFiles;
        }
    }

    // Getting the clipboard's data object and asking it for the file list are retried as one step:
    // either of them can find mstsc still holding the clipboard open.
    public static ClipboardFiles ReadClipboardFiles()
    {
        return ClipboardRetry.Run<ClipboardFiles>(delegate
        {
            ComTypes.IDataObject data = OpenOleClipboard();
            try
            {
                return new ClipboardFiles(data, ParseFileGroupDescriptor(ReadWholeFormat(data, FileGroupDescriptorFormat)));
            }
            catch (Exception)
            {
                Marshal.ReleaseComObject(data);
                throw;
            }
        });
    }

    // Straight from ole32 instead of Clipboard.GetDataObject: FileContents has to be requested per
    // file through FORMATETC.lindex, which WinForms' DataObject API has no parameter for. Talking to
    // the OLE object directly makes sure the index reaches mstsc rather than relying on WinForms
    // internals forwarding it.
    static ComTypes.IDataObject OpenOleClipboard()
    {
        ComTypes.IDataObject clipboardData;
        Marshal.ThrowExceptionForHR(OleGetClipboard(out clipboardData));
        return clipboardData;
    }

    static byte[] ReadWholeFormat(ComTypes.IDataObject data, int format)
    {
        using (MemoryStream buffer = new MemoryStream())
        {
            ReadFormat(data, format, WholeObject, buffer, -1, null);
            return buffer.ToArray();
        }
    }

    static List<VirtualFile> ParseFileGroupDescriptor(byte[] descriptor)
    {
        List<VirtualFile> files = new List<VirtualFile>();
        if (descriptor.Length < DescriptorCountSize) return files;

        uint count = BitConverter.ToUInt32(descriptor, 0);
        for (int index = 0; index < count; index++)
        {
            int offset = DescriptorCountSize + index * FileDescriptorSize;
            if (offset + FileDescriptorSize > descriptor.Length) break;

            VirtualFile file = ParseFileDescriptor(descriptor, offset);
            if (file == null) continue;
            // lindex addresses the descriptor array, so entries without a name still count
            file.DescriptorIndex = index;
            files.Add(file);
        }
        return files;
    }

    // Folders are listed as entries of their own, with the files below them named "folder\file".
    static VirtualFile ParseFileDescriptor(byte[] descriptor, int offset)
    {
        uint flags = BitConverter.ToUInt32(descriptor, offset + FlagsOffset);
        uint attributes = BitConverter.ToUInt32(descriptor, offset + AttributesOffset);

        string name = Encoding.Unicode.GetString(descriptor, offset + FileNameOffset, MaxFileNameChars * 2);
        int terminator = name.IndexOf('\0');
        if (terminator >= 0) name = name.Substring(0, terminator);
        if (name.Length == 0) return null;

        VirtualFile file = new VirtualFile();
        file.RemoteName = name;
        file.IsFolder = (flags & FdAttributes) != 0 && (attributes & FileAttributeDirectory) != 0;
        if ((flags & FdFileSize) != 0)
        {
            long high = BitConverter.ToUInt32(descriptor, offset + FileSizeHighOffset);
            long low = BitConverter.ToUInt32(descriptor, offset + FileSizeLowOffset);
            file.KnownSize = (high << 32) | low;
        }
        if ((flags & FdWritesTime) != 0)
        {
            file.LastWriteTimeUtc = ToDateTime(BitConverter.ToInt64(descriptor, offset + LastWriteTimeOffset));
        }
        return file;
    }

    // A date that cannot be represented is treated as no date.
    static DateTime? ToDateTime(long fileTime)
    {
        try
        {
            if (fileTime <= 0) return null;
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // Returns the top-level file or folder the entry ended up in, or null - a failed file must not
    // be left behind half written.
    static string TryExtract(ComTypes.IDataObject data, VirtualFile file, string batchFolder, Func<bool> abandoned)
    {
        string path = null;
        try
        {
            if (abandoned()) throw new TransferAbandonedException();

            string relativePath = StagingFolder.ToLocalRelativePath(file.RemoteName);
            string target = Path.Combine(batchFolder, relativePath);
            if (relativePath.Length == 0 || !StagingFolder.IsInside(batchFolder, target)) return null;

            string topLevelPath = Path.Combine(batchFolder, relativePath.Split('\\')[0]);
            if (file.IsFolder)
            {
                // has no contents to fetch, but an empty folder would be lost without its own entry
                Directory.CreateDirectory(target);
                return topLevelPath;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            path = StagingFolder.FreePathFor(target);
            using (FileStream contents = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                ReadFormat(data, FileContentsFormat, file.DescriptorIndex, contents, file.KnownSize, abandoned);
            }
            TryStampLastWriteTime(path, file.LastWriteTimeUtc);
            return relativePath.IndexOf('\\') < 0 ? path : topLevelPath;
        }
        catch (TransferAbandonedException)
        {
            StagingFolder.TryDeleteFile(path);
            throw;
        }
        catch (Exception)
        {
            // whatever went wrong - transfer broken off, disk full, unusable name - skip just this file
            StagingFolder.TryDeleteFile(path);
            return null;
        }
    }

    // A pasted file should carry the date it has on the remote machine, not the moment it was
    // downloaded - sorting by date and tools that compare dates rely on it. Not worth failing for.
    static void TryStampLastWriteTime(string path, DateTime? lastWriteTimeUtc)
    {
        if (!lastWriteTimeUtc.HasValue) return;
        try { File.SetLastWriteTimeUtc(path, lastWriteTimeUtc.Value); }
        catch (Exception) { }
    }

    static bool ContainsPath(List<string> paths, string path)
    {
        foreach (string known in paths)
        {
            if (string.Equals(known, path, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static void ReadFormat(ComTypes.IDataObject data, int format, int index, Stream target, long knownSize, Func<bool> abandoned)
    {
        ComTypes.FORMATETC request = new ComTypes.FORMATETC();
        request.cfFormat = unchecked((short)format);
        request.dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT;
        request.lindex = index;
        request.ptd = IntPtr.Zero;
        request.tymed = ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_HGLOBAL;

        ComTypes.STGMEDIUM medium;
        data.GetData(ref request, out medium);
        try
        {
            if (medium.tymed == ComTypes.TYMED.TYMED_ISTREAM) CopyComStream(medium.unionmember, target, knownSize, abandoned);
            else if (medium.tymed == ComTypes.TYMED.TYMED_HGLOBAL) CopyGlobalMemory(medium.unionmember, target, knownSize);
            else throw new NotSupportedException("unexpected storage medium " + medium.tymed);
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    static void CopyComStream(IntPtr streamPointer, Stream target, long knownSize, Func<bool> abandoned)
    {
        ComTypes.IStream source = (ComTypes.IStream)Marshal.GetObjectForIUnknown(streamPointer);
        IntPtr bytesReadPointer = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            TryRewind(source);
            byte[] chunk = new byte[CopyChunkSize];
            long copied = 0;
            while (true)
            {
                if (abandoned != null && abandoned()) throw new TransferAbandonedException();

                // a stream that leaves the count alone must not have the last one counted again
                Marshal.WriteInt32(bytesReadPointer, 0);
                source.Read(chunk, chunk.Length, bytesReadPointer);
                int bytesRead = Marshal.ReadInt32(bytesReadPointer);
                if (bytesRead <= 0) break;
                target.Write(chunk, 0, bytesRead);
                copied += bytesRead;
            }
            ThrowIfCutShort(copied, knownSize);
        }
        finally
        {
            Marshal.FreeCoTaskMem(bytesReadPointer);
            Marshal.ReleaseComObject(source);
        }
    }

    // A transfer that breaks off can end like a complete one, without any error - and the cut-off
    // file would be attached to a mail as if it were whole. More than announced is fine: a log file
    // may have grown since it was copied.
    static void ThrowIfCutShort(long copied, long knownSize)
    {
        if (knownSize >= 0 && copied < knownSize) throw new EndOfStreamException();
    }

    // Some sources hand out the stream positioned at its end, which would read as an empty file.
    // Streams that cannot seek start at the beginning anyway.
    static void TryRewind(ComTypes.IStream stream)
    {
        try { stream.Seek(0, 0, IntPtr.Zero); }
        catch (Exception) { }
    }

    // GlobalSize may round the block up, so the descriptor's size wins when it has one.
    static void CopyGlobalMemory(IntPtr memory, Stream target, long knownSize)
    {
        long length = (long)GlobalSize(memory).ToUInt64();
        ThrowIfCutShort(length, knownSize);
        if (knownSize >= 0) length = knownSize;

        IntPtr source = GlobalLock(memory);
        if (source == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            byte[] chunk = new byte[CopyChunkSize];
            for (long copied = 0; copied < length; copied += chunk.Length)
            {
                int count = (int)Math.Min(chunk.Length, length - copied);
                Marshal.Copy(new IntPtr(source.ToInt64() + copied), chunk, 0, count);
                target.Write(chunk, 0, count);
            }
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }
}

static class ClipboardRetry
{
    const int Attempts = 5;
    const int FirstDelayMilliseconds = 50;

    // mstsc holds the clipboard open for a moment whenever it changes, and anyone else opening it
    // then gets CLIPBRD_E_CANT_OPEN.
    public static T Run<T>(Func<T> clipboardAccess)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return clipboardAccess();
            }
            catch (ExternalException)
            {
                if (attempt == Attempts) throw;
                Thread.Sleep(FirstDelayMilliseconds * attempt);
            }
        }
    }
}
