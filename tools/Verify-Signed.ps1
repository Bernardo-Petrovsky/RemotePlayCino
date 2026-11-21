param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $Directory 'RemotePlayCino.exe'
$signature = Get-AuthenticodeSignature -LiteralPath $exe
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=SignPath Foundation(,|$)') {
    throw 'The downloaded EXE does not have a valid SignPath Foundation signature.'
}
$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Split('+')[0]
[xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../remote-play/RemotePlay.csproj') -Raw
$expected = @($project.Project.PropertyGroup | ForEach-Object Version | Where-Object { $_ })[0]
if ($version -ne $expected) { throw 'Signed executable version mismatch.' }
$hash = (Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($exe+'.sha256', $hash, [Text.UTF8Encoding]::new($false))
Write-Output "Valid signature and signed EXE SHA-256 verified for $version."
