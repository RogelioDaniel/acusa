using System.Diagnostics;
using System.IO.Compression;
using Acusa.Contrato;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Acusa.Sdk.Worker;

/// <summary>Lanza la instalación de una versión ya descargada y verificada.</summary>
public interface IAplicadorActualizacion
{
    void Aplicar(string carpetaPaquete, string version);
}

/// <summary>
/// Ejecuta el <c>instalar.ps1</c> del paquete en un proceso aparte: detiene este servicio, respalda la versión actual, copia la nueva
/// conservando la configuración, reinicia el servicio y, si no abre CONTPAQi, regresa sola a la versión anterior.
/// </summary>
public sealed class AplicadorConPowerShell : IAplicadorActualizacion
{
    public void Aplicar(string carpetaPaquete, string version)
    {
        var instalador = Path.Combine(carpetaPaquete, "instalar.ps1");
        var inicio = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = carpetaPaquete,
        };
        foreach (var argumento in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", instalador, "-Automatica" })
        {
            inicio.ArgumentList.Add(argumento);
        }

        Process.Start(inicio);
    }
}

/// <summary>Archivos de la carpeta de actualizaciones y versiones que ya fallaron (no se vuelven a intentar).</summary>
public sealed class CarpetaActualizaciones(string raiz)
{
    public static readonly string[] ArchivosRequeridos =
        ["Acusa.Sdk.Worker.exe", "Acusa.Worker.Desktop.exe", "appsettings.json", "acusa.ico", "instalar.ps1", "servicio.ps1"];

    public string Raiz { get; } = raiz;

    private string ArchivoFallidas => Path.Combine(Raiz, "fallidas.txt");

    public bool Fallo(string version) =>
        File.Exists(ArchivoFallidas) && File.ReadAllLines(ArchivoFallidas).Any(linea => linea.Trim() == version);

    public string CarpetaDe(string version) => Path.Combine(Raiz, version);
}

/// <summary>
/// Revisa en Acusa Central si hay una versión más nueva del worker y la aplica sola. Reglas: la firma debe ser de quien publica
/// (llave pública compilada), el paquete debe coincidir en tamaño y SHA-256, no debe haber una tarea en curso, se evitan los minutos
/// 00 a 14 de cada hora (rachas programadas de otras aplicaciones con el SDK) y no se reintenta una versión que ya falló.
/// </summary>
public sealed class Actualizador(
    ClienteOrquestador orquestador,
    EstadoWorker estado,
    CarpetaActualizaciones carpeta,
    IAplicadorActualizacion aplicador,
    ILogger<Actualizador> log,
    TimeProvider reloj,
    string versionInstalada,
    string clavePublica) : BackgroundService
{
    public static readonly TimeSpan PrimeraRevision = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(30);

    /// <summary>Si en este momento se puede aplicar una actualización.</summary>
    public static bool EsMomentoDeAplicar(DateTimeOffset horaLocal, bool ocupado) => !ocupado && horaLocal.Minute >= 15;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(PrimeraRevision, reloj, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RevisarAsync(ct);
                }
                catch (Exception excepcion) when (excepcion is not OperationCanceledException)
                {
                    log.LogWarning("No se pudo revisar si hay una versión nueva ({Mensaje}). Se reintenta en {Minutos} min.", excepcion.Message, Intervalo.TotalMinutes);
                }

                await Task.Delay(Intervalo, reloj, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Una revisión completa. Devuelve la versión aplicada, o <c>null</c> si no se aplicó ninguna.</summary>
    public async Task<string?> RevisarAsync(CancellationToken ct)
    {
        var manifiesto = await orquestador.ConsultarActualizacionAsync(versionInstalada, ct);
        if (manifiesto is null || !FirmaActualizaciones.EsMasNueva(manifiesto.Version, versionInstalada))
        {
            return null;
        }

        if (carpeta.Fallo(manifiesto.Version))
        {
            return null;
        }

        if (!FirmaActualizaciones.Verificar(manifiesto, clavePublica))
        {
            log.LogWarning("La versión {Version} publicada en Central no tiene una firma válida: no se instala.", manifiesto.Version);
            return null;
        }

        if (!EsMomentoDeAplicar(reloj.GetLocalNow(), estado.Ocupado))
        {
            log.LogInformation("Hay una versión nueva ({Version}); se instalará en un momento sin tareas, fuera de los minutos 00 a 14.", manifiesto.Version);
            return null;
        }

        var destino = carpeta.CarpetaDe(manifiesto.Version);
        if (Directory.Exists(destino))
        {
            Directory.Delete(destino, recursive: true);
        }

        Directory.CreateDirectory(destino);
        var zip = Path.Combine(destino, "paquete.zip");
        await using (var archivo = File.Create(zip))
        {
            await orquestador.DescargarPaqueteAsync(manifiesto.Version, archivo, ct);
        }

        string sha256;
        await using (var archivo = File.OpenRead(zip))
        {
            sha256 = FirmaActualizaciones.Sha256De(archivo);
        }

        if (new FileInfo(zip).Length != manifiesto.Tamano || !string.Equals(sha256, manifiesto.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("El paquete descargado de la versión {Version} no coincide con su firma: no se instala.", manifiesto.Version);
            Directory.Delete(destino, recursive: true);
            return null;
        }

        var archivos = Path.Combine(destino, "archivos");
        ZipFile.ExtractToDirectory(zip, archivos, overwriteFiles: true);
        var faltante = CarpetaActualizaciones.ArchivosRequeridos.FirstOrDefault(nombre => !File.Exists(Path.Combine(archivos, nombre)));
        if (faltante is not null)
        {
            log.LogWarning("Al paquete de la versión {Version} le falta {Archivo}: no se instala.", manifiesto.Version, faltante);
            return null;
        }

        log.LogInformation("Instalando la versión {Version} (actual {Actual}). El servicio se reinicia en unos segundos.", manifiesto.Version, versionInstalada);
        aplicador.Aplicar(archivos, manifiesto.Version);
        return manifiesto.Version;
    }
}
