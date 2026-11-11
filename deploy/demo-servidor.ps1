<#
Acusa v1.0.0. Levanta Acusa Central (portal + orquestador) y un Acusa Worker sobre una empresa de pruebas.
Estructura esperada (la del zip): orquestador\, worker\, programador\ y desktop\ junto a este script.
Uso (PowerShell, en la carpeta donde se extrajo el zip):  .\demo-servidor.ps1
Opciones: -Empresa adEMPRESA_DE_PRUEBA  -Puerto 5090  -Correo admin@localsolutionsit.com  -Simulado  -Remoto

-Simulado: el worker usa datos en memoria (sin CONTPAQi), solo para probar el ciclo.
-Remoto:   el portal escucha en todas las interfaces; el puerto debe abrirse aparte y solo para quien lo necesite.
-Documentos: permite crear documentos (cada uno consume un folio que no se reutiliza). Solo con referencia PRUEBA.
-Programador: ademas levanta Acusa Programador (sistema externo de ejemplo: programaciones por horario, cola de corridas y factura manual;
             tambien se acepta el alias -Facturador)
             en http://127.0.0.1:5200, ya conectado a
             Acusa Central con su propia llave de API. Opciones: -Concepto -Serie -ClienteCodigo -ProductoCodigo.
-ClientesPermitidos: codigos de clientes existentes que pueden recibir un documento aunque no empiecen con PRUEBA
             (receptor real para probar el timbrado). Solo vale para el cliente; productos y referencia siguen con PRUEBA.
-EmpresaReal: la empresa trabaja con datos reales: no se exige el prefijo PRUEBA en codigos, nombres ni referencias
             (Contpaqi:EmpresasReales). Requiere -Produccion si el nombre no contiene PRUEBA. Sin este modificador todo exige PRUEBA.
-Prueba:    cuando el worker esté en línea, corre solo el ejemplo del flujo de Alain (cliente, producto, factura, y con -Timbrado también
             timbrar y bajar el XML y el PDF) y deja el resultado en pantalla. -Cancelar agrega la cancelación ante el SAT (requiere -Timbrado):
             hágalo solo con autorización. -Unidad: nombre de la unidad del producto de prueba (si no existe, se prueban otras comunes).
-Cliente:   nombre del cliente que se crea en Acusa Central (por omisión «Local Solutions IT»). Con otro nombre se muestra cómo queda
             un cliente distinto: sus propias empresas, llaves y órdenes, aisladas de las de los demás.
-Forzar:    sigue aunque haya otro proceso que use el SDK (por ejemplo el worker de Montek) o sea el minuto de las rachas (:00 a :14).
             Dos procesos con el SDK a la vez pueden cerrarse con un error nativo: no lo use durante una racha de INSTANT CAPITAL.
-ConceptosAlternos: el Programador prueba estos conceptos, en orden, si Comercial rechaza el anterior por falta de permisos.
-ClaveEnComercial: no pide la contrasena del certificado; Comercial usa la que ya tenga guardada. Si no la tiene, el
             timbrado falla sin consecuencias.
-Produccion: obligatorio si la empresa no es de pruebas (su nombre no contiene PRUEBA). Pide escribir PRODUCCION.
-Timbrado:   permite timbrar y cancelar documentos de prueba (implica -Documentos). Gasta timbres del PAC y la
             cancelación avisa al SAT. Pide la contraseña del certificado sin mostrarla; solo vive en el proceso
             del worker y no se escribe en ningún archivo ni viaja en las órdenes.

Los secretos del orquestador y la contraseña del portal se generan en cada arranque y solo viven en memoria:
se imprimen una vez en esta consola y no se guardan en ningún archivo.
#>
param(
    [string]$Empresa = 'adEMPRESA_DE_PRUEBA',
    [int]$Puerto = 5090,
    [string]$Correo = 'admin@localsolutionsit.com',
    [switch]$Simulado,
    [switch]$Remoto,
    [switch]$Documentos,
    [switch]$Timbrado,
    [Alias('Facturador')][switch]$Programador,
    [string[]]$ConceptosAlternos = @(),
    [switch]$ClaveEnComercial,
    [switch]$Produccion,
    [string]$Concepto = '5',
    [string]$Serie = 'PRUEBA',
    [string]$ClienteCodigo = 'PRUEBADEMO01',
    [string]$ProductoCodigo = 'PRUEBAPR01',
    [string[]]$ClientesPermitidos = @(),
    [switch]$EmpresaReal,
    [switch]$Prueba,
    [switch]$Cancelar,
    [switch]$Forzar,
    [string]$Unidad = 'Pieza',
    [string]$Cliente = 'Local Solutions IT'
)

$ErrorActionPreference = 'Stop'
$aqui = Split-Path -Parent $MyInvocation.MyCommand.Path
$orq = Join-Path $aqui 'orquestador\Acusa.Orquestador.exe'
$wrk = Join-Path $aqui 'worker\Acusa.Sdk.Worker.exe'
foreach ($f in $orq, $wrk) { if (-not (Test-Path $f)) { throw "No encuentro $f" } }

function NuevoSecreto([int]$bytes) {
    $b = New-Object byte[] $bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

if ($Empresa -notmatch 'PRUEBA' -and -not $Simulado) {
    if (-not $Produccion) { throw "La empresa '$Empresa' no es de pruebas. Si es a proposito, agregue -Produccion." }
    $ok = Read-Host "Empresa de PRODUCCION '$Empresa'. Se crearan registros PRUEBA y se timbrara en serio. Escriba PRODUCCION para continuar"
    if ($ok -cne 'PRODUCCION') { throw 'Cancelado.' }
}

# Guardia: el SDK de CONTPAQi admite un proceso a la vez. Si el worker de Montek está corriendo en este equipo, un segundo
# proceso con el SDK puede cerrar a los dos con un error nativo (pasó el 6 de noviembre).
if (-not $Simulado) {
    $otros = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*Montek*' } | Select-Object -ExpandProperty Name -Unique)
    $minuto = (Get-Date).Minute
    if ($otros.Count -gt 0 -or $minuto -lt 15) {
        if ($otros.Count -gt 0) { Write-Host ("Aviso: hay procesos de Montek en este equipo: " + ($otros -join ', ')) -ForegroundColor Yellow }
        if ($minuto -lt 15) { Write-Host "Aviso: son los minutos :00 a :14; INSTANT CAPITAL manda sus rachas hacia el :05." -ForegroundColor Yellow }
        if (-not $Forzar) { throw 'Por seguridad no sigo. Espere al minuto :15 o más y confirme con David que Montek no está usando el SDK; si es a propósito, agregue -Forzar.' }
    }
}
if ($Cancelar -and -not $Timbrado) { throw '-Cancelar requiere -Timbrado.' }

$admin = NuevoSecreto 24
$claveInicial = NuevoSecreto 12
$url = "http://127.0.0.1:$Puerto"

# Orquestador. Escucha solo en este equipo salvo que se pida -Remoto.
$env:Orquestador__SecretoJwt = NuevoSecreto 48
$env:Orquestador__ClaveAdmin = $admin
# Una base nueva por arranque: la contraseña inicial solo se aplica cuando no hay usuarios, y aquí cambia en cada corrida.
$env:Orquestador__BaseDatos = Join-Path $aqui "orquestador\datos\demo-$(Get-Date -Format 'yyyyMMdd-HHmmss').db"
$env:Orquestador__CorreoInicial = $Correo
$env:Orquestador__NombreInicial = 'Administrador'
$env:Orquestador__NombreClienteInicial = $Cliente
$env:Orquestador__ClaveInicial = $claveInicial
$env:Urls = if ($Remoto) { "http://0.0.0.0:$Puerto" } else { $url }
$po = Start-Process -FilePath $orq -WorkingDirectory (Split-Path $orq) -PassThru
Remove-Item Env:Orquestador__NombreClienteInicial, Env:Orquestador__SecretoJwt, Env:Orquestador__ClaveAdmin, Env:Orquestador__CorreoInicial, Env:Orquestador__ClaveInicial, Env:Urls

for ($i = 0; $i -lt 30; $i++) {
    try { Invoke-RestMethod "$url/health" | Out-Null; break } catch { Start-Sleep -Seconds 1 }
    if ($i -eq 29) { throw 'El orquestador no respondió en 30 s.' }
}

# Alta del cliente de la demo: la empresa y la llave del worker.
$h = @{ 'x-admin-key' = $admin }
$login = Invoke-RestMethod "$url/api/auth/login" -Method Post -ContentType 'application/json' -Body (@{ correo = $Correo; contrasena = $claveInicial } | ConvertTo-Json)
$hp = @{ Authorization = "Bearer $($login.token)" }
Invoke-RestMethod "$url/api/portal/empresas" -Method Post -Headers $hp -ContentType 'application/json' -Body (@{ nombre = $Empresa } | ConvertTo-Json) | Out-Null
$llaveWorker = (Invoke-RestMethod "$url/api/portal/llaves" -Method Post -Headers $hp -ContentType 'application/json' -Body (@{ rol = 'worker'; nombre = 'Worker del servidor' } | ConvertTo-Json)).llave

# Worker: una empresa por proceso. Con el inicio por diálogo, CONTPAQi pide la clave en esta sesión de Windows.
$env:Orquestador__Url = $url
$env:Orquestador__LlaveWorker = $llaveWorker
$env:Contpaqi__RutaEmpresa = "C:\Compac\Empresas\$Empresa"
# Windows PowerShell 5 no acepta una lista de argumentos vacía en Start-Process: siempre lleva al menos uno.
$argsWorker = @('--modo=real')
if ($Simulado) {
    $argsWorker = @('--simulado')
} else {
    $env:Contpaqi__Modo = 'Real'
    $env:Contpaqi__Inicio = 'Dialogo'
    $env:Contpaqi__PermitirEscritura = 'true'
    $env:Contpaqi__EmpresasEscritura__0 = $Empresa
    if ($EmpresaReal) { $env:Contpaqi__EmpresasReales__0 = $Empresa }
    for ($i = 0; $i -lt $ClientesPermitidos.Count; $i++) { Set-Item "Env:Contpaqi__ClientesPermitidos__$i" $ClientesPermitidos[$i] }
    if ($Documentos -or $Timbrado) { $env:Contpaqi__PermitirDocumentos = 'true' }
    if ($Timbrado) {
        $env:Contpaqi__PermitirTimbrado = 'true'
        if ($ClaveEnComercial) {
            $env:Contpaqi__CertificadoGuardadoEnComercial = 'true'
        } else {
            $segura = Read-Host 'Contraseña del certificado (CSD) para timbrar' -AsSecureString
            $env:ACUSA_CLAVE_CERTIFICADO = [System.Net.NetworkCredential]::new('', $segura).Password
        }
    }
}
$pw = Start-Process -FilePath $wrk -ArgumentList $argsWorker -WorkingDirectory (Split-Path $wrk) -PassThru
Remove-Item Env:Orquestador__LlaveWorker, Env:Orquestador__Url, Env:Contpaqi__RutaEmpresa, Env:ACUSA_CLAVE_CERTIFICADO, Env:Contpaqi__CertificadoGuardadoEnComercial, Env:Contpaqi__EmpresasReales__0 -ErrorAction SilentlyContinue
Get-ChildItem Env:Contpaqi__ClientesPermitidos__* -ErrorAction SilentlyContinue | Remove-Item

$pf = $null
if ($Programador) {
    $fac = Join-Path $aqui 'programador\Acusa.Programador.exe'
    if (-not (Test-Path $fac)) { throw "No encuentro $fac" }
    # El token del portal es de corta vida: pedir uno nuevo, por si la contraseña del certificado tardó en escribirse.
    $login = Invoke-RestMethod "$url/api/auth/login" -Method Post -ContentType 'application/json' -Body (@{ correo = $Correo; contrasena = $claveInicial } | ConvertTo-Json)
    $hp = @{ Authorization = "Bearer $($login.token)" }
    $llaveApi = (Invoke-RestMethod "$url/api/portal/llaves" -Method Post -Headers $hp -ContentType 'application/json' -Body (@{ rol = 'api'; nombre = 'Acusa Programador' } | ConvertTo-Json)).llave
    $env:Urls = 'http://127.0.0.1:5200'
    $env:Programador__UrlCentral = $url
    $env:Programador__LlaveApi = $llaveApi
    $env:Programador__Empresa = $Empresa
    $env:Programador__Concepto = $Concepto
    $env:Programador__Serie = $Serie
    $env:Programador__ClienteCodigo = $ClienteCodigo
    for ($i = 0; $i -lt $ConceptosAlternos.Count; $i++) { Set-Item "Env:Programador__ConceptosAlternos__$i" $ConceptosAlternos[$i] }
    $env:Programador__ProductoCodigo = $ProductoCodigo
    $pf = Start-Process -FilePath $fac -WorkingDirectory (Split-Path $fac) -PassThru
    Remove-Item Env:Urls, Env:Programador__UrlCentral, Env:Programador__LlaveApi, Env:Programador__Empresa, Env:Programador__Concepto, Env:Programador__Serie, Env:Programador__ClienteCodigo, Env:Programador__ProductoCodigo
    Get-ChildItem Env:Programador__ConceptosAlternos__* -ErrorAction SilentlyContinue | Remove-Item
}

if ($Prueba) {
    Write-Host ''
    Write-Host 'Modo prueba: esperando a que el worker esté en línea (si CONTPAQi pide la clave, escríbala en su ventana)...' -ForegroundColor Cyan
    $login = Invoke-RestMethod "$url/api/auth/login" -Method Post -ContentType 'application/json' -Body (@{ correo = $Correo; contrasena = $claveInicial } | ConvertTo-Json)
    $hp = @{ Authorization = "Bearer $($login.token)" }
    $llavePrueba = (Invoke-RestMethod "$url/api/portal/llaves" -Method Post -Headers $hp -ContentType 'application/json' -Body (@{ rol = 'api'; nombre = 'Prueba del flujo de Alain' } | ConvertTo-Json)).llave
    $tokenPrueba = (Invoke-RestMethod "$url/api/auth/token" -Method Post -Headers @{ 'x-api-key' = $llavePrueba }).token
    $enLinea = $false
    for ($i = 0; $i -lt 600; $i++) {
        try { if ((Invoke-RestMethod "$url/api/sdk/status" -Headers @{ Authorization = "Bearer $tokenPrueba" }).workersEnLinea -ge 1) { $enLinea = $true; break } } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $enLinea) { Write-Host 'El worker no se puso en línea en 10 minutos. Revise su ventana y el panel http://127.0.0.1:5095.' -ForegroundColor Red }
    else {
        $ejemplo = Join-Path $aqui 'ejemplo-flujo-alain.ps1'
        if (-not (Test-Path $ejemplo)) { throw "No encuentro $ejemplo" }
        $env:ACUSA_LLAVE_API = $llavePrueba
        $params = @{ Url = $url; DbName = $Empresa; Concepto = $Concepto; Serie = $Serie; Unidad = $Unidad }
        if ($Timbrado) { $params.Timbrar = $true }
        if ($Cancelar) { $params.Cancelar = $true }
        try { & $ejemplo @params } catch { Write-Host "La prueba se detuvo: $($_.Exception.Message)" -ForegroundColor Red }
        Remove-Item Env:ACUSA_LLAVE_API -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host "Acusa Central:  $url   (abra esa dirección en el navegador)" -ForegroundColor Green
Write-Host "Usuario:        $Correo"
Write-Host "Contraseña:     $claveInicial   (no se guarda; cámbiela en Configuración)" -ForegroundColor Yellow
Write-Host "Cliente:        $Cliente"
Write-Host "Empresa:        $Empresa"
Write-Host ''
if ($Programador) { Write-Host 'Acusa Programador (sistema externo): http://127.0.0.1:5200' -ForegroundColor Green }
if (Test-Path (Join-Path $aqui 'desktop\Acusa.Worker.Desktop.exe')) { Write-Host "Acusa Worker Desktop (asistente de configuracion): $(Join-Path $aqui 'desktop\Acusa.Worker.Desktop.exe')" }
Write-Host 'Panel del worker: http://127.0.0.1:5095   El código de acceso aparece en la ventana del worker.'
Write-Host 'Si CONTPAQi muestra "Ingreso a CONTPAQi COMERCIAL", escriba la clave ahi.'
Write-Host 'Para terminar: pulse Enter; se detienen el worker y el orquestador.'
Read-Host | Out-Null
$ids = @($pw.Id, $po.Id)
if ($pf) { $ids += $pf.Id }
Stop-Process -Id $ids -ErrorAction SilentlyContinue
