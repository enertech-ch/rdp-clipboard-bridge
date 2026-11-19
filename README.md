# RDP Clipboard Bridge

A small tray app for Windows that makes files copied inside a Remote Desktop session pasteable in
New Outlook, browsers and other Chromium/WebView2 apps that accept pasted files.

## The problem

Copy a file on the remote machine, press Ctrl+V in New Outlook on your own PC - and nothing happens.
Remote Desktop does not put real files on your clipboard, only *virtual* files that are transferred
when a paste asks for them. Explorer and classic Outlook understand those; Chromium-based apps only
accept a plain list of files and ignore the rest.

RDP Clipboard Bridge runs in the background on your own PC. As soon as you copy files in a remote
session, it downloads them into `%TEMP%\RDPClip`. The clipboard itself is left alone while you stay
in the session, so copying, cutting and pasting there work as before. Once you switch to an app on
your own PC, the clipboard is replaced with the downloaded files, so Ctrl+V works everywhere. The
files keep the date they have on the remote machine.

Explorer on your PC does not count as switching: it pastes Remote Desktop's files by itself.

That is the *Automatic* mode, on from the start. If you would rather say yourself which copies are
converted, switch on *On Demand*: press Ctrl+C two, three or four times in a row (Ctrl+C, Ctrl+C, ...)
in the session and that copy is converted, whatever the Automatic settings say - see
[Automatic and On Demand](#automatic-and-on-demand). Or simply left-click the tray icon after
copying: that converts the copy on the clipboard right away, whatever the settings say.

## Mind the data usage

Without this tool, Remote Desktop transfers a copied file only when you paste it. With it, every
file you copy in a remote session is downloaded as soon as it is copied - whether you paste it on
your PC, paste it inside the session or not at all. On a metered connection, such as a phone hotspot
or mobile data, that can eat into your data allowance.

Ways to limit it, all in the tray menu:

* To transfer nothing you did not ask for, untick *Automatic > Convert Automatically* and choose a
  count under *On Demand*: only a copy made with that many Ctrl+C in a row is downloaded. Mind that
  such a copy is converted whatever its size or type, and on a metered connection too.
* *Size Limit* is the main guard of the Automatic mode: a copy larger than that in total - 25 MB unless you change it,
  about where mail stops taking attachments - is not downloaded but left to Remote Desktop, which
  transfers it only when it is pasted.
* Under *File Types*, untick what you never need on your PC - videos or disk images, say. A copy
  that contains such a file is left to Remote Desktop in the same way.
* *Pause on Metered Connections* stops it on its own whenever Windows considers the connection
  metered; it is off by default.
* *Pause* stops it for the moment.

Hover over the tray icon to see why a copy was not converted.

## Install

1. Download `RdpClipboardBridge.exe` from the [latest release](../../releases/latest).
2. Double-click it and pick one of:
   * **Install and start with Windows** - the usual choice.
   * **Install only** - installed, but you start it yourself. An earlier autostart entry is removed.
   * **Run without installing** - runs from where it is; you are not asked again.

   Closing the dialog runs it from where it is this time and asks again next time.

It installs for your user only, into `%LOCALAPPDATA%\Programs\RdpClipboardBridge`. No administrator
rights are needed. Afterwards it sits in the tray; the downloaded file can be deleted.

The exe is not code-signed, so Windows warns once before starting the downloaded file. The installed
copy starts without that warning, at logon too.

Install it on the PC you connect *from*. Inside a remote session it does nothing.

## Tray menu

Right-click the tray icon:

| Item | What it does |
| --- | --- |
| Pause / Resume | stop and resume converting copied files, in both modes |
| Start with Windows | ticked when it starts automatically with Windows; click to switch |
| **Automatic** ▸ | what is converted without being asked - see [Automatic and On Demand](#automatic-and-on-demand) |
| &nbsp;&nbsp;Convert Automatically | ticked at first: every copy is converted unless one of the three settings below leaves it alone. Unticked, nothing is converted unless asked for *On Demand*, and the three settings below are greyed out |
| &nbsp;&nbsp;File Types | one tick per kind of file, and one for folders - see [File types](#file-types). A copy with anything unticked in it is left to Remote Desktop. The submenu stays open while you tick |
| &nbsp;&nbsp;Size Limit: 25 MB… | shows the current limit; opens a small window to change it (1 to 999 MB). A copy larger than that in total is left to Remote Desktop |
| &nbsp;&nbsp;Pause on Metered Connections | off by default: when ticked, nothing is converted automatically while Windows reports the internet connection as metered, and the tray icon's tooltip says so. Remote Desktop then transfers a file only when it is pasted, as it does without this tool |
| **On Demand** ▸ | *Off* (the default), *Press Ctrl+C 2×*, *Press Ctrl+C 3×* or *Press Ctrl+C 4×*: pressing Ctrl+C that many times in a row in a Remote Desktop window converts the copy, whatever *Automatic* says |
| Open Temp Folder | opens `%TEMP%\RDPClip`, where the copied files are put |
| Install… | only when not installed - the same choices as on first start |
| Uninstall | only when installed - removes the program, its autostart entry, its settings and the temp folder |
| About… | shows the version and a link to the project page |
| Exit | ends it until the next start |

The settings apply from the next copy on. When the Automatic mode left a copy to Remote Desktop,
the tray icon's tooltip says why - metered connection, file type or size limit - until something
else is copied or the copy is converted on demand.

## Automatic and On Demand

There are three ways a copy gets converted, and they work side by side:

* **Automatic** (on at first) converts every copy that passes its filters: *File Types*, *Size
  Limit* and, if ticked, *Pause on Metered Connections*. That is what the tool always did.
* **On Demand** (off at first) converts a copy when you press Ctrl+C several times in a row: with
  *Press Ctrl+C 3×*, select the files in the session - a full remote desktop or a RemoteApp window -
  and press Ctrl+C three times in quick succession, at most half a second apart. The copy is then converted even if Automatic is switched off, a file type is unticked, it
  is over the size limit or the connection is metered. Only *Pause* stops it. The download starts
  about half a second after the last press, once the session has finished copying.
* **A left click on the tray icon** converts the copy that is on the clipboard now, whatever
  Automatic and On Demand are set to - also with both switched off. Only *Pause* stops it.

The tray icon turns orange while files are downloaded, and for a moment after each click, so you see
the click was taken even when there was nothing (more) to download.

Both together: with Automatic on, a copy that passes the filters is converted at the first Ctrl+C,
and pressing again changes nothing; a copy the filters leave alone can still be pushed through by
pressing Ctrl+C again. With Automatic off, nothing is converted until you ask for it - the way to transfer
nothing you did not mean to. Either way, the clipboard is replaced only once you switch to an app
on your PC.

## File types

A copy is converted only if everything in it is ticked under *File Types*. If a single file is of
an unticked type, or the copy contains a folder while *Folders* is unticked, the whole copy is left
to Remote Desktop - see [Known limitations](#known-limitations). A file's type is decided by its
extension, upper or lower case alike.

The tool is meant for attaching files to mails, so what one rarely mails starts unticked:
applications, files without or with an unknown extension, and folders. Ticking one is a click.

| Type | Ticked at first | Extensions |
| --- | --- | --- |
| Images | yes | jpg, png, gif, bmp, jpeg, jpe, jfif, tif, tiff, webp, heic, heif, avif, svg, ico, dib, psd, tga, emf, wmf, jxl, raw, cr2, cr3, nef, arw, dng, orf, rw2 |
| Videos | yes | mp4, mov, avi, mkv, wmv, webm, m4v, mpg, mpeg, flv, 3gp, 3g2, m2ts, mts, ogv, vob, asf, f4v, divx |
| Audio | yes | mp3, wav, flac, m4a, aac, ogg, oga, opus, wma, aif, aiff, mid, midi, amr |
| PDF Documents | yes | pdf |
| Office Documents | yes | docx, xlsx, pptx, odt, ods, odp, doc, xls, ppt, docm, dotx, dotm, dot, xlsm, xlsb, xltx, xltm, xlt, pptm, ppsx, ppsm, pps, potx, potm, pot, rtf, odg, odf, odb, ott, ots, otp, fodt, fods, fodp, vsd, vsdx, vsdm, pub, one, mpp, accdb, mdb, msg, eml, oft, ics, vcf |
| Text Files | yes | txt, log, csv, md, json, xml, yaml, yml, ini, cfg, conf, config, toml, properties, tsv, markdown, rst, tex, nfo, srt, vtt, html, htm, css, js, mjs, ts, tsx, jsx, php, py, rb, java, c, h, cpp, hpp, cs, vb, go, rs, swift, kt, sql, sh |
| Archives | yes | zip, rar, 7z, tar, gz, tgz, bz2, tbz2, xz, txz, zst, lz, lz4, lzma, z, cab, arj, lzh, zipx, iso, img, vhd, vhdx, vmdk, wim |
| Applications | no | exe, dll, msi, bat, cmd, ps1, com, scr, sys, cpl, ocx, drv, msix, msixbundle, appx, appxbundle, msp, msu, vbs, vbe, wsf, wsh, hta, jar, lnk, reg, apk |
| No Extension | no | files without an extension, and names like `.gitignore` |
| Everything Else | no | any extension not listed above |
| Folders | no | a copied folder, with everything in it |

A few placements worth knowing: source code (`js` included) counts as text, disk images (`iso`,
`vhdx`, …) as archives, saved mails (`msg`, `eml`) and calendar and contact files (`ics`, `vcf`) as
office documents, and scripts and shortcuts as applications. For `archive.tar.gz` only the last
extension counts.

## Languages

The menu and dialogs follow the Windows display language: English, German, French, Italian,
Spanish, Portuguese (Brazilian), Russian, Chinese (Simplified), Japanese and Indonesian. Any other
language gets English. There is no setting for it.

Most translations were not written by native speakers. If something reads oddly in your language,
corrections are welcome - open an issue or a pull request.

Each language is one plain-text file in [`src/lang/`](src/lang), named after its two-letter code
(`de.txt`, `fr.txt`, ...), with one `TextId = text` per line; `\n` is a line break. To correct a
translation, edit its line. To add a language, copy `en.txt` to the new code and translate the text
after each `=`, keeping every `{0}`, `{1}`... where the sentence needs it. The build picks the file up
on its own, and `python3 tools/check-texts.py` tells you what is missing or does not match.

## Upgrade

Download the new `RdpClipboardBridge.exe` and double-click it while the old one is running. Confirm
replacing the installed copy: the old one closes, the new one is installed in its place and started.
Whether it starts with Windows stays as it was.

If the copy that is running is not the installed one, the new one asks to close it instead and then
carries on as if it had been started on its own.

If the old copy does not close within a few seconds, you are told so - choose *Exit* from its tray
menu and start the new exe again.

## Known limitations

* **Back in the session, a cut becomes a copy.** Once you have switched to an app on your PC, the
  session's clipboard holds your PC's copies of the files, because Remote Desktop passes every
  clipboard change on to the session. Pasting there afterwards copies them from your PC, and a cut
  (Ctrl+X) no longer moves anything - the original stays where it was. Finish a cut and paste inside
  the session before you switch away.
* **A copy is converted whole or not at all.** If it contains anything unticked under *File Types*,
  or is larger in total than the *Size Limit* (25 MB at first; files inside copied folders count
  too), all of it is left to Remote Desktop and pastes on your PC only where Remote Desktop's own
  files do (Explorer, classic Outlook). Converting only part of it would make the rest vanish from
  what gets pasted. Applications, No Extension, Everything Else and Folders start unticked. Hover
  over the tray icon to see why a copy was not converted.
* **Folders are not converted** unless *File Types > Folders* is ticked. A copy that contains a
  folder - also next to single files - stays as Remote Desktop made it: it pastes into Explorer,
  but not into New Outlook, which attaches files only. Copy the files themselves for that. With
  *Folders* ticked, folders keep their structure, but the whole tree is downloaded as soon as it is
  copied.
* **Files are downloaded as soon as they are copied**, pasted or not - see
  [Mind the data usage](#mind-the-data-usage). For a large file that takes as long as the transfer,
  and the tray menu may be slow to respond meanwhile. Copying something else, *Pause* or *Exit*
  gives up the transfer that is running.
* **Files that could not be downloaded are left out.** A balloon says how many once you switch to an
  app on your PC - not before, since a copy pasted inside the session never needed them. A file
  that arrives shorter than announced counts as not downloaded. Copy the files again to have them
  downloaded once more.
* **On Demand watches for Ctrl+C on your PC's keyboard** - only while *On Demand* is switched on,
  and only while a Remote Desktop window is in front. With *Off* the tool does not watch the
  keyboard at all, and it never looks at any other key. Security software may still ask about it or
  report it, since watching the keyboard is something it looks out for. Copying with the context
  menu or a toolbar button inside the session does not count - only the keys do. Holding Ctrl+C
  down counts as one press, and presses more than half a second apart start counting again.
  Whether the keys are seen while the Remote Desktop window is in full screen has not been
  verified.
* **"Metered" is what Windows says it is**: a connection marked as metered in the Windows network
  settings, a phone hotspot Windows recognises, mobile data. A VPN that carries all traffic may hide
  a metered connection underneath it.
* **The clipboard is replaced** when you switch to an app on your PC. Whatever else the remote
  program put on the clipboard alongside the files (text, its own formats) is gone after that.
* **Only Remote Desktop's copies are converted.** That means the clients `mstsc`, `msrdc` (Azure
  Virtual Desktop, Windows App) and `vmconnect` (Hyper-V). Virtual files from other programs, such
  as a mail copied in classic Outlook, are left alone - and so are those of a remote desktop client
  that is not on this list. Running the tool inside a remote session does nothing: there, copies
  come from the session's side of Remote Desktop, and two copies of the tool, one on each end, would
  otherwise pass the same files back and forth without end.
* **Each new copy clears the previous one** from `%TEMP%\RDPClip`, where every copy gets its own
  subfolder. Files that are still open are left until a later copy.

## Build

Compiled by GitHub Actions on every push and attached to each published release, so normally you
just download it. To build locally anyway:

```powershell
.\build.ps1
.\build.ps1 -Version v1.2.3
```

It uses the `csc.exe` that ships with .NET Framework 4 (present on every Windows machine and on the
GitHub runners), so there is no SDK or NuGet dependency. It compiles every `.cs` file under `src\` and
embeds the language files from `src\lang\`, so the result is a single ~125 KB exe in `bin\` with
nothing next to it and no runtime to install.
