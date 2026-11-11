#requires -Version 5.1
<#
 Ejemplo: cómo el sistema de facturación de Alain llamaría a Acusa en lugar de Nexus.

 Repite, paso por paso, los trabajos de su aplicación (los nombres vienen de su repositorio facturacion_back) con la misma forma de
 orden que ya mandan a Nexus: { tipoProceso, dbName, payload }. Cada paso es UNA petición y la respuesta trae el resultado: no hay
 consulta de estado aparte.

   alta de compañía ........... cliente.crear
   actualizar compañía ........ cliente.actualizar          (build_update_payload)
   alta de producto ........... producto.crear
   create_invoice_job ......... documento.crear             (fecha, forma de pago, uso de CFDI, impuestos por partida)
   stamp_invoice_job .......... documento.emitir            (solo con -Timbrar: gasta un timbre del PAC)
   backfill_invoice_files_job . documento.obtener-archivo   (XML y PDF; solo si se timbró)
   cancel_invoice_job ......... documento.cancelar          (solo con -Cancelar: avisa al SAT)

 Además: repite el crear factura con la MISMA llave de idempotencia (no duplica) y compara el estilo de hoy (mandar, esperar y
 preguntar el estado) con el de Acusa (una sola petición).

 Uso (con demo-servidor.ps1 ya arrancado y un worker en línea para la empresa de pruebas):
   powershell -ExecutionPolicy Bypass -File .\ejemplo-flujo-alain.ps1
   powershell -ExecutionPolicy Bypass -File .\ejemplo-flujo-alain.ps1 -Timbrar

 La llave de API (tipo Sistema) se pide en pantalla o sale de ACUSA_LLAVE_API; no se muestra ni se guarda.
 Solo corre sobre una empresa de pruebas (el nombre debe contener PRUEBA).
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://127.0.0.1:5090',
    [string]$DbName = 'adEMPRESA_DE_PRUEBA',
    [string]$Concepto = '5',
    [string]$Serie = 'PRUEBA',
    [string]$Unidad = 'SERVICIO',
    [string]$Almacen = '1',
    [switch]$Timbrar,
    [switch]$Cancelar,
    [int]$Esperar = 60
)
$ErrorActionPreference = 'Stop'
if ($DbName -notmatch 'PRUEBA') { throw "Por seguridad este ejemplo solo corre en una empresa de pruebas (el nombre debe contener PRUEBA). Recibió: $DbName" }
if ($Cancelar -and -not $Timbrar) { throw 'Cancelar una factura requiere -Timbrar: solo se cancela lo que se timbró en esta corrida.' }

if ($env:ACUSA_LLAVE_API) { $llavePlana = $env:ACUSA_LLAVE_API }
else {
    $llave = Read-Host 'Llave de API (tipo Sistema)' -AsSecureString
    $llavePlana = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($llave))
}
$token = (Invoke-RestMethod "$Url/api/auth/token" -Method Post -Headers @{ 'x-api-key' = $llavePlana }).token
$llavePlana = $null
$h = @{ Authorization = "Bearer $token" }

# Lo que hace su aplicación con Nexus: una orden con tipoProceso, dbName y payload. Aquí va a Acusa, con la espera pedida.
function Orden([string]$tipo, $payload, [string]$llave, [int]$esperar = $Esperar) {
    $hd = $h.Clone(); if ($llave) { $hd['Idempotency-Key'] = $llave }
    $cuerpo = @{ tipoProceso = $tipo; dbName = $DbName; payload = $payload } | ConvertTo-Json -Depth 8
    $uri = "$Url/api/sdk/execute" + $(if ($esperar -gt 0) { "?esperar=$esperar" } else { '' })
    $t0 = Get-Date
    $status = 0; $contenido = $null; $repetida = $false
    try {
        $r = Invoke-WebRequest $uri -Method Post -Headers $hd -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($cuerpo)) -UseBasicParsing
        $status = [int]$r.StatusCode; $contenido = $r.Content
        $repetida = ($r.Headers['Idempotency-Replayed'] -eq 'true')
    }
    catch {
        # 422 (la operación falló) y 409 (llave repetida con otra orden) también traen un cuerpo con el detalle.
        $resp = $_.Exception.Response
        if (-not $resp) { throw }
        $status = [int]$resp.StatusCode
        $contenido = $_.ErrorDetails.Message
        if (-not $contenido -and $resp.GetResponseStream) {
            $lector = New-Object IO.StreamReader($resp.GetResponseStream()); $contenido = $lector.ReadToEnd(); $lector.Dispose()
        }
    }
    $ms = [int]((Get-Date) - $t0).TotalMilliseconds
    $json = if ($contenido) { $contenido | ConvertFrom-Json } else { $null }
    [pscustomobject]@{ Status = $status; Ms = $ms; Json = $json; Repetida = $repetida }
}

function Paso([string]$job, [string]$tipo, $r, [string]$detalle) {
    $estado = if ($r.Json -and $r.Json.estado) { $r.Json.estado } else { '' }
    $color = if ($r.Status -eq 200) { 'Green' } elseif ($r.Status -eq 202) { 'Yellow' } else { 'Red' }
    Write-Host ("  {0,-28} {1,-27} HTTP {2}  {3,-10} {4,6} ms  {5}" -f $job, $tipo, $r.Status, $estado, $r.Ms, $detalle) -ForegroundColor $color
    if ($r.Status -ne 200 -and $r.Json -and $r.Json.error) { Write-Host "      error: $($r.Json.error)" -ForegroundColor Red }
}

$sello = Get-Date -Format 'MMddHHmmss'
$cliente = "PRUEBAEJC$sello"
$producto = "PRUEBAEJP$sello"
$fecha = (Get-Date).ToString('yyyy-MM-dd')

Write-Host "`nSistema de Alain -> Acusa · empresa $DbName · $(Get-Date -Format 'yyyy-MM-dd HH:mm')`n" -ForegroundColor Cyan
Write-Host 'Cada fila es una sola petición; la respuesta ya trae el resultado.' -ForegroundColor DarkGray

# 1. Compañía: lo que arma create_payload.rb (código, razón social, RFC, tipoCliente 1, límite de crédito).
$r = Orden 'cliente.crear' @{ codigo = $cliente; razonSocial = "PRUEBA EJEMPLO ALAIN $sello"; rfc = 'XAXX010101000'; tipoCliente = 1; limiteCredito = 50000 }
Paso 'alta de compañía' 'cliente.crear' $r "código $cliente"

# 2. build_update_payload: cambia el límite de crédito y el correo.
# El régimen fiscal y el uso de CFDI del receptor son obligatorios para timbrar (616 y S01 son los de un público en general).
$r = Orden 'cliente.actualizar' @{ codigo = $cliente; limiteCredito = 80000; email = 'facturacion@prueba.local'; regimenFiscal = '616'; usoCfdi = 'S01' }
Paso 'actualizar compañía' 'cliente.actualizar' $r 'límite 80,000 · régimen 616 · uso S01'

# 3. Producto.
# La unidad debe existir en la empresa: si la indicada no existe se prueban otras comunes (una unidad inexistente no crea nada).
foreach ($u in (@($Unidad) + @('Pieza', 'Servicio', 'Unidad de servicio', 'Unidad', 'No aplica') | Select-Object -Unique)) {
    $r = Orden 'producto.crear' @{ codigo = $producto; nombre = "PRUEBA SERVICIO $sello"; unidadBase = $u; precio1 = 2000; claveSat = '81112200'; tipoProducto = 3; textosExtra = @('Renta de servidor') }
    if ($r.Status -eq 200 -or -not ($r.Json -and "$($r.Json.error)" -match 'unidad base')) { break }
}
Paso 'alta de producto' 'producto.crear' $r "código $producto · unidad $u"

# 4. create_invoice_job. La llave de idempotencia identifica esta factura: si la orden se repite, no se duplica.
$llaveFactura = "factura-ejemplo-$sello"
$factura = @{
    concepto = $Concepto; serie = $Serie; clienteCodigo = $cliente; referencia = "PRUEBAEJ$sello"
    fecha = $fecha; formaPago = '03'
    partidas = @(@{ productoCodigo = $producto; unidades = 2; precio = 2000; ivaPorcentaje = 16; almacen = $Almacen })
}
$crear = Orden 'documento.crear' $factura $llaveFactura
$folio = if ($crear.Json -and $crear.Json.resultado) { $crear.Json.resultado.folio } else { $null }
$total = if ($crear.Json -and $crear.Json.resultado) { $crear.Json.resultado.total } else { $null }
Paso 'create_invoice_job' 'documento.crear' $crear "folio $folio · total $total"
if ($crear.Status -ne 200) { throw 'No se pudo crear la factura; el resto del flujo depende de ella.' }

# 4b. Reintento con la misma llave: Acusa devuelve la misma tarea y no crea otra factura.
$otra = Orden 'documento.crear' $factura $llaveFactura
Paso '  (misma orden, misma llave)' 'documento.crear' $otra "repetida=$($otra.Repetida) · misma tarea=$($otra.Json.tareaId -eq $crear.Json.tareaId)"

# 5. stamp_invoice_job.
if ($Timbrar) {
    $emitir = Orden 'documento.emitir' @{ concepto = $Concepto; serie = $Serie; folio = $folio }
    $uuid = if ($emitir.Json -and $emitir.Json.resultado) { $emitir.Json.resultado.uuid } else { $null }
    Paso 'stamp_invoice_job' 'documento.emitir' $emitir "UUID $uuid"

    # 6. backfill_invoice_files_job.
    $arch = Orden 'documento.obtener-archivo' @{ concepto = $Concepto; serie = $Serie; folio = $folio; formato = 'ambos' }
    $n = if ($arch.Json -and $arch.Json.resultado -and $arch.Json.resultado.archivos) { @($arch.Json.resultado.archivos).Count } else { 0 }
    Paso 'backfill_invoice_files_job' 'documento.obtener-archivo' $arch "$n archivo(s)"

    # 7. cancel_invoice_job (avisa al SAT).
    if ($Cancelar) {
        $can = Orden 'documento.cancelar' @{ concepto = $Concepto; serie = $Serie; folio = $folio; motivo = '02' }
        Paso 'cancel_invoice_job' 'documento.cancelar' $can 'motivo 02'
    }
}
else {
    Write-Host '  (timbrar, bajar archivos y cancelar se omiten: use -Timbrar para incluirlos; gasta un timbre del PAC)' -ForegroundColor DarkGray
}

# Comparación: así pregunta hoy su aplicación (manda, espera y consulta) frente a una sola petición.
Write-Host "`nMismo trabajo, dos estilos (cliente.leer):" -ForegroundColor Cyan
$t0 = Get-Date
$ini = Orden 'cliente.leer' @{ codigo = $cliente } $null 0
$id = $ini.Json.tareaId
$consultas = 0
do {
    Start-Sleep -Milliseconds 1000
    $consultas++
    $x = Invoke-RestMethod "$Url/api/sdk/tasks/$id" -Headers $h
} until ($x.estado -in 'Completada', 'Fallida' -or $consultas -ge 60)
$msConsulta = [int]((Get-Date) - $t0).TotalMilliseconds
$directo = Orden 'cliente.leer' @{ codigo = $cliente } $null $Esperar
Write-Host ("  Estilo de hoy (mandar y consultar el estado cada 1 s): {0,6} ms · {1} petición(es) de consulta" -f $msConsulta, $consultas)
Write-Host ("  Estilo Acusa (una sola petición con ?esperar):          {0,6} ms · 0 consultas" -f $directo.Ms)
Write-Host "`nListo. Código de cliente y producto creados: $cliente, $producto" -ForegroundColor Green
