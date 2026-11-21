param([switch]$IgnoreRequestFile)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$request = Join-Path $workspace 'build-request.txt'
if (-not $IgnoreRequestFile -and -not (Test-Path -LiteralPath $request)) { throw 'No queda una solicitud de compilacion pendiente.' }
$work = Join-Path $workspace ('build\compilacion-' + [guid]::NewGuid().ToString('N').Substring(0,8))
[System.IO.Directory]::CreateDirectory($work) | Out-Null
if (-not $IgnoreRequestFile) {
    # Request text is a label only. It is never interpolated into commands or output paths.
    Move-Item -LiteralPath $request -Destination (Join-Path $work 'solicitud.txt')
}
& (Join-Path $PSScriptRoot 'Compilar-portable.ps1') -WorkDirectory $work
if (-not $?) { throw 'Fallo la compilacion portable.' }
