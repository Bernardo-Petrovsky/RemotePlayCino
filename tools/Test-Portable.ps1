param([Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [Parameter(Mandatory=$true)][string]$ManifestPath)
$ErrorActionPreference = 'Stop'
$source = (Get-Item -LiteralPath $Executable).FullName
$root = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a new, empty smoke-test directory.' }
New-Item -ItemType Directory -Path $root,(Join-Path $root 'Datos') -Force | Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $root 'RemotePlayCino.exe')
# A private fixture prevents import of the Windows user's legacy preferences.
[IO.File]::WriteAllText((Join-Path $root 'Datos/.perfil-portatil'), "CI isolated profile`n")
[IO.File]::WriteAllText((Join-Path $root 'Datos/settings.json'), '{"ClientName":"CI isolated profile"}')
[IO.File]::WriteAllText((Join-Path $root 'Datos/estilo.txt'), 'Grafito')
$before = @(Get-ChildItem -LiteralPath (Join-Path $root 'Datos') -File -Force | ForEach-Object {
    [pscustomobject]@{Name=$_.Name;Hash=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $root 'RemotePlayCino.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.ArgumentList.Add('--portable-check')
$process = [Diagnostics.Process]::Start($start)
try {
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Portable preparation timed out.' }
    if ($process.ExitCode -ne 0) { throw "Portable preparation failed: $($process.ExitCode)" }
} finally { $process.Dispose() }
$manifest = @(Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json)
if ($manifest.Count -eq 0) { throw 'The payload manifest is missing.' }
foreach ($file in $manifest) {
    $path = Join-Path $root $file.Name
    if ((Get-Item -LiteralPath $path).Length -ne $file.Size -or (Get-FileHash -LiteralPath $path).Hash -ine $file.Sha256) {
        throw "Extracted dependency does not match: $($file.Name)"
    }
}
foreach ($file in $before) {
    if ((Get-FileHash -LiteralPath (Join-Path $root ('Datos/'+$file.Name))).Hash -ne $file.Hash) { throw 'Portable preparation changed a profile fixture.' }
}
Write-Output 'Portable preparation verified: dependencies match; preferences preserved; no GUI or sharing started.'
