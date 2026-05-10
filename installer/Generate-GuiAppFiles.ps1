# Generate-GuiAppFiles.ps1
# Generates a WiX v6 ComponentGroup wxs file from all files under AppDir.
# Used as a pre-build step in BootstrapMate.Installer.wixproj to work around the
# WiX v6 SDK NuGet mode not supporting the HarvestDirectory MSBuild item
# (HarvestDirectory only works when WiX is installed as a .NET SDK workload, which
# is not available for .NET 10).

param(
    [Parameter(Mandatory)][string]$AppDir,
    [Parameter(Mandatory)][string]$OutputPath
)

$appDir = $AppDir.TrimEnd('\', '/')

if (-not (Test-Path $appDir)) {
    Write-Error "AppDir does not exist: $appDir"
    exit 1
}

$outputDir = [System.IO.Path]::GetDirectoryName($OutputPath)
if ($outputDir -and -not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('<?xml version="1.0" encoding="UTF-8"?>')
$lines.Add('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
$lines.Add('  <Fragment>')
$lines.Add('    <ComponentGroup Id="GuiAppFiles" Directory="INSTALLDIR">')

$files = Get-ChildItem -Path $appDir -Recurse -File | Sort-Object FullName
$sha1  = [System.Security.Cryptography.SHA1]::Create()
foreach ($file in $files) {
    $relPath = $file.FullName.Substring($appDir.Length + 1)
    $relDir  = [System.IO.Path]::GetDirectoryName($relPath)

    # Deterministic IDs derived from a SHA1 hash of the relative path so that
    # unchanged files keep the same Component Id (and therefore the same
    # Guid="*"-derived GUID) across builds. Sequential IDs would shift whenever
    # files are added/removed (e.g. WinUI 3 ships a new locale), regenerating
    # most GUIDs and breaking MSI upgrade tracking — orphaning old components
    # or duplicating files at the same path.
    $hashHex = [System.BitConverter]::ToString($sha1.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($relPath.ToLowerInvariant()))).Replace('-','').Substring(0,16).ToLowerInvariant()
    $compId  = "c_$hashHex"
    $fileId  = "f_$hashHex"

    if ($relDir) {
        $lines.Add("      <Component Id=`"$compId`" Guid=`"*`" Subdirectory=`"$relDir`">")
    } else {
        $lines.Add("      <Component Id=`"$compId`" Guid=`"*`">")
    }
    $lines.Add("        <File Id=`"$fileId`" Source=`"$($file.FullName)`" KeyPath=`"yes`" />")
    $lines.Add("      </Component>")
}

$lines.Add('    </ComponentGroup>')
$lines.Add('  </Fragment>')
$lines.Add('</Wix>')

[System.IO.File]::WriteAllLines($OutputPath, $lines)
Write-Host "Generated GuiAppFiles.wxs: $($files.Count) files harvested from $appDir"
