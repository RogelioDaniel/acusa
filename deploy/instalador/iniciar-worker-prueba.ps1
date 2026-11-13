<#
Acusa Worker de prueba: arranca el worker con TU usuario de Windows (sin administrador) y lo conecta a Acusa Central, sobre una
empresa de pruebas. Sirve para probar la API REST contra CONTPAQi Comercial cuando el servicio de Windows no se puede cambiar.

Uso (PowerShell, en la carpeta donde se extrajo el zip):   .\iniciar-worker-prueba.ps1
Opciones:
  -Empresa    Carpeta de la empresa en CONTPAQi (por omisión adEMPRESA_DE_PRUEBA). Debe estar registrada en Acusa Central.
  -Central    Dirección de Acusa Central.
  -SinTimbrado  No permite timbrar ni cancelar (solo clientes, productos y documentos).
  -ClaveEnComercial  No pide la contraseña del certificado: Comercial usa la que ya tenga guardada.
  -Forzar     Sigue aunque haya procesos de Montek o sean los minutos :00 a :14.

Seguridad:
  - La llave del worker y la contraseña del certificado se piden sin mostrarlas, viven solo en el proceso del worker y no se
    escriben en ningún archivo.
  - Por omisión no sigue si Montek está corriendo o si son los minutos :00 a :14 (INSTANT CAPITAL manda sus rachas hacia el :05):
    dos procesos con el SDK a la vez pueden cerrarse con un error nativo.
  - Solo trabaja en la empresa indicada y con códigos que empiecen con PRUEBA.
  - Para detenerlo: cierre su ventana o Ctrl+C.
#>
param(
    [string]$Empresa = 'adEMPRESA_DE_PRUEBA',
    [string]$Central = 'https://acusa-i331.onrender.com',
    [switch]$SinTimbrado,
    [switch]$ClaveEnComercial,
    [switch]$Forzar
)
$ErrorActionPreference = 'Stop'
$aqui = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $aqui 'Acusa.Sdk.Worker.exe'
if (-not (Test-Path $exe)) { throw "No encuentro $exe" }
if ($Empresa -notmatch 'PRUEBA') { throw "La empresa '$Empresa' no parece de pruebas. Este script solo trabaja con empresas de pruebas." }

# Guardia: el SDK admite un proceso a la vez y Montek no se toca.
$montek = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*Montek*' } | Select-Object -ExpandProperty Name -Unique)
$minuto = (Get-Date).Minute
if ($montek.Count -gt 0) { Write-Host ('Aviso: hay procesos de Montek en este equipo: ' + ($montek -join ', ')) -ForegroundColor Yellow }
if ($minuto -lt 15) { Write-Host 'Aviso: son los minutos :00 a :14; INSTANT CAPITAL manda sus rachas hacia el :05.' -ForegroundColor Yellow }
if (($montek.Count -gt 0 -or $minuto -lt 15) -and -not $Forzar) {
    throw 'Por seguridad no sigo. Espere al minuto :15 o más y confirme con David que Montek no usa el SDK. Si es a propósito, agregue -Forzar.'
}
$servicio = Get-Process -Name 'Acusa.Sdk.Worker' -ErrorAction SilentlyContinue
if ($servicio) { Write-Host 'Aviso: ya hay un Acusa Worker en este equipo (el servicio de Windows). Se arranca este junto a él; si choca con el SDK, avise a un administrador para detener el servicio.' -ForegroundColor Yellow }

# Llave del worker: sin mostrarla.
$segura = Read-Host 'Pegue la llave del worker (empieza con wk_)' -AsSecureString
$llave = [System.Net.NetworkCredential]::new('', $segura).Password.Trim()
if (-not $llave.StartsWith('wk_')) { throw 'Eso no parece una llave de worker (debe empezar con wk_).' }
try {
    Invoke-RestMethod -Method Post "$Central/api/auth/token" -Headers @{ 'x-api-key' = $llave } -TimeoutSec 90 | Out-Null
    Write-Host 'Acusa Central acepta la llave.' -ForegroundColor Green
} catch {
    $codigo = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    throw "Acusa Central no aceptó la llave (HTTP $codigo). Revise que sea la misma de Render (Orquestador__LlaveWorkerInicial)."
}

$env:Orquestador__Url = $Central
$env:Orquestador__LlaveWorker = $llave
$env:Contpaqi__Modo = 'Real'
$env:Contpaqi__Inicio = 'Dialogo'
$env:Contpaqi__RutaEmpresa = "C:\Compac\Empresas\$Empresa"
$env:Contpaqi__PermitirEscritura = 'true'
$env:Contpaqi__EmpresasEscritura__0 = $Empresa
$env:Contpaqi__PermitirDocumentos = 'true'
if (-not $SinTimbrado) {
    $env:Contpaqi__PermitirTimbrado = 'true'
    if ($ClaveEnComercial) {
        $env:Contpaqi__CertificadoGuardadoEnComercial = 'true'
    } else {
        $csd = Read-Host 'Contraseña del certificado (CSD) para timbrar' -AsSecureString
        $env:ACUSA_CLAVE_CERTIFICADO = [System.Net.NetworkCredential]::new('', $csd).Password
    }
}

Write-Host "Arrancando el worker sobre $Empresa. CONTPAQi puede pedir la clave del SDK en esta sesión: escríbala en esa ventana." -ForegroundColor Cyan
$p = Start-Process -FilePath $exe -ArgumentList @('--modo=real', '--sin-candado') -WorkingDirectory $aqui -PassThru
Remove-Item Env:Orquestador__LlaveWorker, Env:ACUSA_CLAVE_CERTIFICADO, Env:Contpaqi__CertificadoGuardadoEnComercial -ErrorAction SilentlyContinue
Write-Host "Worker iniciado (proceso $($p.Id)). Avise para correr las pruebas. Para detenerlo: Stop-Process -Id $($p.Id)" -ForegroundColor Green
