using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Acusa.Sdk.Worker;

/// <summary>Una tarea ya atendida, para el listado del panel.</summary>
public sealed record TareaReciente(string Id, string Tipo, DateTime Hora, bool Exito, string? Error, int Milisegundos);

/// <summary>Lo que muestra la pantalla de estado.</summary>
public sealed record EstadoSnapshot(
    string Conexion,
    string? ConexionDetalle,
    DateTime? UltimoContacto,
    long Procesadas,
    long Fallidas,
    string? TareaActual,
    DateTime Inicio,
    IReadOnlyList<TareaReciente> Recientes);

/// <summary>Estado vivo del worker, compartido entre el ciclo de trabajo y el panel local.</summary>
public sealed class EstadoWorker
{
    private readonly object _candado = new();
    private readonly List<TareaReciente> _recientes = [];
    private string _conexion = "iniciando";
    private string? _detalle;
    private DateTime? _ultimoContacto;
    private long _procesadas;
    private long _fallidas;
    private string? _actual;

    public DateTime Inicio { get; } = DateTime.UtcNow;

    public void Conexion(string estado, string? detalle = null)
    {
        lock (_candado)
        {
            _conexion = estado;
            _detalle = detalle;
            if (estado == "conectado")
            {
                _ultimoContacto = DateTime.UtcNow;
            }
        }
    }

    public void Inicia(string tipo)
    {
        lock (_candado)
        {
            _actual = tipo;
        }
    }

    public void Termina(string id, string tipo, bool exito, string? error, int milisegundos)
    {
        lock (_candado)
        {
            _actual = null;
            if (exito) _procesadas++; else _fallidas++;
            _recientes.Insert(0, new TareaReciente(id, tipo, DateTime.UtcNow, exito, error, milisegundos));
            if (_recientes.Count > 20)
            {
                _recientes.RemoveAt(_recientes.Count - 1);
            }
        }
    }

    public EstadoSnapshot Snapshot()
    {
        lock (_candado)
        {
            return new EstadoSnapshot(_conexion, _detalle, _ultimoContacto, _procesadas, _fallidas, _actual, Inicio, [.. _recientes]);
        }
    }
}

/// <summary>Una línea de la bitácora, numerada para que el panel pida solo las nuevas.</summary>
public sealed record LineaBitacora(long N, DateTime Hora, string Nivel, string Origen, string Mensaje);

/// <summary>
/// Guarda las últimas líneas en memoria (para el panel) y las escribe en un archivo por día. Las llamadas HTTP
/// rutinarias de la consulta no se registran: harían la bitácora ilegible.
/// </summary>
public sealed class BitacoraProvider : ILoggerProvider
{
    private const int Maximo = 600;
    private readonly ConcurrentQueue<LineaBitacora> _lineas = new();
    private readonly object _archivo = new();
    private readonly string _carpeta;
    private long _contador;

    public BitacoraProvider(string carpetaLogs)
    {
        _carpeta = carpetaLogs;
    }

    public ILogger CreateLogger(string categoria) => new Registro(this, categoria);

    public IReadOnlyList<LineaBitacora> Desde(long n) => [.. _lineas.Where(l => l.N > n)];

    public void Dispose()
    {
    }

    private void Agregar(string nivel, string categoria, string mensaje)
    {
        var origen = categoria.Contains('.') ? categoria[(categoria.LastIndexOf('.') + 1)..] : categoria;
        var linea = new LineaBitacora(Interlocked.Increment(ref _contador), DateTime.Now, nivel, origen, mensaje);
        _lineas.Enqueue(linea);
        while (_lineas.Count > Maximo && _lineas.TryDequeue(out _))
        {
        }

        try
        {
            lock (_archivo)
            {
                Directory.CreateDirectory(_carpeta);
                File.AppendAllText(Path.Combine(_carpeta, $"worker-{linea.Hora:yyyyMMdd}.log"),
                    $"{linea.Hora:HH:mm:ss} {nivel,-5} {origen}: {mensaje}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Si no se puede escribir el archivo, la bitácora en memoria sigue funcionando.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Registro(BitacoraProvider padre, string categoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel nivel) =>
            nivel >= LogLevel.Information && !(categoria.StartsWith("System.Net.Http", StringComparison.Ordinal) && nivel < LogLevel.Warning);

        public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? ex, Func<TState, Exception?, string> formato)
        {
            if (!IsEnabled(nivel))
            {
                return;
            }

            var texto = formato(estado, ex) + (ex is null ? string.Empty : $" ({ex.GetType().Name}: {ex.Message})");
            padre.Agregar(nivel switch { LogLevel.Warning => "AVISO", LogLevel.Error or LogLevel.Critical => "ERROR", _ => "INFO" }, categoria, texto);
        }
    }
}

/// <summary>Dónde guarda el worker su configuración y sus logs, y cómo protege los secretos.</summary>
public static class DatosLocales
{
    private static readonly byte[] Entropia = Encoding.UTF8.GetBytes("Acusa.Worker.v1");

    /// <summary>ProgramData\Acusa\Worker si se puede escribir; si no, el perfil del usuario.</summary>
    public static string Carpeta { get; } = Resolver();

    public static string RutaConfig => Path.Combine(Carpeta, "configuracion.json");

    public static string RutaLogs => Path.Combine(Carpeta, "logs");

    private static string Resolver()
    {
        foreach (var raiz in new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            try
            {
                var ruta = Path.Combine(Environment.GetFolderPath(raiz), "Acusa", "Worker");
                Directory.CreateDirectory(ruta);
                var prueba = Path.Combine(ruta, ".escritura");
                File.WriteAllText(prueba, "ok");
                File.Delete(prueba);
                return ruta;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Se intenta con la siguiente carpeta.
            }
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Cifra con DPAPI para el usuario de Windows actual: solo ese usuario, en este equipo, puede descifrarlo.
    /// Por eso la configuración debe hacerse con la misma cuenta que ejecuta el worker.
    /// </summary>
    public static string Proteger(string texto) =>
        OperatingSystem.IsWindows()
            ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(texto), Entropia, DataProtectionScope.CurrentUser))
            : throw new PlatformNotSupportedException("El cifrado de secretos solo está disponible en Windows.");

    public static string? Revelar(string? protegido)
    {
        if (string.IsNullOrEmpty(protegido) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protegido), Entropia, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Código de 8 caracteres para abrir el panel; se guarda en el perfil del usuario, no en ProgramData.</summary>
    public static string CodigoPanel()
    {
        var ruta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Acusa", "panel-codigo.txt");
        try
        {
            if (File.Exists(ruta) && File.ReadAllText(ruta).Trim() is { Length: 8 } existente)
            {
                return existente;
            }

            const string alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var codigo = new string(Enumerable.Range(0, 8).Select(_ => alfabeto[RandomNumberGenerator.GetInt32(alfabeto.Length)]).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            File.WriteAllText(ruta, codigo);
            return codigo;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            const string alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            return new string(Enumerable.Range(0, 8).Select(_ => alfabeto[RandomNumberGenerator.GetInt32(alfabeto.Length)]).ToArray());
        }
    }
}
