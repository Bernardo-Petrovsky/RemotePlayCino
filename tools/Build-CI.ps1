param([string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$work = Join-Path $root 'build/ci'
$project = Join-Path $root 'remote-play/RemotePlay.csproj'
$publication = Join-Path $work 'publication'
Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'Get-CIDependencies.ps1') -RepositoryRoot $root
    & dotnet restore $project -r win-x64 --locked-mode '-p:RuntimeFrameworkVersion=10.0.2' '-p:PublishSingleFile=true'
    if ($LASTEXITCODE -ne 0) { throw 'Locked NuGet restore failed.' }
    & dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore `
        '-p:RuntimeFrameworkVersion=10.0.2' "-p:PathMap=$root=C:\build" -o (Join-Path $work 'bootstrap')
    if ($LASTEXITCODE -ne 0) { throw 'Native bootstrap build failed.' }
    Copy-Item -LiteralPath (Join-Path $work 'bootstrap/msquic.dll') -Destination (Join-Path $work 'dependencies/msquic.dll')
    & (Join-Path $root 'remote-play/Compilar-portable.ps1') -ProjectPath $project `
        -DependenciesDirectory (Join-Path $work 'dependencies') -WorkDirectory (Join-Path $work 'portable') `
        -OutputPath (Join-Path $work 'output/RemotePlayCino.exe') -PublicationDirectory $publication
    & (Join-Path $PSScriptRoot 'Test-Portable.ps1') -Executable (Join-Path $publication 'RemotePlayCino.exe') `
        -ManifestPath (Join-Path $work 'portable/dependencias/manifest.json') `
        -WorkDirectory (Join-Path $work ('smoke-'+[Guid]::NewGuid().ToString('N').Substring(0,8)))
    $files = @(Get-ChildItem -LiteralPath $publication -Force)
    if ($files.Count -ne 2 -or @($files | Where-Object Name -notin @('RemotePlayCino.exe','RemotePlayCino.exe.sha256')).Count -gt 0) {
        throw 'Only EXE and SHA-256 may be published.'
    }
    Write-Output 'CI publication ready: EXE and SHA-256 only. This build is unsigned until a signing service is configured.'
} finally { Pop-Location }
