using System.Text.Json.Nodes;
using Acusa.Contrato;

namespace Acusa.Orquestador.Facturacion;

/// <summary>
/// Las rutas del Swagger de Alain, con los mismos caminos, métodos y nombres de campo: <c>/companies</c>, <c>/products</c>,
/// <c>/issuing_companies/{id}/invoices</c>, <c>/movements</c> y demás. Un sistema que hoy llama a su aplicación puede apuntar
/// a Acusa Central con solo cambiar la dirección y la forma de obtener el token. Usan el mismo token de Acusa (llave de API).
/// Quedan fuera, por indicación de Alain: reportes, descargas y usuarios; solo se agrega <c>POST /users/sessions</c> para iniciar sesión.
/// </summary>
public static class FacturacionRutas
{
    public static void Mapear(WebApplication app)
    {
        var raiz = app.MapGroup(string.Empty).WithTags("Facturación (rutas de Alain)");

        // ---- Sesión -----------------------------------------------------------------------------------------------
        raiz.MapPost("/users/sessions", (JsonNode? cuerpo, HttpContext http, Almacen a, Seguridad seguridad, IntentosDeAcceso intentos) =>
        {
            var u = (cuerpo as JsonObject)?["user"] as JsonObject;
            var correo = (u?["email"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
            var clave = u?["password"]?.ToString() ?? string.Empty;
            var origen = http.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (!intentos.Permitido(correo) || !intentos.Permitido("ip:" + origen))
            {
                return Results.Json(new { errors = new[] { "Too many attempts. Wait a few minutes." } }, statusCode: 429);
            }

            var usuario = a.BuscarUsuarioPorEmail(correo);
            var valida = Contrasenas.Verificar(clave, usuario?.Clave ?? Contrasenas.Señuelo) && usuario is not null;
            if (!valida)
            {
                intentos.Fallo(correo);
                intentos.Fallo("ip:" + origen);
                return Results.Json(new { errors = new[] { "Invalid email or password" } }, statusCode: 422);
            }

            intentos.Acierto(correo);
            var token = seguridad.CrearToken(new Identidad(usuario!.Id, usuario.ClienteId, Roles.Portal), DateTimeOffset.UtcNow);
            return Results.Json(new { access_token = token, refresh_token = token, user_id = 1, role = "admin" }, statusCode: 201);
        });

        // ---- Empresas emisoras --------------------------------------------------------------------------------------
        Ruta(raiz, "GET", "/issuing_companies", (q, s, ct, l, _) => Task.FromResult(s.ListarEmisoras(q, l)));
        Ruta(raiz, "POST", "/issuing_companies", (q, s, ct, l, b) => Task.FromResult(s.CrearEmisora(q, b)));
        Ruta(raiz, "GET", "/issuing_companies/{id:int}", (q, s, ct, l, _, r) => Task.FromResult(s.ObtenerEmisora(q, Id(r, "id"))));
        Ruta(raiz, "PUT", "/issuing_companies/{id:int}", (q, s, ct, l, b, r) => Task.FromResult(s.ActualizarEmisora(q, Id(r, "id"), b)));
        Ruta(raiz, "POST", "/issuing_companies/{id:int}/upload_seals", (q, s, ct, l, b, r) => Task.FromResult(new Resp(501, new
        {
            errors = new[] { "Uploading the digital seal (CSD) through the API is not available: the certificate is configured in CONTPAQi and its password lives in the worker." },
        })));

        // ---- Compañías ----------------------------------------------------------------------------------------------
        Ruta(raiz, "GET", "/companies", (q, s, ct, l, _) => Task.FromResult(s.ListarCompanias(q, l)));
        Ruta(raiz, "POST", "/companies", (q, s, ct, l, b) => s.CrearCompaniaAsync(q, b, ct));
        Ruta(raiz, "GET", "/companies/{id:int}", (q, s, ct, l, _, r) => Task.FromResult(s.ObtenerCompania(q, Id(r, "id"))));
        Ruta(raiz, "GET", "/companies/{id:int}/edit", (q, s, ct, l, _, r) => Task.FromResult(s.ObtenerCompania(q, Id(r, "id"))));
        Ruta(raiz, "PATCH", "/companies/{id:int}", (q, s, ct, l, b, r) => s.ActualizarCompaniaAsync(q, Id(r, "id"), b, ct));
        Ruta(raiz, "DELETE", "/companies/{id:int}", (q, s, ct, l, _, r) => Task.FromResult(s.EliminarCompania(q, Id(r, "id"))));

        // ---- Direcciones --------------------------------------------------------------------------------------------
        Ruta(raiz, "GET", "/companies/{companyId:int}/addresses", (q, s, ct, l, _, r) => Task.FromResult(s.ListarDirecciones(q, Id(r, "companyId"), l)));
        Ruta(raiz, "POST", "/companies/{companyId:int}/addresses", (q, s, ct, l, b, r) => s.CrearDireccionAsync(q, Id(r, "companyId"), b, ct));
        Ruta(raiz, "GET", "/companies/{companyId:int}/addresses/{id:int}/edit", (q, s, ct, l, _, r) => Task.FromResult(s.ObtenerDireccion(q, Id(r, "companyId"), Id(r, "id"))));
        Ruta(raiz, "PATCH", "/companies/{companyId:int}/addresses/{id:int}", (q, s, ct, l, b, r) => s.ActualizarDireccionAsync(q, Id(r, "companyId"), Id(r, "id"), b, ct));
        Ruta(raiz, "DELETE", "/companies/{companyId:int}/addresses/{id:int}", (q, s, ct, l, _, r) => Task.FromResult(s.EliminarDireccion(q, Id(r, "companyId"), Id(r, "id"))));

        // ---- Productos ----------------------------------------------------------------------------------------------
        Ruta(raiz, "GET", "/products", (q, s, ct, l, _) => Task.FromResult(s.ListarProductos(q, l)));
        Ruta(raiz, "POST", "/products", (q, s, ct, l, b) => s.CrearProductoAsync(q, b, ct));
        Ruta(raiz, "GET", "/products/new", (q, s, ct, l, _) => Task.FromResult(s.ProductoNuevo(q)));
        Ruta(raiz, "GET", "/products/{id:int}/edit", (q, s, ct, l, _, r) => Task.FromResult(s.ObtenerProducto(q, Id(r, "id"))));
        Ruta(raiz, "PATCH", "/products/{id:int}", (q, s, ct, l, b, r) => s.ActualizarProductoAsync(q, Id(r, "id"), b, ct));
        Ruta(raiz, "DELETE", "/products/{id:int}", (q, s, ct, l, _, r) => Task.FromResult(s.EliminarProducto(q, Id(r, "id"))));

        // ---- Facturas -----------------------------------------------------------------------------------------------
        const string F = "/issuing_companies/{eid:int}/invoices";
        Ruta(raiz, "GET", F, (q, s, ct, l, _, r) => Task.FromResult(s.ListarFacturas(q, Id(r, "eid"), l)));
        Ruta(raiz, "POST", F, (q, s, ct, l, b, r) => Task.FromResult(s.CrearFactura(q, Id(r, "eid"), b)));
        Ruta(raiz, "GET", F + "/new", (q, s, ct, l, _, r) => Task.FromResult(s.FacturaNueva(q, Id(r, "eid"))));
        Ruta(raiz, "GET", F + "/{id:int}", (q, s, ct, l, _, r) => Task.FromResult(s.ObtenerFacturaDetalle(q, Id(r, "eid"), Id(r, "id"))));
        Ruta(raiz, "GET", F + "/{id:int}/edit", (q, s, ct, l, _, r) => Task.FromResult(s.ObtenerFacturaDetalle(q, Id(r, "eid"), Id(r, "id"))));
        Ruta(raiz, "PATCH", F + "/{id:int}", (q, s, ct, l, b, r) => Task.FromResult(s.ActualizarFactura(q, Id(r, "eid"), Id(r, "id"), b)));
        Ruta(raiz, "DELETE", F + "/{id:int}", (q, s, ct, l, _, r) => Task.FromResult(s.EliminarFactura(q, Id(r, "eid"), Id(r, "id"))));
        Ruta(raiz, "POST", F + "/{id:int}/stamps", (q, s, ct, l, _, r) => s.TimbrarAsync(q, Id(r, "eid"), Id(r, "id"), ct));
        Ruta(raiz, "POST", F + "/{id:int}/cancelate", (q, s, ct, l, b, r) => s.CancelarAsync(q, Id(r, "eid"), Id(r, "id"), b, ct));
        Ruta(raiz, "GET", F + "/{id:int}/document", (q, s, ct, l, _, r) => s.ArchivoAsync(q, Id(r, "eid"), Id(r, "id"), "pdf", ct));
        Ruta(raiz, "GET", F + "/{id:int}/invoice", (q, s, ct, l, _, r) => s.ArchivoAsync(q, Id(r, "eid"), Id(r, "id"), "pdf", ct));
        Ruta(raiz, "GET", F + "/{id:int}/xml", (q, s, ct, l, _, r) => s.ArchivoAsync(q, Id(r, "eid"), Id(r, "id"), "xml", ct));
        foreach (var previa in new[] { "preinvoice", "proforma" })
        {
            Ruta(raiz, "GET", F + "/{id:int}/" + previa, (q, s, ct, l, _, r) => Task.FromResult(new Resp(501, new
            {
                errors = new[] { "The preliminary document is not available in this version: it requires printing a draft from CONTPAQi." },
            })));
        }

        Ruta(raiz, "POST", "/invoices/batches_stamp", (q, s, ct, l, b) => s.TimbrarLoteAsync(q, b, ct));

        // ---- Partidas -----------------------------------------------------------------------------------------------
        Ruta(raiz, "GET", "/movements", (q, s, ct, l, _) => Task.FromResult(s.ListarMovimientos(q, null, l)), conFactura: true);
        Ruta(raiz, "POST", "/movements", (q, s, ct, l, b) => Task.FromResult(s.CrearMovimiento(q, b)));
        Ruta(raiz, "GET", "/movements/new", (q, s, ct, l, _) => Task.FromResult(s.MovimientoNuevo(q)));
        Ruta(raiz, "GET", "/movements/{id:int}/edit", (q, s, ct, l, _, r) => Task.FromResult(s.ObtenerMovimiento(q, Id(r, "id"))));
        Ruta(raiz, "PATCH", "/movements/{id:int}", (q, s, ct, l, b, r) => Task.FromResult(s.ActualizarMovimiento(q, Id(r, "id"), b)));
        Ruta(raiz, "DELETE", "/movements/{id:int}", (q, s, ct, l, _, r) => Task.FromResult(s.EliminarMovimiento(q, Id(r, "id"))));
    }

    private static int Id(RouteValueDictionary valores, string nombre) =>
        int.TryParse(Convert.ToString(valores[nombre], System.Globalization.CultureInfo.InvariantCulture), out var i) ? i : 0;

    // Sobrecargas para no repetir el parámetro de ruta cuando una ruta no lo usa.
    private static void Ruta(RouteGroupBuilder g, string metodo, string patron, Func<string, FacturacionServicio, CancellationToken, ParametrosLista, JsonNode?, Task<Resp>> f, bool conFactura = false) =>
        Ruta(g, metodo, patron, (q, s, ct, l, b, _) => f(q, s, ct, l, b), conFactura);

    private static void Ruta(RouteGroupBuilder g, string metodo, string patron, Func<string, FacturacionServicio, CancellationToken, ParametrosLista, JsonNode?, RouteValueDictionary, Task<Resp>> f, bool conFactura = false)
    {
        async Task<IResult> Manejar(HttpContext http, Almacen almacen, Seguridad seguridad, FacturacionServicio servicio, int? limit, int? offset, string? order_by_field, string? order_by_direction, int? invoice_id)
        {
            if (Autenticar(http, almacen, seguridad) is not { } quien)
            {
                return Results.Json(new { errors = new[] { "No authorization headers provided" } }, statusCode: 401);
            }

            PeticionActual.Texto = $"{metodo} {http.Request.Path}";
            var (cuerpo, invalido) = await LeerCuerpoAsync(http, metodo);
            if (invalido)
            {
                return Results.Json(new { errors = new[] { "The request body is not valid JSON (UTF-8)" } }, statusCode: 400);
            }

            var lista = new ParametrosLista(limit, offset, order_by_field, order_by_direction);
            var r = conFactura
                ? servicio.ListarMovimientos(quien.ClienteId, invoice_id, lista) // GET /movements?invoice_id=…
                : await f(quien.ClienteId, servicio, http.RequestAborted, lista, cuerpo, http.Request.RouteValues);
            return Results.Json(r.Body, statusCode: r.Status);
        }

        g.MapMethods(patron, [metodo], Manejar);
    }

    /// <summary>
    /// Lee el cuerpo JSON de POST, PATCH y PUT. Un cuerpo vacío es válido (por ejemplo <c>stamps</c> no lleva datos); uno que no es
    /// JSON o no es UTF-8 se rechaza con 400 en lugar de fallar dentro del servidor.
    /// </summary>
    private static async Task<(JsonNode? Cuerpo, bool Invalido)> LeerCuerpoAsync(HttpContext http, string metodo)
    {
        if (metodo is not ("POST" or "PATCH" or "PUT") || http.Request.ContentLength == 0)
        {
            return (null, false);
        }

        try
        {
            using var memoria = new MemoryStream();
            await http.Request.Body.CopyToAsync(memoria, http.RequestAborted);
            return memoria.Length == 0 ? (null, false) : (JsonNode.Parse(memoria.ToArray()), false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.Text.DecoderFallbackException or ArgumentException)
        {
            return (null, true);
        }
    }

    /// <summary>Misma comprobación que el resto de Central: token vigente de una llave de API o de una persona del portal.</summary>
    /// <summary>Misma comprobación para la API REST de Acusa.</summary>
    internal static Identidad? AutenticarPublico(HttpContext http, Almacen almacen, Seguridad seguridad) => Autenticar(http, almacen, seguridad);

    private static Identidad? Autenticar(HttpContext http, Almacen almacen, Seguridad seguridad)
    {
        var cabecera = http.Request.Headers.Authorization.ToString();
        if (!cabecera.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Token de /api/auth/token o la llave de API (ak_…) directamente, como en el resto de Central.
        var credencial = cabecera[7..].Trim();
        var identidad = credencial.StartsWith("ak_", StringComparison.Ordinal)
            ? almacen.BuscarLlave(credencial)
            : seguridad.ValidarToken(credencial, DateTimeOffset.UtcNow);
        if (identidad is null || (identidad.Rol != Roles.Api && identidad.Rol != Roles.Portal))
        {
            return null;
        }

        var vigente = identidad.Rol == Roles.Portal ? almacen.BuscarUsuarioPorId(identidad.LlaveId) is not null : almacen.LlaveVigente(identidad.LlaveId);
        return vigente ? identidad : null;
    }
}
