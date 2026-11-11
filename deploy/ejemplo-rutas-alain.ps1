#requires -Version 5.1
<#
 Ejemplo: las rutas del Swagger de Alain, llamadas directamente a Acusa Central.

 Recorre, con los mismos caminos, métodos y nombres de campo de su documentación, lo que hace una aplicación de facturación:
   emisora -> compañía -> dirección -> producto -> factura con partidas -> timbrar -> XML y PDF -> (cancelar) -> listados.
 Cada llamada responde con su resultado; no hay consulta de estado aparte. Acusa guarda compañías, productos y facturas (la factura
 queda como borrador) y manda las órdenes a CONTPAQi por el worker solo cuando hace falta.

 Uso (con demo-servidor.ps1 ya arrancado y un worker en línea para la empresa de pruebas):
   powershell -ExecutionPolicy Bypass -File .\ejemplo-rutas-alain.ps1
   powershell -ExecutionPolicy Bypass -File .\ejemplo-rutas-alain.ps1 -Cancelar        (avisa al SAT: solo con autorización)

 La llave de API se pide en pantalla o sale de ACUSA_LLAVE_API. Solo corre sobre una empresa de pruebas (el nombre debe contener PRUEBA).
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://127.0.0.1:5090',
    [string]$DbName = 'adEMPRESA_DE_PRUEBA',
    [string]$Concepto = '5',
    [string]$Serie = 'PRUEBA',
    [string]$Unidad = 'Pieza',
    [switch]$Cancelar
)
$ErrorActionPreference = 'Stop'
if ($DbName -notmatch 'PRUEBA') { throw "Por seguridad este ejemplo solo corre en una empresa de pruebas (el nombre debe contener PRUEBA). Recibió: $DbName" }

if ($env:ACUSA_LLAVE_API) { $llavePlana = $env:ACUSA_LLAVE_API }
else {
    $llave = Read-Host 'Llave de API (tipo Sistema)' -AsSecureString
    $llavePlana = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($llave))
}
$token = (Invoke-RestMethod "$Url/api/auth/token" -Method Post -Headers @{ 'x-api-key' = $llavePlana }).token
$llavePlana = $null
$h = @{ Authorization = "Bearer $token" }

function Llamar([string]$metodo, [string]$ruta, $cuerpo = $null) {
    $args2 = @{ Uri = "$Url$ruta"; Method = $metodo; Headers = $h; UseBasicParsing = $true }
    # El cuerpo va en UTF-8: Windows PowerShell 5.1 lo manda en otra codificación y los acentos llegarían rotos.
    if ($null -ne $cuerpo) { $args2.Body = [Text.Encoding]::UTF8.GetBytes(($cuerpo | ConvertTo-Json -Depth 8)); $args2.ContentType = 'application/json; charset=utf-8' }
    $t0 = Get-Date; $status = 0; $contenido = $null
    try { $r = Invoke-WebRequest @args2; $status = [int]$r.StatusCode; $contenido = $r.Content }
    catch {
        $resp = $_.Exception.Response
        if (-not $resp) { throw }
        $status = [int]$resp.StatusCode
        $contenido = $_.ErrorDetails.Message
        if (-not $contenido -and $resp.GetResponseStream) { $lector = New-Object IO.StreamReader($resp.GetResponseStream()); $contenido = $lector.ReadToEnd(); $lector.Dispose() }
    }
    $ms = [int]((Get-Date) - $t0).TotalMilliseconds
    [pscustomobject]@{ Status = $status; Ms = $ms; Json = $(if ($contenido) { $contenido | ConvertFrom-Json } else { $null }); Texto = $contenido }
}

function Paso([string]$titulo, $r, [string]$detalle) {
    $color = if ($r.Status -in 200, 201) { 'Green' } elseif ($r.Status -eq 202) { 'Yellow' } else { 'Red' }
    Write-Host ("  {0,-34} HTTP {1}  {2,6} ms  {3}" -f $titulo, $r.Status, $r.Ms, $detalle) -ForegroundColor $color
    if ($r.Status -ge 400 -and $r.Json -and $r.Json.errors) { Write-Host ("      errores: " + ($r.Json.errors -join ' | ')) -ForegroundColor Red }
}

function Exigir($r, [string]$donde) { if ($r.Status -notin 200, 201) { throw "Se detuvo en: $donde (HTTP $($r.Status))" } }

Write-Host "`nRutas del Swagger de Alain sobre Acusa · empresa $DbName · $(Get-Date -Format 'yyyy-MM-dd HH:mm')`n" -ForegroundColor Cyan

# 1. Empresa emisora (registra también la empresa de CONTPAQi para este cliente).
$r = Llamar POST '/issuing_companies' @{ issuing_company = @{ social_reason = 'PRUEBA EMISORA SA DE CV'; rfc = 'EKU9003173C9'; database_name = $DbName } }
Exigir $r 'POST /issuing_companies'; $emisora = $r.Json.id
Paso 'POST /issuing_companies' $r "id $emisora"

# 2. Compañía (el código lo genera Acusa).
$r = Llamar POST '/companies' @{ company = @{ client_type = 1; credit_limit = 50000; rfc = 'XAXX010101000'; social_reason = 'PRUEBA CLIENTE EJEMPLO SA' } }
Exigir $r 'POST /companies'; $compania = $r.Json.id; $codigoCliente = $r.Json.code
Paso 'POST /companies' $r "id $compania · código $codigoCliente · $($r.Json.status)"

# 3. Dirección fiscal de la compañía.
$r = Llamar POST "/companies/$compania/addresses" @{ address = @{ street = 'Av. Insurgentes Sur 123'; neighborhood = 'Del Valle'; zip_code = '03100'; city = 'Ciudad de México'; state = 'CDMX'; country = 'México'; address_type = 0; catalog_type = 1 } }
Paso 'POST /companies/{id}/addresses' $r ($r.Json.message)

# 4. Actualizar el límite de crédito.
$r = Llamar PATCH "/companies/$compania" @{ company = @{ credit_limit = 80000 } }
Paso 'PATCH /companies/{id}' $r "límite $($r.Json.credit_limit)"

# 5. Producto (la unidad debe existir en la empresa; se prueban otras comunes).
$producto = $null
foreach ($u in (@($Unidad) + @('Pieza', 'Servicio', 'Unidad de servicio', 'Unidad', 'No aplica') | Select-Object -Unique)) {
    $r = Llamar POST '/products' @{ product = @{ name = 'PRUEBA SERVICIO'; price = '2000.00'; issuing_company_id = $emisora; description = 'Renta de servidor'; unit_base = $u; series_sat = '81112200'; product_type = 3; extra_texts_attributes = @(@{ extra_text = 'Texto adicional para la factura' }) } }
    if ($r.Status -eq 201 -or -not ($r.Texto -match 'unidad base')) { break }
}
Exigir $r 'POST /products'; $producto = $r.Json.id; $codigoProducto = $r.Json.code
Paso 'POST /products' $r "id $producto · código $codigoProducto · unidad $u"

# 6. Pantalla de «nueva factura»: los catálogos.
$r = Llamar GET "/issuing_companies/$emisora/invoices/new"
Paso 'GET  .../invoices/new' $r ("catálogos: {0} clientes, {1} productos, {2} formas de pago, {3} regímenes, {4} usos de CFDI" -f @($r.Json.clients).Count, @($r.Json.products).Count, @($r.Json.payment_methods).Count, @($r.Json.tax_regimes).Count, @($r.Json.cfdis).Count)

# 7. Factura (borrador) con sus partidas; todavía no existe nada en CONTPAQi.
$r = Llamar POST "/issuing_companies/$emisora/invoices" @{ invoice = @{
        concept = $Concepto; series = $Serie; invoice_date = (Get-Date).ToString('yyyy-MM-dd'); client_code = $codigoCliente; payment_method = '03'; cfdi = 'G03'; tax_regime = '601'
        movements_attributes = @(@{ product_code = $codigoProducto; quantity = 2; vat_percentage = 16 }) } }
Exigir $r 'POST .../invoices'; $factura = $r.Json.id
Paso 'POST .../invoices' $r "id $factura · $($r.Json.payment_method)"

# 8. Otra partida, en una llamada aparte, y el listado de partidas.
$r = Llamar POST '/movements' @{ movement = @{ product_code = $codigoProducto; quantity = 1; invoice_id = $factura; vat_percentage = 16; vat_retention_percentage = 0 } }
Paso 'POST /movements' $r "id $($r.Json.id) · producto $($r.Json.product.name)"
$r = Llamar GET "/movements?invoice_id=$factura"
Paso 'GET  /movements?invoice_id=' $r "$(@($r.Json).Count) partidas"

# 9. Timbrar: crea el documento en CONTPAQi y lo timbra, todo en la misma respuesta.
$r = Llamar POST "/issuing_companies/$emisora/invoices/$factura/stamps"
Paso 'POST .../stamps' $r ("{0} · folio {1} · UUID {2}" -f $r.Json.message, $r.Json.folio, $r.Json.uuid)
$timbrada = ($r.Status -eq 200)

# 10. Detalle, XML y PDF.
$r = Llamar GET "/issuing_companies/$emisora/invoices/$factura"
Paso 'GET  .../invoices/{id}' $r ("{0} · folio {1} · {2} partida(s)" -f $r.Json.sat_status, $r.Json.folio, @($r.Json.movements).Count)
if ($timbrada) {
    $r = Llamar GET "/issuing_companies/$emisora/invoices/$factura/xml"
    Paso 'GET  .../invoices/{id}/xml' $r ("{0} · {1} bytes en base64" -f $r.Json.file_name, ([string]$r.Json.content_base64).Length)
    $r = Llamar GET "/issuing_companies/$emisora/invoices/$factura/document"
    Paso 'GET  .../invoices/{id}/document' $r ("{0} · {1} bytes en base64" -f $r.Json.file_name, ([string]$r.Json.content_base64).Length)

    if ($Cancelar) {
        $r = Llamar POST "/issuing_companies/$emisora/invoices/$factura/cancelate" @{ cancellation = @{ cancellation_reason = '02' } }
        Paso 'POST .../cancelate' $r ($r.Json.message)
    }
}

# 11. Repetir el timbrado no duplica nada.
$r = Llamar POST "/issuing_companies/$emisora/invoices/$factura/stamps"
Paso 'POST .../stamps (otra vez)' $r ($(if ($r.Json.errors) { $r.Json.errors -join ' | ' } else { $r.Json.message }))

# 12. Listados con paginación y orden.
$r = Llamar GET '/companies?limit=5&offset=0&order_by_field=id&order_by_direction=desc'
Paso 'GET  /companies (paginado)' $r "$(@($r.Json).Count) registro(s)"
$r = Llamar GET "/issuing_companies/$emisora/invoices?limit=5&order_by_field=id&order_by_direction=desc"
Paso 'GET  .../invoices (paginado)' $r "$(@($r.Json).Count) factura(s)"

# 13. Rutas que Acusa no ofrece todavía.
$r = Llamar GET "/issuing_companies/$emisora/invoices/$factura/preinvoice"
Paso 'GET  .../preinvoice' $r 'no disponible en esta versión'

Write-Host "`nListo. Compañía $codigoCliente, producto $codigoProducto y factura $factura creados.`n" -ForegroundColor Green
