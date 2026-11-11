using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Acusa.Orquestador.Facturacion;

/// <summary>Respuesta HTTP del servicio: código y cuerpo.</summary>
public sealed record Resp(int Status, object? Body);

/// <summary>Parámetros de listado del Swagger: <c>limit</c>, <c>offset</c>, <c>order_by_field</c> y <c>order_by_direction</c>.</summary>
public sealed record ParametrosLista(int? Limit, int? Offset, string? Campo, string? Direccion);

/// <summary>
/// Las rutas de facturación del Swagger de Alain: compañías, direcciones, productos, empresas emisoras, facturas y partidas.
/// Acusa guarda estos registros (como su aplicación) y, cuando hay que tocar CONTPAQi, manda la orden al worker por la cola y
/// espera el resultado en la misma llamada. Una factura se guarda como borrador; <c>stamps</c> la crea en Comercial y la timbra.
/// </summary>
public sealed class FacturacionServicio(DocStore store, Almacen almacen, IEjecutorOrdenes ejecutor, Opciones opciones)
{
    private const string Compania = "compania", Direccion = "direccion", Producto = "producto", Emisora = "emisora", Factura = "factura", Movimiento = "movimiento";

    public static readonly IReadOnlyDictionary<string, string> FormasDePago = new Dictionary<string, string>
    {
        ["01"] = "Efectivo", ["02"] = "Cheque nominativo", ["03"] = "Transferencia electrónica de fondos", ["04"] = "Tarjeta de crédito",
        ["05"] = "Monedero electrónico", ["06"] = "Dinero electrónico", ["28"] = "Tarjeta de débito", ["29"] = "Tarjeta de servicios", ["99"] = "Por definir",
    };

    public static readonly IReadOnlyDictionary<string, string> Regimenes = new Dictionary<string, string>
    {
        ["601"] = "General de Ley Personas Morales", ["603"] = "Personas Morales con Fines no Lucrativos",
        ["605"] = "Sueldos y Salarios e Ingresos Asimilados a Salarios", ["606"] = "Arrendamiento", ["607"] = "Régimen de Enajenación o Adquisición de Bienes",
        ["608"] = "Demás ingresos", ["610"] = "Residentes en el Extranjero sin Establecimiento Permanente en México", ["611"] = "Ingresos por Dividendos (socios y accionistas)",
        ["612"] = "Personas Físicas con Actividades Empresariales y Profesionales", ["614"] = "Ingresos por intereses", ["615"] = "Régimen de los ingresos por obtención de premios",
        ["616"] = "Sin obligaciones fiscales", ["620"] = "Sociedades Cooperativas de Producción que optan por diferir sus ingresos", ["621"] = "Incorporación Fiscal",
        ["622"] = "Actividades Agrícolas, Ganaderas, Silvícolas y Pesqueras", ["623"] = "Opcional para Grupos de Sociedades", ["624"] = "Coordinados",
        ["625"] = "Actividades Empresariales con ingresos a través de Plataformas Tecnológicas", ["626"] = "Régimen Simplificado de Confianza",
    };

    public static readonly IReadOnlyDictionary<string, string> UsosCfdi = new Dictionary<string, string>
    {
        ["G01"] = "Adquisición de mercancías", ["G02"] = "Devoluciones, descuentos o bonificaciones", ["G03"] = "Gastos en general", ["I01"] = "Construcciones",
        ["I02"] = "Mobiliario y equipo de oficina por inversiones", ["I03"] = "Equipo de transporte", ["I04"] = "Equipo de cómputo y accesorios",
        ["I05"] = "Dados, troqueles, moldes, matrices y herramental", ["I06"] = "Comunicaciones telefónicas", ["I07"] = "Comunicaciones satelitales",
        ["I08"] = "Otra maquinaria y equipo", ["D01"] = "Honorarios médicos, dentales y gastos hospitalarios", ["D02"] = "Gastos médicos por incapacidad o discapacidad",
        ["D03"] = "Gastos funerales", ["D04"] = "Donativos", ["D05"] = "Intereses reales por créditos hipotecarios (casa habitación)", ["D06"] = "Aportaciones voluntarias al SAR",
        ["D07"] = "Primas por seguros de gastos médicos", ["D08"] = "Gastos de transportación escolar obligatoria", ["D09"] = "Depósitos en cuentas para el ahorro y planes de pensiones",
        ["D10"] = "Pagos por servicios educativos (colegiaturas)", ["S01"] = "Sin efectos fiscales", ["CP01"] = "Pagos", ["CN01"] = "Nómina",
    };

    // ---------------------------------------------------------------------------------------------------------------
    // Utilidades
    // ---------------------------------------------------------------------------------------------------------------

    private static string? Txt(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString().Trim('"'),
        _ => null,
    };

    private static decimal? Num(JsonNode? n) =>
        decimal.TryParse(Txt(n), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int? Ent(JsonNode? n) =>
        int.TryParse(Txt(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static Resp Error(int status, params string[] errores) => new(status, new { errors = errores });

    private static Resp Mensaje(int status, string mensaje, object? extra = null) =>
        new(status, extra is null ? new { message = mensaje } : new { message = mensaje, datos = extra });

    private static Resp NoExiste(string que) => Error(404, $"{que} not found");

    private static string FormaDePagoEtiqueta(string? clave) =>
        clave is not null && FormasDePago.TryGetValue(clave, out var nombre) ? $"{clave} - {nombre}" : clave ?? string.Empty;

    private static string Estado(ResultadoOrden r) => r.Ok ? "completed" : r.EnCurso ? "pending" : "failed";

    /// <summary>Valor de un campo dentro del objeto que envuelve el cuerpo, por ejemplo <c>company</c> en <c>{ "company": { ... } }</c>.</summary>
    private static JsonObject? Envuelto(JsonNode? cuerpo, string nombre) => (cuerpo as JsonObject)?[nombre] as JsonObject;

    private static IReadOnlyList<JsonObject> Paginar(IEnumerable<JsonObject> origen, ParametrosLista p)
    {
        var lista = origen.ToList();
        if (!string.IsNullOrWhiteSpace(p.Campo))
        {
            var campo = p.Campo.Trim();
            var desc = string.Equals(p.Direccion, "desc", StringComparison.OrdinalIgnoreCase);

            // Números como números y texto como texto, para que 10 vaya después de 9.
            int Comparar(JsonObject a, JsonObject b) =>
                Num(a[campo]) is { } x && Num(b[campo]) is { } y
                    ? x.CompareTo(y)
                    : string.Compare(Txt(a[campo]), Txt(b[campo]), StringComparison.OrdinalIgnoreCase);

            var comparador = Comparer<JsonObject>.Create(Comparar);
            lista = (desc ? lista.OrderByDescending(o => o, comparador) : lista.OrderBy(o => o, comparador)).ToList();
        }

        var offset = Math.Max(0, p.Offset ?? 0);
        var limit = Math.Clamp(p.Limit ?? 50, 1, 200);
        return lista.Skip(offset).Take(limit).ToList();
    }

    /// <summary>Empresa de CONTPAQi para una emisora; sin emisora, la configurada o la primera registrada del cliente.</summary>
    private string? EmpresaDe(string clienteId, int? emisoraId)
    {
        if (emisoraId is { } id)
        {
            return Txt(store.Obtener(Emisora, clienteId, id)?["database_name"]);
        }

        if (!string.IsNullOrWhiteSpace(opciones.FacturacionEmpresa))
        {
            return opciones.FacturacionEmpresa.Trim();
        }

        return store.Listar(Emisora, clienteId).Select(e => Txt(e["database_name"])).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
               ?? almacen.EmpresasDelCliente(clienteId).FirstOrDefault();
    }

    private static string Codigo(string prefijo, int id) => prefijo + id.ToString("000", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------------------------------
    // Empresas emisoras
    // ---------------------------------------------------------------------------------------------------------------

    private static object EmisoraDto(JsonObject e) => new { id = Ent(e["id"]), social_reason = Txt(e["social_reason"]), rfc = Txt(e["rfc"]), database_name = Txt(e["database_name"]) };

    public Resp ListarEmisoras(string clienteId, ParametrosLista p) =>
        new(200, Paginar(store.Listar(Emisora, clienteId), p).Select(EmisoraDto).ToList());

    public Resp ObtenerEmisora(string clienteId, int id) =>
        store.Obtener(Emisora, clienteId, id) is { } e ? new(200, EmisoraDto(e)) : NoExiste("Issuing company");

    public Resp CrearEmisora(string clienteId, JsonNode? cuerpo)
    {
        var d = Envuelto(cuerpo, "issuing_company");
        if (d is null) return Error(422, "Issuing company can't be blank");
        var errores = new List<string>();
        if (string.IsNullOrWhiteSpace(Txt(d["social_reason"]))) errores.Add("Social reason can't be blank");
        if (string.IsNullOrWhiteSpace(Txt(d["rfc"]))) errores.Add("Rfc can't be blank");
        if (string.IsNullOrWhiteSpace(Txt(d["database_name"]))) errores.Add("Database name can't be blank");
        if (errores.Count > 0) return new(422, new { errors = errores });

        var nombre = Txt(d["database_name"])!.Trim();
        almacen.AgregarEmpresa(clienteId, nombre);
        var doc = new JsonObject { ["social_reason"] = Txt(d["social_reason"])!.Trim(), ["rfc"] = Txt(d["rfc"])!.Trim().ToUpperInvariant(), ["database_name"] = nombre };
        store.Insertar(Emisora, clienteId, doc);
        return new(200, EmisoraDto(doc));
    }

    public Resp ActualizarEmisora(string clienteId, int id, JsonNode? cuerpo)
    {
        var e = store.Obtener(Emisora, clienteId, id);
        if (e is null) return NoExiste("Issuing company");
        var d = Envuelto(cuerpo, "issuing_company");
        if (d is null) return Error(422, "Issuing company can't be blank");
        if (Txt(d["social_reason"]) is { Length: > 0 } sr) e["social_reason"] = sr.Trim();
        if (Txt(d["rfc"]) is { Length: > 0 } rfc) e["rfc"] = rfc.Trim().ToUpperInvariant();
        if (Txt(d["database_name"]) is { Length: > 0 } db) { e["database_name"] = db.Trim(); almacen.AgregarEmpresa(clienteId, db.Trim()); }
        store.Actualizar(Emisora, clienteId, id, e);
        return new(200, EmisoraDto(e));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Compañías (clientes de CONTPAQi)
    // ---------------------------------------------------------------------------------------------------------------

    private static bool Activo(JsonObject o) => o["active"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;

    private static object CompaniaLista(JsonObject c) => new
    {
        id = Ent(c["id"]), code = Txt(c["code"]), social_reason = Txt(c["social_reason"]), rfc = Txt(c["rfc"]), task_id = Txt(c["task_id"]), status = Txt(c["status"]),
    };

    private static object CompaniaDetalle(JsonObject c) => new
    {
        id = Ent(c["id"]), code = Txt(c["code"]), social_reason = Txt(c["social_reason"]), rfc = Txt(c["rfc"]),
        client_type = Ent(c["client_type"]), credit_limit = Num(c["credit_limit"]), task_id = Txt(c["task_id"]), status = Txt(c["status"]), error = Txt(c["error"]),
    };

    public Resp ListarCompanias(string clienteId, ParametrosLista p) =>
        new(200, Paginar(store.Listar(Compania, clienteId).Where(Activo), p).Select(CompaniaLista).ToList());

    public Resp ObtenerCompania(string clienteId, int id) =>
        store.Obtener(Compania, clienteId, id) is { } c && Activo(c) ? new(200, CompaniaDetalle(c)) : NoExiste("Company");

    public async Task<Resp> CrearCompaniaAsync(string clienteId, JsonNode? cuerpo, CancellationToken ct)
    {
        var d = Envuelto(cuerpo, "company");
        if (d is null) return Error(422, "Company can't be blank");
        var errores = new List<string>();
        if (string.IsNullOrWhiteSpace(Txt(d["social_reason"]))) errores.Add("Social reason can't be blank");
        if (string.IsNullOrWhiteSpace(Txt(d["rfc"]))) errores.Add("Rfc can't be blank");
        if (Ent(d["client_type"]) is null) errores.Add("Client type can't be blank");
        if (Num(d["credit_limit"]) is null) errores.Add("Credit limit can't be blank");
        if (errores.Count > 0) return new(422, new { errors = errores });

        var empresa = EmpresaDe(clienteId, null);
        if (empresa is null) return Error(422, "No hay una empresa de CONTPAQi registrada para este cliente");

        var doc = new JsonObject
        {
            ["social_reason"] = Txt(d["social_reason"])!.Trim(), ["rfc"] = Txt(d["rfc"])!.Trim().ToUpperInvariant(),
            ["client_type"] = Ent(d["client_type"])!.Value, ["credit_limit"] = Num(d["credit_limit"])!.Value, ["status"] = "pending", ["active"] = true,
            ["empresa"] = empresa,
        };
        var id = store.Insertar(Compania, clienteId, doc);
        doc["code"] = Codigo(opciones.FacturacionPrefijoCliente, id);

        var r = await ejecutor.EjecutarAsync(clienteId, empresa, "cliente.crear", new
        {
            codigo = Txt(doc["code"]), razonSocial = Txt(doc["social_reason"]), rfc = Txt(doc["rfc"]), tipoCliente = Ent(doc["client_type"]), limiteCredito = Num(doc["credit_limit"]),
        }, $"compania-{id}-crear", ct);

        doc["task_id"] = r.TareaId; doc["status"] = Estado(r); doc["error"] = r.Ok ? null : r.Error;
        store.Actualizar(Compania, clienteId, id, doc);
        return r.Ok || r.EnCurso ? new(201, new { id, code = Txt(doc["code"]), social_reason = Txt(doc["social_reason"]), rfc = Txt(doc["rfc"]), task_id = r.TareaId, status = Estado(r) }) : Error(422, r.Error ?? "Company could not be created");
    }

    public async Task<Resp> ActualizarCompaniaAsync(string clienteId, int id, JsonNode? cuerpo, CancellationToken ct)
    {
        var c = store.Obtener(Compania, clienteId, id);
        if (c is null || !Activo(c)) return NoExiste("Company");
        var d = Envuelto(cuerpo, "company");
        if (d is null) return Error(422, "Company can't be blank");

        var razon = Txt(d["social_reason"]) is { Length: > 0 } sr ? sr.Trim() : null;
        var rfc = Txt(d["rfc"]) is { Length: > 0 } rf ? rf.Trim().ToUpperInvariant() : null;
        var limite = Num(d["credit_limit"]);
        if (razon is null && rfc is null && limite is null) return Error(422, "Nothing to update");

        var r = await ejecutor.EjecutarAsync(clienteId, Txt(c["empresa"]) ?? EmpresaDe(clienteId, null) ?? string.Empty, "cliente.actualizar", new
        {
            codigo = Txt(c["code"]), razonSocial = razon, rfc, limiteCredito = limite,
        }, null, ct);
        if (!r.Ok && !r.EnCurso) return Error(422, r.Error ?? "Company could not be updated");

        if (razon is not null) c["social_reason"] = razon;
        if (rfc is not null) c["rfc"] = rfc;
        if (limite is not null) c["credit_limit"] = limite.Value;
        c["task_id"] = r.TareaId; c["status"] = Estado(r);
        store.Actualizar(Compania, clienteId, id, c);
        return new(200, CompaniaDetalle(c));
    }

    /// <summary>Da de baja la compañía en Acusa. En CONTPAQi el cliente se conserva: Acusa no borra registros de Comercial.</summary>
    public Resp EliminarCompania(string clienteId, int id)
    {
        var c = store.Obtener(Compania, clienteId, id);
        if (c is null || !Activo(c)) return NoExiste("Company");
        c["active"] = false;
        store.Actualizar(Compania, clienteId, id, c);
        return Mensaje(200, "Company removed from Acusa; the client remains in CONTPAQi");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Direcciones
    // ---------------------------------------------------------------------------------------------------------------

    private static object DireccionDto(JsonObject a) => new
    {
        id = Ent(a["id"]), street = Txt(a["street"]), neighborhood = Txt(a["neighborhood"]), zip_code = Txt(a["zip_code"]), city = Txt(a["city"]), state = Txt(a["state"]),
        country = Txt(a["country"]), address_type = Ent(a["address_type"]), catalog_type = Ent(a["catalog_type"]), status = Txt(a["status"]),
    };

    public Resp ListarDirecciones(string clienteId, int companiaId, ParametrosLista p) =>
        store.Obtener(Compania, clienteId, companiaId) is null
            ? NoExiste("Company")
            : new(200, Paginar(store.Listar(Direccion, clienteId).Where(a => Ent(a["company_id"]) == companiaId && Activo(a)), p).Select(DireccionDto).ToList());

    public Resp ObtenerDireccion(string clienteId, int companiaId, int id) =>
        store.Obtener(Direccion, clienteId, id) is { } a && Ent(a["company_id"]) == companiaId && Activo(a) ? new(200, DireccionDto(a)) : NoExiste("Address");

    private static List<string> ValidarDireccion(JsonObject d, bool parcial)
    {
        var errores = new List<string>();
        foreach (var (campo, etiqueta) in new[] { ("street", "Street"), ("neighborhood", "Neighborhood"), ("zip_code", "Zip code"), ("city", "City"), ("state", "State"), ("country", "Country") })
        {
            if (!parcial && string.IsNullOrWhiteSpace(Txt(d[campo]))) errores.Add($"{etiqueta} can't be blank");
        }

        if (!parcial && Ent(d["address_type"]) is null) errores.Add("Address type can't be blank");
        if (Ent(d["address_type"]) is { } t && t is not (0 or 1)) errores.Add("Address type must be 0 (fiscal) or 1 (shipping)");
        return errores;
    }

    private async Task<Resp> EnviarDireccionAsync(string clienteId, JsonObject compania, JsonObject a, int id, CancellationToken ct)
    {
        var r = await ejecutor.EjecutarAsync(clienteId, Txt(compania["empresa"]) ?? EmpresaDe(clienteId, null) ?? string.Empty, "direccion.crear", new
        {
            codigoCliente = Txt(compania["code"]), tipo = Ent(a["address_type"]) ?? 0, calle = Txt(a["street"]), colonia = Txt(a["neighborhood"]), codigoPostal = Txt(a["zip_code"]),
            ciudad = Txt(a["city"]), estado = Txt(a["state"]), pais = Txt(a["country"]),
        }, null, ct);
        a["task_id"] = r.TareaId; a["status"] = Estado(r); a["error"] = r.Ok ? null : r.Error;
        store.Actualizar(Direccion, clienteId, id, a);
        return r.Ok || r.EnCurso ? new(r.Ok ? 201 : 202, new { message = "success", id, task_id = r.TareaId, status = Estado(r) }) : Error(422, r.Error ?? "Address could not be saved");
    }

    public async Task<Resp> CrearDireccionAsync(string clienteId, int companiaId, JsonNode? cuerpo, CancellationToken ct)
    {
        var c = store.Obtener(Compania, clienteId, companiaId);
        if (c is null || !Activo(c)) return NoExiste("Company");
        var d = Envuelto(cuerpo, "address");
        if (d is null) return Error(422, "Address can't be blank");
        var errores = ValidarDireccion(d, false);
        if (errores.Count > 0) return new(422, new { errors = errores });

        var a = new JsonObject
        {
            ["company_id"] = companiaId, ["street"] = Txt(d["street"])!.Trim(), ["neighborhood"] = Txt(d["neighborhood"])!.Trim(), ["zip_code"] = Txt(d["zip_code"])!.Trim(),
            ["city"] = Txt(d["city"])!.Trim(), ["state"] = Txt(d["state"])!.Trim(), ["country"] = Txt(d["country"])!.Trim(), ["address_type"] = Ent(d["address_type"])!.Value,
            ["catalog_type"] = Ent(d["catalog_type"]) ?? 1, ["status"] = "pending", ["active"] = true,
        };
        var id = store.Insertar(Direccion, clienteId, a);
        return await EnviarDireccionAsync(clienteId, c, a, id, ct);
    }

    public async Task<Resp> ActualizarDireccionAsync(string clienteId, int companiaId, int id, JsonNode? cuerpo, CancellationToken ct)
    {
        var c = store.Obtener(Compania, clienteId, companiaId);
        var a = store.Obtener(Direccion, clienteId, id);
        if (c is null || a is null || Ent(a["company_id"]) != companiaId || !Activo(a)) return NoExiste("Address");
        var d = Envuelto(cuerpo, "address");
        if (d is null) return Error(422, "Address can't be blank");
        var errores = ValidarDireccion(d, true);
        if (errores.Count > 0) return new(422, new { errors = errores });
        foreach (var campo in new[] { "street", "neighborhood", "zip_code", "city", "state", "country" })
        {
            if (Txt(d[campo]) is { Length: > 0 } v) a[campo] = v.Trim();
        }

        if (Ent(d["address_type"]) is { } t) a["address_type"] = t;
        return await EnviarDireccionAsync(clienteId, c, a, id, ct);
    }

    public Resp EliminarDireccion(string clienteId, int companiaId, int id)
    {
        var a = store.Obtener(Direccion, clienteId, id);
        if (a is null || Ent(a["company_id"]) != companiaId || !Activo(a)) return NoExiste("Address");
        a["active"] = false;
        store.Actualizar(Direccion, clienteId, id, a);
        return Mensaje(200, "Address removed from Acusa; it remains in CONTPAQi");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Productos
    // ---------------------------------------------------------------------------------------------------------------

    private static object ProductoLista(JsonObject p) => new
    {
        id = Ent(p["id"]), code = Txt(p["code"]), name = Txt(p["name"]), unit_base = Txt(p["unit_base"]), description = Txt(p["description"]),
        price = Num(p["price"])?.ToString("0.0###", CultureInfo.InvariantCulture), series_sat = Txt(p["series_sat"]), task_id = Txt(p["task_id"]), status = Txt(p["status"]),
    };

    private static object ProductoDetalle(JsonObject p) => new
    {
        id = Ent(p["id"]), code = Txt(p["code"]), name = Txt(p["name"]), unit_base = Txt(p["unit_base"]), description = Txt(p["description"]),
        price = Num(p["price"])?.ToString("0.0###", CultureInfo.InvariantCulture), series_sat = Txt(p["series_sat"]), product_type = Ent(p["product_type"]),
        issuing_company_id = Ent(p["issuing_company_id"]), extra_texts = (p["extra_texts"] as JsonArray)?.Select(Txt).ToList() ?? new List<string?>(), task_id = Txt(p["task_id"]), status = Txt(p["status"]), error = Txt(p["error"]),
    };

    private static List<string> TextosExtra(JsonObject d) =>
        (d["extra_texts_attributes"] as JsonArray)?.Select(x => Txt((x as JsonObject)?["extra_text"])).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()).ToList() ?? [];

    public Resp ListarProductos(string clienteId, ParametrosLista p) =>
        new(200, Paginar(store.Listar(Producto, clienteId).Where(Activo), p).Select(ProductoLista).ToList());

    public Resp ObtenerProducto(string clienteId, int id) =>
        store.Obtener(Producto, clienteId, id) is { } p && Activo(p) ? new(200, ProductoDetalle(p)) : NoExiste("Product");

    public Resp ProductoNuevo(string clienteId) => new(200, new
    {
        product_types = new[] { new { id = 1, name = "Materia prima" }, new { id = 2, name = "Producto terminado" }, new { id = 3, name = "Servicio" } },
        unit_bases = opciones.FacturacionUnidades,
        issuing_companies = store.Listar(Emisora, clienteId).Select(EmisoraDto).ToList(),
    });

    public async Task<Resp> CrearProductoAsync(string clienteId, JsonNode? cuerpo, CancellationToken ct)
    {
        var d = Envuelto(cuerpo, "product");
        if (d is null) return Error(422, "Product can't be blank");
        var errores = new List<string>();
        if (string.IsNullOrWhiteSpace(Txt(d["name"]))) errores.Add("Name can't be blank");
        if (Num(d["price"]) is null) errores.Add("Price can't be blank");
        if (string.IsNullOrWhiteSpace(Txt(d["description"]))) errores.Add("Description can't be blank");
        if (string.IsNullOrWhiteSpace(Txt(d["unit_base"]))) errores.Add("Unit base can't be blank");
        if (string.IsNullOrWhiteSpace(Txt(d["series_sat"]))) errores.Add("Series sat can't be blank");
        if (Ent(d["product_type"]) is null) errores.Add("Product type can't be blank");
        var emisoraId = Ent(d["issuing_company_id"]);
        if (emisoraId is not null && store.Obtener(Emisora, clienteId, emisoraId.Value) is null) errores.Add("Issuing company not found");
        if (errores.Count > 0) return new(422, new { errors = errores });

        var empresa = EmpresaDe(clienteId, emisoraId);
        if (empresa is null) return Error(422, "No hay una empresa de CONTPAQi registrada para este cliente");

        var doc = new JsonObject
        {
            ["name"] = Txt(d["name"])!.Trim(), ["price"] = Num(d["price"])!.Value, ["description"] = Txt(d["description"])!.Trim(), ["unit_base"] = Txt(d["unit_base"])!.Trim(),
            ["series_sat"] = Txt(d["series_sat"])!.Trim(), ["product_type"] = Ent(d["product_type"])!.Value, ["issuing_company_id"] = emisoraId, ["empresa"] = empresa,
            ["extra_texts"] = new JsonArray(TextosExtra(d).Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()), ["status"] = "pending", ["active"] = true,
        };
        var id = store.Insertar(Producto, clienteId, doc);
        doc["code"] = Codigo(opciones.FacturacionPrefijoProducto, id);

        var r = await ejecutor.EjecutarAsync(clienteId, empresa, "producto.crear", new
        {
            codigo = Txt(doc["code"]), nombre = Txt(doc["name"]), unidadBase = Txt(doc["unit_base"]), precio1 = Num(doc["price"]), descripcion = Txt(doc["description"]),
            claveSat = Txt(doc["series_sat"]), tipoProducto = Ent(doc["product_type"]), textosExtra = TextosExtra(d),
        }, $"producto-{id}-crear", ct);

        doc["task_id"] = r.TareaId; doc["status"] = Estado(r); doc["error"] = r.Ok ? null : r.Error;
        store.Actualizar(Producto, clienteId, id, doc);
        return r.Ok || r.EnCurso ? new(201, new { id, code = Txt(doc["code"]), name = Txt(doc["name"]), task_id = r.TareaId, status = Estado(r) }) : Error(422, r.Error ?? "Product could not be created");
    }

    public async Task<Resp> ActualizarProductoAsync(string clienteId, int id, JsonNode? cuerpo, CancellationToken ct)
    {
        var p = store.Obtener(Producto, clienteId, id);
        if (p is null || !Activo(p)) return NoExiste("Product");
        var d = Envuelto(cuerpo, "product");
        if (d is null) return Error(422, "Product can't be blank");

        var nombre = Txt(d["name"]) is { Length: > 0 } n ? n.Trim() : null;
        var desc = Txt(d["description"]) is { Length: > 0 } ds ? ds.Trim() : null;
        var precio = Num(d["price"]);
        var clave = Txt(d["series_sat"]) is { Length: > 0 } cs ? cs.Trim() : null;
        var textos = TextosExtra(d);
        if (nombre is null && desc is null && precio is null && clave is null && textos.Count == 0) return Error(422, "Nothing to update");

        var r = await ejecutor.EjecutarAsync(clienteId, Txt(p["empresa"]) ?? EmpresaDe(clienteId, null) ?? string.Empty, "producto.actualizar", new
        {
            codigo = Txt(p["code"]), nombre, descripcion = desc, precio1 = precio, claveSat = clave, textosExtra = textos.Count > 0 ? textos : null,
        }, null, ct);
        if (!r.Ok && !r.EnCurso) return Error(422, r.Error ?? "Product could not be updated");

        if (nombre is not null) p["name"] = nombre;
        if (desc is not null) p["description"] = desc;
        if (precio is not null) p["price"] = precio.Value;
        if (clave is not null) p["series_sat"] = clave;
        if (textos.Count > 0) p["extra_texts"] = new JsonArray(textos.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        p["task_id"] = r.TareaId; p["status"] = Estado(r);
        store.Actualizar(Producto, clienteId, id, p);
        return new(200, ProductoDetalle(p));
    }

    public Resp EliminarProducto(string clienteId, int id)
    {
        var p = store.Obtener(Producto, clienteId, id);
        if (p is null || !Activo(p)) return NoExiste("Product");
        p["active"] = false;
        store.Actualizar(Producto, clienteId, id, p);
        return Mensaje(200, "Product removed from Acusa; it remains in CONTPAQi");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Facturas y partidas
    // ---------------------------------------------------------------------------------------------------------------

    private JsonObject? ObtenerFactura(string clienteId, int emisoraId, int id) =>
        store.Obtener(Factura, clienteId, id) is { } f && Ent(f["issuing_company_id"]) == emisoraId ? f : null;

    private JsonObject? CompaniaPorCodigo(string clienteId, string? codigo) =>
        store.Listar(Compania, clienteId).FirstOrDefault(c => Activo(c) && string.Equals(Txt(c["code"]), codigo, StringComparison.OrdinalIgnoreCase));

    private JsonObject? ProductoPorCodigo(string clienteId, string? codigo) =>
        store.Listar(Producto, clienteId).FirstOrDefault(p => Activo(p) && string.Equals(Txt(p["code"]), codigo, StringComparison.OrdinalIgnoreCase));

    private List<JsonObject> Partidas(string clienteId, int facturaId) =>
        store.Listar(Movimiento, clienteId).Where(m => Ent(m["invoice_id"]) == facturaId).ToList();

    private object ClienteDto(string clienteId, string? codigo) =>
        CompaniaPorCodigo(clienteId, codigo) is { } c
            ? new { id = Ent(c["id"]), code = Txt(c["code"]), social_reason = Txt(c["social_reason"]), rfc = Txt(c["rfc"]) }
            : new { id = (int?)null, code = codigo, social_reason = (string?)null, rfc = (string?)null };

    private object FacturaResumen(string clienteId, JsonObject f) => new
    {
        id = Ent(f["id"]), concept = Txt(f["concept"]), series = Txt(f["series"]), invoice_date = Txt(f["invoice_date"]), client = ClienteDto(clienteId, Txt(f["client_code"])),
        payment_method = FormaDePagoEtiqueta(Txt(f["payment_method"])), sat_status = Txt(f["sat_status"]), uuid = Txt(f["uuid"]), folio = Txt(f["folio"]),
    };

    private object FacturaDetalle(string clienteId, JsonObject f) => new
    {
        id = Ent(f["id"]), concept = Txt(f["concept"]), series = Txt(f["series"]), invoice_date = Txt(f["invoice_date"]), payment_method = FormaDePagoEtiqueta(Txt(f["payment_method"])),
        sat_status = Txt(f["sat_status"]), uuid = Txt(f["uuid"]), cancellation_reason = Txt(f["cancellation_reason"]), error_message = Txt(f["error_message"]), folio = Txt(f["folio"]),
        tax_regime = Txt(f["tax_regime"]), cfdi = Txt(f["cfdi"]), client = ClienteDto(clienteId, Txt(f["client_code"])),
        movements = Partidas(clienteId, Ent(f["id"]) ?? 0).Select(m => MovimientoDto(clienteId, m)).ToList(),
    };

    private object MovimientoDto(string clienteId, JsonObject m)
    {
        var p = ProductoPorCodigo(clienteId, Txt(m["product_code"]));
        return new
        {
            id = Ent(m["id"]), concept = Txt(p?["name"]) ?? Txt(m["product_code"]), quantity = Num(m["quantity"]), price = (Num(m["price"]) ?? Num(p?["price"]))?.ToString("0.0###", CultureInfo.InvariantCulture),
            vat_percentage = Num(m["vat_percentage"]), vat_retention_percentage = Num(m["vat_retention_percentage"]), ieps_percentage = Num(m["ieps_percentage"]), invoice_id = Ent(m["invoice_id"]),
            product = new { code = Txt(m["product_code"]), name = Txt(p?["name"]), description = Txt(p?["description"]), unit_price = Num(p?["price"])?.ToString("0.0###", CultureInfo.InvariantCulture), unit_base = Txt(p?["unit_base"]) },
        };
    }

    private List<string> ValidarFactura(string clienteId, JsonObject d, bool parcial)
    {
        var errores = new List<string>();
        foreach (var (campo, etiqueta) in new[] { ("concept", "Concept"), ("series", "Series"), ("invoice_date", "Invoice date"), ("client_code", "Client code"), ("payment_method", "Payment method"), ("cfdi", "Cfdi"), ("tax_regime", "Tax regime") })
        {
            if (!parcial && string.IsNullOrWhiteSpace(Txt(d[campo]))) errores.Add($"{etiqueta} can't be blank");
        }

        if (Txt(d["invoice_date"]) is { Length: > 0 } fecha && !DateOnly.TryParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errores.Add("Invoice date must be yyyy-MM-dd");
        if (Txt(d["payment_method"]) is { Length: > 0 } pm && !FormasDePago.ContainsKey(pm)) errores.Add("Payment method is not a SAT payment method key");
        if (Txt(d["cfdi"]) is { Length: > 0 } uso && !UsosCfdi.ContainsKey(uso.ToUpperInvariant())) errores.Add("Cfdi is not a SAT CFDI use key");
        if (Txt(d["tax_regime"]) is { Length: > 0 } rg && !Regimenes.ContainsKey(rg)) errores.Add("Tax regime is not a SAT tax regime key");
        if (Txt(d["client_code"]) is { Length: > 0 } cc && CompaniaPorCodigo(clienteId, cc) is null) errores.Add("Client code not found");
        return errores;
    }

    private List<string> ValidarMovimiento(string clienteId, JsonObject d, bool parcial)
    {
        var errores = new List<string>();
        if (!parcial && string.IsNullOrWhiteSpace(Txt(d["product_code"]))) errores.Add("Product code can't be blank");
        if (!parcial && Num(d["quantity"]) is null) errores.Add("Quantity can't be blank");
        if (Txt(d["product_code"]) is { Length: > 0 } pc && ProductoPorCodigo(clienteId, pc) is null) errores.Add("Product code not found");
        if (Num(d["quantity"]) is <= 0) errores.Add("Quantity must be greater than 0");
        if (Num(d["price"]) is < 0) errores.Add("Price must be 0 or greater");
        foreach (var (campo, etiqueta) in new[] { ("vat_percentage", "Vat percentage"), ("vat_retention_percentage", "Vat retention percentage"), ("ieps_percentage", "Ieps percentage") })
        {
            if (Num(d[campo]) is < 0 or > 100) errores.Add($"{etiqueta} must be between 0 and 100");
        }

        return errores;
    }

    private static JsonObject NuevaPartida(JsonObject d, int facturaId)
    {
        var m = new JsonObject { ["invoice_id"] = facturaId, ["product_code"] = Txt(d["product_code"])!.Trim().ToUpperInvariant(), ["quantity"] = Num(d["quantity"])!.Value };
        if (Num(d["price"]) is { } pr) m["price"] = pr;
        if (Num(d["vat_percentage"]) is { } iva) m["vat_percentage"] = iva;
        if (Num(d["vat_retention_percentage"]) is { } ret) m["vat_retention_percentage"] = ret;
        if (Num(d["ieps_percentage"]) is { } ieps) m["ieps_percentage"] = ieps;
        return m;
    }

    public Resp ListarFacturas(string clienteId, int emisoraId, ParametrosLista p) =>
        store.Obtener(Emisora, clienteId, emisoraId) is null
            ? NoExiste("Issuing company")
            : new(200, Paginar(store.Listar(Factura, clienteId).Where(f => Ent(f["issuing_company_id"]) == emisoraId && Activo(f)), p).Select(f => FacturaResumen(clienteId, f)).ToList());

    public Resp FacturaNueva(string clienteId, int emisoraId) =>
        store.Obtener(Emisora, clienteId, emisoraId) is null
            ? NoExiste("Issuing company")
            : new(200, new
            {
                clients = store.Listar(Compania, clienteId).Where(Activo).Select(CompaniaLista).ToList(),
                products = store.Listar(Producto, clienteId).Where(Activo).Select(ProductoLista).ToList(),
                payment_methods = FormasDePago.Select(x => new { id = x.Key, name = $"{x.Key} - {x.Value}" }).ToList(),
                tax_regimes = Regimenes.Select(x => new { id = x.Key, name = $"{x.Key} - {x.Value}" }).ToList(),
                cfdis = UsosCfdi.Select(x => new { id = x.Key, name = $"{x.Key} - {x.Value}" }).ToList(),
                concepts = opciones.FacturacionConceptos.Select(c => new { id = c, name = c }).ToList(),
                issuing_companies = store.Listar(Emisora, clienteId).Select(EmisoraDto).ToList(),
            });

    public Resp ObtenerFacturaDetalle(string clienteId, int emisoraId, int id) =>
        ObtenerFactura(clienteId, emisoraId, id) is { } f && Activo(f) ? new(200, FacturaDetalle(clienteId, f)) : NoExiste("Invoice");

    public Resp CrearFactura(string clienteId, int emisoraId, JsonNode? cuerpo)
    {
        if (store.Obtener(Emisora, clienteId, emisoraId) is null) return NoExiste("Issuing company");
        var d = Envuelto(cuerpo, "invoice");
        if (d is null) return Error(422, "Invoice can't be blank");
        var errores = ValidarFactura(clienteId, d, false);
        var movimientos = (d["movements_attributes"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        for (var i = 0; i < movimientos.Count; i++)
        {
            errores.AddRange(ValidarMovimiento(clienteId, movimientos[i], false).Select(e => $"Movement {i + 1}: {e}"));
        }

        if (errores.Count > 0) return new(422, new { errors = errores });

        var f = new JsonObject
        {
            ["issuing_company_id"] = emisoraId, ["concept"] = Txt(d["concept"])!.Trim(), ["series"] = Txt(d["series"])!.Trim().ToUpperInvariant(), ["invoice_date"] = Txt(d["invoice_date"])!.Trim(),
            ["client_code"] = Txt(d["client_code"])!.Trim().ToUpperInvariant(), ["payment_method"] = Txt(d["payment_method"])!.Trim(), ["cfdi"] = Txt(d["cfdi"])!.Trim().ToUpperInvariant(),
            ["tax_regime"] = Txt(d["tax_regime"])!.Trim(), ["sat_status"] = "draft", ["active"] = true, ["intento_crear"] = 0, ["intento_emitir"] = 0, ["intento_cancelar"] = 0,
        };
        var id = store.Insertar(Factura, clienteId, f);
        foreach (var m in movimientos)
        {
            store.Insertar(Movimiento, clienteId, NuevaPartida(m, id));
        }

        return new(201, new { id, concept = Txt(f["concept"]), series = Txt(f["series"]), invoice_date = Txt(f["invoice_date"]), client = ClienteDto(clienteId, Txt(f["client_code"])), payment_method = FormaDePagoEtiqueta(Txt(f["payment_method"])) });
    }

    public Resp ActualizarFactura(string clienteId, int emisoraId, int id, JsonNode? cuerpo)
    {
        var f = ObtenerFactura(clienteId, emisoraId, id);
        if (f is null || !Activo(f)) return NoExiste("Invoice");
        if (Txt(f["sat_status"]) != "draft" || Txt(f["folio"]) is not null) return Error(422, "Only an invoice that has not been created in CONTPAQi can be edited");
        var d = Envuelto(cuerpo, "invoice");
        if (d is null) return Error(422, "Invoice can't be blank");
        var errores = ValidarFactura(clienteId, d, true);
        if (errores.Count > 0) return new(422, new { errors = errores });
        foreach (var campo in new[] { "concept", "series", "invoice_date", "client_code", "payment_method", "cfdi", "tax_regime" })
        {
            if (Txt(d[campo]) is { Length: > 0 } v) f[campo] = campo is "series" or "client_code" or "cfdi" ? v.Trim().ToUpperInvariant() : v.Trim();
        }

        store.Actualizar(Factura, clienteId, id, f);
        return new(200, FacturaDetalle(clienteId, f));
    }

    public Resp EliminarFactura(string clienteId, int emisoraId, int id)
    {
        var f = ObtenerFactura(clienteId, emisoraId, id);
        if (f is null || !Activo(f)) return NoExiste("Invoice");
        if (Txt(f["sat_status"]) != "draft" || Txt(f["folio"]) is not null) return Error(422, "Only a draft can be removed; cancel a stamped invoice instead");
        foreach (var m in Partidas(clienteId, id)) store.Eliminar(Movimiento, clienteId, Ent(m["id"])!.Value);
        f["active"] = false;
        store.Actualizar(Factura, clienteId, id, f);
        return Mensaje(200, "Draft removed");
    }

    // ---- Timbrar ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Crea la factura en CONTPAQi (si todavía no existe) y la timbra. El régimen fiscal y el uso de CFDI viven en el cliente de
    /// Comercial, así que primero se actualizan ahí. Cada paso lleva una llave de idempotencia: repetir la llamada tras un corte no
    /// duplica el documento. La llave cambia solo cuando un intento falló del todo.
    /// </summary>
    public async Task<Resp> TimbrarAsync(string clienteId, int emisoraId, int id, CancellationToken ct)
    {
        var f = ObtenerFactura(clienteId, emisoraId, id);
        if (f is null || !Activo(f)) return NoExiste("Invoice");
        var estado = Txt(f["sat_status"]);
        if (estado == "stamped") return Error(422, "Invoice was stamped before");
        if (estado == "canceled") return Error(422, "Invoice was canceled before");

        var empresa = EmpresaDe(clienteId, emisoraId);
        if (empresa is null) return Error(422, "La empresa emisora no tiene empresa de CONTPAQi");
        var partidas = Partidas(clienteId, id);
        if (partidas.Count == 0) return Error(422, "Invoice has no movements");
        var compania = CompaniaPorCodigo(clienteId, Txt(f["client_code"]));
        if (compania is null) return Error(422, "Client code not found");

        Resp Falla(string paso, ResultadoOrden r, string campoIntento)
        {
            if (!r.EnCurso) f[campoIntento] = (Ent(f[campoIntento]) ?? 0) + 1;
            f["error_message"] = r.EnCurso ? $"{paso}: el worker no respondió a tiempo" : r.Error;
            store.Actualizar(Factura, clienteId, id, f);
            return r.EnCurso
                ? new Resp(202, new { message = "Stamping invoice in progress", id, task_id = r.TareaId })
                : Error(422, r.Error ?? $"{paso} failed");
        }

        var empresaCompania = Txt(compania["empresa"]) ?? empresa;
        if (Txt(f["folio"]) is null)
        {
            var act = await ejecutor.EjecutarAsync(clienteId, empresaCompania, "cliente.actualizar", new
            {
                codigo = Txt(compania["code"]), regimenFiscal = Txt(f["tax_regime"]), usoCfdi = Txt(f["cfdi"]),
            }, $"factura-{id}-cliente-{Ent(f["intento_crear"]) ?? 0}", ct);
            if (!act.Ok) return Falla("Customer data", act, "intento_crear");

            var crear = await ejecutor.EjecutarAsync(clienteId, empresa, "documento.crear", new
            {
                concepto = Txt(f["concept"]), serie = Txt(f["series"]), clienteCodigo = Txt(f["client_code"]), referencia = $"{opciones.FacturacionPrefijoReferencia}{id}",
                fecha = Txt(f["invoice_date"]), formaPago = Txt(f["payment_method"]), usoCfdi = Txt(f["cfdi"]),
                partidas = partidas.Select(m => new
                {
                    productoCodigo = Txt(m["product_code"]), unidades = Num(m["quantity"]), almacen = opciones.FacturacionAlmacen, precio = Num(m["price"]) ?? Num(ProductoPorCodigo(clienteId, Txt(m["product_code"]))?["price"]) ?? 0m,
                    ivaPorcentaje = Num(m["vat_percentage"]), retencionIvaPorcentaje = Num(m["vat_retention_percentage"]), iepsPorcentaje = Num(m["ieps_percentage"]),
                }).ToList(),
            }, $"factura-{id}-crear-{Ent(f["intento_crear"]) ?? 0}", ct);
            if (!crear.Ok) return Falla("Document creation", crear, "intento_crear");

            var folio = crear.Datos is { } dc && dc.TryGetProperty("folio", out var fo) ? fo.ToString() : null;
            f["folio"] = folio; f["sat_status"] = "created"; f["error_message"] = null;
            if (crear.Datos is { } dd && dd.TryGetProperty("id", out var di)) f["documento_id"] = di.ToString();
            store.Actualizar(Factura, clienteId, id, f);
        }

        var emitir = await ejecutor.EjecutarAsync(clienteId, empresa, "documento.emitir", new
        {
            concepto = Txt(f["concept"]), serie = Txt(f["series"]), folio = Num(f["folio"]),
        }, $"factura-{id}-emitir-{Ent(f["intento_emitir"]) ?? 0}", ct);
        if (!emitir.Ok) return Falla("Stamping", emitir, "intento_emitir");

        var uuid = emitir.Datos is { } de && de.TryGetProperty("uuid", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        f["uuid"] = uuid; f["sat_status"] = "stamped"; f["error_message"] = null;
        store.Actualizar(Factura, clienteId, id, f);
        return new(200, new { message = "Invoice stamped", id, folio = Txt(f["folio"]), uuid, sat_status = "stamped" });
    }

    public async Task<Resp> TimbrarLoteAsync(string clienteId, JsonNode? cuerpo, CancellationToken ct)
    {
        var ids = (cuerpo as JsonObject)?["invoice_ids"] as JsonArray;
        if (ids is null || ids.Count == 0) return Error(422, "Invoice ids can't be blank");
        if (ids.Count > 25) return Error(422, "A batch has up to 25 invoices");

        var resultados = new List<object>();
        foreach (var n in ids)
        {
            var id = Ent(n);
            var f = id is null ? null : store.Obtener(Factura, clienteId, id.Value);
            if (f is null) { resultados.Add(new { id, ok = false, error = "Invoice not found" }); continue; }
            var r = await TimbrarAsync(clienteId, Ent(f["issuing_company_id"]) ?? 0, id!.Value, ct);
            resultados.Add(new { id, ok = r.Status == 200, status = r.Status, detail = r.Body });
        }

        return new(200, new { message = "Stamping finished", results = resultados });
    }

    // ---- Cancelar y archivos ----------------------------------------------------------------------------------------

    public async Task<Resp> CancelarAsync(string clienteId, int emisoraId, int id, JsonNode? cuerpo, CancellationToken ct)
    {
        var f = ObtenerFactura(clienteId, emisoraId, id);
        if (f is null || !Activo(f)) return NoExiste("Invoice");
        if (Txt(f["sat_status"]) != "stamped") return Error(422, "Invoice was canceled before or is not stamped.");
        var d = Envuelto(cuerpo, "cancellation");
        var motivo = Txt(d?["cancellation_reason"]);
        if (string.IsNullOrWhiteSpace(motivo)) return Error(422, "Cancellation reason can't be blank");
        var sustituto = Txt(d?["substitution_uuid"]);

        var empresa = EmpresaDe(clienteId, emisoraId);
        if (empresa is null) return Error(422, "La empresa emisora no tiene empresa de CONTPAQi");
        var r = await ejecutor.EjecutarAsync(clienteId, empresa, "documento.cancelar", new
        {
            concepto = Txt(f["concept"]), serie = Txt(f["series"]), folio = Num(f["folio"]), motivo = motivo.Trim(), uuidSustituto = string.IsNullOrWhiteSpace(sustituto) ? null : sustituto.Trim(),
        }, $"factura-{id}-cancelar-{Ent(f["intento_cancelar"]) ?? 0}", ct);
        if (!r.Ok)
        {
            if (!r.EnCurso) f["intento_cancelar"] = (Ent(f["intento_cancelar"]) ?? 0) + 1;
            f["error_message"] = r.Error;
            store.Actualizar(Factura, clienteId, id, f);
            return r.EnCurso ? new(202, new { message = "Canceling invoice in progress", id, task_id = r.TareaId }) : Error(422, r.Error ?? "Cancellation failed");
        }

        f["sat_status"] = "canceled"; f["cancellation_reason"] = motivo.Trim(); f["error_message"] = null;
        store.Actualizar(Factura, clienteId, id, f);
        return new(200, new { message = "Invoice canceled", id, sat_status = "canceled" });
    }

    /// <summary>XML o PDF de una factura timbrada, en base64.</summary>
    public async Task<Resp> ArchivoAsync(string clienteId, int emisoraId, int id, string formato, CancellationToken ct)
    {
        var f = ObtenerFactura(clienteId, emisoraId, id);
        if (f is null || !Activo(f)) return NoExiste("Invoice");
        if (Txt(f["sat_status"]) is not ("stamped" or "canceled")) return Error(422, "Invoice is not stamped");
        var empresa = EmpresaDe(clienteId, emisoraId);
        if (empresa is null) return Error(422, "La empresa emisora no tiene empresa de CONTPAQi");

        var r = await ejecutor.EjecutarAsync(clienteId, empresa, "documento.obtener-archivo", new
        {
            concepto = Txt(f["concept"]), serie = Txt(f["series"]), folio = Num(f["folio"]), formato, incluirContenido = true,
        }, null, ct);
        if (!r.Ok) return r.EnCurso ? new(202, new { message = "File request in progress", task_id = r.TareaId }) : Error(422, r.Error ?? "File could not be retrieved");

        var archivos = r.Datos is { } d && d.TryGetProperty("archivos", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];
        var archivo = archivos.FirstOrDefault(x => x.TryGetProperty("tipo", out var t) && string.Equals(t.GetString(), formato, StringComparison.OrdinalIgnoreCase));
        if (archivo.ValueKind != JsonValueKind.Object) return Error(422, $"CONTPAQi did not deliver the {formato.ToUpperInvariant()} file");
        var contenido = archivo.TryGetProperty("base64", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        if (contenido is null) return Error(422, "The file is larger than the size Acusa returns inline");
        return new(200, new { id, format = formato, uuid = Txt(f["uuid"]), file_name = $"{Txt(f["series"])}{Txt(f["folio"])}.{formato}", content_base64 = contenido });
    }

    // ---- Partidas ---------------------------------------------------------------------------------------------------

    public Resp ListarMovimientos(string clienteId, int? facturaId, ParametrosLista p) =>
        new(200, Paginar(store.Listar(Movimiento, clienteId).Where(m => facturaId is null || Ent(m["invoice_id"]) == facturaId), p).Select(m => MovimientoDto(clienteId, m)).ToList());

    public Resp MovimientoNuevo(string clienteId) => new(200, new { products = store.Listar(Producto, clienteId).Where(Activo).Select(ProductoLista).ToList() });

    public Resp ObtenerMovimiento(string clienteId, int id) =>
        store.Obtener(Movimiento, clienteId, id) is { } m ? new(200, MovimientoDto(clienteId, m)) : NoExiste("Movement");

    private bool FacturaEditable(string clienteId, int? facturaId, out JsonObject? factura)
    {
        factura = facturaId is null ? null : store.Obtener(Factura, clienteId, facturaId.Value);
        return factura is not null && Activo(factura) && Txt(factura["sat_status"]) == "draft" && Txt(factura["folio"]) is null;
    }

    public Resp CrearMovimiento(string clienteId, JsonNode? cuerpo)
    {
        var d = Envuelto(cuerpo, "movement");
        if (d is null) return Error(422, "Movement can't be blank");
        var facturaId = Ent(d["invoice_id"]);
        if (facturaId is null) return Error(422, "Invoice can't be blank");
        if (!FacturaEditable(clienteId, facturaId, out var f))
            return f is null ? NoExiste("Invoice") : Error(422, "Movements can only be added to a draft invoice");
        var errores = ValidarMovimiento(clienteId, d, false);
        if (errores.Count > 0) return new(422, new { errors = errores });
        var m = NuevaPartida(d, facturaId.Value);
        var id = store.Insertar(Movimiento, clienteId, m);
        return new(201, MovimientoDto(clienteId, m));
    }

    public Resp ActualizarMovimiento(string clienteId, int id, JsonNode? cuerpo)
    {
        var m = store.Obtener(Movimiento, clienteId, id);
        if (m is null) return NoExiste("Movement");
        if (!FacturaEditable(clienteId, Ent(m["invoice_id"]), out _)) return Error(422, "Movements can only be changed on a draft invoice");
        var d = Envuelto(cuerpo, "movement");
        if (d is null) return Error(422, "Movement can't be blank");
        var errores = ValidarMovimiento(clienteId, d, true);
        if (errores.Count > 0) return new(422, new { errors = errores });
        if (Txt(d["product_code"]) is { Length: > 0 } pc) m["product_code"] = pc.Trim().ToUpperInvariant();
        if (Num(d["quantity"]) is { } q) m["quantity"] = q;
        if (Num(d["price"]) is { } pr) m["price"] = pr;
        if (Num(d["vat_percentage"]) is { } iva) m["vat_percentage"] = iva;
        if (Num(d["vat_retention_percentage"]) is { } ret) m["vat_retention_percentage"] = ret;
        if (Num(d["ieps_percentage"]) is { } ieps) m["ieps_percentage"] = ieps;
        store.Actualizar(Movimiento, clienteId, id, m);
        return new(200, MovimientoDto(clienteId, m));
    }

    public Resp EliminarMovimiento(string clienteId, int id)
    {
        var m = store.Obtener(Movimiento, clienteId, id);
        if (m is null) return NoExiste("Movement");
        if (!FacturaEditable(clienteId, Ent(m["invoice_id"]), out _)) return Error(422, "Movements can only be removed from a draft invoice");
        store.Eliminar(Movimiento, clienteId, id);
        return Mensaje(200, "Movement removed");
    }
}
