# Compiles RdpClipboardBridge.exe with the csc that ships with .NET Framework 4, so there is no sdk to
# install - not locally and not on the github runner.
#
#   .\build.ps1                 -> version 0.0.0
#   .\build.ps1 -Version v1.2.3 -> version 1.2.3 stamped into the exe

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

$assemblyInfo = 'src\AssemblyInfo.g.cs'
@(
  '[assembly: System.Reflection.AssemblyTitle("RDP Clipboard Bridge")]',
  '[assembly: System.Reflection.AssemblyCompany("Enertech AG")]',
  "[assembly: System.Reflection.AssemblyVersion(""${Version}.0"")]",
  "[assembly: System.Reflection.AssemblyFileVersion(""${Version}.0"")]"
) | Set-Content $assemblyInfo

# /codepage:65001 because the source holds the translations as UTF-8 without a BOM, and this csc
# would otherwise read such a file in the machine's ANSI code page - garbling every non-ASCII text,
# differently on every machine.
try {
  & $csc /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror+ /codepage:65001 `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /win32manifest:src\app.manifest /win32icon:src\app.ico "/out:$Output" `
    src\RdpClipboardBridge.cs $assemblyInfo
  if ($LASTEXITCODE -ne 0) { throw 'compile failed' }
}
finally {
  Remove-Item $assemblyInfo -ErrorAction SilentlyContinue
}

Write-Output "built $Output (version $Version, $((Get-Item $Output).Length) bytes)"
