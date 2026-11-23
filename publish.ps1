# Builds the two ways Glance is handed out:
#
#   dist\Glance.exe          Small (under 1 MB). Needs the .NET 10 Desktop Runtime, which Windows offers to
#                            download the first time if it is missing. Best for your own PC.
#   dist\share\Glance.exe    Everything included (about 70 MB). Runs on any 64-bit Windows 10 or 11 PC with
#                            nothing installed. This is the one to send to other people.
#
# Usage:  .\publish.ps1            both
#         .\publish.ps1 -Small     only the small one

param([switch]$Small)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Glance.csproj'
$dist = Join-Path $PSScriptRoot 'dist'

dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'The small build failed. If Glance.exe is running from dist, exit it first.' }

if (-not $Small) {
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -o (Join-Path $dist 'share') -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The self-contained build failed.' }
}

Get-ChildItem $dist -Recurse -Filter Glance.exe | ForEach-Object {
    '{0,-28} {1,8:N1} MB' -f $_.FullName.Substring($dist.Length + 1), ($_.Length / 1MB)
}
