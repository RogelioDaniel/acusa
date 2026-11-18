using System.Diagnostics;
using Acusa.Contrato;

namespace Acusa.Orquestador;

/// <summary>Identificador de correlación de la petición en curso; llega a la respuesta (<c>X-Correlation-Id</c>) y a cada línea del log.</summary>
public static class Correlacion
{
    public const string Cabecera = "X-Correlation-Id";

    private static readonly AsyncLocal<string?> Valor = new();

    public static string? Actual
    {
        get => Valor.Value;
        set => Valor.Value = value;
    }

    /// <summary>Acepta el identificador que manda quien llama si es corto y legible; si no, genera uno.</summary>
    public static string Resolver(string? recibido) =>
        recibido is { Length: > 0 and <= 64 } valor && valor.All(caracter => char.IsAsciiLetterOrDigit(caracter) || caracter is '-' or '_' or '.' or ':')
            ? valor
            : Guid.NewGuid().ToString("N")[..16];
}

/// <summary>Respuestas de error con el formato único de la API (<see cref="ErrorApi"/>).</summary>
public static class Errores
{
    public static ErrorApi Cuerpo(int estado, string mensaje, IReadOnlyList<string>? detalles = null, string? codigo = null, int? codigoSdk = null, string? tareaId = null) =>
        new(codigo ?? CodigosError.DeEstado(estado), mensaje, detalles ?? [], Correlacion.Actual, codigoSdk, tareaId);

    public static IResult Json(int estado, string mensaje, IReadOnlyList<string>? detalles = null, string? codigo = null, int? codigoSdk = null, string? tareaId = null) =>
        Results.Json(Cuerpo(estado, mensaje, detalles, codigo, codigoSdk, tareaId), statusCode: estado);

    /// <summary>422 con una entrada en <c>detalles</c> por cada validación que falló.</summary>
    public static IResult Validacion(IReadOnlyList<string> errores) =>
        Json(StatusCodes.Status422UnprocessableEntity, "La solicitud tiene errores de validación.", errores, CodigosError.Validacion);

    public static IResult NoAutorizado() =>
        Json(StatusCodes.Status401Unauthorized, "Falta la cabecera Authorization (Bearer <llave de API o token>) o la llave no es válida.");

    /// <summary>422 de una tarea que el worker terminó con error. Si viene un código del SDK de CONTPAQi, el mensaje se traduce.</summary>
    public static ErrorApi DeTarea(string? error, int? codigoSdk, string? tareaId)
    {
        var (mensaje, textoTecnico) = ErroresSdk.Traducir(codigoSdk, error);
        return Cuerpo(StatusCodes.Status422UnprocessableEntity, mensaje, textoTecnico is null ? [] : [textoTecnico],
            codigoSdk is > 0 ? CodigosError.ErrorSdk : CodigosError.OperacionFallida, codigoSdk, tareaId);
    }

    public static IResult DeTareaJson(string? error, int? codigoSdk, string? tareaId) =>
        Results.Json(DeTarea(error, codigoSdk, tareaId), statusCode: StatusCodes.Status422UnprocessableEntity);

    private static string MensajePorEstado(int estado) => estado switch
    {
        401 => "Falta la cabecera Authorization (Bearer <llave de API o token>) o la llave no es válida.",
        403 => "No tiene permiso para esta operación.",
        404 => "No existe el recurso solicitado.",
        405 => "El método HTTP no está permitido en esta ruta.",
        415 => "El tipo de contenido no es compatible: envíe JSON (Content-Type: application/json).",
        >= 500 => "Ocurrió un error interno. Indique el identificador de correlación al soporte.",
        _ => "La solicitud no se pudo procesar.",
    };

    /// <summary>La petición ya se registra en <see cref="Usar"/>; el registro propio de ASP.NET Core repetiría la ruta con su query string.</summary>
    public static void SinRuido(ILoggingBuilder constructorDeLogs)
    {
        constructorDeLogs.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        constructorDeLogs.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
    }

    /// <summary>
    /// Middleware: asigna el identificador de correlación, registra cada petición (sin cabeceras, cuerpo ni query string) y
    /// convierte en el formato único tanto las excepciones no controladas como las respuestas de error que salieron sin cuerpo.
    /// </summary>
    public static void Usar(WebApplication aplicacion)
    {
        var registro = aplicacion.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Acusa.Peticion");
        aplicacion.Use(async (contexto, siguiente) =>
        {
            var idCorrelacion = Correlacion.Resolver(contexto.Request.Headers[Correlacion.Cabecera].FirstOrDefault());
            Correlacion.Actual = idCorrelacion;
            contexto.TraceIdentifier = idCorrelacion;
            contexto.Response.OnStarting(() =>
            {
                contexto.Response.Headers[Correlacion.Cabecera] = idCorrelacion;
                return Task.CompletedTask;
            });

            using var ambito = registro.BeginScope("IdCorrelacion:{IdCorrelacion}", idCorrelacion);
            var cronometro = Stopwatch.StartNew();
            try
            {
                await siguiente();
            }
            catch (OperationCanceledException) when (contexto.RequestAborted.IsCancellationRequested)
            {
                registro.LogInformation("{Metodo} {Ruta} cancelada por quien llama ({Ms} ms)", contexto.Request.Method, contexto.Request.Path.Value, cronometro.ElapsedMilliseconds);
                return;
            }
            catch (Exception excepcion)
            {
                registro.LogError("Error no controlado en {Metodo} {Ruta}: {Tipo}: {Mensaje}{Traza}", contexto.Request.Method, contexto.Request.Path.Value,
                    excepcion.GetType().Name, Redaccion.Limpiar(excepcion.Message), Environment.NewLine + Redaccion.Limpiar(excepcion.StackTrace));
                if (!contexto.Response.HasStarted)
                {
                    contexto.Response.Clear();
                    contexto.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await contexto.Response.WriteAsJsonAsync(Cuerpo(500, MensajePorEstado(500)));
                }
            }

            var respuesta = contexto.Response;
            var ruta = contexto.Request.Path.Value ?? string.Empty;
            var esApi = ruta.StartsWith("/api", StringComparison.OrdinalIgnoreCase) || ruta.StartsWith("/admin", StringComparison.OrdinalIgnoreCase);
            if (esApi && !respuesta.HasStarted && respuesta.StatusCode >= 400 && (respuesta.ContentLength ?? 0) == 0 && string.IsNullOrEmpty(respuesta.ContentType))
            {
                await respuesta.WriteAsJsonAsync(Cuerpo(respuesta.StatusCode, MensajePorEstado(respuesta.StatusCode)));
            }

            var nivel = ruta == "/health" ? LogLevel.Debug : respuesta.StatusCode >= 500 ? LogLevel.Error : respuesta.StatusCode >= 400 ? LogLevel.Warning : LogLevel.Information;
            registro.Log(nivel, "{Metodo} {Ruta} -> {Estado} ({Ms} ms)", contexto.Request.Method, ruta, respuesta.StatusCode, cronometro.ElapsedMilliseconds);
        });
    }
}
