# AGENTS.md

Context for coding agents. `README.md` is for the people installing the exe; this file is the
short version of what matters when changing it.

## What this is

One small Windows tray exe. When files are copied inside a Remote Desktop session, mstsc puts
*virtual* files on the local clipboard (`FileGroupDescriptorW` + `FileContents`). New Outlook and
every other Chromium/WebView2 app only accept `CF_HDROP`, so pasting there does nothing. This exe
listens for clipboard changes and downloads the virtual files into `%TEMP%\RDPClip` right after the
copy, but replaces the clipboard with a real file drop list only once a local app comes to the
front. Copies of unticked file types or over a size limit are left to Remote Desktop. It also
installs, autostarts, upgrades and uninstalls itself, because there is no setup program.

| File | Purpose |
| --- | --- |
| `src/RdpClipboardBridge.cs` | the whole program - keep it a single file |
| `src/app.manifest` | `asInvoker` and system DPI awareness |
| `src/app.ico` | exe and tray icon - generated, do not edit by hand |
| `tools/make-icon.py` | draws `src/app.ico` from shapes (stdlib only) |
| `tools/check-texts.py` | checks the translation tables (stdlib only); CI runs it before compiling |
| `build.ps1` | compiles with the .NET Framework `csc` into `bin/` |
| `.github/workflows/build.yml` | CI, and attaches the exe to a published release |

## Constraints

* **No SDK, no NuGet, no dependencies.** It builds with `csc.exe` from .NET Framework 4, which is
  present on every Windows machine and on the GitHub runners. That is the reason the exe needs no
  runtime install on the machines it is used on. Do not introduce a `.csproj`, `dotnet build`, or a
  package reference.
* **C# 5 only.** That csc stops at C# 5: no string interpolation, `?.`, `nameof`, expression-bodied
  members, `out var`, auto-property initializers, pattern matching, tuples or local functions.
* **Warning-free.** `build.ps1` compiles with `/warnaserror+`.
* **The source is UTF-8 without BOM.** It holds the translations, and `build.ps1` passes
  `/codepage:65001` because this csc would otherwise read it in the machine's ANSI code page.
* **Never needs administrator rights.** It installs into the per-user programs folder and writes only
  under `HKCU`. Keep the manifest at `asInvoker`.
* **Nothing may escape the message loop.** It runs unattended; failures end in a tray balloon or a
  message box, never in a crash dialog.

## Non-obvious whys

**`OleGetClipboard`, not `Clipboard.GetDataObject`.** `FileContents` is requested per file through
`FORMATETC.lindex`, and only the OLE data object takes it. WinForms' `DataObject` has no parameter for
it; going to ole32 directly makes sure the index reaches mstsc. `lindex` is the position in the
descriptor array, folder entries included.

**Only Remote Desktop's clipboard, and only the client side.** The formats alone do not say where
virtual files come from: classic Outlook uses the same ones for a copied mail, next to private
formats that replacing the clipboard would destroy. So the clipboard owner's process must be one of
the clients `mstsc`, `msrdc` or `vmconnect` (`RemoteDesktop.Clients`). A new remote desktop client
has to be added to that list or it is ignored. `rdpclip`, the counterpart inside a session, is left
out on purpose, so the tool does nothing when it runs inside a session: with a copy on both ends of
one connection, each would convert what the other hands over - it arrives as rdpclip's or mstsc's
virtual files - and the files would go back and forth without end.

**The clipboard is replaced late.** Downloading starts right after the copy, but the file list goes
on the clipboard only when a local app comes to the front (`HandOverStagedCopy`,
`RemoteDesktop.HasUserLeftSession`). Replacing the clipboard is not a local affair: mstsc announces
every local clipboard change back into the session, where the PC's file list then replaces what was
copied there. A cut (Ctrl+X) would no longer move, a paste inside the session would send the files
to the PC and back, and a mail copied in the session's Outlook would lose the formats Outlook needs.
As long as the user stays in the session none of that happens. What remains: after switching to a
local app the session's clipboard does hold the PC's copies, so going back and pasting there copies
from the PC, and a cut is then a copy. Window changes come from `ForegroundWatcher`
(`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`, out of context, delivered through our message loop) -
no polling. Explorer does not count as a local app: it pastes virtual files natively (lazily, with
the remote dates), and its taskbar and task switcher pass through the foreground on the way between
windows. Warnings about failed files are shown at the hand-over too, not at copy time: a copy pasted
inside the session never needed them. Before handing over, the clipboard is checked to still hold
the same copy, so something copied since is not overwritten.

**A copy is converted whole or not at all.** Converting part of a copy would make the rest vanish
from the paste, so `CopyFilter` leaves the whole copy to Remote Desktop if anything in it is
unticked in the tray submenu *File Types* (`FileTypes`). Each entry there is a `FileTypeGroup` with
its own `Convert<Group>` value under `HKCU\Software\RdpClipboardBridge` and a default for when the
value is not set:

* Ten groups of files, judged by the cleaned local name, so what is judged is what would land on
  disk. Every extension belongs to one group; each list starts with the extensions the menu shows
  next to the label. Deliberate placements: `js` and other source code are text, disk images
  (`iso`, `vhdx`, ...) count as archives, mail items (`msg`, `eml`) and the calendar and contact
  files that get mailed (`ics`, `vcf`) as office, scripts and shortcuts as applications.
* `Folders`, last in the list: a copy with a folder entry or a name with a sub-path. It has no
  extensions and `FileTypes.Of` never returns it. Its value is still called `ConvertFolders`, the
  name it had as a tray item of its own, so an earlier choice applies. Besides the reason below,
  Remote Desktop's own handling pastes folders into Explorer without downloading the tree up front.

Images, videos, audio, PDF, office, text and archives start ticked; applications, no extension,
everything else and folders start unticked. The tool exists for attaching files to mails, and those
four are rarely something one mails - ticking them is one click. The README lists every group with
its default and its extensions; update it when a list changes. `OnMenuOpening` casts every submenu
item to `ToolStripMenuItem`, so a separator there needs handling first.

The submenu stays open while ticking (`KeepOpenWhileTickingFileTypes`, by cancelling the close the
item click causes). That is untested; if it does not work, the menu simply closes
after each click, as a plain menu would.

**Size limit.** `CopyFilter` also leaves a copy alone whose announced total size is over the limit
(`SizeLimit`, DWORD `SizeLimitMB`, 1 to 999 MB of 1024 * 1024 bytes, default 25). The sum covers
every descriptor entry, so files inside copied folders count, and an unknown size counts as 0. It is
decided from the descriptor before anything is downloaded. The default of 25 is where mail stops
taking attachments, and attaching to mails is what this tool is for; a larger copy would be
downloaded for nothing. There is no "unlimited" on purpose, and what is read is clamped into the
range, so a hand-edited value can neither switch the limit off nor block every copy. The tray item
shows the current value and opens `SizeLimitDialog`, built like `InstallDialog` (unowned, so the
exit request of a takeover closes it as well).

**Why a copy was left alone.** With a 25 MB limit and four types unticked from the start, "nothing
pastes" is common, so the tray tooltip names the reason - metered connection, file type (folders
included) or size limit (`LeftAloneReason`, which `Stage` returns on the copy) - until the clipboard
changes again. Changing the switch behind a metered or size-limit reason clears it, so the tooltip
does not name a limit the copy was not judged by. There is no balloon: most copies are pasted inside
the session and need no comment.

**Metered connections.** Converting downloads every copied file at once, pasted or not, while Remote
Desktop on its own transfers only what is pasted. With `PauseOnMetered` set (tray item *Pause on
Metered Connections*, off by default) a copy is therefore left alone while the connection is metered,
and the tray tooltip says so. The cost comes from the Network List
Manager's COM interface (`INetworkCostManager.GetCost` with no destination: the connection used for
internet access); the WinRT API for it would need references this build does not have. Not being
able to ask counts as not metered, so a failure cannot switch the tool off. Untested: with a VPN
that takes the default route, Windows may report the VPN adapter's cost instead of the metered
connection below it.

**One pass at a time.** Reading `FileContents` is a cross-process COM call into mstsc, and COM keeps
pumping messages while it waits, so `WM_CLIPBOARDUPDATE` and foreground changes arrive in the middle
of a transfer. `Bridge` therefore never nests: clipboard updates are counted (`clipboardUpdates`
against `updatesLookedAt`), window changes flagged (`foregroundChanged`), and the running pass goes
round again in `TryBridgeOnce` for whatever arrived meanwhile. Our own replacement needs no guard: it
carries `CF_HDROP`, which `HasVirtualFilesOnly` rejects. mstsc announces the same copy again at
times; it is recognised by its description (index, name, size and write time of every entry) and
not downloaded twice - `Stage` hands back the staged copy it was given. Content is compared rather
than the clipboard sequence number, because whether mstsc moves that number while it serves the
files is unknown, and if it did nothing would ever reach the clipboard.

**Abandoned transfers.** A transfer nobody waits for is given up (`IsAbandoned`, ending in
`TransferAbandonedException`): the app is closing, it was paused, or something else was copied
meanwhile. That is checked before each file and each chunk; the clipboard itself is only asked once
per clipboard update, since an update alone does not prove a new copy. `OnFormClosing` defers any
close during a transfer - tray *Exit*, an upgrade's exit request, taskkill - because closing would
dispose the window under the running COM call; the abandoned transfer ends and the close follows.
Only Windows shutting down is not kept waiting.

**Clipboard retries.** mstsc holds the clipboard open for a moment after every change, so opening it
right then fails with `CLIPBRD_E_CANT_OPEN`. Getting the OLE data object and reading the file list
from it are retried as one step with a short backoff (`ReadClipboardFiles`), since either can hit
that. `Clipboard.SetFileDropList` retries on its own and is not wrapped.

**Staging folder.** Every copy gets its own subfolder of `%TEMP%\RDPClip`, and the folder structure
of what was copied is recreated below it. The clipboard gets only the top-level files and folders:
that is what Explorer puts there itself, and listing a folder's files as well would paste them twice.
Flattening everything into one folder was tried first and broke pasting folders into Explorer, which
native RDP supports - replacing the clipboard must not take that away. Each batch clears the previous
ones, but a file being uploaded or opened from the last paste may be locked; it is left, and the
fresh subfolder keeps it out of the new copy. Files get the remote last-write time. A transfer that
ends short of the announced size counts as failed (`ThrowIfCutShort`) - a broken-off stream can end
like a complete one; more than announced is accepted (a log that grew since).

**Remote names are untrusted.** `StagingFolder.ToLocalRelativePath` cleans each path part on its
own, so no separator or drive colon survives, and trims dots and spaces from the end together.
Trimming only dots is not enough: the exe has no `TargetFramework` attribute, so .NET uses its
legacy path handling, which reads a part like `.. ` (dot dot space) as `..` - the review showed a
remote name could walk out of the staging folder that way. `StagingFolder.IsInside` is the second
lock: whatever the cleaning produced, nothing is written outside the batch folder.

**Install location.** `%LOCALAPPDATA%\Programs\RdpClipboardBridge` is the per-user programs folder:
writable without elevation and the place Windows expects per-user apps. Autostart is the
`HKCU\...\Run` value `RdpClipboardBridge` with the quoted path; the tray item is only ticked when it
points at the running exe, so a stale value from a moved exe does not count. Installing sets the Run
value either way: "Install only" removes an existing entry instead of leaving one that points at the
copy it was installed from, which would keep starting that copy and asking to be installed at every
logon. The settings live under `HKCU\Software\RdpClipboardBridge` (`Settings`): `InstallDeclined`
("run without installing"), `PauseOnMetered`, `SizeLimitMB` and one `Convert<Group>` per entry of
*File Types*, `ConvertFolders` among them. All are DWORDs. Installing clears only `InstallDeclined`; uninstalling removes the whole key.

**Delayed self-delete.** A running exe cannot delete itself. Uninstall starts a hidden `cmd /c` that
retries `del` and `rmdir` for a few seconds after we exit. Its working directory is `%TEMP%`, because
a cmd sitting in the install folder would keep the folder from being removed.

**Upgrade handshake.** A second start normally exits silently (single-instance mutex
`Local\RdpClipBridge_SingleInstance`). But a newer exe started from Downloads while another copy
runs must not just vanish, or upgrading looks like nothing happened. So a second instance that is
*not* at the install location asks, then broadcasts the registered window message
`RdpClipBridge_ExitRequest` (the hidden window is top-level and unowned, so a broadcast reaches it -
which is why the form does not set `ShowInTaskbar = false`) and waits until it can create the mutex
itself - the sign the old process is gone. Whether this is an upgrade is decided by whether the
*running* copy is the installed one (`Installation.IsInstalledCopyRunning`), not by whether an
installed exe exists: a standalone copy may be the one running. On an upgrade it copies itself over
the installed exe (retrying while the old image is still locked), sets the Run value to what it was
(`InstallFlow.Upgrade`) and starts it; otherwise it carries on as a first start would. The exit
request also reaches the install dialog of a copy that was only just started, which then exits
instead of going on to the tray (`SingleInstance.ExitRequested`). The mutex is only ever created,
never waited on, and is released before the installed copy is started, or that copy would find it
and quit. The tray menu does not open while one of the tool's own dialogs is open.

**Icon.** `src/app.ico` is generated by `tools/make-icon.py`, which draws every size from shapes so
the 16 px tray icon stays crisp instead of being scaled down - change the script and rerun
`python3 tools/make-icon.py`, never edit the .ico. `build.ps1` embeds it with `/win32icon`, which csc
stores as icon group 32512. The tray and the install dialog load it from there with `LoadImage` at
the size they need, so Windows picks the matching frame and the file is not embedded a second time as
a managed resource. Those handles live as long as the process, on purpose.

## Translations

All user-visible text lives in `Texts` at the end of the source, one `Dictionary<TextId, string>` per
language, keyed by two-letter ISO 639-1 code. The language is the Windows display language
(`CurrentUICulture`, reduced to its neutral parent: `de-CH` -> `de`, `pt-BR` -> `pt`, `zh-TW` ->
`zh`); there is no setting. English is the reference: a missing text, or a language that is not
listed, falls back to it.

* Placeholders are `string.Format` style (`{0}`), so each language orders them as its grammar wants.
  The product name is always passed in as one of them, never written into a text, so it stays
  untranslated. Exception messages from Windows are passed through as they are.
* Where a text names a menu item, the item's own text is passed in (`ErrorRunningCopyDidNotExit`
  gets `MenuExit`), so the two cannot drift apart.
* There is no right-to-left handling, because no right-to-left language is shipped. Hindi, Bengali,
  Arabic and Urdu were removed again: nobody could vouch for the translations. Adding Arabic or
  Hebrew means mirroring the install dialog and the tray menu (`RightToLeft`, `RightToLeftLayout`)
  and passing `MessageBoxOptions.RtlReading | RightAlign`.
* `Application.SetCompatibleTextRenderingDefault(false)` matters: GDI+ text rendering measures and
  shapes non-Latin scripts poorly.
* The install dialog stacks its buttons in one column under a text wrapped at a fixed width, so the
  longer German or Russian labels do not stretch it.

To add a language: copy the `"en"` block inside `Languages`, change the key to the two-letter code
and translate the texts, keeping every `{n}` of the English text. Use the wording Windows itself uses
in that language for Remote Desktop, clipboard, install and exit. That one block is all - no other
code changes, unless the language is right-to-left (see above).

## How to verify

There is no test suite; the exe cannot run on Linux. From WSL you can still reach the Windows side,
which is enough to compile:

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w build.ps1)" -Version v1.2.3
```

Do not launch the exe from an agent session: it pops dialogs, can install itself, writes to `HKCU`
and rewrites the clipboard of whoever is logged in.

Pure logic can be probed without launching it: load the built exe as an assembly in PowerShell and
call internal static methods by reflection. Loading does not run `Main`. This was done for the file
types, descriptor parsing and the name cleaning; stay away from anything that touches the clipboard
or writes the registry.

```powershell
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$PWD\bin\RdpClipboardBridge.exe"))
$static = [Reflection.BindingFlags]'Static, Public, NonPublic'
$assembly.GetType('StagingFolder').GetMethod('ToLocalRelativePath', $static).Invoke($null, [object[]]@('.. \..\x.txt'))
$assembly.GetType('FileTypes').GetMethod('Of', $static).Invoke($null, [object[]]@('setup.ISO')).SettingName
```

After changing texts, run `python3 tools/check-texts.py`: it checks per language that every id is
present, that each text has exactly the `{n}` placeholders of the English one, that nothing was
left in English by mistake, and that every `TrayTooltip*` text fits the 63 characters `NotifyIcon`
takes (with the product name and `999` filled in). A text that is rightly the same as in English
(`Audio`, `OK`, say) goes into its `INTENDED_SAME`.

By hand on Windows:

* Copy one file and several files in an RDP session; switch to New Outlook and paste. Tick
  *File Types > Folders* and do the same with a folder.
* Cut and paste a file inside the session: it must still move, and the clipboard must stay as
  Remote Desktop made it - the download into `%TEMP%\RDPClip` happens anyway, that is expected.
* Copy in the session, switch to a local app, paste there; then go back to the session and paste
  (expected: copied from the PC, a cut is only a copy).
* Copy something else during a long transfer; pause and exit during one.
* Untick a file type, copy a file of that type together with another one: neither is converted,
  and the tray tooltip says "file type".
* Copy more than the size limit in total (several files below it each): nothing is downloaded,
  the tooltip names the limit. Change the limit in its dialog with Enter, leave it with Esc and
  with the X; the menu item shows the stored value.
* With the tool installed on both ends of one connection, copy in either direction: no endless
  back-and-forth.
* Install, toggle autostart, "Install only" over an autostart install, upgrade with a newer exe
  while the installed one runs, uninstall.
* Switch the display language to German once to see the dialog, the menu and a message box.

## Code philosophy

Write for the next developer. Names carry the intent, methods stay short and single-purpose. If a
comment explains *what* code does, rename it instead. Comments explaining *why* are wanted, and the
ones about mstsc, OLE and clipboard behaviour each cost someone a debugging session - keep them.
