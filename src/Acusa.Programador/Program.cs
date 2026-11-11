// Acusa Programador 1.0.0: un sistema externo de ejemplo. Guarda "programaciones" (trabajos con su horario), las activa con una
// casilla y manda las órdenes a la API de Acusa Central: documento.crear y documento.emitir. Tambien permite una factura manual.
// No conoce CONTPAQi: solo cliente, producto, concepto y horario. La llave de API vive aquí, en el servidor, nunca en el navegador.
// Solo trabaja sobre una empresa de pruebas (el nombre debe contener PRUEBA).
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(builder.Configuration.GetSection("Programador").Get<Opciones>() ?? new Opciones());
builder.Services.AddSingleton<Central>();
builder.Services.AddSingleton<Bitacora>();
builder.Services.AddSingleton<Planificador>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Planificador>());
builder.Services.AddHttpClient();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // La página es pequeña y cambia con las versiones: que el navegador no la guarde.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, no-store",
});

app.MapGet("/api/config", (Opciones o) => new
{
    o.Empresa, o.Concepto, o.Serie, o.ClienteCodigo, o.ProductoCodigo, o.Referencia, o.Almacen, o.UrlCentral,
    empresaSegura = o.EmpresaSegura,
    ahora = DateTime.Now,
    version = Marca.Version,
});

// ---- Programaciones -------------------------------------------------------------------------------------------------

app.MapGet("/api/programaciones", (Planificador p) => p.Todas().Select(p.Vista));

app.MapPut("/api/programaciones/{id}", (string id, CambioProgramacion c, Planificador p) =>
{
    var r = p.Cambiar(id, c);
    return r is null ? Results.NotFound(new { error = "No existe esa programación." }) : Results.Ok(p.Vista(r));
});

app.MapPost("/api/programaciones/{id}/ejecutar", (string id, Planificador p, Opciones o) =>
{
    if (!o.EmpresaSegura) return Results.BadRequest(new { error = $"Por seguridad solo se trabaja en una empresa de pruebas; '{o.Empresa}' no lo es." });
    return p.EjecutarAhora(id) is { } c ? Results.Accepted($"/api/corridas/{c.Id}", c) : Results.NotFound(new { error = "No existe esa programación." });
});

app.MapGet("/api/corridas", (Bitacora b) => b.Corridas());

// ---- Factura manual ---------------------------------------------------------------------

app.MapGet("/api/facturas", (Bitacora b) => b.Todas());

app.MapPost("/api/facturas", (SolicitudFactura s, Opciones o, Central c, Bitacora b) =>
{
    if (!o.EmpresaSegura) return Results.BadRequest(new { error = $"Por seguridad solo se trabaja en una empresa de pruebas; '{o.Empresa}' no lo es." });
    if (s.Cantidad <= 0 || s.Precio <= 0) return Results.BadRequest(new { error = "La cantidad y el precio deben ser mayores que cero." });
    var f = b.Nueva(
        string.IsNullOrWhiteSpace(s.ClienteCodigo) ? o.ClienteCodigo : s.ClienteCodigo.Trim().ToUpperInvariant(),
        string.IsNullOrWhiteSpace(s.ProductoCodigo) ? o.ProductoCodigo : s.ProductoCodigo.Trim().ToUpperInvariant(),
        s.Cantidad, s.Precio);
    _ = Task.Run(() => c.FacturarAsync(f));
    return Results.Accepted($"/api/facturas/{f.Id}", f);
});

app.MapPost("/api/facturas/{id}/cancelar", (string id, SolicitudCancelar s, Central c, Bitacora b) =>
{
    var f = b.Buscar(id);
    if (f is null) return Results.NotFound(new { error = "No existe la factura." });
    if (f.Estado != "Timbrada") return Results.BadRequest(new { error = "Solo se cancela una factura timbrada." });
    _ = Task.Run(() => c.CancelarAsync(f, string.IsNullOrWhiteSpace(s.Motivo) ? "02" : s.Motivo.Trim()));
    return Results.Accepted($"/api/facturas/{id}", f);
});

app.MapGet("/api/facturas/{id}/archivo/{tipo}", async (string id, string tipo, Central c, Bitacora b) =>
{
    var f = b.Buscar(id);
    if (f is null || f.Folio is null) return Results.NotFound(new { error = "No existe la factura." });
    if (tipo is not ("xml" or "pdf")) return Results.BadRequest(new { error = "El tipo es xml o pdf." });
    var (bytes, error) = await c.ArchivoAsync(f, tipo);
    return bytes is null
        ? Results.Json(new { error }, statusCode: 502)
        : Results.File(bytes, tipo == "xml" ? "application/xml" : "application/pdf", $"{f.Serie}{f.Folio}.{tipo}");
});

app.Run();

// =====================================================================================================================

public sealed class Opciones
{
    public string UrlCentral { get; set; } = "http://127.0.0.1:5090";
    public string LlaveApi { get; set; } = string.Empty;
    public string Empresa { get; set; } = "adEMPRESA_DE_PRUEBA";
    public string Concepto { get; set; } = "5";

    /// <summary>Conceptos que se prueban, en orden, si Comercial rechaza el anterior por falta de permisos.</summary>
    public string[] ConceptosAlternos { get; set; } = [];
    public string Serie { get; set; } = "PRUEBA";
    public string ClienteCodigo { get; set; } = "PRUEBADEMO01";
    public string ProductoCodigo { get; set; } = "PRUEBAPR01";
    public string Referencia { get; set; } = "PRUEBA-PROGRAMADOR";
    public string Almacen { get; set; } = "1";

    /// <summary>Solo se opera sobre empresas de pruebas.</summary>
    public bool EmpresaSegura => Empresa.Contains("PRUEBA", StringComparison.OrdinalIgnoreCase);
}

public sealed record SolicitudFactura(string? ClienteCodigo, string? ProductoCodigo, decimal Cantidad, decimal Precio);

public sealed record SolicitudCancelar(string? Motivo);

/// <summary>Nombre y versión del producto.</summary>
public static class Marca
{
    public const string Version = "1.0.0";
}

public sealed record CambioProgramacion(bool? Activa, bool? Timbrar, int? DocumentosMin, int? DocumentosMax);

// ---- Programaciones ------------------------------------------------------------------------------------------------------

/// <summary>
/// Una plantilla de trabajo con su horario. Cada ejecución crea los documentos (documento.crear) y, si se pide, los timbra (documento.emitir).
/// </summary>
public sealed class Programacion
{
    public string Id { get; init; } = "";
    public string Nombre { get; init; } = "";
    public string Descripcion { get; init; } = "";
    public bool Activa { get; set; }
    public bool Timbrar { get; set; } = true;

    /// <summary>Cada cuántas horas dentro de la ventana (1 = cada hora, 2 = cada dos horas).</summary>
    public int CadaHoras { get; init; } = 1;
    public int Minuto { get; init; } = 5;

    /// <summary>Si es mayor que cero, la programación corre cada tantos minutos a cualquier hora (para la demostración).</summary>
    public int CadaMinutos { get; init; }
    public int HoraInicio { get; init; } = 9;
    public int HoraFin { get; init; } = 19;
    public bool SoloLunesAViernes { get; init; } = true;
    public int DocumentosMin { get; set; } = 1;
    public int DocumentosMax { get; set; } = 2;
    public decimal PrecioMin { get; init; } = 20;
    public decimal PrecioMax { get; init; } = 10000;

    public DateTime? Proxima { get; set; }
    public DateTime? Ultima { get; set; }
    public int Ejecuciones { get; set; }

    /// <summary>Texto del horario, para mostrarlo.</summary>
    public string Horario()
    {
        if (CadaMinutos > 0) return $"cada {CadaMinutos} minutos, a cualquier hora y cualquier día";
        var dias = SoloLunesAViernes ? "de lunes a viernes" : "todos los días";
        var cada = CadaHoras == 1 ? "cada hora" : $"cada {CadaHoras} horas";
        return $"{dias}, de {HoraInicio}:00 a {HoraFin}:00, {cada} al minuto :{Minuto:00}";
    }

    /// <summary>Primer horario posible estrictamente posterior a <paramref name="desde"/>.</summary>
    public DateTime? Siguiente(DateTime desde)
    {
        if (CadaMinutos > 0)
        {
            var minutos = (long)Math.Floor(desde.TimeOfDay.TotalMinutes / CadaMinutos) + 1;
            return desde.Date.AddMinutes(minutos * CadaMinutos);
        }

        for (var d = 0; d < 9; d++)
        {
            var dia = desde.Date.AddDays(d);
            if (SoloLunesAViernes && dia.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            for (var h = HoraInicio; h <= HoraFin; h += Math.Max(1, CadaHoras))
            {
                var t = dia.AddHours(h).AddMinutes(Minuto);
                if (t > desde) return t;
            }
        }

        return null;
    }
}

public sealed class Paso
{
    public string Tipo { get; init; } = "";
    public string Estado { get; set; } = "Pendiente";
    public int? Ms { get; set; }
    public DateTime? Hora { get; set; }
    public string? Detalle { get; set; }
}

public sealed class Corrida
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string ProgramacionId { get; init; } = "";
    public string Nombre { get; init; } = "";
    public bool Manual { get; init; }
    public DateTime Programada { get; init; }
    public DateTime? Inicio { get; set; }
    public DateTime? Fin { get; set; }
    public string Estado { get; set; } = "En cola";
    public List<Paso> Pasos { get; } = [];
    public List<string> Folios { get; } = [];
    public string? Error { get; set; }
}

public sealed class Planificador(Opciones o, Central central, Bitacora bitacora, ILogger<Planificador> log) : BackgroundService
{
    private readonly List<Programacion> _lista = Plantillas();
    private readonly object _l = new();
    private readonly SemaphoreSlim _turno = new(1, 1);
    private readonly Random _azar = new();

    /// <summary>Las programaciones de ejemplo que trae el Programador.</summary>
    private static List<Programacion> Plantillas() =>
    [
        new()
        {
            Id = "cada-hora", Nombre = "Cada hora al :05",
            Descripcion = "Una racha por hora, al minuto :05, con uno o dos documentos por racha.",
            CadaHoras = 1, Minuto = 5, HoraInicio = 9, HoraFin = 19, SoloLunesAViernes = true, DocumentosMin = 1, DocumentosMax = 2,
        },
        new()
        {
            Id = "cada-2h", Nombre = "Cada 2 horas al :00",
            Descripcion = "Una racha cada dos horas, al minuto :00, a cualquier hora, con dos documentos.",
            CadaHoras = 2, Minuto = 0, HoraInicio = 0, HoraFin = 22, SoloLunesAViernes = false, DocumentosMin = 2, DocumentosMax = 2,
        },
        new()
        {
            Id = "demo-5min", Nombre = "Demostración: cada 5 minutos",
            Descripcion = "Solo para la demo: una racha cada 5 minutos para ver cómo aparecen los documentos solos en Comercial. Apáguela al terminar.",
            CadaMinutos = 5, DocumentosMin = 1, DocumentosMax = 1,
        },
    ];

    public IReadOnlyList<Programacion> Todas() { lock (_l) return [.. _lista]; }

    public object Vista(Programacion p) => new
    {
        p.Id, p.Nombre, p.Descripcion, p.Activa, p.Timbrar, horario = p.Horario(), p.Proxima, p.Ultima, p.Ejecuciones,
        p.DocumentosMin, p.DocumentosMax, p.PrecioMin, p.PrecioMax,
    };

    public Programacion? Cambiar(string id, CambioProgramacion c)
    {
        lock (_l)
        {
            var p = _lista.FirstOrDefault(x => x.Id == id);
            if (p is null) return null;
            if (c.Timbrar is { } t) p.Timbrar = t;
            if (c.DocumentosMin is { } mn) p.DocumentosMin = Math.Clamp(mn, 1, 25);
            if (c.DocumentosMax is { } mx) p.DocumentosMax = Math.Clamp(mx, p.DocumentosMin, 25);
            if (c.Activa is { } a)
            {
                p.Activa = a && o.EmpresaSegura;
                p.Proxima = p.Activa ? p.Siguiente(DateTime.Now) : null;
            }

            return p;
        }
    }

    public Corrida? EjecutarAhora(string id)
    {
        Programacion? p;
        lock (_l) p = _lista.FirstOrDefault(x => x.Id == id);
        if (p is null) return null;
        var c = bitacora.NuevaCorrida(p, DateTime.Now, manual: true);
        _ = Task.Run(() => CorrerAsync(p, c));
        return c;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Planificador listo. Empresa {Empresa}; {Cantidad} programaciones.", o.Empresa, _lista.Count);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ahora = DateTime.Now;
                List<(Programacion P, DateTime Hora)> vencidas = [];
                lock (_l)
                {
                    foreach (var p in _lista.Where(x => x.Activa && x.Proxima is not null && x.Proxima <= ahora))
                    {
                        vencidas.Add((p, p.Proxima!.Value));
                        p.Proxima = p.Siguiente(ahora);
                    }
                }

                foreach (var (p, hora) in vencidas)
                {
                    var c = bitacora.NuevaCorrida(p, hora, manual: false);
                    _ = Task.Run(() => CorrerAsync(p, c), ct);
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Error en el planificador."); }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    /// <summary>Una corrida: crea los documentos y, si se pide, los emite.</summary>
    private async Task CorrerAsync(Programacion p, Corrida c)
    {
        try
        {
            c.Estado = "Esperando turno";
            await _turno.WaitAsync();
            try
            {
                c.Inicio = DateTime.Now;
                c.Estado = "En curso";
                var n = _azar.Next(p.DocumentosMin, p.DocumentosMax + 1);
                var folios = new List<(string Concepto, string Serie, double Folio)>();
                var fallo = false;
                for (var i = 1; i <= n && !fallo; i++)
                {
                    var precio = Math.Round((decimal)(_azar.NextDouble() * (double)(p.PrecioMax - p.PrecioMin)) + p.PrecioMin, 2);
                    var concepto = o.Concepto;
                    var referencia = "PRUEBAP" + DateTime.Now.ToString("MMddHHmmss") + i;
                    var (ok, doc) = await Paso(c, "documento.crear", () => central.EjecutarAsync("documento.crear", new
                    {
                        concepto, serie = o.Serie, clienteCodigo = o.ClienteCodigo, referencia,
                        partidas = new[] { new { productoCodigo = o.ProductoCodigo, unidades = 1m, precio, almacen = o.Almacen } },
                    }));
                    if (!ok) { fallo = true; break; }
                    var serie = doc?["serie"]?.GetValue<string>() ?? o.Serie;
                    var folio = doc?["folio"]?.GetValue<double>() ?? 0;
                    c.Folios.Add($"{serie}{folio}");
                    folios.Add((concepto, serie, folio));
                }

                if (p.Timbrar && !fallo)
                {
                    foreach (var (concepto, serie, folio) in folios)
                    {
                        var (ok, _) = await Paso(c, "documento.emitir", () => central.EjecutarAsync("documento.emitir", new { concepto, serie, folio }));
                        if (!ok) fallo = true;
                    }
                }

                c.Fin = DateTime.Now;
                c.Estado = fallo ? "Con errores" : "Completada";
                p.Ultima = c.Inicio;
                p.Ejecuciones++;
            }
            finally { _turno.Release(); }
        }
        catch (Exception ex)
        {
            c.Estado = "Con errores";
            c.Error = ex.Message;
            c.Fin = DateTime.Now;
        }
    }

    private static async Task<(bool Ok, JsonNode? Datos)> Paso(Corrida c, string tipo, Func<Task<(JsonNode? Datos, string? Error)>> accion)
    {
        var paso = new Paso { Tipo = tipo, Estado = "En curso", Hora = DateTime.Now };
        c.Pasos.Add(paso);
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var (datos, error) = await accion();
        paso.Ms = (int)reloj.ElapsedMilliseconds;
        paso.Estado = error is null ? "Completada" : "Fallida";
        paso.Detalle = error;
        if (error is not null) c.Error = error;
        return (error is null, datos);
    }
}

// ---- Factura manual y bitácora ----------------------------------------------------------------------------------------------

public sealed class Factura
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Fecha { get; init; } = DateTime.Now;
    public string ClienteCodigo { get; init; } = "";
    public string ProductoCodigo { get; init; } = "";
    public string Concepto { get; set; } = "";
    public decimal Cantidad { get; init; }
    public decimal Precio { get; init; }
    public string Estado { get; set; } = "Enviando";
    public string? Serie { get; set; }
    public double? Folio { get; set; }
    public string? Uuid { get; set; }
    public string? Error { get; set; }
    public List<string> Pasos { get; } = [];
}

public sealed class Bitacora
{
    private readonly List<Factura> _lista = [];
    private readonly List<Corrida> _corridas = [];
    private readonly object _l = new();

    public Factura Nueva(string cliente, string producto, decimal cantidad, decimal precio)
    {
        var f = new Factura { ClienteCodigo = cliente, ProductoCodigo = producto, Cantidad = cantidad, Precio = precio };
        lock (_l) _lista.Insert(0, f);
        return f;
    }

    public Factura? Buscar(string id) { lock (_l) return _lista.FirstOrDefault(x => x.Id == id); }

    public List<Factura> Todas() { lock (_l) return [.. _lista]; }

    public Corrida NuevaCorrida(Programacion p, DateTime programada, bool manual)
    {
        var c = new Corrida { ProgramacionId = p.Id, Nombre = p.Nombre, Programada = programada, Manual = manual };
        lock (_l)
        {
            _corridas.Insert(0, c);
            if (_corridas.Count > 200) _corridas.RemoveRange(200, _corridas.Count - 200);
        }

        return c;
    }

    public List<Corrida> Corridas() { lock (_l) return [.. _corridas.Take(60)]; }
}

/// <summary>Cliente de la API de Acusa Central: token de corta vida, órdenes asíncronas y espera del resultado.</summary>
public sealed class Central(Opciones o, IHttpClientFactory fabrica)
{
    private string? _token;
    private DateTime _vence;
    private readonly SemaphoreSlim _acceso = new(1, 1);

    private async Task<HttpClient> ClienteAsync()
    {
        var http = fabrica.CreateClient();
        http.BaseAddress = new Uri(o.UrlCentral);
        await _acceso.WaitAsync();
        try
        {
            if (_token is null || DateTime.UtcNow > _vence)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/token");
                req.Headers.Add("x-api-key", o.LlaveApi);
                var r = await http.SendAsync(req);
                r.EnsureSuccessStatusCode();
                var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
                _token = j["token"]!.GetValue<string>();
                _vence = DateTime.UtcNow.AddSeconds(Math.Max(30, (j["expiraEnSegundos"]?.GetValue<int>() ?? 300) - 30));
            }
        }
        finally { _acceso.Release(); }

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return http;
    }

    /// <summary>Envía una orden y espera su resultado. Devuelve (datos, error).</summary>
    public async Task<(JsonNode? Datos, string? Error)> EjecutarAsync(string tipo, object payload, int segundos = 90)
    {
        var http = await ClienteAsync();
        var cuerpo = JsonSerializer.Serialize(new { tipoProceso = tipo, empresa = o.Empresa, payload });
        var r = await http.PostAsync("/api/sdk/execute", new StringContent(cuerpo, Encoding.UTF8, "application/json"));
        var texto = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) return (null, $"Acusa Central rechazó la orden ({(int)r.StatusCode}): {texto}");
        var id = JsonNode.Parse(texto)!["tareaId"]?.GetValue<string>();
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        while (DateTime.UtcNow < limite)
        {
            await Task.Delay(500);
            var t = JsonNode.Parse(await http.GetStringAsync($"/api/sdk/tasks/{id}"))!;
            var estado = t["estado"]?.GetValue<string>();
            if (estado == "Completada") return (t["resultado"], null);
            if (estado == "Fallida") return (null, t["error"]?.GetValue<string>() ?? "La tarea falló.");
        }

        return (null, "Tiempo agotado esperando al worker.");
    }

    public async Task FacturarAsync(Factura f)
    {
        try
        {
            var conceptos = new[] { o.Concepto }.Concat(o.ConceptosAlternos).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
            JsonNode? doc = null;
            string? error = null;
            foreach (var concepto in conceptos)
            {
                f.Pasos.Add($"Orden enviada a Acusa Central: crear el documento en Comercial (concepto {concepto}).");
                (doc, error) = await EjecutarAsync("documento.crear", new
                {
                    concepto,
                    serie = o.Serie,
                    clienteCodigo = f.ClienteCodigo,
                    referencia = o.Referencia,
                    partidas = new[] { new { productoCodigo = f.ProductoCodigo, unidades = f.Cantidad, precio = f.Precio, almacen = o.Almacen } },
                });
                if (error is null) { f.Concepto = concepto; break; }
                f.Pasos.Add($"Comercial no lo permitió con el concepto {concepto}: {error}");
                if (!error.Contains("privilegios", StringComparison.OrdinalIgnoreCase)) break;
            }

            if (error is not null) { f.Estado = "Error"; f.Error = error; return; }
            f.Serie = doc?["serie"]?.GetValue<string>() ?? o.Serie;
            f.Folio = doc?["folio"]?.GetValue<double>();
            f.Pasos.Add($"Documento creado en Comercial: {f.Serie}{f.Folio}.");
            f.Estado = "Timbrando";
            f.Pasos.Add("Orden enviada: timbrar el documento.");
            var (r, e2) = await EjecutarAsync("documento.emitir", new { concepto = f.Concepto, serie = f.Serie, folio = f.Folio });
            if (e2 is not null) { f.Estado = "Error"; f.Error = e2; return; }
            f.Uuid = r?["uuid"]?.GetValue<string>();
            f.Estado = "Timbrada";
            f.Pasos.Add($"Timbrada. UUID {f.Uuid}.");
        }
        catch (Exception ex)
        {
            f.Estado = "Error";
            f.Error = ex.Message;
        }
    }

    public async Task CancelarAsync(Factura f, string motivo)
    {
        try
        {
            f.Estado = "Cancelando";
            f.Pasos.Add($"Orden enviada: cancelar con motivo {motivo}.");
            var (_, e) = await EjecutarAsync("documento.cancelar", new { concepto = f.Concepto, serie = f.Serie, folio = f.Folio, motivo });
            if (e is not null) { f.Estado = "Timbrada"; f.Error = e; f.Pasos.Add("No se pudo cancelar: " + e); return; }
            f.Estado = "Cancelada";
            f.Error = null;
            f.Pasos.Add("Cancelada ante el SAT.");
        }
        catch (Exception ex)
        {
            f.Estado = "Timbrada";
            f.Error = ex.Message;
        }
    }

    public async Task<(byte[]? Bytes, string? Error)> ArchivoAsync(Factura f, string tipo)
    {
        var (r, e) = await EjecutarAsync(
            "documento.obtener-archivo",
            new { concepto = f.Concepto, serie = f.Serie, folio = f.Folio, formato = tipo, incluirContenido = true }, 60);
        if (e is not null) return (null, e);
        foreach (var a in r?["archivos"]?.AsArray() ?? [])
        {
            if (a?["tipo"]?.GetValue<string>() == tipo && a["base64"]?.GetValue<string>() is { } b64) return (Convert.FromBase64String(b64), null);
        }

        return (null, "Comercial no devolvió el archivo.");
    }
}
