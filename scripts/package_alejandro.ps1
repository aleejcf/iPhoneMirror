[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DotnetPath,
    [Parameter(Mandatory)][string]$BasePackageDirectory,
    [Parameter(Mandatory)][string]$NativeCorePath,
    [Parameter(Mandatory)][string]$InnoCompiler,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$BasePackageDirectory = [IO.Path]::GetFullPath($BasePackageDirectory)
$NativeCorePath = [IO.Path]::GetFullPath($NativeCorePath)
$DotnetPath = [IO.Path]::GetFullPath($DotnetPath)
$InnoCompiler = [IO.Path]::GetFullPath($InnoCompiler)
# This edition reuses Alejandro's supplied native build; do not silently
# substitute the upstream binary, which lacks his native protocol changes.
$expectedCore = 'da3c9130c5d8852f006c27225d5475703401b3876f16bdfae9adec301b2605c7'
if ((Get-FileHash -LiteralPath $NativeCorePath).Hash.ToLowerInvariant() -ne $expectedCore) {
    throw 'La biblioteca nativa no coincide con la compilación personalizada verificada.'
}
foreach ($required in 'iPhoneMirror.Driver.exe','tools\iUsbBridge.runtime.json','Wireless\iPhoneMirror.WirelessHost.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $BasePackageDirectory $required))) { throw "Falta $required" }
}
$publish = Join-Path $repo 'outputs\alejandro-publish'
$payload = Join-Path $OutputDirectory 'iPhoneMirror-Alejandro'
if (Test-Path -LiteralPath $payload) { throw "Use una carpeta de entrega nueva: $payload" }
New-Item -ItemType Directory -Force $publish,$payload | Out-Null
& $DotnetPath publish (Join-Path $repo 'src\App\iPhoneMirror.App.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=false' "-p:UsbBridgeRoot=$(Join-Path $BasePackageDirectory 'tools')" -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la interfaz.' }
$compiledVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'iPhoneMirror.dll')).ProductVersion
if ($compiledVersion -notlike '1.8.5-pre3.alejandro.1*') {
    throw "Se detectaron resultados antiguos ($compiledVersion). Extraiga las fuentes en una carpeta nueva, sin bin/obj, y repita."
}
Copy-Item (Join-Path $BasePackageDirectory '*') $payload -Recurse -Force
Copy-Item (Join-Path $publish '*') $payload -Recurse -Force
Copy-Item $NativeCorePath (Join-Path $payload 'iPhoneMirror.Core.dll') -Force
Copy-Item (Join-Path $repo 'installer\LEEME-Alejandro.txt') (Join-Path $payload 'LEEME.txt')
& $InnoCompiler "/DPayloadDir=$payload" "/DDeliveryDir=$OutputDirectory" (Join-Path $repo 'installer\alejandro.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falló la creación del instalador.' }
Compress-Archive -LiteralPath $payload -DestinationPath (Join-Path $OutputDirectory 'iPhoneMirror-Alejandro-Portable-x64.zip') -CompressionLevel Optimal
Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object Extension -in '.zip','.exe' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
