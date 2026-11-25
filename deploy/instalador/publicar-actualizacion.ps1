#Requires -Version 7
# Publica una versión nueva del worker en Acusa Central. Cada worker instalado la descarga, verifica la firma y se actualiza solo.
# La firma usa la llave privada de quien publica (%APPDATA%\Acusa\firma-actualizaciones.dat, cifrada con DPAPI de este usuario de
# Windows); Central solo guarda y distribuye. La versión es la del proyecto (Acusa.Sdk.Worker.csproj).
#
#   .\publicar-actualizacion.ps1 -Central https://acusa-i331.onrender.com -Notas "Corrige X"
#   .\publicar-actualizacion.ps1 -SoloEmpaquetar          (arma y firma el paquete sin subirlo)
#
# La clave de administración de Central se lee de ACUSA_CLAVE_ADMIN o se pide en pantalla sin mostrarla.
param(
    [string]$Central = 'https://acusa-i331.onrender.com',
    [string]$Notas = '',
    [switch]$SinConstruir,
    [switch]$SoloEmpaquetar,
    [string]$Salida = (Join-Path $PSScriptRoot '..\..\dist\actualizaciones')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$archivos = 'Acusa.Sdk.Worker.exe', 'Acusa.Worker.Desktop.exe', 'appsettings.json', 'acusa.ico', 'instalar.ps1', 'servicio.ps1'
$trabajo = Join-Path $env:TEMP 'acusa-instalador'

if (-not $SinConstruir) {
    & (Join-Path $PSScriptRoot 'construir-instalador.ps1') | Out-Host
}
foreach ($a in $archivos) { if (-not (Test-Path (Join-Path $trabajo $a))) { throw "Falta $a en $trabajo. Ejecute sin -SinConstruir." } }

$version = ((Get-Item (Join-Path $trabajo 'Acusa.Sdk.Worker.exe')).VersionInfo.ProductVersion -split '\+')[0]
New-Item -ItemType Directory -Force $Salida | Out-Null
$zip = Join-Path (Resolve-Path $Salida) "acusa-worker-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path ($archivos | ForEach-Object { Join-Path $trabajo $_ }) -DestinationPath $zip -CompressionLevel Optimal

$bytes = [IO.File]::ReadAllBytes($zip)
$sha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
$texto = "acusa-worker|$version|$sha256|$($bytes.Length)"

$llave = Join-Path $env:APPDATA 'Acusa\firma-actualizaciones.dat'
if (-not (Test-Path $llave)) { throw "No se encontró la llave de firma en $llave. Solo se publica desde el equipo que tiene la llave." }
$pkcs8 = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($llave), [Text.Encoding]::UTF8.GetBytes('acusa-firma'), 'CurrentUser')
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportPkcs8PrivateKey($pkcs8, [ref]$null)
    $firma = [Convert]::ToBase64String($rsa.SignData([Text.Encoding]::UTF8.GetBytes($texto), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss))
} finally {
    $rsa.Dispose(); [Array]::Clear($pkcs8, 0, $pkcs8.Length)
}
Set-Content "$zip.firma" $firma -Encoding ascii
Write-Output ("Paquete {0}: {1:N1} MB, SHA-256 {2}" -f $version, ($bytes.Length / 1MB), $sha256)
if ($SoloEmpaquetar) { Write-Output "Firmado sin publicar: $zip"; return }

$clave = $env:ACUSA_CLAVE_ADMIN
if (-not $clave) { $clave = Read-Host 'Clave de administración de Acusa Central' -MaskInput }
$encabezados = @{ 'x-admin-key' = $clave; 'x-version' = $version; 'x-firma' = $firma; 'x-notas' = [Uri]::EscapeDataString($Notas) }
$respuesta = Invoke-RestMethod -Method Post -Uri ($Central.TrimEnd('/') + '/api/admin/worker/versiones') -Headers $encabezados -InFile $zip -ContentType 'application/zip' -TimeoutSec 900
Write-Output "Publicada la versión $($respuesta.version) en $Central. Los workers la instalarán en su siguiente revisión (cada 30 minutos, fuera de los minutos 00 a 14)."
