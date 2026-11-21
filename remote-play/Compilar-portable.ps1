param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot 'RemotePlay.csproj'),
    [string]$DependenciesDirectory = '',
    [string]$OutputPath = '',
    [string]$PublicationDirectory = '',
    [string]$WorkDirectory = (Join-Path $PSScriptRoot ('..\build\portable-' + [guid]::NewGuid().ToString('N').Substring(0,8)))
)
$ErrorActionPreference = 'Stop'
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\dist'))
[xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
$version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'La version del proyecto no tiene un formato valido.' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $distRoot ("RemotePlayCino V $version\RemotePlayCino.exe")
}
if ([string]::IsNullOrWhiteSpace($DependenciesDirectory)) {
    $previous = Get-ChildItem -LiteralPath $distRoot -Directory |
        Where-Object { $_.Name -like 'RemotePlayCino V*' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'ffmpeg\ffmpeg.exe')) } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($previous) { $DependenciesDirectory = $previous.FullName }
    elseif (Test-Path -LiteralPath (Join-Path $distRoot 'ffmpeg\ffmpeg.exe')) { $DependenciesDirectory = $distRoot }
    else { throw 'Indica la carpeta con las dependencias mediante -DependenciesDirectory.' }
}
$workRoot = [System.IO.Path]::GetFullPath($WorkDirectory)
if ([string]::IsNullOrWhiteSpace($PublicationDirectory)) { $PublicationDirectory = Join-Path $workRoot 'publicacion' }
$payloadDirectory = Join-Path $workRoot 'dependencias'
[System.IO.Directory]::CreateDirectory($payloadDirectory) | Out-Null
$files = @('msquic.dll','SDL3.dll','SDL3-LICENSE.txt','Wired-Pro2-LICENSE.txt','ffmpeg/ffmpeg.exe','ffmpeg/LICENSE','ffmpeg/README.txt')
$manifest = @(foreach ($name in $files) {
    $source = Join-Path $DependenciesDirectory $name
    $target = Join-Path $payloadDirectory $name
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    [pscustomobject]@{ Name=$name; Size=(Get-Item -LiteralPath $target).Length; Sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[System.IO.File]::WriteAllText((Join-Path $payloadDirectory 'manifest.json'), ($manifest | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
$payload = Join-Path $workRoot ('dependencias-' + [guid]::NewGuid().ToString('N').Substring(0,8) + '.zip')
[System.IO.Compression.ZipFile]::CreateFromDirectory($payloadDirectory, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$publishDirectory = Join-Path $workRoot 'publicado'
$workspaceRoot = Split-Path $PSScriptRoot -Parent
& dotnet publish $ProjectPath -c Release -r win-x64 --self-contained true '-p:RuntimeFrameworkVersion=10.0.2' '-p:PublishSingleFile=true' '-p:EnableCompressionInSingleFile=true' "-p:PortablePayloadPath=$payload" "-p:PathMap=$workspaceRoot=C:\build" -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el EXE portatil.' }
foreach ($name in @('msquic.dll','SDL3.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $publishDirectory $name) -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $payloadDirectory $name) -Algorithm SHA256).Hash) { throw "La biblioteca compilada difiere del contenido portatil: $name" }
}
$destination = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDirectory 'RemotePlayCino.exe') -Destination $destination
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($destination + '.sha256', $hash, [System.Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Executable=$destination; Version=(Get-Item -LiteralPath $destination).VersionInfo.ProductVersion; SHA256=$hash; Bytes=(Get-Item -LiteralPath $destination).Length; Payload=$payload } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workRoot 'portable-compilado.json') -Encoding utf8
Write-Output ('Portable listo: ' + $destination)
& (Join-Path $PSScriptRoot 'Preparar-publicacion.ps1') -ExecutablePath $destination -DestinationDirectory $PublicationDirectory -ExpectedVersion $version
