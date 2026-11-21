param([string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$cache = Join-Path $root 'build/downloads'
$sdl = Join-Path $root 'build/native/sdl3-3.4.20'
$deps = Join-Path $root 'build/ci/dependencies'
New-Item -ItemType Directory -Path $cache,$sdl,(Join-Path $deps 'ffmpeg') -Force | Out-Null

function Get-VerifiedArchive([string]$Url, [string]$Name, [string]$Sha256) {
    $path = Join-Path $cache $Name
    if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ine $Sha256) {
        Invoke-WebRequest -Uri $Url -OutFile $path -TimeoutSec 240
    }
    if ((Get-FileHash -LiteralPath $path).Hash -ine $Sha256) { throw "Archive checksum mismatch: $Name" }
    return $path
}

function Copy-ArchiveEntry($Archive, [string]$Pattern, [string]$Target) {
    $entries = @($Archive.Entries | Where-Object { $_.FullName -like $Pattern })
    if ($entries.Count -ne 1) { throw "Ambiguous or missing dependency: $Pattern" }
    $inputStream = $entries[0].Open()
    try {
        $outputStream = [IO.File]::Create($Target)
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
}

$sdlArchive = Get-VerifiedArchive `
    'https://github.com/libsdl-org/SDL/releases/download/release-3.4.20/SDL3-3.4.20-win32-x64.zip' `
    'SDL3-3.4.20-win32-x64.zip' '368bed8b24e44ad9ea372990814f2343fcfb9e468abb7f8f4a3bc146c7304752'
$archive = [IO.Compression.ZipFile]::OpenRead($sdlArchive)
try {
    Copy-ArchiveEntry $archive '*SDL3.dll' (Join-Path $sdl 'SDL3.dll')
    Copy-ArchiveEntry $archive '*LICENSE.txt' (Join-Path $sdl 'SDL3-LICENSE.txt')
} finally { $archive.Dispose() }
Copy-Item -LiteralPath (Join-Path $sdl 'SDL3.dll'),(Join-Path $sdl 'SDL3-LICENSE.txt') -Destination $deps

$ffmpegArchive = Get-VerifiedArchive `
    'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip' `
    'ffmpeg-9.0.2-essentials_build.zip' '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$archive = [IO.Compression.ZipFile]::OpenRead($ffmpegArchive)
try {
    Copy-ArchiveEntry $archive '*/bin/ffmpeg.exe' (Join-Path $deps 'ffmpeg/ffmpeg.exe')
    Copy-ArchiveEntry $archive '*/LICENSE' (Join-Path $deps 'ffmpeg/LICENSE')
    Copy-ArchiveEntry $archive '*/README.txt' (Join-Path $deps 'ffmpeg/README.txt')
} finally { $archive.Dispose() }
if ((Get-FileHash -LiteralPath (Join-Path $deps 'ffmpeg/ffmpeg.exe')).Hash -ine
    '3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec') { throw 'FFmpeg binary differs from the pinned build.' }
Copy-Item -LiteralPath (Join-Path $root 'remote-play/Wired-Pro2-LICENSE.txt') -Destination $deps
Write-Output 'Pinned SDL and FFmpeg dependencies verified.'
