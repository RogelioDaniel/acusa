using System.Diagnostics;
using Acusa.Sdk.Integration.Contpaqi.Native;
using Microsoft.Extensions.Logging;

namespace Acusa.Sdk.Integration.Contpaqi;

/// <summary>
/// Registra cada llamada al SDK con su firma, el código que devuelve, el mensaje del SDK cuando el código
/// no es 0 y el tiempo que tardó. Es lo que se ve en la consola cuando la API atiende una petición.
/// </summary>
public sealed class TrazaSdk(ILogger logger)
{
    /// <summary>Código de <c>fBuscaCteProv</c> cuando no existe el registro: es un resultado normal.</summary>
    private const int NoEncontrado = SdkNative.NoEncontrado;

    /// <param name="firma">Texto que se muestra, por ejemplo <c>fBuscaCteProv('PRUEBACS12')</c>.</param>
    /// <param name="llamada">La llamada al SDK; devuelve su código de retorno.</param>
    /// <param name="anunciar">Escribe una línea antes de llamar. Sirve para llamadas que se quedan esperando, como el diálogo de clave.</param>
    /// <param name="valor">Texto devuelto por el SDK (campo leído) que se muestra junto al código.</param>
    public int Llamar(string firma, Func<int> llamada, bool anunciar = false, Func<string>? valor = null)
    {
        if (anunciar)
        {
            logger.LogInformation(">> {Firma}", firma);
        }

        var reloj = Stopwatch.StartNew();
        int codigo;
        try
        {
            codigo = llamada();
        }
        catch (Exception ex)
        {
            logger.LogError("!! {Firma} lanzó {Tipo}: {Mensaje}", firma, ex.GetType().Name, ex.Message);
            throw;
        }

        reloj.Stop();
        Escribir(firma, codigo, reloj.ElapsedMilliseconds, valor);
        return codigo;
    }

    /// <summary>Llamadas que no devuelven código (fCierraEmpresa, fTerminaSDK).</summary>
    public void Llamar(string firma, Action llamada)
    {
        var reloj = Stopwatch.StartNew();
        try
        {
            llamada();
        }
        catch (Exception ex)
        {
            logger.LogError("!! {Firma} lanzó {Tipo}: {Mensaje}", firma, ex.GetType().Name, ex.Message);
            throw;
        }

        reloj.Stop();
        logger.LogInformation("{Firma} ok ({Ms} ms)", firma, reloj.ElapsedMilliseconds);
    }

    private void Escribir(string firma, int codigo, long ms, Func<string>? valor)
    {
        if (codigo == 0)
        {
            var detalle = valor is null ? string.Empty : $" -> '{valor()}'";
            logger.LogInformation("{Firma} = 0{Detalle} ({Ms} ms)", firma, detalle, ms);
            return;
        }

        var mensaje = SdkNative.Mensaje(codigo);
        var nivel = codigo == NoEncontrado ? LogLevel.Information : LogLevel.Warning;
        logger.Log(nivel, "{Firma} = {Codigo} ({Mensaje}) ({Ms} ms)", firma, codigo, mensaje, ms);
    }
}
