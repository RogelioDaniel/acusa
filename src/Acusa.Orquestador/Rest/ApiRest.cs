using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acusa.Contrato;
using Acusa.Orquestador.Facturacion;

namespace Acusa.Orquestador.Rest;

public static class ApiRest
{
    public const string TipoCliente = "cliente";
    public const string TipoProducto = "producto";
    public const string TipoDocumento = "documento";

    private const int DireccionFiscal = 0;
    private const int DireccionEnvio = 1;

    private static readonly string[] CamposDireccion = ["calle", "numeroExterior", "numeroInterior", "colonia", "codigoPostal", "municipio", "ciudad", "estado", "pais"];

    private delegate Task<IResult> Manejo(Contexto contexto);

    private sealed record Contexto(
        HttpContext Http,
        Identidad Quien,
        Almacen Almacen,
        IEjecutorOrdenes Ejecutor,
        Opciones Opciones,
        string Empresa,
        RecursoAcusa? Recurso,
        JsonObject? Cuerpo,
        string? Llave,
        CancellationToken Cancelacion)
    {
        public string Consulta(string nombre) => Http.Request.Query[nombre].FirstOrDefault()?.Trim() ?? string.Empty;

        public string? LlaveCon(string sufijo)
        {
            if (Llave is null)
            {
                return null;
            }

            return Llave + ":" + sufijo;
        }

        public Task<ResultadoOrden> Ejecutar(string tipo, object payload, string? llave) =>
            Ejecutor.EjecutarAsync(Quien.ClienteId, Empresa, tipo, payload, llave, Cancelacion);
    }

    public static void Mapear(WebApplication aplicacion)
    {
        aplicacion.MapGet("/api/empresas/listar", (HttpContext http, Almacen almacen, Seguridad seguridad) =>
        {
            if (FacturacionRutas.AutenticarPublico(http, almacen, seguridad) is not { } quien)
            {
                return Errores.NoAutorizado();
            }

            return Results.Json(almacen.EmpresasConId(quien.ClienteId));
        });

        var porEmpresa = aplicacion.MapGroup("/api/empresas/{empresaId:long}");
        RutaDeEmpresa(porEmpresa, "POST", "/clientes/crear", true, CrearClienteAsync);
        RutaDeEmpresa(porEmpresa, "GET", "/clientes/buscar", false, BuscarClienteAsync);
        RutaDeEmpresa(porEmpresa, "POST", "/productos/crear", true, CrearProductoAsync);
        RutaDeEmpresa(porEmpresa, "GET", "/productos/buscar", false, BuscarProductoAsync);
        RutaDeEmpresa(porEmpresa, "POST", "/documentos/crear", true, CrearDocumentoAsync);
        RutaDeEmpresa(porEmpresa, "GET", "/documentos/buscar", false, BuscarDocumentoAsync);

        RutaDeRecurso(aplicacion, "GET", "/api/clientes/{id:long}/consultar", TipoCliente, false, LeerClienteAsync);
        RutaDeRecurso(aplicacion, "PATCH", "/api/clientes/{id:long}/actualizar", TipoCliente, true, ActualizarClienteAsync);
        RutaDeRecurso(aplicacion, "GET", "/api/clientes/{id:long}/direcciones/consultar", TipoCliente, false, LeerDireccionesAsync);
        RutaDeRecurso(aplicacion, "POST", "/api/clientes/{id:long}/direcciones/crear", TipoCliente, true, CrearDireccionAsync);
        RutaDeRecurso(aplicacion, "PATCH", "/api/clientes/{id:long}/direcciones/actualizar", TipoCliente, true, ActualizarDireccionAsync);

        RutaDeRecurso(aplicacion, "GET", "/api/productos/{id:long}/consultar", TipoProducto, false, LeerProductoAsync);
        RutaDeRecurso(aplicacion, "PATCH", "/api/productos/{id:long}/actualizar", TipoProducto, true, ActualizarProductoAsync);

        RutaDeRecurso(aplicacion, "GET", "/api/documentos/{id:long}/consultar", TipoDocumento, false, LeerDocumentoAsync);
        RutaDeRecurso(aplicacion, "POST", "/api/documentos/{id:long}/timbrar", TipoDocumento, false, TimbrarAsync);
        RutaDeRecurso(aplicacion, "POST", "/api/documentos/{id:long}/cancelar", TipoDocumento, true, CancelarAsync);
        RutaDeRecurso(aplicacion, "GET", "/api/documentos/{id:long}/descargar-xml", TipoDocumento, false, contexto => ArchivoAsync(contexto, "xml"));
        RutaDeRecurso(aplicacion, "GET", "/api/documentos/{id:long}/descargar-pdf", TipoDocumento, false, contexto => ArchivoAsync(contexto, "pdf"));

        RutaGeneral(aplicacion, "POST", "/api/documentos/timbrar", true, contexto => VariosAsync(contexto, cancelar: false));
        RutaGeneral(aplicacion, "POST", "/api/documentos/cancelar", true, contexto => VariosAsync(contexto, cancelar: true));

        RutaGeneral(aplicacion, "GET", "/api/tareas/{tareaId}/consultar", false, ConsultarTareaAsync);
        RutaGeneral(aplicacion, "GET", "/api/tareas/pendientes", false, ListarTareasPendientesAsync);
    }

    private static void RutaDeEmpresa(RouteGroupBuilder grupo, string metodo, string patron, bool conCuerpo, Manejo manejo) =>
        grupo.MapMethods(patron, [metodo], (HttpContext http, Almacen almacen, Seguridad seguridad, IEjecutorOrdenes ejecutor, Opciones opciones) =>
        {
            var texto = Convert.ToString(http.Request.RouteValues["empresaId"], CultureInfo.InvariantCulture);
            return AtenderAsync(http, almacen, seguridad, ejecutor, opciones, conCuerpo, manejo, quien =>
            {
                if (!long.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || almacen.BuscarRecurso(quien.ClienteId, Almacen.TipoEmpresa, id) is not { } empresa
                    || !almacen.EmpresaDelCliente(quien.ClienteId, empresa.Clave))
                {
                    return (null, null, Errores.Json(404, $"No existe la empresa {texto}."));
                }

                return (empresa.Clave, null, null);
            });
        });

    private static void RutaDeRecurso(WebApplication aplicacion, string metodo, string patron, string tipo, bool conCuerpo, Manejo manejo) =>
        aplicacion.MapMethods(patron, [metodo], (HttpContext http, Almacen almacen, Seguridad seguridad, IEjecutorOrdenes ejecutor, Opciones opciones) =>
        {
            var texto = Convert.ToString(http.Request.RouteValues["id"], CultureInfo.InvariantCulture);
            return AtenderAsync(http, almacen, seguridad, ejecutor, opciones, conCuerpo, manejo, quien =>
            {
                if (!long.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || almacen.BuscarRecurso(quien.ClienteId, tipo, id) is not { } recurso
                    || !almacen.EmpresaDelCliente(quien.ClienteId, recurso.Empresa))
                {
                    return (null, null, Errores.Json(404, $"No existe el {tipo} {texto}."));
                }

                return (recurso.Empresa, recurso, null);
            });
        });

    private static void RutaGeneral(WebApplication aplicacion, string metodo, string patron, bool conCuerpo, Manejo manejo) =>
        aplicacion.MapMethods(patron, [metodo], (HttpContext http, Almacen almacen, Seguridad seguridad, IEjecutorOrdenes ejecutor, Opciones opciones) =>
            AtenderAsync(http, almacen, seguridad, ejecutor, opciones, conCuerpo, manejo, quien => (string.Empty, null, null)));

    private static async Task<IResult> AtenderAsync(
        HttpContext http,
        Almacen almacen,
        Seguridad seguridad,
        IEjecutorOrdenes ejecutor,
        Opciones opciones,
        bool conCuerpo,
        Manejo manejo,
        Func<Identidad, (string? Empresa, RecursoAcusa? Recurso, IResult? Error)> resolver)
    {
        if (FacturacionRutas.AutenticarPublico(http, almacen, seguridad) is not { } quien)
        {
            return Errores.NoAutorizado();
        }

        PeticionActual.Texto = $"{http.Request.Method} {http.Request.Path}";
        var esperaPedida = http.Request.Query["esperar"].FirstOrDefault();
        PeticionActual.Inicio = DateTimeOffset.UtcNow;
        PeticionActual.EsperaSegundos = null;
        if (int.TryParse(esperaPedida, NumberStyles.None, CultureInfo.InvariantCulture, out var segundos))
        {
            PeticionActual.EsperaSegundos = segundos;
        }

        var (empresa, recurso, error) = resolver(quien);
        if (error is not null)
        {
            return error;
        }

        JsonObject? cuerpo = null;
        if (conCuerpo)
        {
            try
            {
                using var lector = new StreamReader(http.Request.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
                var texto = await lector.ReadToEndAsync(http.RequestAborted);
                var nodo = JsonNode.Parse(texto);
                if (nodo is not null and not JsonObject)
                {
                    return Errores.Json(400, "El cuerpo no es un objeto JSON válido.");
                }

                cuerpo = nodo as JsonObject;
            }
            catch (Exception excepcion) when (excepcion is JsonException or DecoderFallbackException or InvalidOperationException or BadHttpRequestException)
            {
                return Errores.Json(400, "El cuerpo no es un objeto JSON válido.");
            }

            if (cuerpo is null)
            {
                return Errores.Json(400, "El cuerpo es obligatorio.");
            }

            if (JsonSerializer.SerializeToElement(cuerpo) is { ValueKind: JsonValueKind.Object } objeto && CamposSecretos.Buscar(objeto) is { } campo)
            {
                return Errores.Json(400, $"El campo '{campo}' parece una contraseña y no se acepta: la contraseña del certificado se configura en el worker.");
            }
        }

        var llave = http.Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(llave))
        {
            llave = null;
        }
        else if (llave.Length > 100 || !llave.All(caracter => char.IsAsciiLetterOrDigit(caracter) || caracter is '-' or '_' or '.' or ':'))
        {
            return Errores.Json(400, "Idempotency-Key admite hasta 100 caracteres: letras, números, guion, guion bajo, punto y dos puntos.");
        }

        return await manejo(new Contexto(http, quien, almacen, ejecutor, opciones, empresa!, recurso, cuerpo, llave, http.RequestAborted));
    }

    private static async Task<IResult> Orden(Contexto contexto, string tipo, object payload, int estadoCorrecto = 200, IReadOnlyList<string>? errores = null)
    {
        if (errores is { Count: > 0 })
        {
            return Errores.Validacion(errores);
        }

        var resultado = await contexto.Ejecutar(tipo, payload, contexto.Llave);
        return Responder(contexto, resultado, estadoCorrecto, contexto.Recurso?.Id);
    }

    private static IResult Responder(Contexto contexto, ResultadoOrden resultado, int estadoCorrecto = 200, long? id = null)
    {
        if (resultado.Ok)
        {
            return Results.Json(new { id, tareaId = resultado.TareaId, resultado = SinCorreo(resultado.Datos) }, statusCode: estadoCorrecto);
        }

        if (resultado.EnCurso)
        {
            return EnProceso(contexto, resultado.TareaId, id);
        }

        if (resultado.CodigoSdk is null && resultado.Error is { } error && error.StartsWith("Ya existe", StringComparison.OrdinalIgnoreCase))
        {
            return Errores.Json(409, error, codigo: CodigosError.Conflicto, tareaId: resultado.TareaId);
        }

        return Errores.DeTareaJson(resultado.Error, resultado.CodigoSdk, resultado.TareaId);
    }

    private static IResult EnProceso(Contexto contexto, string? tareaId, long? id = null, string mensaje = "El worker sigue trabajando; consulte la tarea.")
    {
        contexto.Http.Response.Headers.Location = $"/api/tareas/{tareaId}/consultar";
        return Results.Json(new { id, tareaId, estado = "EnProceso", mensaje }, statusCode: 202);
    }

    private static JsonNode? SinCorreo(JsonElement? datos)
    {
        if (datos is not { } elemento)
        {
            return null;
        }

        var nodo = JsonNode.Parse(elemento.GetRawText());
        if (nodo is JsonObject objeto)
        {
            objeto.Remove("email");
        }

        return nodo;
    }

    private static async Task<IResult> CrearClienteAsync(Contexto contexto)
    {
        var cuerpo = contexto.Cuerpo!;
        var errores = Requeridos(cuerpo, ("codigo", "Codigo"), ("razonSocial", "Razon social"), ("rfc", "Rfc"), ("codigoPostal", "Codigo postal"), ("pais", "Pais"));
        ValidarCodigoPostal(cuerpo, errores);
        if (errores.Count > 0)
        {
            return Errores.Validacion(errores);
        }

        var codigo = Texto(cuerpo["codigo"])!.Trim();
        var id = contexto.Almacen.RegistrarRecurso(contexto.Quien.ClienteId, TipoCliente, contexto.Empresa, codigo);
        var cliente = await contexto.Ejecutar(
            Operaciones.ClienteCrear,
            DatosDeClienteNuevo(cuerpo),
            contexto.LlaveCon("cliente"));
        if (cliente.EnCurso)
        {
            return EnProceso(contexto, cliente.TareaId, id, $"El cliente se sigue creando; al terminar la tarea, guarde su dirección con POST /api/clientes/{id}/direcciones/crear.");
        }

        if (!cliente.Ok)
        {
            return Responder(contexto, cliente, 201, id);
        }

        var datosFiscales = Campos(cuerpo, "regimenFiscal", "usoCfdi");
        if (datosFiscales.Count > 0)
        {
            datosFiscales["codigo"] = codigo;
            var fiscales = await contexto.Ejecutar(Operaciones.ClienteActualizar, datosFiscales, contexto.LlaveCon("fiscales"));
            if (fiscales.Ok)
            {
                cliente = fiscales;
            }
            else if (!fiscales.EnCurso)
            {
                var errorFiscal = Errores.DeTarea(fiscales.Error, fiscales.CodigoSdk, fiscales.TareaId) with
                {
                    Mensaje = $"El cliente {codigo} se creó, pero no se pudo guardar su régimen fiscal o uso de CFDI. Guárdelos con PATCH /api/clientes/{id}/actualizar.",
                };
                return Results.Json(errorFiscal, statusCode: 422);
            }
        }

        var direccion = DatosDeDireccion(cuerpo, codigo, DireccionFiscal);
        var guardado = await contexto.Ejecutar(Operaciones.DireccionCrear, direccion, contexto.LlaveCon("direccion"));
        if (guardado.EnCurso)
        {
            return EnProceso(contexto, guardado.TareaId, id);
        }

        if (!guardado.Ok)
        {
            var error = Errores.DeTarea(guardado.Error, guardado.CodigoSdk, guardado.TareaId) with
            {
                Mensaje = $"El cliente {codigo} se creó, pero no se pudo guardar su dirección fiscal. Guárdela con POST /api/clientes/{id}/direcciones/crear.",
            };
            return Results.Json(error, statusCode: 422);
        }

        return Results.Json(new { id, tareaId = guardado.TareaId, resultado = ClienteConDireccion(cliente, guardado) }, statusCode: 201);
    }

    private static JsonObject DatosDeClienteNuevo(JsonObject cuerpo)
    {
        var datos = Campos(cuerpo, "codigo", "razonSocial", "rfc", "tipoCliente", "regimenFiscal", "usoCfdi");
        datos["limiteCredito"] = 0;
        return datos;
    }

    private static Task<IResult> BuscarClienteAsync(Contexto contexto) =>
        BuscarPorCodigoAsync(contexto, TipoCliente, Operaciones.ClienteLeer);

    private static Task<IResult> BuscarProductoAsync(Contexto contexto) =>
        BuscarPorCodigoAsync(contexto, TipoProducto, Operaciones.ProductoLeer);

    private static async Task<IResult> BuscarPorCodigoAsync(Contexto contexto, string tipo, string operacion)
    {
        var codigo = contexto.Consulta("codigo");
        if (codigo.Length == 0)
        {
            return Errores.Validacion(["Codigo no puede estar vacio"]);
        }

        var resultado = await contexto.Ejecutar(operacion, new { codigo }, null);
        if (!resultado.Ok)
        {
            return Responder(contexto, resultado);
        }

        var id = contexto.Almacen.RegistrarRecurso(contexto.Quien.ClienteId, tipo, contexto.Empresa, codigo);
        return Responder(contexto, resultado, 200, id);
    }

    private static Task<IResult> LeerClienteAsync(Contexto contexto) =>
        Orden(contexto, Operaciones.ClienteLeer, new { codigo = contexto.Recurso!.Clave });

    private static async Task<IResult> ActualizarClienteAsync(Contexto contexto)
    {
        var cuerpo = contexto.Cuerpo!;
        var codigo = contexto.Recurso!.Clave;
        var cambios = Campos(cuerpo, "razonSocial", "rfc", "regimenFiscal", "usoCfdi");
        if (cambios.Count == 0)
        {
            return Errores.Validacion(["No hay datos que actualizar; la dirección se actualiza con PATCH /api/clientes/{id}/direcciones/actualizar"]);
        }

        cambios["codigo"] = codigo;
        return await Orden(contexto, Operaciones.ClienteActualizar, cambios);
    }

    private static async Task<IResult> LeerDireccionesAsync(Contexto contexto)
    {
        var codigo = contexto.Recurso!.Clave;
        var tipos = new List<int> { DireccionFiscal, DireccionEnvio };
        var tipoPedido = contexto.Consulta("tipoDireccion");
        if (tipoPedido.Length == 0)
        {
            tipoPedido = contexto.Consulta("tipo");
        }

        if (tipoPedido.Length > 0)
        {
            if (!TipoDireccionValido(tipoPedido, out var tipoFiltrado))
            {
                return Errores.Validacion(["Tipo de dirección debe ser 0 (fiscal) o 1 (envío)"]);
            }

            tipos = [tipoFiltrado];
        }

        var direcciones = new JsonArray();
        string? ultimaTarea = null;
        foreach (var tipo in tipos)
        {
            var lectura = await contexto.Ejecutar(Operaciones.DireccionLeer, new { codigoCliente = codigo, tipo }, null);
            ultimaTarea = lectura.TareaId;
            if (lectura.Ok && lectura.Datos is { } datos)
            {
                direcciones.Add(JsonNode.Parse(datos.GetRawText()));
                continue;
            }

            if (lectura.EnCurso)
            {
                return EnProceso(contexto, lectura.TareaId, contexto.Recurso.Id);
            }

            if (!EsDireccionInexistente(lectura))
            {
                return Responder(contexto, lectura);
            }
        }

        return Results.Json(new { id = contexto.Recurso.Id, tareaId = ultimaTarea, resultado = direcciones });
    }

    private static async Task<IResult> CrearDireccionAsync(Contexto contexto)
    {
        var cuerpo = contexto.Cuerpo!;
        var errores = Requeridos(cuerpo, ("codigoPostal", "Codigo postal"), ("pais", "Pais"));
        ValidarCodigoPostal(cuerpo, errores);
        var tipo = LeerTipoDireccion(cuerpo, errores);
        if (errores.Count > 0)
        {
            return Errores.Validacion(errores);
        }

        var codigo = contexto.Recurso!.Clave;
        var existente = await contexto.Ejecutar(Operaciones.DireccionLeer, new { codigoCliente = codigo, tipo }, null);
        if (existente.EnCurso)
        {
            return EnProceso(contexto, existente.TareaId, contexto.Recurso.Id);
        }

        if (existente.Ok)
        {
            return Errores.Json(409, $"El cliente ya tiene dirección de tipo {tipo}; actualícela con PATCH /api/clientes/{contexto.Recurso.Id}/direcciones/actualizar.");
        }

        if (!EsDireccionInexistente(existente))
        {
            return Responder(contexto, existente);
        }

        var guardado = await contexto.Ejecutar(Operaciones.DireccionCrear, DatosDeDireccion(cuerpo, codigo, tipo), contexto.Llave);
        return Responder(contexto, guardado, 201, contexto.Recurso.Id);
    }

    private static async Task<IResult> ActualizarDireccionAsync(Contexto contexto)
    {
        var cuerpo = contexto.Cuerpo!;
        var errores = new List<string>();
        ValidarCodigoPostal(cuerpo, errores);
        var tipo = LeerTipoDireccion(cuerpo, errores);
        if (!CamposDireccion.Any(campo => cuerpo[campo] is not null))
        {
            errores.Add("No hay datos de dirección que actualizar");
        }

        foreach (var campo in new[] { ("codigoPostal", "Codigo postal"), ("pais", "Pais") })
        {
            if (cuerpo[campo.Item1] is not null && string.IsNullOrWhiteSpace(Texto(cuerpo[campo.Item1])))
            {
                errores.Add($"{campo.Item2} no puede estar vacio");
            }
        }

        if (errores.Count > 0)
        {
            return Errores.Validacion(errores);
        }

        var codigo = contexto.Recurso!.Clave;
        var existente = await contexto.Ejecutar(Operaciones.DireccionLeer, new { codigoCliente = codigo, tipo }, null);
        if (existente.EnCurso)
        {
            return EnProceso(contexto, existente.TareaId, contexto.Recurso.Id);
        }

        if (!existente.Ok)
        {
            if (EsDireccionInexistente(existente))
            {
                return Errores.Json(404, $"El cliente no tiene dirección de tipo {tipo}; créela con POST /api/clientes/{contexto.Recurso.Id}/direcciones/crear.");
            }

            return Responder(contexto, existente);
        }

        var combinada = new JsonObject();
        if (existente.Datos is { ValueKind: JsonValueKind.Object } actual)
        {
            foreach (var campo in CamposDireccion)
            {
                if (actual.TryGetProperty(campo, out var valor) && valor.ValueKind != JsonValueKind.Null)
                {
                    combinada[campo] = JsonNode.Parse(valor.GetRawText());
                }
            }
        }

        foreach (var campo in CamposDireccion)
        {
            if (cuerpo[campo] is { } valor)
            {
                combinada[campo] = valor.DeepClone();
            }
        }

        combinada["codigoCliente"] = codigo;
        combinada["tipo"] = tipo;
        var guardado = await contexto.Ejecutar(Operaciones.DireccionCrear, combinada, contexto.Llave);
        return Responder(contexto, guardado, 200, contexto.Recurso.Id);
    }

    private static async Task<IResult> CrearProductoAsync(Contexto contexto)
    {
        var cuerpo = contexto.Cuerpo!;
        var errores = Requeridos(cuerpo, ("codigo", "Codigo"), ("nombre", "Nombre"), ("unidadBase", "Unidad base"));
        if (errores.Count > 0)
        {
            return Errores.Validacion(errores);
        }

        var codigo = Texto(cuerpo["codigo"])!.Trim();
        var id = contexto.Almacen.RegistrarRecurso(contexto.Quien.ClienteId, TipoProducto, contexto.Empresa, codigo);
        var datos = Campos(cuerpo, "codigo", "nombre", "unidadBase", "precio1", "descripcion", "claveSat", "tipoProducto", "textosExtra");
        var resultado = await contexto.Ejecutar(Operaciones.ProductoCrear, datos, contexto.Llave);
        return Responder(contexto, resultado, 201, id);
    }

    private static Task<IResult> LeerProductoAsync(Contexto contexto) =>
        Orden(contexto, Operaciones.ProductoLeer, new { codigo = contexto.Recurso!.Clave });

    private static Task<IResult> ActualizarProductoAsync(Contexto contexto)
    {
        var cambios = Campos(contexto.Cuerpo!, "nombre", "descripcion", "precio1", "claveSat", "textosExtra");
        var errores = new List<string>();
        if (cambios.Count == 0)
        {
            errores.Add("No hay datos que actualizar");
        }

        cambios["codigo"] = contexto.Recurso!.Clave;
        return Orden(contexto, Operaciones.ProductoActualizar, cambios, 200, errores);
    }

    private static async Task<IResult> CrearDocumentoAsync(Contexto contexto)
    {
        var cuerpo = contexto.Cuerpo!;
        var errores = new List<string>();
        foreach (var (campo, etiqueta) in new[] { ("concepto", "Concepto"), ("fecha", "Fecha"), ("formaPago", "Forma de pago"), ("usoCfdi", "Uso de CFDI"), ("regimenFiscal", "Regimen fiscal") })
        {
            if (string.IsNullOrWhiteSpace(Texto(cuerpo[campo])))
            {
                errores.Add($"{etiqueta} no puede estar vacio");
            }
        }

        if (Texto(cuerpo["fecha"]) is { Length: > 0 } fecha && !DateOnly.TryParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errores.Add("Fecha debe tener el formato aaaa-mm-dd");
        }

        if (Texto(cuerpo["formaPago"]) is { Length: > 0 } formaPago && !FacturacionServicio.FormasDePago.ContainsKey(formaPago))
        {
            errores.Add("Forma de pago no es una clave del SAT");
        }

        if (Texto(cuerpo["usoCfdi"]) is { Length: > 0 } usoCfdi && !FacturacionServicio.UsosCfdi.ContainsKey(usoCfdi.ToUpperInvariant()))
        {
            errores.Add("Uso de CFDI no es una clave del SAT");
        }

        if (Texto(cuerpo["regimenFiscal"]) is { Length: > 0 } regimen && !FacturacionServicio.Regimenes.ContainsKey(regimen))
        {
            errores.Add("Regimen fiscal no es una clave del SAT");
        }

        var clienteCodigo = ResolverCodigo(contexto, cuerpo, "clienteId", "clienteCodigo", TipoCliente, "Cliente", errores);
        var movimientos = (cuerpo["movimientos"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (movimientos.Count == 0)
        {
            errores.Add("Movimientos: se necesita al menos uno");
        }

        var partidasPedidas = new List<(string? ProductoCodigo, decimal? Unidades, decimal? Precio, string Almacen, decimal? Iva, decimal? RetencionIva, decimal? Ieps)>();
        for (var indice = 0; indice < movimientos.Count; indice++)
        {
            var movimiento = movimientos[indice];
            var etiqueta = $"Movimiento {indice + 1}: producto";
            var productoCodigo = ResolverCodigo(contexto, movimiento, "productoId", "productoCodigo", TipoProducto, etiqueta, errores);
            if (Numero(movimiento["unidades"]) is not > 0)
            {
                errores.Add($"Movimiento {indice + 1}: unidades debe ser mayor que 0");
            }

            if (Numero(movimiento["precio"]) is < 0)
            {
                errores.Add($"Movimiento {indice + 1}: precio debe ser 0 o mayor");
            }

            foreach (var campo in new[] { "ivaPorcentaje", "retencionIvaPorcentaje", "iepsPorcentaje" })
            {
                if (Numero(movimiento[campo]) is < 0 or > 100)
                {
                    errores.Add($"Movimiento {indice + 1}: {campo} debe estar entre 0 y 100");
                }
            }

            var almacen = contexto.Opciones.FacturacionAlmacen;
            if (Texto(movimiento["almacen"]) is { Length: > 0 } almacenPedido)
            {
                almacen = almacenPedido.Trim();
            }

            partidasPedidas.Add((
                productoCodigo,
                Numero(movimiento["unidades"]),
                Numero(movimiento["precio"]),
                almacen,
                Numero(movimiento["ivaPorcentaje"]),
                Numero(movimiento["retencionIvaPorcentaje"]),
                Numero(movimiento["iepsPorcentaje"])));
        }

        if (errores.Count > 0)
        {
            return Errores.Validacion(errores);
        }

        var partidas = new List<object>();
        var preciosDeProducto = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var partida in partidasPedidas)
        {
            var precio = partida.Precio;
            if (precio is null)
            {
                if (!preciosDeProducto.TryGetValue(partida.ProductoCodigo!, out var precioDelProducto))
                {
                    var producto = await contexto.Ejecutar(Operaciones.ProductoLeer, new { codigo = partida.ProductoCodigo }, null);
                    if (!producto.Ok)
                    {
                        return Responder(contexto, producto);
                    }

                    if (producto.Datos is not { ValueKind: JsonValueKind.Object } datosDelProducto
                        || !datosDelProducto.TryGetProperty("precio1", out var precio1)
                        || precio1.ValueKind != JsonValueKind.Number)
                    {
                        return Errores.Validacion([$"El producto {partida.ProductoCodigo} no tiene precio en CONTPAQi Comercial; envíe el precio en el movimiento"]);
                    }

                    precioDelProducto = precio1.GetDecimal();
                    preciosDeProducto[partida.ProductoCodigo!] = precioDelProducto;
                }

                precio = precioDelProducto;
            }

            partidas.Add(new
            {
                productoCodigo = partida.ProductoCodigo,
                unidades = partida.Unidades,
                precio,
                almacen = partida.Almacen,
                ivaPorcentaje = partida.Iva,
                retencionIvaPorcentaje = partida.RetencionIva,
                iepsPorcentaje = partida.Ieps,
            });
        }

        var regimenFiscal = Texto(cuerpo["regimenFiscal"])!.Trim();
        var usoDelCfdi = Texto(cuerpo["usoCfdi"])!.Trim().ToUpperInvariant();
        var actualizacion = await contexto.Ejecutar(
            Operaciones.ClienteActualizar,
            new { codigo = clienteCodigo, regimenFiscal, usoCfdi = usoDelCfdi },
            contexto.LlaveCon("cliente"));
        if (!actualizacion.Ok && !actualizacion.EnCurso)
        {
            return Responder(contexto, actualizacion);
        }

        var referencia = ReferenciaNueva(contexto);
        if (Texto(cuerpo["referencia"]) is { Length: > 0 } referenciaPedida)
        {
            referencia = referenciaPedida.Trim();
            if (contexto.Llave is null)
            {
                contexto = contexto with { Llave = $"referencia:{contexto.Empresa}:{referencia}" };
            }
        }

        string? serie = null;
        if (Texto(cuerpo["serie"]) is { Length: > 0 } seriePedida)
        {
            serie = seriePedida.Trim();
        }

        var creacion = await contexto.Ejecutar(Operaciones.DocumentoCrear, new
        {
            concepto = Texto(cuerpo["concepto"])!.Trim(),
            serie,
            clienteCodigo,
            referencia,
            fecha = Texto(cuerpo["fecha"]),
            formaPago = Texto(cuerpo["formaPago"]),
            usoCfdi = usoDelCfdi,
            partidas,
        }, contexto.LlaveCon("documento"));
        if (!creacion.Ok)
        {
            return Responder(contexto, creacion, 201);
        }

        long? id = null;
        if (ClaveDeDocumento(creacion.Datos, Texto(cuerpo["concepto"])!.Trim()) is { } clave)
        {
            id = contexto.Almacen.RegistrarRecurso(contexto.Quien.ClienteId, TipoDocumento, contexto.Empresa, clave);
        }

        return Responder(contexto, creacion, 201, id);
    }

    private static async Task<IResult> BuscarDocumentoAsync(Contexto contexto)
    {
        var concepto = contexto.Consulta("concepto");
        var serie = contexto.Consulta("serie");
        var errores = new List<string>();
        if (concepto.Length == 0)
        {
            errores.Add("Concepto no puede estar vacio");
        }

        if (!long.TryParse(contexto.Consulta("folio"), NumberStyles.None, CultureInfo.InvariantCulture, out var folio) || folio <= 0)
        {
            errores.Add("Folio debe ser un número entero positivo");
        }

        if (errores.Count > 0)
        {
            return Errores.Validacion(errores);
        }

        string? serieDelDocumento = null;
        if (serie.Length > 0 && serie != "-")
        {
            serieDelDocumento = serie;
        }

        var lectura = await contexto.Ejecutar(Operaciones.DocumentoLeer, new { concepto, serie = serieDelDocumento, folio }, null);
        if (!lectura.Ok)
        {
            return Responder(contexto, lectura);
        }

        var id = contexto.Almacen.RegistrarRecurso(contexto.Quien.ClienteId, TipoDocumento, contexto.Empresa, ClaveDeDocumento(concepto, serieDelDocumento, folio));
        return Responder(contexto, lectura, 200, id);
    }

    private static Task<IResult> LeerDocumentoAsync(Contexto contexto)
    {
        var documento = Documento(contexto.Recurso!);
        return Orden(contexto, Operaciones.DocumentoLeer, new { concepto = documento.Concepto, serie = documento.Serie, folio = documento.Folio });
    }

    private static async Task<IResult> TimbrarAsync(Contexto contexto)
    {
        var documento = Documento(contexto.Recurso!);
        var emision = await contexto.Ejecutar(
            Operaciones.DocumentoEmitir,
            new { concepto = documento.Concepto, serie = documento.Serie, folio = documento.Folio },
            contexto.Llave);
        if (!emision.Ok)
        {
            return Responder(contexto, emision, 200, contexto.Recurso!.Id);
        }

        string? uuid = null;
        if (emision.Datos is { } datos && datos.TryGetProperty("uuid", out var valorUuid) && valorUuid.ValueKind == JsonValueKind.String)
        {
            uuid = valorUuid.GetString();
        }

        string? xml = null;
        try
        {
            var archivo = await Archivo(contexto, documento, "xml");
            if (archivo.Ok && ContenidoBase64(archivo, "xml") is { } base64)
            {
                xml = FacturacionServicio.DecodificarXml(base64);
            }
        }
        catch (Exception)
        {
            xml = null;
        }

        return Results.Json(new
        {
            id = contexto.Recurso!.Id,
            tareaId = emision.TareaId,
            documento.Concepto,
            documento.Serie,
            documento.Folio,
            uuid,
            estado = "timbrada",
            xml,
            resultado = emision.Datos,
        });
    }

    private static Task<IResult> CancelarAsync(Contexto contexto)
    {
        var documento = Documento(contexto.Recurso!);
        var cuerpo = contexto.Cuerpo!;
        var errores = new List<string>();
        var motivo = ValidarCancelacion(cuerpo, errores, string.Empty);
        return Orden(contexto, Operaciones.DocumentoCancelar, new
        {
            concepto = documento.Concepto,
            serie = documento.Serie,
            folio = documento.Folio,
            motivo,
            uuidSustituto = UuidSustituto(cuerpo),
        }, 200, errores);
    }

    private static async Task<IResult> ArchivoAsync(Contexto contexto, string formato)
    {
        var documento = Documento(contexto.Recurso!);
        var archivo = await Archivo(contexto, documento, formato);
        if (!archivo.Ok)
        {
            return Responder(contexto, archivo);
        }

        var base64 = ContenidoBase64(archivo, formato);
        if (base64 is null)
        {
            return Errores.Json(422, $"Comercial no entregó el archivo {formato.ToUpperInvariant()} (el documento debe estar timbrado).", codigo: CodigosError.OperacionFallida);
        }

        if (formato == "xml")
        {
            return Results.Content(FacturacionServicio.DecodificarXml(base64) ?? string.Empty, "application/xml; charset=utf-8");
        }

        return Results.File(Convert.FromBase64String(base64), "application/pdf", $"{documento.Serie}{documento.Folio}.pdf");
    }

    private static async Task<IResult> VariosAsync(Contexto contexto, bool cancelar)
    {
        var elementos = contexto.Cuerpo!["documentos"] as JsonArray ?? [];
        var errores = new List<string>();
        var maximo = Math.Max(1, contexto.Almacen.LeerAjusteEntero(contexto.Quien.ClienteId, Almacen.AjusteMaximoPorLote) ?? contexto.Opciones.MaximoDocumentosPorLote);
        if (elementos.Count == 0 || elementos.Count > maximo)
        {
            errores.Add($"Documentos: se envían entre 1 y {maximo}");
        }

        var pedidos = new List<(RecursoAcusa Recurso, string? Motivo, string? UuidSustituto)>();
        var vistos = new HashSet<long>();
        for (var indice = 0; indice < elementos.Count; indice++)
        {
            var elemento = elementos[indice];
            var datos = new JsonObject();
            var idTexto = Texto(elemento);
            if (elemento is JsonObject objeto)
            {
                datos = objeto;
                idTexto = Texto(objeto["id"]);
            }

            var etiqueta = $"Documento {indice + 1}";
            if (!long.TryParse(idTexto, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || contexto.Almacen.BuscarRecurso(contexto.Quien.ClienteId, TipoDocumento, id) is not { } recurso
                || !contexto.Almacen.EmpresaDelCliente(contexto.Quien.ClienteId, recurso.Empresa))
            {
                errores.Add($"{etiqueta}: no existe el documento {idTexto}");
                continue;
            }

            if (!vistos.Add(id))
            {
                errores.Add($"{etiqueta}: el documento {id} está repetido en la lista");
                continue;
            }

            string? motivo = null;
            if (cancelar)
            {
                motivo = ValidarCancelacion(datos, errores, etiqueta + ": ");
            }

            pedidos.Add((recurso, motivo, UuidSustituto(datos)));
        }

        if (errores.Count > 0)
        {
            return Errores.Validacion(errores);
        }

        var tareas = new List<object>();
        foreach (var pedido in pedidos)
        {
            var documento = Documento(pedido.Recurso);
            var tipo = Operaciones.DocumentoEmitir;
            object payload = new { concepto = documento.Concepto, serie = documento.Serie, folio = documento.Folio };
            if (cancelar)
            {
                tipo = Operaciones.DocumentoCancelar;
                payload = new { concepto = documento.Concepto, serie = documento.Serie, folio = documento.Folio, motivo = pedido.Motivo, uuidSustituto = pedido.UuidSustituto };
            }

            var encolada = await contexto.Ejecutor.EncolarAsync(
                contexto.Quien.ClienteId, pedido.Recurso.Empresa, tipo, payload, contexto.LlaveCon(pedido.Recurso.Id.ToString(CultureInfo.InvariantCulture)), contexto.Cancelacion);
            if (!encolada.EnCurso)
            {
                tareas.Add(new { documentoId = pedido.Recurso.Id, tareaId = encolada.TareaId, error = encolada.Error });
                continue;
            }

            tareas.Add(new { documentoId = pedido.Recurso.Id, tareaId = encolada.TareaId, error = (string?)null });
        }

        contexto.Http.Response.Headers.Location = "/api/tareas/pendientes";
        return Results.Json(new { estado = "EnProceso", mensaje = "Los documentos se procesan en segundo plano; consulte cada tarea o las pendientes.", tareas }, statusCode: 202);
    }

    private static Task<IResult> ConsultarTareaAsync(Contexto contexto)
    {
        var tareaId = Convert.ToString(contexto.Http.Request.RouteValues["tareaId"], CultureInfo.InvariantCulture) ?? string.Empty;
        if (contexto.Almacen.ObtenerTarea(contexto.Quien.ClienteId, tareaId) is not { } tarea)
        {
            return Task.FromResult(Errores.Json(404, $"No existe la tarea {tareaId}."));
        }

        return Task.FromResult(Results.Json(tarea));
    }

    private static Task<IResult> ListarTareasPendientesAsync(Contexto contexto)
    {
        var limite = 200;
        if (int.TryParse(contexto.Consulta("limite"), NumberStyles.None, CultureInfo.InvariantCulture, out var limitePedido))
        {
            limite = limitePedido;
        }

        return Task.FromResult(Results.Json(contexto.Almacen.ListarTareasPendientes(contexto.Quien.ClienteId, limite)));
    }

    private static string? ValidarCancelacion(JsonObject datos, List<string> errores, string prefijo)
    {
        var motivo = Texto(datos["motivo"])?.Trim();
        if (motivo is not ("01" or "02" or "03" or "04"))
        {
            errores.Add($"{prefijo}Motivo debe ser 01, 02, 03 o 04");
        }

        if (motivo == "01" && UuidSustituto(datos) is null)
        {
            errores.Add($"{prefijo}El motivo 01 requiere uuidSustituto");
        }

        return motivo;
    }

    private static string? UuidSustituto(JsonObject datos)
    {
        var sustituto = Texto(datos["uuidSustituto"])?.Trim();
        if (string.IsNullOrWhiteSpace(sustituto))
        {
            return null;
        }

        return sustituto;
    }

    private static string? ResolverCodigo(Contexto contexto, JsonObject datos, string campoId, string campoCodigo, string tipo, string etiqueta, List<string> errores)
    {
        var idTexto = Texto(datos[campoId]);
        if (!string.IsNullOrWhiteSpace(idTexto))
        {
            if (long.TryParse(idTexto, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && contexto.Almacen.BuscarRecurso(contexto.Quien.ClienteId, tipo, id) is { } recurso
                && string.Equals(recurso.Empresa, contexto.Empresa, StringComparison.OrdinalIgnoreCase))
            {
                return recurso.Clave;
            }

            errores.Add($"{etiqueta}: no existe el {tipo} {idTexto} en la empresa {contexto.Empresa}");
            return null;
        }

        var codigo = Texto(datos[campoCodigo])?.Trim();
        if (string.IsNullOrWhiteSpace(codigo))
        {
            errores.Add($"{etiqueta}: indique {campoId} o {campoCodigo}");
            return null;
        }

        return codigo;
    }

    private static (string Concepto, string? Serie, long Folio) Documento(RecursoAcusa recurso)
    {
        var partes = recurso.Clave.Split('|');
        string? serie = null;
        if (partes[1].Length > 0)
        {
            serie = partes[1];
        }

        return (partes[0], serie, long.Parse(partes[2], CultureInfo.InvariantCulture));
    }

    public static string ClaveDeDocumento(string concepto, string? serie, long folio) =>
        $"{concepto}|{serie ?? string.Empty}|{folio.ToString(CultureInfo.InvariantCulture)}";

    private static string? ClaveDeDocumento(JsonElement? datos, string concepto)
    {
        if (datos is not { ValueKind: JsonValueKind.Object } documento)
        {
            return null;
        }

        if (!Propiedad(documento, "folio", out var valorFolio) || !valorFolio.TryGetInt64(out var folio))
        {
            return null;
        }

        string? serie = null;
        if (Propiedad(documento, "serie", out var valorSerie) && valorSerie.ValueKind == JsonValueKind.String)
        {
            serie = valorSerie.GetString();
        }

        if (Propiedad(documento, "concepto", out var valorConcepto) && valorConcepto.ValueKind == JsonValueKind.String && valorConcepto.GetString() is { Length: > 0 } conceptoDevuelto)
        {
            concepto = conceptoDevuelto;
        }

        return ClaveDeDocumento(concepto, serie, folio);
    }

    private static bool Propiedad(JsonElement objeto, string nombre, out JsonElement valor)
    {
        foreach (var propiedad in objeto.EnumerateObject())
        {
            if (string.Equals(propiedad.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                valor = propiedad.Value;
                return true;
            }
        }

        valor = default;
        return false;
    }

    private static Task<ResultadoOrden> Archivo(Contexto contexto, (string Concepto, string? Serie, long Folio) documento, string formato) =>
        contexto.Ejecutar(
            Operaciones.DocumentoObtenerArchivo,
            new { concepto = documento.Concepto, serie = documento.Serie, folio = documento.Folio, formato, incluirContenido = true },
            null);

    private static string? ContenidoBase64(ResultadoOrden resultado, string formato)
    {
        if (resultado.Datos is not { } datos || !datos.TryGetProperty("archivos", out var archivos) || archivos.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var archivo in archivos.EnumerateArray())
        {
            if (archivo.TryGetProperty("tipo", out var tipo) && string.Equals(tipo.GetString(), formato, StringComparison.OrdinalIgnoreCase)
                && archivo.TryGetProperty("base64", out var contenido) && contenido.ValueKind == JsonValueKind.String)
            {
                return contenido.GetString();
            }
        }

        return null;
    }

    private static JsonObject ClienteConDireccion(ResultadoOrden cliente, ResultadoOrden direccion)
    {
        var resultado = new JsonObject();
        if (cliente.Datos is { ValueKind: JsonValueKind.Object } datosCliente)
        {
            foreach (var propiedad in datosCliente.EnumerateObject())
            {
                if (propiedad.NameEquals("email"))
                {
                    continue;
                }

                resultado[propiedad.Name] = JsonNode.Parse(propiedad.Value.GetRawText());
            }
        }

        if (direccion.Datos is { } datosDireccion)
        {
            resultado["direccion"] = JsonNode.Parse(datosDireccion.GetRawText());
        }

        return resultado;
    }

    private static JsonObject DatosDeDireccion(JsonObject cuerpo, string codigoCliente, int tipo)
    {
        var direccion = Campos(cuerpo, CamposDireccion);
        direccion["codigoCliente"] = codigoCliente;
        direccion["tipo"] = tipo;
        direccion["codigoPostal"] = Texto(cuerpo["codigoPostal"])!.Trim();
        return direccion;
    }

    private static int LeerTipoDireccion(JsonObject cuerpo, List<string> errores)
    {
        var valor = cuerpo["tipoDireccion"] ?? cuerpo["tipo"];
        if (valor is null)
        {
            return DireccionFiscal;
        }

        if (!TipoDireccionValido(Texto(valor), out var tipo))
        {
            errores.Add("Tipo de dirección debe ser 0 (fiscal) o 1 (envío)");
        }

        return tipo;
    }

    private static bool TipoDireccionValido(string? texto, out int tipo)
    {
        if (int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out tipo) && tipo is DireccionFiscal or DireccionEnvio)
        {
            return true;
        }

        tipo = DireccionFiscal;
        return false;
    }

    private static bool EsDireccionInexistente(ResultadoOrden resultado) =>
        !resultado.Ok && !resultado.EnCurso && resultado.CodigoSdk is null
        && resultado.Error is { } error && error.Contains("no tiene dirección", StringComparison.OrdinalIgnoreCase);

    private static void ValidarCodigoPostal(JsonObject cuerpo, List<string> errores)
    {
        if (Texto(cuerpo["codigoPostal"])?.Trim() is { Length: > 0 } codigoPostal && !(codigoPostal.Length == 5 && codigoPostal.All(char.IsAsciiDigit)))
        {
            errores.Add("Codigo postal debe tener 5 digitos");
        }
    }

    private static string ReferenciaNueva(Contexto contexto)
    {
        var sufijo = DateTime.UtcNow.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
        if (contexto.Llave is not null)
        {
            sufijo = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contexto.Llave)))[..12];
        }

        var referencia = contexto.Opciones.FacturacionPrefijoReferencia + sufijo;
        if (referencia.Length > 20)
        {
            return referencia[..20];
        }

        return referencia;
    }

    private static string? Texto(JsonNode? nodo)
    {
        if (nodo is JsonValue valor && valor.TryGetValue<string>(out var texto))
        {
            return texto;
        }

        return nodo?.ToString();
    }

    private static decimal? Numero(JsonNode? nodo)
    {
        if (nodo is not JsonValue valor)
        {
            return null;
        }

        if (valor.TryGetValue<decimal>(out var numero))
        {
            return numero;
        }

        if (valor.TryGetValue<string>(out var texto) && decimal.TryParse(texto, NumberStyles.Number, CultureInfo.InvariantCulture, out var convertido))
        {
            return convertido;
        }

        return null;
    }

    private static JsonObject Campos(JsonObject cuerpo, params string[] permitidos)
    {
        var copia = new JsonObject();
        foreach (var campo in permitidos)
        {
            if (cuerpo[campo] is { } valor)
            {
                copia[campo] = valor.DeepClone();
            }
        }

        return copia;
    }

    private static List<string> Requeridos(JsonObject cuerpo, params (string Campo, string Etiqueta)[] campos) =>
        campos.Where(campo => string.IsNullOrWhiteSpace(Texto(cuerpo[campo.Campo]))).Select(campo => $"{campo.Etiqueta} no puede estar vacio").ToList();
}
