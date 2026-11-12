using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acusa.Contrato;
using Acusa.Sdk.Application.Sesion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acusa.Sdk.Worker;

/// <summary>Configuración de la sección <c>Orquestador</c> del worker.</summary>
public sealed class OpcionesOrquestador
{
    public const string Seccion = "Orquestador";

    public string Url { get; set; } = "http://127.0.0.1:5090";

    /// <summary>Llave de worker entregada al dar de alta el cliente. Es secreta: va en variable de entorno, no en el repositorio.</summary>
    public string LlaveWorker { get; set; } = string.Empty;

    /// <summary>Identificador de esta instalación; si va vacío se usa el nombre del equipo y la empresa.</summary>
    public string WorkerId { get; set; } = string.Empty;

    public int ConsultaSegundos { get; set; } = 5;

    /// <summary>
    /// Segundos que el worker deja abierta cada consulta a Central: si entra una orden, la recibe al instante. Con 0 consulta
    /// cada <see cref="ConsultaSegundos"/>. Solo se usa con una empresa; con varias el worker las revisa una por una.
    /// </summary>
    public int SondeoLargoSegundos { get; set; } = 20;
}

/// <summary>Habla con el orquestador: pide token, consulta tareas y entrega resultados. Solo hace llamadas salientes.</summary>
public sealed class ClienteOrquestador(HttpClient http, IOptions<OpcionesOrquestador> opciones)
{
    /// <summary>Versión que se informa al portal.</summary>
    public const string VersionWorker = "1.0.0";

    private string? _token;
    private DateTimeOffset _vence;

    public async Task<TareaAsignada?> ConsultarAsync(string workerId, string empresa, CancellationToken ct, int esperarSegundos = 0)
    {
        var ruta = esperarSegundos > 0 ? $"api/worker/poll?esperar={esperarSegundos}" : "api/worker/poll";
        using var r = await EnviarAsync(() => Peticion(HttpMethod.Post, ruta, new SolicitudPoll(workerId, empresa, VersionWorker, Environment.MachineName)), ct);
        if (r.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<TareaAsignada>(ct);
    }

    public async Task ReportarAsync(string tareaId, string workerId, ResultadoTarea resultado, CancellationToken ct)
    {
        using var r = await EnviarAsync(
            () => Peticion(HttpMethod.Post, $"api/worker/tasks/{tareaId}/result?workerId={Uri.EscapeDataString(workerId)}", resultado), ct);
        // 409: la tarea ya no es de este worker (por ejemplo se recuperó). No hay nada más que hacer con ella.
        if (r.StatusCode != HttpStatusCode.Conflict)
        {
            r.EnsureSuccessStatusCode();
        }
    }

    private HttpRequestMessage Peticion(HttpMethod metodo, string ruta, object cuerpo) =>
        new(metodo, ruta) { Content = JsonContent.Create(cuerpo) };

    /// <summary>Envía con el token vigente; si el orquestador responde 401, pide uno nuevo y reintenta una vez.</summary>
    private async Task<HttpResponseMessage> EnviarAsync(Func<HttpRequestMessage> crear, CancellationToken ct)
    {
        for (var intento = 0; ; intento++)
        {
            await AsegurarTokenAsync(ct);
            var peticion = crear();
            peticion.Headers.Authorization = new("Bearer", _token);
            var respuesta = await http.SendAsync(peticion, ct);
            if (respuesta.StatusCode != HttpStatusCode.Unauthorized || intento > 0)
            {
                return respuesta;
            }

            respuesta.Dispose();
            _token = null;
        }
    }

    private async Task AsegurarTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _vence)
        {
            return;
        }

        using var peticion = new HttpRequestMessage(HttpMethod.Post, "api/auth/token");
        peticion.Headers.Add("x-api-key", opciones.Value.LlaveWorker);
        using var r = await http.SendAsync(peticion, ct);
        if (r.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("El orquestador rechazó la llave del worker. Revise Orquestador:LlaveWorker.");
        }

        r.EnsureSuccessStatusCode();
        var token = await r.Content.ReadFromJsonAsync<TokenRespuesta>(ct) ?? throw new InvalidOperationException("Respuesta de token vacía.");
        _token = token.Token;
        _vence = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, token.ExpiraEnSegundos - 60));
    }
}

/// <summary>
/// Ciclo del worker: abre la sesión del SDK y, mientras esté lista, consulta las tareas de cada empresa que atiende, las
/// ejecuta y informa el resultado. Una tarea a la vez: el SDK de CONTPAQi atiende una empresa por proceso y no admite
/// llamadas en paralelo; para otra empresa se cierra la abierta y se abre la que toca, sin volver a iniciar sesión.
/// </summary>
public sealed class ServicioWorker(
    ClienteOrquestador orquestador,
    Ejecutor ejecutor,
    ISesionSdk sesion,
    EstadoWorker estado,
    IReadOnlyList<string> empresas,
    Func<string, string> rutaDe,
    Func<string, bool> escrituraEn,
    string workerId,
    IOptions<OpcionesOrquestador> opciones,
    ILogger<ServicioWorker> log) : BackgroundService
{
    private static readonly HashSet<string> OperacionesDeLectura = new(StringComparer.Ordinal)
    {
        Operaciones.ClienteLeer, Operaciones.ProductoLeer, Operaciones.DocumentoLeer, Operaciones.DireccionLeer, Operaciones.DocumentoObtenerArchivo,
    };

    /// <summary>Con una sola empresa se conserva el identificador; con varias, cada una se registra como su propio worker.</summary>
    private string IdDe(string empresa) => empresas.Count == 1 ? workerId : $"{workerId}-{empresa}";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Worker {Worker} para {Cantidad} empresa(s): {Empresas}.", workerId, empresas.Count, string.Join(", ", empresas));
        sesion.Iniciar();

        var fallos = 0;
        var sesionAvisada = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (sesion.Estado.Estado != EstadoSesion.Lista)
                {
                    // Con inicio por diálogo alguien debe escribir la clave en el equipo; mientras tanto no se toman tareas.
                    estado.Conexion("esperando-sdk", $"Sesión del SDK: {sesion.Estado.Estado}");
                    log.LogInformation("Esperando la sesión del SDK (estado: {Estado}). {Motivo}", sesion.Estado.Estado, sesion.Estado.Mensaje);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                if (!sesionAvisada)
                {
                    sesionAvisada = true;
                    log.LogInformation("Sesión del SDK lista (empresa {Empresa}).", sesion.Estado.Empresa);
                }

                var hizo = false;
                var consulta = System.Diagnostics.Stopwatch.StartNew();

                // Con una sola empresa la consulta queda abierta y la orden llega al instante; con varias se revisan una por una.
                var espera = empresas.Count == 1 ? Math.Max(0, opciones.Value.SondeoLargoSegundos) : 0;
                foreach (var empresa in empresas)
                {
                    ct.ThrowIfCancellationRequested();
                    var tarea = await orquestador.ConsultarAsync(IdDe(empresa), empresa, ct, espera);
                    if (tarea is null)
                    {
                        continue;
                    }

                    hizo = true;
                    await AtenderAsync(tarea, empresa, ct);
                }

                if (fallos > 0)
                {
                    log.LogInformation("Se recuperó la conexión con Acusa Central.");
                }

                fallos = 0;
                estado.Conexion("conectado");
                // Una consulta que esperó no necesita pausa; si Central respondió enseguida (versión sin espera) se pausa como antes.
                if (!hizo && (espera == 0 || consulta.Elapsed < TimeSpan.FromSeconds(1)))
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, opciones.Value.ConsultaSegundos)), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Sin red, orquestador caído o llave rechazada: se espera cada vez más, hasta un minuto, y se sigue intentando.
                fallos++;
                estado.Conexion(ex.Message.Contains("rechazó la llave", StringComparison.Ordinal) ? "llave-rechazada" : "sin-conexion", ex.Message);
                var espera = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(fallos, 6))));
                log.LogWarning("No se pudo hablar con el orquestador ({Mensaje}). Reintento en {Espera:0} s.", ex.Message, espera.TotalSeconds);
                await Task.Delay(espera, ct).ContinueWith(_ => { }, CancellationToken.None);
            }
        }

        await sesion.CerrarAsync();
        log.LogInformation("Worker detenido; sesión del SDK cerrada.");
    }

    private async Task AtenderAsync(TareaAsignada tarea, string empresa, CancellationToken ct)
    {
        log.LogInformation("Tarea {Id}: {Tipo} ({Empresa})", tarea.Id, tarea.TipoProceso, empresa);
        estado.Inicia(tarea.TipoProceso);
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        ResultadoTarea resultado;

        var cambio = await sesion.CambiarEmpresaAsync(rutaDe(empresa));
        if (cambio is not null)
        {
            resultado = new ResultadoTarea(false, null, $"No se pudo abrir la empresa {empresa}: {cambio}", null);
        }
        else if (!OperacionesDeLectura.Contains(tarea.TipoProceso) && !escrituraEn(empresa))
        {
            // Solo las empresas nombradas en EmpresasEscritura admiten escritura; las demás son de consulta.
            resultado = new ResultadoTarea(false, null, $"La empresa {empresa} es de consulta en este worker: no admite escribir, timbrar ni cancelar.", null);
        }
        else
        {
            resultado = await ejecutor.EjecutarAsync(tarea, ct);
        }

        await InformarConReintentoAsync(tarea, empresa, resultado, ct);
        estado.Termina(tarea.Id, tarea.TipoProceso, resultado.Exito, resultado.Error, (int)reloj.ElapsedMilliseconds);
        log.LogInformation("Tarea {Id}: {Estado}{Detalle} ({Ms} ms)", tarea.Id, resultado.Exito ? "completada" : "fallida",
            resultado.Exito ? string.Empty : $" ({resultado.Error})", reloj.ElapsedMilliseconds);
    }

    /// <summary>El resultado ya está en CONTPAQi: si falla el envío se insiste, y no se vuelve a ejecutar la tarea.</summary>
    private async Task InformarConReintentoAsync(TareaAsignada tarea, string empresa, ResultadoTarea resultado, CancellationToken ct)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                await orquestador.ReportarAsync(tarea.Id, IdDe(empresa), resultado, ct);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                var espera = TimeSpan.FromSeconds(Math.Min(30, 2 * intento));
                log.LogWarning("No se pudo informar la tarea {Id} ({Mensaje}). Reintento en {Espera:0} s.", tarea.Id, ex.Message, espera.TotalSeconds);
                await Task.Delay(espera, ct);
            }
        }
    }
}
