# Compiles RdpClipboardBridge.exe with the csc that ships with .NET Framework 4, so there is no sdk to
# install - not locally and not on the github runner.
#
#   .\build.ps1                 -> version 0.0.0
#   .\build.ps1 -Version v1.2.3 -> version 1.2.3 stamped into the exe
#
# Every .cs file under src\ is compiled, and every src\lang\<code>.txt is embedded as the resource
# lang.<code>.txt - adding a source file or a language needs no change here.

param(
  [string]$Version = '0.0.0',
  [string]$Output = 'bin\RdpClipboardBridge.exe'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$outputDirectory = Split-Path -Parent $Output
if ($outputDirectory -and -not (Test-Path $outputDirectory)) {
  New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

if ($Version -match '^v?(\d+\.\d+\.\d+)') { $Version = $Matches[1] } else { $Version = '0.0.0' }

# English is the reference every other language falls back to, so the exe is not built without it.
# The file name is the two-letter code Texts looks the display language up by.
$languages = @(Get-ChildItem -Path 'src\lang' -Filter '*.txt' -File | Sort-Object Name)
if (-not ($languages | Where-Object { $_.Name -ceq 'en.txt' })) { throw 'src\lang\en.txt is missing - English is the reference and must be there' }
foreach ($language in $languages) {
  if ($language.BaseName -cnotmatch '^[a-z]{2}$') { throw "src\lang\$($language.Name): a language file is named after its lower-case two-letter ISO 639-1 code, like de.txt" }
}
$resources = $languages | ForEach-Object { "/resource:src\lang\$($_.Name),lang.$($_.Name)" }

# Written outside src\, so the /recurse below does not pick it up a second time, and removed after.
$assemblyInfo = Join-Path ([IO.Path]::GetTempPath()) "RdpClipboardBridge.AssemblyInfo.$PID.g.cs"
@(
  '[assembly: System.Reflection.AssemblyTitle("RDP Clipboard Bridge")]',
  '[assembly: System.Reflection.AssemblyCompany("Enertech AG")]',
  "[assembly: System.Reflection.AssemblyVersion(""${Version}.0"")]",
  "[assembly: System.Reflection.AssemblyFileVersion(""${Version}.0"")]"
) | Set-Content $assemblyInfo

# /codepage:65001 because the sources are UTF-8 without a BOM, and this csc would otherwise read such
# a file in the machine's ANSI code page - garbling every non-ASCII character, differently on every
# machine. The language files are embedded as they are and read as UTF-8 at run time.
try {
  & $csc /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror+ /codepage:65001 `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /win32manifest:src\app.manifest /win32icon:src\app.ico "/out:$Output" `
    $resources /recurse:src\*.cs $assemblyInfo
  if ($LASTEXITCODE -ne 0) { throw 'compile failed' }
}
finally {
  Remove-Item $assemblyInfo -ErrorAction SilentlyContinue
}

Write-Output "built $Output (version $Version, $((Get-Item $Output).Length) bytes, languages: $(($languages | ForEach-Object { $_.BaseName }) -join ' '))"
