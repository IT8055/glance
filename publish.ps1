# Builds the two ways Glance is handed out:
#
#   dist\Glance.exe          Small (under 1 MB). Needs the .NET 10 Desktop Runtime, which Windows offers to
#                            download the first time if it is missing. Best for your own PC.
#   dist\share\Glance.exe    Everything included (about 70 MB). Runs on any 64-bit Windows 10 or 11 PC with
#                            nothing installed. This is the one to send to other people.
#
# Usage:  .\publish.ps1                      both
#         .\publish.ps1 -Small               only the small one
#         .\publish.ps1 -Sign <thumbprint>   also sign both with a code-signing certificate
#
# About signing: an unsigned exe makes Windows SmartScreen warn people the first time they run it
# ("Windows protected your PC", then More info, Run anyway). The only cure is a code-signing
# certificate bought from a certificate authority. Once you have one installed, pass its thumbprint
# with -Sign. A certificate you make yourself does not remove the warning on other people's PCs.

param([switch]$Small, [string]$Sign)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Glance.csproj'
$dist = Join-Path $PSScriptRoot 'dist'

# If Glance is running from dist its exe cannot be overwritten, but Windows does allow renaming it.
# The running copy carries on untouched from the renamed file, and the new build takes the usual name:
# exit Glance and start it again to switch over.
$current = Join-Path $dist 'Glance.exe'
$previous = Join-Path $dist 'Glance.previous.exe'
$inUse = $false
if (Test-Path $current) {
    try { [System.IO.File]::Open($current, 'Open', 'ReadWrite', 'None').Dispose() } catch { $inUse = $true }
}
if ($inUse) {
    if (Test-Path $previous) { try { [System.IO.File]::Delete($previous) } catch { throw 'Glance.previous.exe is also still running. Exit that older copy first.' } }
    Rename-Item $current 'Glance.previous.exe'
    'Glance is running, so its exe was renamed to Glance.previous.exe. Exit Glance and start it again to use the new build.'
}
elseif (Test-Path $previous) {
    try { [System.IO.File]::Delete($previous) } catch { }
}

dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'The small build failed.' }

if (-not $Small) {
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -o (Join-Path $dist 'share') -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The self-contained build failed.' }
}

$built = @(Join-Path $dist 'Glance.exe')
if (-not $Small) { $built += Join-Path $dist 'share\Glance.exe' }

if ($Sign) {
    # signtool comes with the Windows SDK. Use the newest one installed.
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe was not found. Install the Windows SDK (the "Signing Tools" part is enough).' }

    foreach ($exe in $built) {
        & $signtool.FullName sign /sha1 $Sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /d Glance $exe
        if ($LASTEXITCODE -ne 0) { throw "Signing failed for $exe." }
    }
}

foreach ($exe in $built) {
    $signature = (Get-AuthenticodeSignature $exe).Status
    '{0,-28} {1,8:N1} MB   {2}' -f $exe.Substring($dist.Length + 1), ((Get-Item $exe).Length / 1MB),
        $(if ($signature -eq 'Valid') { 'signed' } else { 'not signed' })
}
