using System.Text.Json;
using Acusa.Contrato;
using Acusa.Orquestador;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var config = builder.Configuration.GetSection(Opciones.Seccion).Get<Opciones>() ?? new Opciones();
var problema = config.Validar();
if (problema is not null)
{
    throw new InvalidOperationException(problema);
}

var almacen = new Almacen(config.BaseDatos);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(almacen);
builder.Services.AddSingleton(new Seguridad(config.SecretoJwt, config.TokenSegundos));
builder.Services.AddSingleton<IntentosDeAcceso>();
builder.Services.AddSingleton<Notificador>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Acusa.Orquestador.MantenerDespierto>();
builder.Services.AddSingleton(new Acusa.Orquestador.Facturacion.DocStore(config.BaseDatos));
builder.Services.AddSingleton<Acusa.Orquestador.Facturacion.IEjecutorOrdenes, Acusa.Orquestador.Facturacion.EjecutorCola>();
builder.Services.AddSingleton<Acusa.Orquestador.Facturacion.FacturacionServicio>();
builder.Services.AddHostedService<RecuperadorDeTareas>();

// Primer arranque: si todavía no hay usuarios y se definió la cuenta inicial, se crea el cliente y su usuario.
if (almacen.ContarUsuarios() == 0 && !string.IsNullOrWhiteSpace(config.CorreoInicial) && config.ClaveInicial.Length >= 10)
{
    var cliente = almacen.CrearCliente(string.IsNullOrWhiteSpace(config.NombreClienteInicial) ? "Local Solutions IT" : config.NombreClienteInicial);
    almacen.CrearUsuario(cliente, config.CorreoInicial, config.NombreInicial, Contrasenas.Crear(config.ClaveInicial));

    // Hosting sin disco (Render Free): cada despliegue empieza con la base vacía. Estas variables, todas opcionales, la dejan
    // igual de lista que antes: empresas, llaves con el mismo valor y más usuarios del portal. Quedan en el panel del proveedor.
    foreach (var e in config.EmpresasIniciales.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) almacen.AgregarEmpresa(cliente, e);
    if (config.LlaveApiInicial.StartsWith("ak_") && config.LlaveApiInicial.Length >= 30) almacen.CrearLlaveConocida(cliente, Roles.Api, config.LlaveApiInicial, "Sistema de Alain");
    if (config.LlaveWorkerInicial.StartsWith("wk_") && config.LlaveWorkerInicial.Length >= 30) almacen.CrearLlaveConocida(cliente, Roles.Worker, config.LlaveWorkerInicial, "Servidor CONTPAQi");
    foreach (var u in config.UsuariosAdicionales.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var p = u.Split('|', StringSplitOptions.TrimEntries); // correo|nombre|contraseña
        if (p.Length == 3 && p[0].Contains('@') && p[2].Length >= 10 && almacen.BuscarUsuarioPorEmail(p[0]) is null) almacen.CrearUsuario(cliente, p[0], p[1], Contrasenas.Crear(p[2]));
    }
}

var app = builder.Build();

// En la nube Central va detrás del proxy del proveedor (que pone el HTTPS): sin esto, todas las personas parecerían venir de la
// misma IP y el límite de intentos de acceso las bloquearía juntas. Se activa con Orquestador__ProxyInverso=true.
if (builder.Configuration.GetValue<bool>("Orquestador:ProxyInverso"))
{
    var reenvio = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
    };
    reenvio.KnownNetworks.Clear();
    reenvio.KnownProxies.Clear();
    app.UseForwardedHeaders(reenvio);
}

app.UseDefaultFiles();
// /api-docs: Swagger UI con las rutas de Alain sobre Acusa (openapi.yaml necesita su tipo de contenido).
var tiposDeContenido = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
tiposDeContenido.Mappings[".yaml"] = "application/yaml";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = tiposDeContenido });

app.MapGet("/health", () => Results.Ok(new { status = "ok", producto = Marca.Producto, version = Marca.Version }));

// ---- Administración (solo desde el propio servidor, con x-admin-key) -----------------------------

var admin = app.MapGroup("/admin").WithTags("Administración");

admin.MapPost("/clientes", (SolicitudCliente cuerpo, HttpContext http, Opciones o, Almacen a) =>
{
    if (!EsAdmin(http, o)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(cuerpo.Nombre)) return Results.BadRequest(new { error = "Falta el nombre." });
    return Results.Created("/admin/clientes", new { clienteId = a.CrearCliente(cuerpo.Nombre.Trim()) });
});

admin.MapPost("/clientes/{clienteId}/empresas", (string clienteId, SolicitudEmpresa cuerpo, HttpContext http, Opciones o, Almacen a) =>
{
    if (!EsAdmin(http, o)) return Results.Unauthorized();
    if (!a.ClienteExiste(clienteId)) return Results.NotFound(new { error = "No existe el cliente." });
    if (string.IsNullOrWhiteSpace(cuerpo.Nombre)) return Results.BadRequest(new { error = "Falta el nombre de la empresa." });
    return Results.Ok(new { clienteId, empresa = cuerpo.Nombre.Trim(), nueva = a.AgregarEmpresa(clienteId, cuerpo.Nombre.Trim()) });
});

admin.MapPost("/clientes/{clienteId}/llaves", (string clienteId, SolicitudLlave cuerpo, HttpContext http, Opciones o, Almacen a) =>
{
    if (!EsAdmin(http, o)) return Results.Unauthorized();
    if (!a.ClienteExiste(clienteId)) return Results.NotFound(new { error = "No existe el cliente." });
    if (cuerpo.Rol is not (Roles.Api or Roles.Worker)) return Results.BadRequest(new { error = "El rol debe ser 'api' o 'worker'." });
    // La llave se muestra una sola vez: en la base queda únicamente su hash.
    return Results.Ok(new { clienteId, rol = cuerpo.Rol, llave = a.CrearLlave(clienteId, cuerpo.Rol, cuerpo.Nombre) });
});

admin.MapPost("/clientes/{clienteId}/usuarios", (string clienteId, SolicitudUsuario cuerpo, HttpContext http, Opciones o, Almacen a) =>
{
    if (!EsAdmin(http, o)) return Results.Unauthorized();
    if (!a.ClienteExiste(clienteId)) return Results.NotFound(new { error = "No existe el cliente." });
    if (string.IsNullOrWhiteSpace(cuerpo.Correo) || !cuerpo.Correo.Contains('@')) return Results.BadRequest(new { error = "Correo inválido." });
    if ((cuerpo.Contrasena ?? string.Empty).Length < 10) return Results.BadRequest(new { error = "La contraseña debe tener al menos 10 caracteres." });
    if (a.BuscarUsuarioPorEmail(cuerpo.Correo) is not null) return Results.Conflict(new { error = "Ya existe un usuario con ese correo." });
    var id = a.CrearUsuario(clienteId, cuerpo.Correo, string.IsNullOrWhiteSpace(cuerpo.Nombre) ? cuerpo.Correo : cuerpo.Nombre, Contrasenas.Crear(cuerpo.Contrasena!));
    return Results.Created("/admin/usuarios", new { usuarioId = id });
});

// ---- API REST: los procesos del worker (clientes, productos, documentos, timbrado, archivos, cancelación) -------

Acusa.Orquestador.Rest.ApiRest.Mapear(app);

// ---- Autenticación ---------------------------------------------------------------------------------

// Llave de API (sistemas) o llave de worker -> token de corta vida.
app.MapPost("/api/auth/token", (HttpContext http, Almacen a, Seguridad seguridad) =>
{
    var llave = http.Request.Headers["x-api-key"].ToString();
    var identidad = string.IsNullOrEmpty(llave) ? null : a.BuscarLlave(llave);
    return identidad is null
        ? Results.Json(new { error = "Llave inválida." }, statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(new TokenRespuesta(seguridad.CrearToken(identidad, DateTimeOffset.UtcNow), seguridad.DuracionTokenSegundos));
}).WithTags("Auth");

// Persona del portal: correo y contraseña. Se frenan los intentos repetidos por correo y por origen.
app.MapPost("/api/auth/login", (SolicitudLogin cuerpo, HttpContext http, Almacen a, Seguridad seguridad, IntentosDeAcceso intentos) =>
{
    var correo = (cuerpo.Correo ?? string.Empty).Trim().ToLowerInvariant();
    var origen = http.Connection.RemoteIpAddress?.ToString() ?? "?";
    if (!intentos.Permitido(correo) || !intentos.Permitido("ip:" + origen))
    {
        return Results.Json(new { error = "Demasiados intentos. Espere unos minutos." }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var usuario = a.BuscarUsuarioPorEmail(correo);
    var valida = Contrasenas.Verificar(cuerpo.Contrasena ?? string.Empty, usuario?.Clave ?? Contrasenas.Señuelo) && usuario is not null;
    if (!valida)
    {
        intentos.Fallo(correo);
        intentos.Fallo("ip:" + origen);
        return Results.Json(new { error = "Correo o contraseña incorrectos." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    intentos.Acierto(correo);
    var token = seguridad.CrearToken(new Identidad(usuario!.Id, usuario.ClienteId, Roles.Portal), DateTimeOffset.UtcNow);
    return Results.Ok(new { token, expiraEnSegundos = seguridad.DuracionTokenSegundos, usuario = new { usuario.Nombre, usuario.Email, cliente = usuario.ClienteNombre } });
}).WithTags("Auth");

// ---- API de integración: sistemas del cliente (llave de API) o portal ------------------------------------

var api = app.MapGroup("/api/sdk").WithTags("SDK");

api.MapPost("/execute", async Task<IResult> (SolicitudEjecutar cuerpo, HttpContext http, Almacen a, Seguridad seguridad, Opciones o, Notificador notificador, int? esperar) =>
{
    if (Autenticar(http, a, seguridad, Roles.Api, Roles.Portal) is not { } quien) return Results.Unauthorized();

    // v1 expone solo 8 operaciones. El worker conserva el soporte de otras (actualizar, direcciones, lotes, leer documento),
    // pero aquí no se aceptan: ampliar la API es agregarlas a Operaciones.Soportadas y al catálogo.
    var tipo = (cuerpo.TipoProceso ?? string.Empty).Trim().ToLowerInvariant();
    if (!Operaciones.Soportadas.Contains(tipo))
    {
        return Results.BadRequest(new
        {
            error = $"tipoProceso '{cuerpo.TipoProceso}' no está disponible en esta versión.",
            operacionesDisponibles = Operaciones.Soportadas,
        });
    }

    // La empresa también se acepta con el nombre dbName.
    var empresa = (string.IsNullOrWhiteSpace(cuerpo.Empresa) ? cuerpo.DbName : cuerpo.Empresa)?.Trim() ?? string.Empty;
    if (!a.EmpresaDelCliente(quien.ClienteId, empresa))
    {
        return Results.BadRequest(new { error = $"La empresa '{empresa}' no está registrada para este cliente." });
    }

    // La cola guarda cada payload. Una contraseña (la del certificado, por ejemplo) no debe quedar ahí: vive en el worker.
    if (cuerpo.Payload is { ValueKind: JsonValueKind.Object } objeto && CamposSecretos.Buscar(objeto) is { } campo)
    {
        return Results.BadRequest(new
        {
            error = $"El campo '{campo}' parece una contraseña y no se acepta en la orden: la cola guarda cada payload. " +
                    "La contraseña del certificado se configura en el panel del worker.",
        });
    }

    var payload = cuerpo.Payload is { ValueKind: JsonValueKind.Object } p ? p.GetRawText() : "{}";
    if (payload.Length > o.MaxPayloadBytes)
    {
        return Results.BadRequest(new { error = $"El payload excede {o.MaxPayloadBytes} caracteres." });
    }

    // Llave de idempotencia: encabezado Idempotency-Key o campo llaveIdempotencia. Un reintento con la misma llave y la
    // misma orden devuelve la tarea original en lugar de crear otra (no se duplica una factura).
    var llave = (http.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? cuerpo.LlaveIdempotencia)?.Trim();
    if (!string.IsNullOrEmpty(llave))
    {
        if (llave.Length > 100 || !llave.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':'))
        {
            return Results.BadRequest(new { error = "La llave de idempotencia admite hasta 100 caracteres: letras, números, guion, guion bajo, punto y dos puntos." });
        }

        var r = a.EncolarIdempotente(quien.ClienteId, empresa, tipo, payload, llave);
        if (r.Conflicto)
        {
            return Results.Conflict(new
            {
                error = "Esa llave de idempotencia ya se usó con una orden distinta. Use una llave nueva para una orden nueva.",
                tareaId = r.Id,
            });
        }

        if (r.Repetida)
        {
            http.Response.Headers["Idempotency-Replayed"] = "true";
        }
        else
        {
            notificador.TareaEncolada();
        }

        return await RespuestaAsync(r.Id, r.Estado);
    }

    var id = a.EncolarTarea(quien.ClienteId, empresa, tipo, payload);
    notificador.TareaEncolada();
    return await RespuestaAsync(id, EstadoTarea.Pendiente);

    // Con espera, la misma respuesta trae el resultado (200 si se completó, 422 si falló). Si pasa el tiempo sin que el
    // worker termine, responde 202 con el identificador: la tarea sigue y se puede consultar después.
    async Task<IResult> RespuestaAsync(string tareaId, EstadoTarea estadoInicial)
    {
        var segundos = Math.Clamp(esperar ?? PreferenciaDeEspera(http) ?? o.EsperaPorOmisionSegundos, 0, o.EsperaMaximaSegundos);
        if (segundos == 0 && estadoInicial is not (EstadoTarea.Completada or EstadoTarea.Fallida))
        {
            return Results.Accepted($"/api/sdk/tasks/{tareaId}", new TareaCreada(tareaId, estadoInicial));
        }

        var tarea = await notificador.EsperarResultadoAsync(a, quien.ClienteId, tareaId, TimeSpan.FromSeconds(segundos), http.RequestAborted);
        if (tarea is null)
        {
            return Results.NotFound(new { error = "No existe la tarea." });
        }

        return tarea.Estado switch
        {
            EstadoTarea.Completada => Results.Ok(Resultado(tarea)),
            EstadoTarea.Fallida => Results.UnprocessableEntity(Resultado(tarea)),
            _ => Results.Accepted($"/api/sdk/tasks/{tareaId}", new TareaCreada(tareaId, tarea.Estado)),
        };
    }
});

api.MapGet("/status", (HttpContext http, Almacen a, Seguridad seguridad, Opciones o) =>
{
    if (Autenticar(http, a, seguridad, Roles.Api, Roles.Portal) is not { } quien) return Results.Unauthorized();
    var workers = a.ListarWorkers(quien.ClienteId, TimeSpan.FromSeconds(o.WorkerEnLineaSegundos));
    return Results.Ok(new
    {
        servicio = Marca.Producto,
        version = Marca.Version,
        operacionNormal = true,
        workersEnLinea = workers.Count(w => w.EnLinea),
        workersTotal = workers.Count,
        hora = DateTime.UtcNow,
    });
});

// Estado de una tarea. Con ?esperar=N (segundos) la respuesta espera a que termine: sirve para recuperar el resultado de una
// orden cuya respuesta se perdió (corte de red, tiempo agotado) sin volver a mandarla.
api.MapGet("/tasks/{id}", async Task<IResult> (string id, HttpContext http, Almacen a, Seguridad seguridad, Opciones o, Notificador notificador, int? esperar) =>
{
    if (Autenticar(http, a, seguridad, Roles.Api, Roles.Portal) is not { } quien) return Results.Unauthorized();
    if (esperar is > 0)
    {
        var segundos = Math.Clamp(esperar.Value, 0, o.EsperaMaximaSegundos);
        var espera = await notificador.EsperarResultadoAsync(a, quien.ClienteId, id, TimeSpan.FromSeconds(segundos), http.RequestAborted);
        return espera is null ? Results.NotFound(new { error = "No existe la tarea." }) : Results.Ok(espera);
    }

    return a.ObtenerTarea(quien.ClienteId, id) is { } tarea ? Results.Ok(tarea) : Results.NotFound(new { error = "No existe la tarea." });
});

api.MapGet("/tasks", (HttpContext http, Almacen a, Seguridad seguridad, EstadoTarea? estado, int? limite) =>
{
    if (Autenticar(http, a, seguridad, Roles.Api, Roles.Portal) is not { } quien) return Results.Unauthorized();
    return Results.Ok(a.ListarTareas(quien.ClienteId, estado, limite ?? 50));
});

api.MapGet("/workers", (HttpContext http, Almacen a, Seguridad seguridad, Opciones o) =>
{
    if (Autenticar(http, a, seguridad, Roles.Api, Roles.Portal) is not { } quien) return Results.Unauthorized();
    return Results.Ok(a.ListarWorkers(quien.ClienteId, TimeSpan.FromSeconds(o.WorkerEnLineaSegundos)));
});

// ---- API del portal (persona con sesión) -----------------------------------------------------------------

var portal = app.MapGroup("/api/portal").WithTags("Portal");

portal.MapGet("/yo", (HttpContext http, Almacen a, Seguridad seguridad) =>
{
    if (Autenticar(http, a, seguridad, Roles.Portal) is not { } quien || a.BuscarUsuarioPorId(quien.LlaveId) is not { } u) return Results.Unauthorized();
    return Results.Ok(new { u.Nombre, u.Email, cliente = u.ClienteNombre, empresas = a.EmpresasDelCliente(quien.ClienteId) });
});

portal.MapGet("/resumen", (HttpContext http, Almacen a, Seguridad seguridad, Opciones o) =>
{
    if (Autenticar(http, a, seguridad, Roles.Portal) is not { } quien) return Results.Unauthorized();
    return Results.Ok(a.Resumen(quien.ClienteId, TimeSpan.FromSeconds(o.WorkerEnLineaSegundos)));
});

// Catálogo de las operaciones expuestas, con su ejemplo; lo usa la pantalla Desarrolladores.
portal.MapGet("/operaciones", (HttpContext http, Almacen a, Seguridad seguridad) =>
    Autenticar(http, a, seguridad, Roles.Portal) is null ? Results.Unauthorized() : Results.Ok(CatalogoOperaciones.Todas));

portal.MapGet("/empresas", (HttpContext http, Almacen a, Seguridad seguridad) =>
    Autenticar(http, a, seguridad, Roles.Portal) is { } quien ? Results.Ok(a.EmpresasDelCliente(quien.ClienteId)) : Results.Unauthorized());

portal.MapPost("/empresas", (SolicitudEmpresa cuerpo, HttpContext http, Almacen a, Seguridad seguridad) =>
{
    if (Autenticar(http, a, seguridad, Roles.Portal) is not { } quien) return Results.Unauthorized();
    var nombre = (cuerpo.Nombre ?? string.Empty).Trim();
    if (nombre.Length is < 3 or > 80 || nombre.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or ' ')))
    {
        return Results.BadRequest(new { error = "El nombre de la empresa debe tener entre 3 y 80 caracteres: letras, números, guion y guion bajo." });
    }

    return a.AgregarEmpresa(quien.ClienteId, nombre) ? Results.Created("/api/portal/empresas", new { nombre }) : Results.Conflict(new { error = "Esa empresa ya está registrada." });
});

portal.MapDelete("/empresas/{nombre}", (string nombre, HttpContext http, Almacen a, Seguridad seguridad) =>
    Autenticar(http, a, seguridad, Roles.Portal) is not { } quien ? Results.Unauthorized()
    : a.QuitarEmpresa(quien.ClienteId, nombre) ? Results.NoContent() : Results.NotFound(new { error = "No existe esa empresa." }));

portal.MapGet("/llaves", (HttpContext http, Almacen a, Seguridad seguridad) =>
    Autenticar(http, a, seguridad, Roles.Portal) is { } quien ? Results.Ok(a.ListarLlaves(quien.ClienteId)) : Results.Unauthorized());

portal.MapPost("/llaves", (SolicitudLlave cuerpo, HttpContext http, Almacen a, Seguridad seguridad) =>
{
    if (Autenticar(http, a, seguridad, Roles.Portal) is not { } quien) return Results.Unauthorized();
    if (cuerpo.Rol is not (Roles.Api or Roles.Worker)) return Results.BadRequest(new { error = "El tipo debe ser 'api' o 'worker'." });
    // La llave completa se muestra solo en esta respuesta; después queda únicamente su prefijo.
    return Results.Ok(new { rol = cuerpo.Rol, llave = a.CrearLlave(quien.ClienteId, cuerpo.Rol, cuerpo.Nombre) });
});

portal.MapDelete("/llaves/{id}", (string id, HttpContext http, Almacen a, Seguridad seguridad) =>
    Autenticar(http, a, seguridad, Roles.Portal) is not { } quien ? Results.Unauthorized()
    : a.RevocarLlave(quien.ClienteId, id) ? Results.NoContent() : Results.NotFound(new { error = "No existe la llave o ya estaba revocada." }));

portal.MapPost("/clave", (SolicitudCambioClave cuerpo, HttpContext http, Almacen a, Seguridad seguridad) =>
{
    if (Autenticar(http, a, seguridad, Roles.Portal) is not { } quien || a.BuscarUsuarioPorId(quien.LlaveId) is not { } u) return Results.Unauthorized();
    if (!Contrasenas.Verificar(cuerpo.Actual ?? string.Empty, u.Clave)) return Results.BadRequest(new { error = "La contraseña actual no es correcta." });
    if ((cuerpo.Nueva ?? string.Empty).Length < 10) return Results.BadRequest(new { error = "La contraseña nueva debe tener al menos 10 caracteres." });
    a.CambiarClave(u.Id, Contrasenas.Crear(cuerpo.Nueva!));
    return Results.NoContent();
});

// ---- API del worker (llave de worker) ------------------------------------------------------------------------

var worker = app.MapGroup("/api/worker").WithTags("Worker");

// El worker consulta; el orquestador nunca le abre una conexión. Cada consulta cuenta como latido.
worker.MapPost("/poll", async Task<IResult> (SolicitudPoll cuerpo, HttpContext http, Almacen a, Seguridad seguridad, Opciones o, Notificador notificador, int? esperar) =>
{
    if (Autenticar(http, a, seguridad, Roles.Worker) is not { } quien) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(cuerpo.WorkerId) || string.IsNullOrWhiteSpace(cuerpo.Empresa))
    {
        return Results.BadRequest(new { error = "Faltan workerId y empresa." });
    }

    if (!a.EmpresaDelCliente(quien.ClienteId, cuerpo.Empresa))
    {
        return Results.BadRequest(new { error = $"La empresa '{cuerpo.Empresa}' no está registrada para este cliente." });
    }

    // Con ?esperar=N el worker deja la consulta abierta hasta N segundos: si entra una orden, la recibe al instante en lugar
    // de esperar a su siguiente consulta. Sin el parámetro responde enseguida, como siempre.
    var fin = DateTime.UtcNow.AddSeconds(Math.Clamp(esperar ?? 0, 0, o.SondeoLargoMaximoSegundos));
    while (true)
    {
        // Si el worker cerró la conexión, no se le asigna una tarea que nadie recibiría.
        if (http.RequestAborted.IsCancellationRequested)
        {
            return Results.NoContent();
        }

        var aviso = notificador.SiguienteEncolada();
        if (a.TomarTarea(quien.ClienteId, cuerpo.Empresa.Trim(), cuerpo.WorkerId.Trim(), Recortar(cuerpo.Version), Recortar(cuerpo.Host)) is { } tarea)
        {
            return Results.Ok(tarea);
        }

        var resto = fin - DateTime.UtcNow;
        if (resto <= TimeSpan.Zero)
        {
            return Results.NoContent();
        }

        try
        {
            await Task.WhenAny(aviso, Task.Delay(resto < TimeSpan.FromSeconds(5) ? resto : TimeSpan.FromSeconds(5), http.RequestAborted));
        }
        catch (OperationCanceledException)
        {
            return Results.NoContent();
        }
    }
});

worker.MapPost("/tasks/{id}/result", (string id, string workerId, ResultadoTarea cuerpo, HttpContext http, Almacen a, Seguridad seguridad, Notificador notificador) =>
{
    if (Autenticar(http, a, seguridad, Roles.Worker) is not { } quien) return Results.Unauthorized();
    if (!a.RegistrarResultado(quien.ClienteId, id, workerId, cuerpo))
    {
        return Results.Conflict(new { error = "La tarea no está en proceso con este worker." });
    }

    notificador.TareaTerminada(id);
    return Results.NoContent();
});

app.Run();

/// <summary>Cuerpo de la respuesta con espera: el resultado de la operación en la misma llamada.</summary>
static object Resultado(TareaDto t) => new
{
    tareaId = t.Id,
    estado = t.Estado,
    tipoProceso = t.TipoProceso,
    empresa = t.Empresa,
    resultado = t.Resultado,
    error = t.Error,
    codigoSdk = t.CodigoSdk,
    creada = t.Creada,
    terminada = t.Terminada,
    duracionMs = t.Iniciada is { } ini && t.Terminada is { } fin ? (int)(fin - ini).TotalMilliseconds : (int?)null,
};

/// <summary>Encabezado <c>Prefer: wait=60</c> (RFC 7240) como otra forma de pedir la espera.</summary>
static int? PreferenciaDeEspera(HttpContext http)
{
    foreach (var valor in http.Request.Headers["Prefer"])
    {
        foreach (var parte in (valor ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (parte.StartsWith("wait=", StringComparison.OrdinalIgnoreCase) && int.TryParse(parte[5..], out var n))
            {
                return n;
            }
        }
    }

    return null;
}

static string? Recortar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim()[..Math.Min(texto.Trim().Length, 80)];

// Las rutas /admin solo responden a llamadas hechas desde el propio servidor, aunque el puerto esté abierto a otras máquinas.
static bool EsAdmin(HttpContext http, Opciones o) =>
    http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip) &&
    Seguridad.IgualesEnTiempoConstante(http.Request.Headers["x-admin-key"].ToString(), o.ClaveAdmin);

static Identidad? Autenticar(HttpContext http, Almacen almacen, Seguridad seguridad, params string[] roles)
{
    var cabecera = http.Request.Headers.Authorization.ToString();
    if (!cabecera.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    // Se acepta el token de /api/auth/token o, directamente, la llave de API (ak_…): así Swagger «Authorize» y Postman
    // pueden usar la llave sin el paso del token.
    var credencial = cabecera[7..].Trim();
    var identidad = credencial.StartsWith("ak_", StringComparison.Ordinal) || credencial.StartsWith("wk_", StringComparison.Ordinal)
        ? almacen.BuscarLlave(credencial)
        : seguridad.ValidarToken(credencial, DateTimeOffset.UtcNow);
    if (identidad is null || !roles.Contains(identidad.Rol))
    {
        return null;
    }

    // Revocar una llave o dar de baja a un usuario corta el acceso de inmediato, sin esperar a que venza el token.
    var vigente = identidad.Rol == Roles.Portal ? almacen.BuscarUsuarioPorId(identidad.LlaveId) is not null : almacen.LlaveVigente(identidad.LlaveId);
    return vigente ? identidad : null;
}

/// <summary>Cuerpo de <c>POST /admin/clientes</c>.</summary>
public sealed record SolicitudCliente(string Nombre);

/// <summary>Cuerpo de <c>POST /admin/clientes/{id}/empresas</c> y <c>POST /api/portal/empresas</c>.</summary>
public sealed record SolicitudEmpresa(string Nombre);

/// <summary>Cuerpo para crear una llave. <c>Nombre</c> es una etiqueta libre para reconocerla en el portal.</summary>
public sealed record SolicitudLlave(string Rol, string? Nombre = null);

/// <summary>Cuerpo de <c>POST /admin/clientes/{id}/usuarios</c>.</summary>
public sealed record SolicitudUsuario(string Correo, string? Nombre, string? Contrasena);

/// <summary>Cuerpo de <c>POST /api/auth/login</c>.</summary>
public sealed record SolicitudLogin(string? Correo, string? Contrasena);

/// <summary>Cuerpo de <c>POST /api/portal/clave</c>.</summary>
public sealed record SolicitudCambioClave(string? Actual, string? Nueva);

/// <summary>Nombre y versión del producto, en un solo lugar.</summary>
public static class Marca
{
    public const string Producto = "Acusa Central";
    public const string Version = "1.0.0";
}

/// <summary>Configuración de la sección <c>Orquestador</c>.</summary>
public sealed class Opciones
{
    public const string Seccion = "Orquestador";

    public string BaseDatos { get; set; } = "datos/orquestador.db";

    /// <summary>Secreto con el que se firman los tokens. Mínimo 32 caracteres; no va en el repositorio.</summary>
    public string SecretoJwt { get; set; } = string.Empty;

    /// <summary>Clave de las rutas /admin. Mínimo 16 caracteres; no va en el repositorio.</summary>
    public string ClaveAdmin { get; set; } = string.Empty;

    /// <summary>Cuenta del primer usuario del portal; solo se usa si todavía no existe ninguno.</summary>
    public string CorreoInicial { get; set; } = string.Empty;

    public string NombreInicial { get; set; } = "Administrador";
    public string NombreClienteInicial { get; set; } = "Local Solutions IT";

    /// <summary>Contraseña inicial (mínimo 10 caracteres); va en variable de entorno, no en el repositorio.</summary>
    public string ClaveInicial { get; set; } = string.Empty;

    /// <summary>Opcionales, solo para hosting sin disco: se aplican al crear la base vacía. Ver el primer arranque.</summary>
    public string EmpresasIniciales { get; set; } = string.Empty;
    public string LlaveApiInicial { get; set; } = string.Empty;
    public string LlaveWorkerInicial { get; set; } = string.Empty;
    /// <summary>Más usuarios del portal: <c>correo|nombre|contraseña;correo|nombre|contraseña</c>.</summary>
    public string UsuariosAdicionales { get; set; } = string.Empty;

    public int TokenSegundos { get; set; } = 3600;
    public int WorkerEnLineaSegundos { get; set; } = 90;
    public int TareaColgadaMinutos { get; set; } = 10;
    public int MaxIntentos { get; set; } = 3;
    public int MaxPayloadBytes { get; set; } = 262_144;

    /// <summary>
    /// Segundos que <c>POST /api/sdk/execute</c> espera el resultado antes de responder, cuando la solicitud no indica
    /// <c>?esperar=</c>. Con 0 (por omisión) responde enseguida con 202 y el identificador de la tarea. Con un valor mayor,
    /// la misma respuesta trae el resultado: el sistema que llama no tiene que consultar el estado.
    /// </summary>
    public int EsperaPorOmisionSegundos { get; set; }

    /// <summary>Tope de espera que una solicitud puede pedir con <c>?esperar=</c>.</summary>
    public int EsperaMaximaSegundos { get; set; } = 120;

    // ---- Rutas de facturación (las del Swagger de Alain) ----------------------------------------------------------

    /// <summary>Segundos que cada paso de las rutas de facturación espera al worker antes de responder «en curso».</summary>
    public int FacturacionEsperaSegundos { get; set; } = 60;

    /// <summary>Empresa de CONTPAQi donde se crean clientes y productos cuando no se indica una emisora. Vacía: la primera registrada.</summary>
    public string FacturacionEmpresa { get; set; } = string.Empty;

    /// <summary>Prefijos de los códigos que Acusa genera. En una empresa de pruebas deben empezar con PRUEBA (variables Orquestador__FacturacionPrefijoCliente, ...Producto y ...Referencia); en una empresa real, no.</summary>
    public string FacturacionPrefijoCliente { get; set; } = "CS";

    public string FacturacionPrefijoProducto { get; set; } = "PR";

    /// <summary>Prefijo de la referencia con la que se crea cada documento (hasta 20 caracteres en total).</summary>
    public string FacturacionPrefijoReferencia { get; set; } = "FAC";

    /// <summary>
    /// Código de almacén de las partidas de los documentos que crean las rutas de facturación. El SDK real responde 120115
    /// («el almacén solicitado no existe») si no se indica uno que exista en la empresa.
    /// </summary>
    public string FacturacionAlmacen { get; set; } = "1";

    /// <summary>Conceptos de documento que ofrece <c>/invoices/new</c> (por ejemplo, el de factura).</summary>
    public string[] FacturacionConceptos { get; set; } = ["5"];

    /// <summary>Unidades base que ofrece <c>/products/new</c>. Deben existir en la empresa de CONTPAQi.</summary>
    public string[] FacturacionUnidades { get; set; } = ["Pieza", "Servicio"];

    /// <summary>Tope de la espera del worker en <c>/api/worker/poll?esperar=</c>: la orden le llega al instante en lugar de a la siguiente consulta.</summary>
    public int SondeoLargoMaximoSegundos { get; set; } = 25;

    public string? Validar() =>
        SecretoJwt.Length < 32 ? "Orquestador:SecretoJwt debe tener al menos 32 caracteres (defínalo por variable de entorno o user-secrets)."
        : ClaveAdmin.Length < 16 ? "Orquestador:ClaveAdmin debe tener al menos 16 caracteres (defínala por variable de entorno o user-secrets)."
        : null;
}

/// <summary>Frena los intentos de acceso repetidos: 8 fallos en 10 minutos bloquean esa clave de conteo.</summary>
public sealed class IntentosDeAcceso
{
    private const int Maximo = 8;
    private static readonly TimeSpan Ventana = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, List<DateTime>> _fallos = new();
    private readonly object _candado = new();

    public bool Permitido(string clave)
    {
        lock (_candado)
        {
            return !_fallos.TryGetValue(clave, out var lista) || lista.Count(f => DateTime.UtcNow - f < Ventana) < Maximo;
        }
    }

    public void Fallo(string clave)
    {
        lock (_candado)
        {
            if (!_fallos.TryGetValue(clave, out var lista))
            {
                _fallos[clave] = lista = [];
            }

            lista.RemoveAll(f => DateTime.UtcNow - f >= Ventana);
            lista.Add(DateTime.UtcNow);
        }
    }

    public void Acierto(string clave)
    {
        lock (_candado)
        {
            _fallos.Remove(clave);
        }
    }
}

/// <summary>Cada minuto devuelve a la cola, o marca como inciertas, las tareas que un worker dejó sin responder.</summary>
public sealed class RecuperadorDeTareas(Almacen almacen, Opciones o, ILogger<RecuperadorDeTareas> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var reloj = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await reloj.WaitForNextTickAsync(ct))
        {
            try
            {
                var n = almacen.RecuperarColgadas(TimeSpan.FromMinutes(o.TareaColgadaMinutos), o.MaxIntentos);
                if (n > 0)
                {
                    log.LogWarning("{N} tarea(s) sin respuesta del worker se recuperaron o se marcaron como inciertas.", n);
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Falló la recuperación de tareas.");
            }
        }
    }
}

/// <summary>Permite arrancar el orquestador desde pruebas.</summary>
public partial class Program;
