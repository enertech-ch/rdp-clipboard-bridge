using System;
using System.Collections.Generic;

// One line of the file type filter: its setting, its label and the extensions it stands for.
sealed class FileTypeGroup
{
    public readonly string SettingName;
    public readonly TextId Label;
    public readonly string[] Extensions;

    readonly bool convertedByDefault;

    public FileTypeGroup(string settingName, TextId label, bool convertedByDefault, string extensions)
    {
        SettingName = settingName;
        Label = label;
        this.convertedByDefault = convertedByDefault;
        Extensions = extensions.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }

    public bool IsConverted
    {
        get { return Settings.IsOn(SettingName, convertedByDefault); }
    }

    public string Examples(int count)
    {
        if (Extensions.Length <= count) return string.Join(", ", Extensions);
        return string.Join(", ", Extensions, 0, count) + " …";
    }
}

// The file types the tray menu lets the user leave out. Each list starts with its best-known
// extensions, which are the ones the menu shows. An extension belongs to one type only.
//
// The tool exists for attaching files to mails. Applications, files without or with an unknown
// extension, and folders are rarely something one mails, so they start unticked and such a copy is
// left to Remote Desktop - which transfers nothing until it is pasted. Ticking them is one click.
static class FileTypes
{
    const bool OnByDefault = true;
    const bool OffByDefault = false;

    public static readonly FileTypeGroup Images = new FileTypeGroup("ConvertImages", TextId.FileTypeImages, OnByDefault,
        "jpg png gif bmp jpeg jpe jfif tif tiff webp heic heif avif svg ico dib psd tga emf wmf jxl raw cr2 cr3 nef arw dng orf rw2");

    public static readonly FileTypeGroup Videos = new FileTypeGroup("ConvertVideos", TextId.FileTypeVideos, OnByDefault,
        "mp4 mov avi mkv wmv webm m4v mpg mpeg flv 3gp 3g2 m2ts mts ogv vob asf f4v divx");

    public static readonly FileTypeGroup Audio = new FileTypeGroup("ConvertAudio", TextId.FileTypeAudio, OnByDefault,
        "mp3 wav flac m4a aac ogg oga opus wma aif aiff mid midi amr");

    public static readonly FileTypeGroup Pdf = new FileTypeGroup("ConvertPdf", TextId.FileTypePdf, OnByDefault,
        "pdf");

    // Microsoft Office, OpenDocument (LibreOffice, OpenOffice), mail items, and the calendar and
    // contact files that get mailed - Everything Else, where they would land otherwise, starts unticked
    public static readonly FileTypeGroup Office = new FileTypeGroup("ConvertOffice", TextId.FileTypeOffice, OnByDefault,
        "docx xlsx pptx odt ods odp doc xls ppt docm dotx dotm dot xlsm xlsb xltx xltm xlt pptm ppsx ppsm pps potx potm pot rtf " +
        "odg odf odb ott ots otp fodt fods fodp vsd vsdx vsdm pub one mpp accdb mdb msg eml oft ics vcf");

    // plain text of any kind, source code included. js is here although Windows runs it on a
    // double-click: far more of them are web sources than scripts
    public static readonly FileTypeGroup Text = new FileTypeGroup("ConvertText", TextId.FileTypeText, OnByDefault,
        "txt log csv md json xml yaml yml ini cfg conf config toml properties tsv markdown rst tex nfo srt vtt " +
        "html htm css js mjs ts tsx jsx php py rb java c h cpp hpp cs vb go rs swift kt sql sh");

    // disk images are archives of a kind, and as large
    public static readonly FileTypeGroup Archives = new FileTypeGroup("ConvertArchives", TextId.FileTypeArchives, OnByDefault,
        "zip rar 7z tar gz tgz bz2 tbz2 xz txz zst lz lz4 lzma z cab arj lzh zipx iso img vhd vhdx vmdk wim");

    // whatever Windows runs or installs on a double-click, scripts and shortcuts included
    public static readonly FileTypeGroup Applications = new FileTypeGroup("ConvertApplications", TextId.FileTypeApplications, OffByDefault,
        "exe dll msi bat cmd ps1 com scr sys cpl ocx drv msix msixbundle appx appxbundle msp msu vbs vbe wsf wsh hta jar lnk reg apk");

    public static readonly FileTypeGroup NoExtension = new FileTypeGroup("ConvertNoExtension", TextId.FileTypeNoExtension, OffByDefault, "");
    public static readonly FileTypeGroup Other = new FileTypeGroup("ConvertOtherFiles", TextId.FileTypeOther, OffByDefault, "");

    // Not a type of file, so Of never returns it, but it is ticked in the same place. The setting
    // keeps the name it had as a tray item of its own, so an earlier choice still applies.
    public static readonly FileTypeGroup Folders = new FileTypeGroup("ConvertFolders", TextId.FileTypeFolders, OffByDefault, "");

    public static readonly FileTypeGroup[] All = { Images, Videos, Audio, Pdf, Office, Text, Archives, Applications, NoExtension, Other, Folders };

    static readonly Dictionary<string, FileTypeGroup> ByExtension = IndexByExtension();

    public static FileTypeGroup Of(string path)
    {
        string name = path.Substring(path.LastIndexOf('\\') + 1);
        int dot = name.LastIndexOf('.');
        // a name like ".gitignore" is all name and no extension
        if (dot <= 0) return NoExtension;

        FileTypeGroup group;
        return ByExtension.TryGetValue(name.Substring(dot + 1), out group) ? group : Other;
    }

    public static List<FileTypeGroup> NotConverted()
    {
        List<FileTypeGroup> groups = new List<FileTypeGroup>();
        foreach (FileTypeGroup group in All)
        {
            if (!group.IsConverted) groups.Add(group);
        }
        return groups;
    }

    static Dictionary<string, FileTypeGroup> IndexByExtension()
    {
        Dictionary<string, FileTypeGroup> index = new Dictionary<string, FileTypeGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (FileTypeGroup group in All)
        {
            foreach (string extension in group.Extensions) index[extension] = group;
        }
        return index;
    }
}
