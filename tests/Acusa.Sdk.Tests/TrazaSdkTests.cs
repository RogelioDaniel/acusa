using Acusa.Sdk.Integration.Contpaqi;
using Microsoft.Extensions.Logging;

namespace Acusa.Sdk.Tests;

public class TrazaSdkTests
{
    private sealed class Registro : ILogger
    {
        public List<(LogLevel Nivel, string Texto)> Lineas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lineas.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Una_llamada_exitosa_muestra_la_firma_el_codigo_y_el_tiempo()
    {
        var registro = new Registro();
        var traza = new TrazaSdk(registro);

        var r = traza.Llamar("fInsertaCteProv()", () => 0);

        Assert.Equal(0, r);
        var linea = Assert.Single(registro.Lineas);
        Assert.Equal(LogLevel.Information, linea.Nivel);
        Assert.StartsWith("fInsertaCteProv() = 0", linea.Texto);
        Assert.EndsWith(" ms)", linea.Texto);
    }

    [Fact]
    public void Una_lectura_muestra_el_valor_devuelto_por_el_sdk()
    {
        var registro = new Registro();
        var traza = new TrazaSdk(registro);
        var texto = string.Empty;

        traza.Llamar("fLeeDatoCteProv('CRAZONSOCIAL')", () => { texto = "PRUEBA API CS-11"; return 0; }, valor: () => texto);

        Assert.Contains("= 0 -> 'PRUEBA API CS-11'", Assert.Single(registro.Lineas).Texto);
    }

    [Fact]
    public void Con_anunciar_escribe_una_linea_antes_de_llamar()
    {
        var registro = new Registro();
        var traza = new TrazaSdk(registro);

        traza.Llamar("fInicializaSDK()", () => 0, anunciar: true);

        Assert.Equal(2, registro.Lineas.Count);
        Assert.Equal(">> fInicializaSDK()", registro.Lineas[0].Texto);
    }

    [Fact]
    public void Si_la_llamada_lanza_una_excepcion_la_registra_y_la_propaga()
    {
        var registro = new Registro();
        var traza = new TrazaSdk(registro);

        Assert.Throws<DllNotFoundException>(() =>
            traza.Llamar("fInicializaSDK()", () => throw new DllNotFoundException("MGWServicios.dll")));

        var linea = Assert.Single(registro.Lineas);
        Assert.Equal(LogLevel.Error, linea.Nivel);
        Assert.Contains("DllNotFoundException", linea.Texto);
    }

    [Fact]
    public void Las_llamadas_sin_codigo_se_registran_como_ok()
    {
        var registro = new Registro();
        var traza = new TrazaSdk(registro);

        traza.Llamar("fTerminaSDK()", () => { });

        Assert.StartsWith("fTerminaSDK() ok", Assert.Single(registro.Lineas).Texto);
    }
}
