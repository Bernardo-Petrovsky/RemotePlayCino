$ErrorActionPreference = 'Stop'
$version = '3.4.20'
$expected = '368BED8B24E44AD9EA372990814F2343FCFB9E468ABB7F8F4A3BC146C7304752'
$destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\build\native\sdl3-$version"))
$url = "https://github.com/libsdl-org/SDL/releases/download/release-$version/SDL3-$version-win32-x64.zip"
Add-Type -AssemblyName System.IO.Compression
$client = [Net.Http.HttpClient]::new()
try {
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('RemotePlay-native-dependencies')
    $bytes = $client.GetByteArrayAsync($url).GetAwaiter().GetResult()
    $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if ($actual -ne $expected) { throw 'SDL archive integrity check failed.' }
    $memory = [IO.MemoryStream]::new($bytes, $false)
    try {
        $archive = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read)
        try {
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            foreach ($fileName in @('SDL3.dll','LICENSE.txt')) {
                $entry = @($archive.Entries | Where-Object { $_.Name -eq $fileName })
                if ($entry.Count -ne 1) { throw "SDL dependency entry is ambiguous: $fileName" }
                $targetName = if ($fileName -eq 'LICENSE.txt') { 'SDL3-LICENSE.txt' } else { $fileName }
                $source = $entry[0].Open()
                try {
                    $target = [IO.File]::Create((Join-Path $destination $targetName))
                    try { $source.CopyTo($target) } finally { $target.Dispose() }
                } finally { $source.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $memory.Dispose() }
} finally { $client.Dispose() }
[pscustomobject]@{Version=$version;ArchiveSHA256=$expected;Directory=$destination;SavedZip=$false} | ConvertTo-Json
