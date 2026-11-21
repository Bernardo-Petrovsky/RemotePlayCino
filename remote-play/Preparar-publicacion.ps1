param(
    [Parameter(Mandatory=$true)][string]$ExecutablePath,
    [Parameter(Mandatory=$true)][string]$DestinationDirectory,
    [string]$ExpectedVersion = ''
)
$ErrorActionPreference = 'Stop'
$source = (Get-Item -LiteralPath $ExecutablePath).FullName
$version = (Get-Item -LiteralPath $source).VersionInfo.ProductVersion.Split('+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'El EXE no tiene una version valida.' }
if ($ExpectedVersion -and $version -ne $ExpectedVersion) { throw 'La version interna del EXE no coincide con el proyecto.' }
$directory = [System.IO.Path]::GetFullPath($DestinationDirectory)
$allowed = @('RemotePlayCino.exe','RemotePlayCino.exe.sha256')
if (Test-Path -LiteralPath $directory) {
    if (Get-ChildItem -LiteralPath $directory -Force | Where-Object { $_.PSIsContainer -or $_.Name -notin $allowed }) {
        throw 'La carpeta de publicacion contiene otros archivos. Usa una carpeta limpia para no compartir datos personales.'
    }
}
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$target = Join-Path $directory 'RemotePlayCino.exe'
if ($source -ine $target) { Copy-Item -LiteralPath $source -Destination $target }
$hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($target + '.sha256', $hash, [System.Text.UTF8Encoding]::new($false))
if ((Get-ChildItem -LiteralPath $directory -Force).Count -ne 2) { throw 'El paquete debe contener solo EXE y SHA-256.' }
Write-Output ('TAG=v' + $version)
Write-Output ('PUBLICACION=' + $directory)
Write-Output ('EXE=' + $target)
Write-Output ('SHA256=' + $hash)
