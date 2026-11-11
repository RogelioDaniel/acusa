#requires -Version 5.1
<#
 Prueba de un clic contra la API de Nexus (Montek): login, crear un cliente de prueba y seguir la tarea hasta que termine.
 Si se corre en el servidor de CONTPAQi, además muestra la traza del trabajo en la bitácora de Montek.

   powershell -ExecutionPolicy Bypass -File .\prueba-nexus.ps1
   powershell -ExecutionPolicy Bypass -File .\prueba-nexus.ps1 -DbName adLOCALSOLUTIONIT

 Pide en pantalla, sin mostrarlos y sin guardarlos: la llave de API (x-api-key), el usuario y la contraseña de Nexus.
 Crea UN cliente con prefijo PRUEBANX en la empresa indicada.
#>
[CmdletBinding()]
param(
    [string]$Base = 'https://montekvps.cloud/api',
    [string]$DbName = 'adLOCALSOLUTIONIT',
    [int]$EsperaMaxSegundos = 180
)
$ErrorActionPreference = 'Stop'

function Pedir([string]$texto) {
    $s = Read-Host $texto -AsSecureString
    [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))
}
function Llamar([string]$metodo, [string]$ruta, $cuerpo, $cab) {
    $t0 = Get-Date
    $p = @{ Uri = "$Base$ruta"; Method = $metodo; Headers = $cab; UseBasicParsing = $true; TimeoutSec = 120 }
    if ($null -ne $cuerpo) { $p.Body = [Text.Encoding]::UTF8.GetBytes(($cuerpo | ConvertTo-Json -Depth 8)); $p.ContentType = 'application/json; charset=utf-8' }
    try { $r = Invoke-WebRequest @p; $estado = [int]$r.StatusCode; $txt = $r.Content }
    catch { $resp = $_.Exception.Response; if (-not $resp) { throw }; $estado = [int]$resp.StatusCode; $txt = $_.ErrorDetails.Message }
    [pscustomobject]@{ Estado = $estado; Ms = [int]((Get-Date) - $t0).TotalMilliseconds; Json = $(if ($txt) { $txt | ConvertFrom-Json } else { $null }); Texto = $txt }
}

$llave = Pedir 'Llave de API de Nexus (x-api-key)'
$usuario = Read-Host 'Usuario de Nexus'
$clave = Pedir 'Contrasena de Nexus'
$h = @{ 'x-api-key' = $llave }

Write-Host "`n1. Login ($Base/auth/login)..." -ForegroundColor Cyan
$r = Llamar POST '/auth/login' @{ usuario = $usuario; password = $clave } $h
$clave = $null
if ($r.Estado -ne 200 -or -not $r.Json.token) { throw "El login fallo (HTTP $($r.Estado))." }
Write-Host ("   HTTP {0} en {1:N1} s: token recibido" -f $r.Estado, ($r.Ms / 1000)) -ForegroundColor Green
$h['Authorization'] = "Bearer $($r.Json.token)"

$codigo = 'PRUEBANX' + (Get-Date -Format 'MMddHHmm')
Write-Host "`n2. Orden cliente.crear en $DbName, codigo $codigo..." -ForegroundColor Cyan
$orden = @{ tipoProceso = 'cliente.crear'; dbName = $DbName; payload = @{ codigo = $codigo; razonSocial = 'PRUEBA CLIENTE NEXUS'; rfc = 'XAXX010101000'; tipoCliente = 1; limiteCredito = 50000.00 } }
$r = Llamar POST '/sdk/execute' $orden $h
Write-Host ("   HTTP {0} en {1} ms: {2}" -f $r.Estado, $r.Ms, $r.Json.mensaje) -ForegroundColor Green
$taskId = $r.Json.taskId; $jobId = $r.Json.jobId
Write-Host "   taskId $taskId, jobId $jobId (la orden quedo en cola; el worker de Montek la toma por sondeo)"

Write-Host "`n3. Siguiendo la tarea $taskId..." -ForegroundColor Cyan
$limite = (Get-Date).AddSeconds($EsperaMaxSegundos); $estado = ''
while ((Get-Date) -lt $limite) {
    Start-Sleep -Seconds 5
    $t = Llamar GET "/sdk/tasks/$taskId" $null $h
    $estado = $t.Json.data.status
    Write-Host ("   {0:HH:mm:ss}  status = {1}  ({2:N1} s)" -f (Get-Date), $estado, ($t.Ms / 1000))
    if ($estado -in 'completed', 'failed', 'error') { break }
}
if ($estado -eq 'completed') { Write-Host ("`nTarea completada: " + $t.Json.data.errorMessage) -ForegroundColor Green }
else { Write-Host "`nLa tarea no termino en $EsperaMaxSegundos s (ultimo estado: $estado)." -ForegroundColor Yellow }

# Traza en la bitacora de Montek (solo si corre en el servidor).
$bit = "C:\ProgramData\Montekapp\logs\worker_$(Get-Date -Format 'yyyyMMdd').log"
if ($jobId -and (Test-Path $bit)) {
    Write-Host "`n4. Traza del trabajo en la bitacora de Montek:" -ForegroundColor Cyan
    Select-String -Path $bit -Pattern $jobId.Substring(0, 8) -SimpleMatch | ForEach-Object { '   ' + $_.Line.Substring(0, [Math]::Min(170, $_.Line.Length)) }
}
$llave = $null; $h = $null
Write-Host "`nListo. El cliente $codigo quedo en la empresa $DbName (abra esa empresa en Comercial para verlo)." -ForegroundColor Green
