param(
    [string]$Version = "0.4.0.0",
    [string]$Repository = "thehearthz/jellyfin-plugin-library-tv",
    [string]$OutputDirectory = "artifacts"
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Use a four-part numeric version.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
New-Item -ItemType Directory -Path $output -Force | Out-Null
dotnet build (Join-Path $projectRoot 'src/LibraryTV/LibraryTV.csproj') -c Release "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$assembly = Join-Path $projectRoot 'src/LibraryTV/bin/Release/net10.0/Jellyfin.Plugin.LibraryTV.dll'
$archive = Join-Path $output "library-tv-$Version.zip"
Compress-Archive -LiteralPath $assembly -DestinationPath $archive -Force
$entry = [ordered]@{
    guid = '512a2a0d-5101-4fe8-9651-484f077a601a'
    name = 'Library TV'
    overview = 'Always-on channels from your own libraries'
    description = 'Experimental virtual television with persistent episode order, time blocks, automatic library channels, and commercial breaks. No idle encoding. Stream copy requires compatible media; compatibility encoding costs CPU.'
    owner = $Repository.Split('/')[0]
    category = 'Live TV'
    versions = @([ordered]@{
        version = $Version
        changelog = 'Consolidated update: bounded missing-media recovery, hardened FFmpeg cleanup, safe future-slot rebuilding, private-token-free settings backup/restore, detailed channel diagnostics, guide size limits, and real transport tests. Includes all previous builder, numbering and deferred-startup improvements.'
        targetAbi = '12.2.0.0'
        sourceUrl = "https://github.com/$Repository/releases/download/v$Version/library-tv-$Version.zip"
        checksum = (Get-FileHash -LiteralPath $archive -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    })
}
# Jellyfin's catalog requires MD5; SHA-256 is also published for independent verification.
ConvertTo-Json -InputObject @($entry) -Depth 20 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8NoBOM
"$((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS') -Encoding utf8NoBOM
Write-Output "Packaged $archive"
