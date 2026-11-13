using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acusa.Contrato;
using Acusa.Orquestador.Facturacion;

namespace Acusa.Orquestador.Rest;

/// <summary>
/// API REST de Acusa Central: los procesos que ejecuta el worker en CONTPAQi Comercial (clientes, productos, documentos, timbrado,
/// archivos y cancelación), expuestos como recursos en lugar de una sola ruta con <c>tipoProceso</c>. La empresa de CONTPAQi va en
/// la ruta (<c>/api/empresas/{empresa}/…</c>). Cada petición espera al worker hasta 60 s: si responde, la misma respuesta trae el
/// resultado; si no, responde 202 con la tarea. <c>Idempotency-Key</c> evita duplicar una escritura al reintentar.
/// </summary>
public static class ApiRest
{
    private delegate Task<IResult> Manejo(Contexto c);

    private sealed record Contexto(HttpContext Http, Identidad Quien, IEjecutorOrdenes Ejecutor, Opciones Opciones, string Empresa, JsonObject? Cuerpo, string? Llave, CancellationToken Ct)
    {
        public string Ruta(string nombre) => Convert.ToString(Http.Request.RouteValues[nombre], CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public static void Mapear(WebApplication app)
    {
        // ---- Empresas -----------------------------------------------------------------------------------------------
        app.MapGet("/api/empresas", (HttpContext http, Almacen a, Seguridad s) =>
            Facturacion.FacturacionRutas.AutenticarPublico(http, a, s) is { } q
                ? Results.Json(a.EmpresasDelCliente(q.ClienteId))
                : NoAutorizado());

        var e = app.MapGroup("/api/empresas/{empresa}");

        // ---- Clientes -----------------------------------------------------------------------------------------------
        Ruta(e, "POST", "/clientes", true, c => Orden(c, Operaciones.ClienteCrear, Campos(c.Cuerpo!, "codigo", "razonSocial", "rfc", "tipoCliente", "limiteCredito", "email", "regimenFiscal", "usoCfdi"), 201,
            Requeridos(c.Cuerpo!, ("codigo", "Codigo"), ("razonSocial", "Razon social"), ("rfc", "Rfc"))));
        Ruta(e, "POST", "/clientes/{codigo}/direcciones", true, c =>
        {
            var p = Campos(c.Cuerpo!, "tipo", "calle", "numeroExterior", "numeroInterior", "colonia", "codigoPostal", "municipio", "ciudad", "estado", "pais");
            p["codigoCliente"] = c.Ruta("codigo");
            return Orden(c, Operaciones.DireccionCrear, p, 201, Requeridos(c.Cuerpo!, ("codigoPostal", "Codigo postal")));
        });
        Ruta(e, "GET", "/clientes/{codigo}/direcciones/{tipo}", false, c => Orden(c, Operaciones.DireccionLeer, new { codigoCliente = c.Ruta("codigo"), tipo = c.Ruta("tipo") }));
        Ruta(e, "GET", "/clientes/{codigo}", false, c => Orden(c, Operaciones.ClienteLeer, new { codigo = c.Ruta("codigo") }));
        Ruta(e, "PATCH", "/clientes/{codigo}", true, c =>
        {
            var p = Campos(c.Cuerpo!, "razonSocial", "rfc", "limiteCredito", "email", "regimenFiscal", "usoCfdi");
            p["codigo"] = c.Ruta("codigo");
            return Orden(c, Operaciones.ClienteActualizar, p, 200, p.Count == 1 ? ["No hay datos que actualizar"] : []);
        });

        // ---- Productos ----------------------------------------------------------------------------------------------
        Ruta(e, "POST", "/productos", true, c => Orden(c, Operaciones.ProductoCrear, Campos(c.Cuerpo!, "codigo", "nombre", "unidadBase", "precio1", "descripcion", "claveSat", "tipoProducto", "textosExtra"), 201,
            Requeridos(c.Cuerpo!, ("codigo", "Codigo"), ("nombre", "Nombre"), ("unidadBase", "Unidad base"))));
        Ruta(e, "GET", "/productos/{codigo}", false, c => Orden(c, Operaciones.ProductoLeer, new { codigo = c.Ruta("codigo") }));
        Ruta(e, "PATCH", "/productos/{codigo}", true, c =>
        {
            var p = Campos(c.Cuerpo!, "nombre", "descripcion", "precio1", "claveSat", "textosExtra");
            p["codigo"] = c.Ruta("codigo");
            return Orden(c, Operaciones.ProductoActualizar, p, 200, p.Count == 1 ? ["No hay datos que actualizar"] : []);
        });

        // ---- Documentos ---------------------------------------------------------------------------------------------
        Ruta(e, "POST", "/documentos", true, CrearDocumentoAsync);
        Ruta(e, "POST", "/documentos/timbrado", true, c => LoteAsync(c, Operaciones.DocumentoEmitirLote, cancelar: false));
        Ruta(e, "POST", "/documentos/cancelacion", true, c => LoteAsync(c, Operaciones.DocumentoCancelarLote, cancelar: true));
        Ruta(e, "GET", "/documentos/{concepto}/{serie}/{folio}", false, LeerDocumentoAsync);
        Ruta(e, "POST", "/documentos/{concepto}/{serie}/{folio}/timbrado", false, TimbrarAsync);
        Ruta(e, "GET", "/documentos/{concepto}/{serie}/{folio}/xml", false, c => ArchivoAsync(c, "xml"));
        Ruta(e, "GET", "/documentos/{concepto}/{serie}/{folio}/pdf", false, c => ArchivoAsync(c, "pdf"));
        Ruta(e, "POST", "/documentos/{concepto}/{serie}/{folio}/cancelacion", true, CancelarAsync);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Mapeo de rutas
    // ------------------------------------------------------------------------------------------------------------------

    private static void Ruta(RouteGroupBuilder g, string metodo, string patron, bool conCuerpo, Manejo f) =>
        g.MapMethods(patron, [metodo], async (HttpContext http, Almacen a, Seguridad s, IEjecutorOrdenes ejecutor, Opciones o) =>
        {
            if (Facturacion.FacturacionRutas.AutenticarPublico(http, a, s) is not { } quien) return NoAutorizado();
            PeticionActual.Texto = $"{metodo} {http.Request.Path}";

            var empresa = Convert.ToString(http.Request.RouteValues["empresa"], CultureInfo.InvariantCulture) ?? string.Empty;
            if (!a.EmpresaDelCliente(quien.ClienteId, empresa)) return Results.Json(new { error = $"La empresa '{empresa}' no está registrada para este cliente." }, statusCode: 404);

            JsonObject? cuerpo = null;
            if (conCuerpo)
            {
                try { cuerpo = await http.Request.ReadFromJsonAsync<JsonObject>(http.RequestAborted); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException) { return Results.Json(new { error = "El cuerpo no es un objeto JSON válido." }, statusCode: 400); }
                if (cuerpo is null) return Results.Json(new { error = "El cuerpo es obligatorio." }, statusCode: 400);
                if (JsonSerializer.SerializeToElement(cuerpo) is { ValueKind: JsonValueKind.Object } objeto && CamposSecretos.Buscar(objeto) is { } campo)
                {
                    return Results.Json(new { error = $"El campo '{campo}' parece una contraseña y no se acepta: la contraseña del certificado se configura en el worker." }, statusCode: 400);
                }
            }

            var llave = http.Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(llave) && (llave.Length > 100 || !llave.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':')))
            {
                return Results.Json(new { error = "Idempotency-Key admite hasta 100 caracteres: letras, números, guion, guion bajo, punto y dos puntos." }, statusCode: 400);
            }

            return await f(new Contexto(http, quien, ejecutor, o, empresa, cuerpo, string.IsNullOrEmpty(llave) ? null : llave, http.RequestAborted));
        });

    private static IResult NoAutorizado() => Results.Json(new { error = "Falta la cabecera Authorization: Bearer <llave de API o token>, o no es válida." }, statusCode: 401);

    // ------------------------------------------------------------------------------------------------------------------
    // Una orden simple
    // ------------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> Orden(Contexto c, string tipo, object payload, int estadoOk = 200, IReadOnlyList<string>? errores = null)
    {
        if (errores is { Count: > 0 }) return Results.Json(new { errors = errores }, statusCode: 422);
        var r = await c.Ejecutor.EjecutarAsync(c.Quien.ClienteId, c.Empresa, tipo, payload, c.Llave, c.Ct);
        return Responder(c, r, estadoOk);
    }

    private static IResult Responder(Contexto c, ResultadoOrden r, int estadoOk = 200)
    {
        if (r.Ok) return Results.Json(new { tareaId = r.TareaId, resultado = r.Datos }, statusCode: estadoOk);
        if (r.EnCurso)
        {
            c.Http.Response.Headers.Location = $"/api/sdk/tasks/{r.TareaId}";
            return Results.Json(new { tareaId = r.TareaId, estado = "EnProceso", mensaje = "El worker no respondió a tiempo; consulte la tarea." }, statusCode: 202);
        }

        return Results.Json(new { error = r.Error, codigoSdk = r.CodigoSdk, tareaId = r.TareaId }, statusCode: 422);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Documentos
    // ------------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> CrearDocumentoAsync(Contexto c)
    {
        var d = c.Cuerpo!;
        var errores = new List<string>();
        foreach (var (campo, etiqueta) in new[] { ("concepto", "Concepto"), ("clienteCodigo", "Cliente codigo"), ("fecha", "Fecha"), ("formaPago", "Forma de pago"), ("usoCfdi", "Uso de CFDI"), ("regimenFiscal", "Regimen fiscal") })
        {
            if (string.IsNullOrWhiteSpace(Txt(d[campo]))) errores.Add($"{etiqueta} no puede estar vacio");
        }

        if (Txt(d["fecha"]) is { Length: > 0 } fecha && !DateOnly.TryParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errores.Add("Fecha debe tener el formato aaaa-mm-dd");
        if (Txt(d["formaPago"]) is { Length: > 0 } fp && !FacturacionServicio.FormasDePago.ContainsKey(fp)) errores.Add("Forma de pago no es una clave del SAT");
        if (Txt(d["usoCfdi"]) is { Length: > 0 } uso && !FacturacionServicio.UsosCfdi.ContainsKey(uso.ToUpperInvariant())) errores.Add("Uso de CFDI no es una clave del SAT");
        if (Txt(d["regimenFiscal"]) is { Length: > 0 } rg && !FacturacionServicio.Regimenes.ContainsKey(rg)) errores.Add("Regimen fiscal no es una clave del SAT");

        var movimientos = (d["movimientos"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (movimientos.Count == 0) errores.Add("Movimientos: se necesita al menos uno");
        for (var i = 0; i < movimientos.Count; i++)
        {
            var m = movimientos[i];
            if (string.IsNullOrWhiteSpace(Txt(m["productoCodigo"]))) errores.Add($"Movimiento {i + 1}: producto codigo no puede estar vacio");
            if (Num(m["unidades"]) is not > 0) errores.Add($"Movimiento {i + 1}: unidades debe ser mayor que 0");
            if (Num(m["precio"]) is not >= 0) errores.Add($"Movimiento {i + 1}: precio debe ser 0 o mayor");
            foreach (var campo in new[] { "ivaPorcentaje", "retencionIvaPorcentaje", "iepsPorcentaje" })
            {
                if (Num(m[campo]) is < 0 or > 100) errores.Add($"Movimiento {i + 1}: {campo} debe estar entre 0 y 100");
            }
        }

        if (errores.Count > 0) return Results.Json(new { errors = errores }, statusCode: 422);

        // El régimen fiscal y el uso de CFDI viven en el cliente de Comercial: se actualizan antes de crear el documento.
        var cliente = Txt(d["clienteCodigo"])!.Trim();
        var act = await c.Ejecutor.EjecutarAsync(c.Quien.ClienteId, c.Empresa, Operaciones.ClienteActualizar,
            new { codigo = cliente, regimenFiscal = Txt(d["regimenFiscal"])!.Trim(), usoCfdi = Txt(d["usoCfdi"])!.Trim().ToUpperInvariant() }, c.Llave is null ? null : c.Llave + ":cliente", c.Ct);
        if (!act.Ok) return Responder(c, act);

        var referencia = Txt(d["referencia"]) is { Length: > 0 } rf ? rf.Trim() : ReferenciaNueva(c);
        var crear = await c.Ejecutor.EjecutarAsync(c.Quien.ClienteId, c.Empresa, Operaciones.DocumentoCrear, new
        {
            concepto = Txt(d["concepto"])!.Trim(), serie = Txt(d["serie"]) is { Length: > 0 } s ? s.Trim() : null, clienteCodigo = cliente, referencia,
            fecha = Txt(d["fecha"]), formaPago = Txt(d["formaPago"]), usoCfdi = Txt(d["usoCfdi"])!.Trim().ToUpperInvariant(),
            partidas = movimientos.Select(m => new
            {
                productoCodigo = Txt(m["productoCodigo"])!.Trim(), unidades = Num(m["unidades"]), precio = Num(m["precio"]),
                almacen = Txt(m["almacen"]) is { Length: > 0 } al ? al.Trim() : c.Opciones.FacturacionAlmacen,
                ivaPorcentaje = Num(m["ivaPorcentaje"]), retencionIvaPorcentaje = Num(m["retencionIvaPorcentaje"]), iepsPorcentaje = Num(m["iepsPorcentaje"]),
            }).ToList(),
        }, c.Llave is null ? null : c.Llave + ":documento", c.Ct);
        return Responder(c, crear, 201);
    }

    private static string ReferenciaNueva(Contexto c)
    {
        var prefijo = c.Opciones.FacturacionPrefijoReferencia;
        var sufijo = c.Llave is null
            ? DateTime.UtcNow.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture)
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.Llave)))[..12];
        var referencia = prefijo + sufijo;
        return referencia.Length > 20 ? referencia[..20] : referencia;
    }

    private static bool Clave(Contexto c, out string concepto, out string? serie, out int folio, out IResult? error)
    {
        concepto = c.Ruta("concepto");
        var s = c.Ruta("serie");
        serie = s == "-" ? null : s;
        folio = 0;
        error = null;
        if (!int.TryParse(c.Ruta("folio"), NumberStyles.None, CultureInfo.InvariantCulture, out folio) || folio <= 0)
        {
            error = Results.Json(new { error = "El folio debe ser un número entero positivo." }, statusCode: 400);
            return false;
        }

        return true;
    }

    private static async Task<IResult> LeerDocumentoAsync(Contexto c)
    {
        if (!Clave(c, out var concepto, out var serie, out var folio, out var error)) return error!;
        return await Orden(c, Operaciones.DocumentoLeer, new { concepto, serie, folio });
    }

    /// <summary>Timbrado o cancelación de varios documentos en una sola orden (hasta 25). El worker los procesa de uno en uno y detalla cada resultado.</summary>
    private static async Task<IResult> LoteAsync(Contexto c, string tipo, bool cancelar)
    {
        var lista = (c.Cuerpo!["documentos"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var errores = new List<string>();
        if (lista.Count is 0 or > Operaciones.MaximoDocumentosPorLote) errores.Add($"Documentos: un lote lleva entre 1 y {Operaciones.MaximoDocumentosPorLote}");
        for (var i = 0; i < lista.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(Txt(lista[i]["concepto"]))) errores.Add($"Documento {i + 1}: concepto no puede estar vacio");
            if (Num(lista[i]["folio"]) is not > 0) errores.Add($"Documento {i + 1}: folio debe ser un número positivo");
            if (cancelar && Txt(lista[i]["motivo"]) is not ("01" or "02" or "03" or "04")) errores.Add($"Documento {i + 1}: motivo debe ser 01, 02, 03 o 04");
        }

        var documentos = lista.Select(d => cancelar
            ? (object)new { concepto = Txt(d["concepto"]), serie = Txt(d["serie"]) is { Length: > 0 } s && s != "-" ? s : null, folio = Num(d["folio"]), motivo = Txt(d["motivo"]), uuidSustituto = Txt(d["uuidSustituto"]) }
            : new { concepto = Txt(d["concepto"]), serie = Txt(d["serie"]) is { Length: > 0 } s2 && s2 != "-" ? s2 : null, folio = Num(d["folio"]) }).ToList();
        return await Orden(c, tipo, new { documentos }, 200, errores);
    }

    private static async Task<IResult> TimbrarAsync(Contexto c)
    {
        if (!Clave(c, out var concepto, out var serie, out var folio, out var error)) return error!;
        var emitir = await c.Ejecutor.EjecutarAsync(c.Quien.ClienteId, c.Empresa, Operaciones.DocumentoEmitir, new { concepto, serie, folio }, c.Llave, c.Ct);
        if (!emitir.Ok) return Responder(c, emitir);

        var uuid = emitir.Datos is { } d && d.TryGetProperty("uuid", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;

        // El XML timbrado va en texto plano en su propio campo. Si Comercial no lo entrega a tiempo, el timbrado sigue siendo
        // válido y el XML se pide después con GET …/xml.
        string? xml = null;
        try
        {
            var arch = await Archivo(c, concepto, serie, folio, "xml");
            if (arch.Ok && ContenidoBase64(arch, "xml") is { } b64) xml = FacturacionServicio.DecodificarXml(b64);
        }
        catch (Exception) { /* el XML se puede pedir después */ }

        return Results.Json(new { tareaId = emitir.TareaId, concepto, serie, folio, uuid, estado = "timbrada", xml, resultado = emitir.Datos }, statusCode: 200);
    }

    private static Task<ResultadoOrden> Archivo(Contexto c, string concepto, string? serie, int folio, string formato) =>
        c.Ejecutor.EjecutarAsync(c.Quien.ClienteId, c.Empresa, Operaciones.DocumentoObtenerArchivo, new { concepto, serie, folio, formato, incluirContenido = true }, null, c.Ct);

    private static string? ContenidoBase64(ResultadoOrden r, string formato)
    {
        if (r.Datos is not { } d || !d.TryGetProperty("archivos", out var a) || a.ValueKind != JsonValueKind.Array) return null;
        foreach (var x in a.EnumerateArray())
        {
            if (x.TryGetProperty("tipo", out var t) && string.Equals(t.GetString(), formato, StringComparison.OrdinalIgnoreCase)
                && x.TryGetProperty("base64", out var b) && b.ValueKind == JsonValueKind.String) return b.GetString();
        }

        return null;
    }

    private static async Task<IResult> ArchivoAsync(Contexto c, string formato)
    {
        if (!Clave(c, out var concepto, out var serie, out var folio, out var error)) return error!;
        var r = await Archivo(c, concepto, serie, folio, formato);
        if (!r.Ok) return Responder(c, r);
        var b64 = ContenidoBase64(r, formato);
        if (b64 is null) return Results.Json(new { error = $"Comercial no entregó el archivo {formato.ToUpperInvariant()} (el documento debe estar timbrado)." }, statusCode: 422);
        var nombre = $"{serie}{folio}.{formato}";
        return formato == "xml"
            ? Results.Content(FacturacionServicio.DecodificarXml(b64) ?? string.Empty, "application/xml; charset=utf-8")
            : Results.File(Convert.FromBase64String(b64), "application/pdf", nombre);
    }

    private static async Task<IResult> CancelarAsync(Contexto c)
    {
        if (!Clave(c, out var concepto, out var serie, out var folio, out var error)) return error!;
        var motivo = Txt(c.Cuerpo!["motivo"])?.Trim();
        var sustituto = Txt(c.Cuerpo!["uuidSustituto"])?.Trim();
        var errores = new List<string>();
        if (motivo is not ("01" or "02" or "03" or "04")) errores.Add("Motivo debe ser 01, 02, 03 o 04");
        if (motivo == "01" && string.IsNullOrWhiteSpace(sustituto)) errores.Add("El motivo 01 requiere uuidSustituto");
        return await Orden(c, Operaciones.DocumentoCancelar, new { concepto, serie, folio, motivo, uuidSustituto = string.IsNullOrWhiteSpace(sustituto) ? null : sustituto }, 200, errores);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Utilidades
    // ------------------------------------------------------------------------------------------------------------------

    private static string? Txt(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();

    private static decimal? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var d)) return d;
        return v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    /// <summary>Copia solo los campos permitidos del cuerpo: lo que no esté en la lista no llega al worker.</summary>
    private static JsonObject Campos(JsonObject cuerpo, params string[] permitidos)
    {
        var r = new JsonObject();
        foreach (var k in permitidos)
        {
            if (cuerpo[k] is { } v) r[k] = v.DeepClone();
        }

        return r;
    }

    private static List<string> Requeridos(JsonObject cuerpo, params (string Campo, string Etiqueta)[] campos) =>
        campos.Where(x => string.IsNullOrWhiteSpace(Txt(cuerpo[x.Campo]))).Select(x => $"{x.Etiqueta} no puede estar vacio").ToList();
}
