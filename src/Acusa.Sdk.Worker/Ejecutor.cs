using System.Text.Json;
using Acusa.Contrato;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Documentos;
using Acusa.Sdk.Application.Productos;
using Acusa.Sdk.Application.Sesion;

namespace Acusa.Sdk.Worker;

/// <summary>
/// Traduce una tarea del orquestador a una llamada de los servicios de Acusa. No conoce HTTP ni la cola:
/// recibe el tipo de proceso y el payload, y devuelve el resultado listo para informar.
/// </summary>
public sealed class Ejecutor(ClienteService clientes, ProductoService productos, DocumentoService documentos)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task<ResultadoTarea> EjecutarAsync(TareaAsignada tarea, CancellationToken ct)
    {
        try
        {
            return tarea.TipoProceso switch
            {
                Operaciones.ClienteLeer => await LeerClienteAsync(tarea.Payload, ct),
                Operaciones.ClienteCrear => await CrearClienteAsync(tarea.Payload, ct),
                Operaciones.ProductoLeer => await LeerProductoAsync(tarea.Payload, ct),
                Operaciones.ProductoCrear => await CrearProductoAsync(tarea.Payload, ct),
                Operaciones.DocumentoLeer => await LeerDocumentoAsync(tarea.Payload, ct),
                Operaciones.DocumentoCrear => await CrearDocumentoAsync(tarea.Payload, ct),
                Operaciones.DocumentoEmitir => await EmitirDocumentoAsync(tarea.Payload, ct),
                Operaciones.DocumentoCancelar => await CancelarDocumentoAsync(tarea.Payload, ct),
                Operaciones.ClienteActualizar => await ActualizarClienteAsync(tarea.Payload, ct),
                Operaciones.ProductoActualizar => await ActualizarProductoAsync(tarea.Payload, ct),
                Operaciones.DireccionCrear => await GuardarDireccionAsync(tarea.Payload, ct),
                Operaciones.DireccionLeer => await LeerDireccionAsync(tarea.Payload, ct),
                Operaciones.DocumentoObtenerArchivo => await ObtenerArchivoAsync(tarea.Payload, ct),
                Operaciones.DocumentoEmitirLote => await LoteAsync(tarea.Payload, emitir: true, ct),
                Operaciones.DocumentoCancelarLote => await LoteAsync(tarea.Payload, emitir: false, ct),
                _ => Falla($"Este worker no implementa '{tarea.TipoProceso}'."),
            };
        }
        catch (SdkNoListoException ex)
        {
            return Falla(ex.Message);
        }
        catch (JsonException ex)
        {
            return Falla($"El payload no tiene el formato esperado: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Falla($"Error inesperado en el worker: {ex.Message}");
        }
    }

    private async Task<ResultadoTarea> LeerClienteAsync(JsonElement p, CancellationToken ct)
    {
        var codigo = Texto(p, "codigo");
        return await clientes.ObtenerAsync(codigo, ct) is { } c ? Ok(c) : Falla($"No existe el cliente {codigo}.");
    }

    private async Task<ResultadoTarea> CrearClienteAsync(JsonElement p, CancellationToken ct)
    {
        var nuevo = new NuevoCliente(
            Texto(p, "codigo"), Texto(p, "razonSocial"), TextoOpcional(p, "rfc"),
            EnteroOpcional(p, "tipoCliente") ?? EnteroOpcional(p, "tipo"),
            NumeroOpcional(p, "limiteCredito"));
        var r = await clientes.CrearAsync(nuevo, ct);
        return r.Tipo == TipoResultadoCrear.Creado ? Ok(r.Cliente) : Falla(r.Mensaje, r.CodigoSdk);
    }

    private async Task<ResultadoTarea> LeerProductoAsync(JsonElement p, CancellationToken ct)
    {
        var codigo = Texto(p, "codigo");
        return await productos.ObtenerAsync(codigo, ct) is { } x ? Ok(x) : Falla($"No existe el producto {codigo}.");
    }

    private async Task<ResultadoTarea> CrearProductoAsync(JsonElement p, CancellationToken ct)
    {
        var nuevo = new NuevoProducto(
            Texto(p, "codigo"), Texto(p, "nombre"), Texto(p, "unidadBase"), Numero(p, "precio1"), TextoOpcional(p, "descripcion"),
            TextoOpcional(p, "claveSat") ?? TextoOpcional(p, "claveSAT"),
            EnteroOpcional(p, "tipoProducto") ?? EnteroOpcional(p, "tipo"),
            TextosExtra(p));
        var r = await productos.CrearAsync(nuevo, ct);
        return r.Tipo == TipoResultadoCrear.Creado ? Ok(r.Producto) : Falla(r.Mensaje, r.CodigoSdk);
    }

    private async Task<ResultadoTarea> LeerDocumentoAsync(JsonElement p, CancellationToken ct)
    {
        var concepto = Texto(p, "concepto");
        var serie = TextoOpcional(p, "serie");
        var folio = p.TryGetProperty("folio", out var f) && f.TryGetInt64(out var n) ? n : 0;
        return await documentos.ObtenerAsync(concepto, serie, folio, ct) is { } d
            ? Ok(d)
            : Falla($"No existe el documento {concepto} {serie} {folio}.");
    }

    private async Task<ResultadoTarea> CrearDocumentoAsync(JsonElement p, CancellationToken ct)
    {
        var partidas = new List<NuevaPartida>();
        // «movimientos» es el nombre vigente; «partidas» se sigue aceptando por compatibilidad.
        if ((p.TryGetProperty("movimientos", out var lista) || p.TryGetProperty("partidas", out lista)) && lista.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in lista.EnumerateArray())
            {
                partidas.Add(new NuevaPartida(
                    Texto(x, "productoCodigo"), Numero(x, "unidades"), Numero(x, "precio"), TextoOpcional(x, "almacen"),
                    NumeroOpcional(x, "ivaPorcentaje"), NumeroOpcional(x, "retencionIvaPorcentaje"), NumeroOpcional(x, "iepsPorcentaje")));
            }
        }

        var textoFecha = TextoOpcional(p, "fecha");
        DateOnly? fecha = null;
        if (!string.IsNullOrWhiteSpace(textoFecha))
        {
            if (!DateOnly.TryParseExact(textoFecha.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var f))
            {
                return Falla("La fecha debe venir como año-mes-día, por ejemplo 2026-11-09.");
            }

            fecha = f;
        }

        var nuevo = new NuevoDocumento(
            Texto(p, "concepto"), TextoOpcional(p, "serie"), Texto(p, "clienteCodigo"), Texto(p, "referencia"), partidas,
            fecha, TextoOpcional(p, "formaPago"), TextoOpcional(p, "usoCfdi"));
        var r = await documentos.CrearAsync(nuevo, ct);
        return r.Tipo == TipoResultadoCrear.Creado ? Ok(r.Documento) : Falla(r.Mensaje, r.CodigoSdk);
    }

    private async Task<ResultadoTarea> EmitirDocumentoAsync(JsonElement p, CancellationToken ct)
    {
        var r = await documentos.EmitirAsync(Referencia(p), ct);
        return Resultado(r);
    }

    private async Task<ResultadoTarea> CancelarDocumentoAsync(JsonElement p, CancellationToken ct)
    {
        var uuid = TextoOpcional(p, "uuidSustituto") ?? TextoOpcional(p, "uuidRemplaza");
        var r = await documentos.CancelarAsync(Referencia(p), TextoOpcional(p, "motivo"), uuid, ct);
        return Resultado(r);
    }

    private async Task<ResultadoTarea> ActualizarProductoAsync(JsonElement p, CancellationToken ct)
    {
        decimal? precio = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("precio1", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)
            ? d
            : null;
        var cambios = new CambiosProducto(
            TextoOpcional(p, "nombre"), TextoOpcional(p, "descripcion"), precio, TextoOpcional(p, "claveSat") ?? TextoOpcional(p, "claveSAT"), TextosExtra(p));
        var r = await productos.ActualizarAsync(Texto(p, "codigo"), cambios, ct);
        return r.Tipo == TipoResultadoActualizar.Actualizado ? Ok(r.Producto) : Falla(r.Mensaje, r.CodigoSdk);
    }

    /// <summary>Tipo de dirección: 0 o "fiscal" (por omisión), 1 o "envio".</summary>
    private static int TipoDireccion(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("tipo", out var t))
        {
            return 0;
        }

        return t.ValueKind switch
        {
            JsonValueKind.Number when t.TryGetInt32(out var n) => n,
            JsonValueKind.String => t.GetString()?.Trim().ToLowerInvariant() switch
            {
                "fiscal" or "0" => 0,
                "envio" or "envío" or "1" => 1,
                _ => -1,
            },
            _ => -1,
        };
    }

    private async Task<ResultadoTarea> GuardarDireccionAsync(JsonElement p, CancellationToken ct)
    {
        var d = new Direccion(
            TextoOpcional(p, "codigoCliente") ?? Texto(p, "codigo"), TipoDireccion(p),
            TextoOpcional(p, "calle"), TextoOpcional(p, "numeroExterior"), TextoOpcional(p, "numeroInterior"), TextoOpcional(p, "colonia"),
            Texto(p, "codigoPostal"), TextoOpcional(p, "municipio"), TextoOpcional(p, "ciudad"), TextoOpcional(p, "estado"), TextoOpcional(p, "pais"));
        var r = await clientes.GuardarDireccionAsync(d, ct);
        return r.Tipo == TipoResultadoDireccion.Guardada ? Ok(r.Direccion) : Falla(r.Mensaje, r.CodigoSdk);
    }

    private async Task<ResultadoTarea> LeerDireccionAsync(JsonElement p, CancellationToken ct)
    {
        var codigo = TextoOpcional(p, "codigoCliente") ?? Texto(p, "codigo");
        var tipo = TipoDireccion(p);
        if (tipo is not (0 or 1))
        {
            return Falla("El tipo de dirección es fiscal (0) o envío (1).");
        }

        return await clientes.ObtenerDireccionAsync(codigo, tipo, ct) is { } d
            ? Ok(d)
            : Falla($"El cliente {codigo} no tiene dirección de tipo {tipo}.");
    }

    private async Task<ResultadoTarea> ActualizarClienteAsync(JsonElement p, CancellationToken ct)
    {
        var cambios = new CambiosCliente(
            TextoOpcional(p, "razonSocial"), TextoOpcional(p, "rfc"), TextoOpcional(p, "regimenFiscal"),
            TextoOpcional(p, "usoCfdi"), TextoOpcional(p, "email"), NumeroOpcional(p, "limiteCredito"));
        var r = await clientes.ActualizarAsync(Texto(p, "codigo"), cambios, ct);
        return r.Tipo == TipoResultadoActualizar.Actualizado ? Ok(r.Cliente) : Falla(r.Mensaje, r.CodigoSdk);
    }

    /// <summary>
    /// Timbrado o cancelación masivos. Procesa los documentos de uno en uno y sigue con el siguiente si uno falla: el
    /// resultado detalla cada documento. La tarea queda como fallida si alguno falló, pero el detalle siempre viaja.
    /// </summary>
    private async Task<ResultadoTarea> LoteAsync(JsonElement p, bool emitir, CancellationToken ct)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("documentos", out var lista) || lista.ValueKind != JsonValueKind.Array)
        {
            return Falla("Indique 'documentos': una lista con documentoId, o concepto, serie y folio de cada uno.");
        }

        var elementos = lista.EnumerateArray().ToList();
        if (elementos.Count is 0 or > Operaciones.MaximoDocumentosPorLote)
        {
            return Falla($"Un lote lleva entre 1 y {Operaciones.MaximoDocumentosPorLote} documentos; recibió {elementos.Count}.");
        }

        var detalle = new List<object>();
        var correctos = 0;
        for (var i = 0; i < elementos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var elemento = elementos[i];
            var referencia = Referencia(elemento);
            ResultadoDocumento r;
            try
            {
                r = emitir
                    ? await documentos.EmitirAsync(referencia, ct)
                    : await documentos.CancelarAsync(referencia, TextoOpcional(elemento, "motivo"), TextoOpcional(elemento, "uuidSustituto") ?? TextoOpcional(elemento, "uuidRemplaza"), ct);
            }
            catch (Exception ex) when (ex is SdkNoListoException or JsonException)
            {
                r = new ResultadoDocumento(TipoResultadoDocumento.ErrorSdk, Mensaje: ex.Message);
            }

            var hecho = r.Tipo == TipoResultadoDocumento.Hecho;
            correctos += hecho ? 1 : 0;
            detalle.Add(new
            {
                indice = i + 1,
                documento = referencia.ToString(),
                ok = hecho,
                uuid = r.Documento?.Uuid,
                cancelado = r.Documento?.Cancelado,
                error = hecho ? null : r.Mensaje,
                aviso = r.Aviso,
                codigoSdk = r.CodigoSdk,
            });
        }

        var fallidos = elementos.Count - correctos;
        var resumen = new { total = elementos.Count, correctos, fallidos, resultados = detalle };
        return fallidos == 0
            ? Ok(resumen)
            : new ResultadoTarea(false, JsonSerializer.SerializeToElement(resumen, Json), $"{fallidos} de {elementos.Count} documentos no se procesaron. Revise el detalle de cada uno.", null);
    }

    private async Task<ResultadoTarea> ObtenerArchivoAsync(JsonElement p, CancellationToken ct)
    {
        var texto = (TextoOpcional(p, "formato") ?? "ambos").Trim().ToLowerInvariant();
        FormatoArchivo? formato = texto switch
        {
            "xml" => FormatoArchivo.Xml,
            "pdf" => FormatoArchivo.Pdf,
            "ambos" or "xml,pdf" or "ambos formatos" => FormatoArchivo.Ambos,
            _ => null,
        };
        if (formato is null)
        {
            return Falla("El formato debe ser xml, pdf o ambos.");
        }

        var incluir = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("incluirContenido", out var i) && i.ValueKind == JsonValueKind.True;
        var r = await documentos.ObtenerArchivosAsync(Referencia(p), formato.Value, incluir, ct);
        return r.Tipo == TipoResultadoDocumento.Hecho
            ? Ok(new { documento = r.Documento, uuid = r.Documento?.Uuid, archivos = r.Archivos, aviso = r.Aviso })
            : Falla(r.Mensaje, r.CodigoSdk);
    }

    private static ResultadoTarea Resultado(ResultadoDocumento r) =>
        r.Tipo == TipoResultadoDocumento.Hecho
            ? Ok(new { documento = r.Documento, uuid = r.Documento?.Uuid, aviso = r.Aviso })
            : Falla(r.Mensaje, r.CodigoSdk);

    /// <summary>Documento por <c>documentoId</c>, o por <c>concepto</c>, <c>serie</c> y <c>folio</c>.</summary>
    private static ReferenciaDocumento Referencia(JsonElement p)
    {
        int? id = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("documentoId", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n)
            ? n
            : null;
        var folio = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("folio", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt64(out var l) ? l : 0;
        return new ReferenciaDocumento(id, Texto(p, "concepto"), TextoOpcional(p, "serie"), folio);
    }

    private static ResultadoTarea Ok(object? datos) =>
        new(true, JsonSerializer.SerializeToElement(datos, Json), null, null);

    private static ResultadoTarea Falla(string? mensaje, int? codigoSdk = null) =>
        new(false, null, mensaje ?? "La operación no se completó.", codigoSdk);

    private static string Texto(JsonElement p, string nombre) => TextoOpcional(p, nombre) ?? string.Empty;

    private static string? TextoOpcional(JsonElement p, string nombre) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Un entero; si el valor trae decimales devuelve 0, que la validación rechaza con un mensaje claro.</summary>
    private static int? EnteroOpcional(JsonElement p, string nombre) =>
        NumeroOpcional(p, nombre) is { } n ? (n == decimal.Truncate(n) && Math.Abs(n) < int.MaxValue ? (int)n : 0) : null;

    private static List<string>? TextosExtra(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("textosExtra", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : null;

    private static decimal? NumeroOpcional(JsonElement p, string nombre) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static decimal Numero(JsonElement p, string nombre) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : 0m;
}
