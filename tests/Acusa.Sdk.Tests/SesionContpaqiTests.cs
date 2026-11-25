using Acusa.Sdk.Application;
using Acusa.Sdk.Application.Sesion;
using Acusa.Sdk.Integration.Contpaqi;
using Microsoft.Extensions.Logging;

namespace Acusa.Sdk.Integration.Contpaqi.Tests;

/// <summary>Sustituto de <c>MGWServicios.dll</c>: guarda las llamadas que recibe y devuelve los códigos que se le indiquen.</summary>
internal sealed class FuncionesSdkFalsas : IFuncionesSdk
{
    private readonly object _candado = new();
    private readonly List<string> _llamadas = [];

    public int CodigoInicializar { get; set; }

    public int CodigoNombrePaq { get; set; }

    /// <summary>Códigos de <c>fAbreEmpresa</c> en el orden en que se llaman; cuando se acaban, devuelve 0.</summary>
    public Queue<int> CodigosApertura { get; } = new();

    public Exception? ExcepcionAlInicializar { get; set; }

    public Exception? ExcepcionAlCerrarEmpresa { get; set; }

    public List<string> Llamadas
    {
        get
        {
            lock (_candado)
            {
                return [.. _llamadas];
            }
        }
    }

    public void PrepararEntorno(string rutaSdk) => Registrar("PrepararEntorno");

    public int InicializarSdk()
    {
        Registrar("InicializarSdk");
        return ExcepcionAlInicializar is null ? CodigoInicializar : throw ExcepcionAlInicializar;
    }

    public void IniciarSesion(string usuario, string clave) => Registrar($"IniciarSesion({usuario})");

    public void IniciarSesionContabilidad(string usuario, string clave) => Registrar($"IniciarSesionContabilidad({usuario})");

    public int FijarNombrePaq(string nombrePaq)
    {
        Registrar("FijarNombrePaq");
        return CodigoNombrePaq;
    }

    public int AbrirEmpresa(string rutaEmpresa)
    {
        Registrar($"AbrirEmpresa({rutaEmpresa})");
        return CodigosApertura.Count > 0 ? CodigosApertura.Dequeue() : 0;
    }

    public void CerrarEmpresa()
    {
        Registrar("CerrarEmpresa");
        if (ExcepcionAlCerrarEmpresa is not null)
        {
            throw ExcepcionAlCerrarEmpresa;
        }
    }

    public void TerminarSdk() => Registrar("TerminarSdk");

    public string ObtenerMensajeDeError(int codigoError) => $"mensaje del SDK {codigoError}";

    private void Registrar(string llamada)
    {
        lock (_candado)
        {
            _llamadas.Add(llamada);
        }
    }
}

internal sealed class RegistroEnMemoria : ILogger
{
    private readonly object _candado = new();
    private readonly List<string> _lineas = [];

    public string Texto
    {
        get
        {
            lock (_candado)
            {
                return string.Join('\n', _lineas);
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_candado)
        {
            _lineas.Add(formatter(state, exception));
        }
    }
}

internal sealed class ClaveFija(string clave) : IProveedorClave
{
    public string ObtenerClave() => clave;
}

public sealed class SesionContpaqiTests : IDisposable
{
    private const string RutaEmpresa = @"C:\Compac\Empresas\adEMPRESA_DE_PRUEBA";

    private readonly FuncionesSdkFalsas _funcionesSdk = new();
    private readonly RegistroEnMemoria _registro = new();
    private readonly OpcionesContpaqi _opciones = new() { RutaSdk = Path.GetTempPath(), RutaEmpresa = RutaEmpresa };
    private SesionContpaqi? _sesion;

    public void Dispose() => _sesion?.Dispose();

    private SesionContpaqi CrearSesion(IProveedorClave? proveedorClave = null)
    {
        var traza = new TrazaSdk(_registro, _funcionesSdk.ObtenerMensajeDeError);
        _sesion = new SesionContpaqi(_opciones, traza, _funcionesSdk, proveedorClave);
        return _sesion;
    }

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (!condicion())
        {
            Assert.True(DateTime.UtcNow < limite, "Se agotó el tiempo esperando el cambio de estado.");
            await Task.Delay(10);
        }
    }

    private static Task EsperarFinDeInicioAsync(SesionContpaqi sesion) =>
        EsperarAsync(() => sesion.Estado.Estado is not (EstadoSesion.NoIniciada or EstadoSesion.Iniciando));

    [Fact]
    public async Task Iniciar_ConDialogo_InicializaElSdkYAbreLaEmpresa()
    {
        // Arrange
        var sesion = CrearSesion();

        // Act
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        // Assert
        Assert.Equal(EstadoSesion.Lista, sesion.Estado.Estado);
        Assert.Equal(["PrepararEntorno", "InicializarSdk", $"AbrirEmpresa({RutaEmpresa})"], _funcionesSdk.Llamadas);
    }

    [Fact]
    public async Task Iniciar_RutaConBarraNormal_AbreLaEmpresaConBarraInvertida()
    {
        // Arrange
        _opciones.RutaEmpresa = "C:/Compac/Empresas/adEMPRESA_DE_PRUEBA";
        var sesion = CrearSesion();

        // Act
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        // Assert
        Assert.Contains($"AbrirEmpresa({RutaEmpresa})", _funcionesSdk.Llamadas);
    }

    [Fact]
    public async Task Iniciar_FallaInicializarSdk_QuedaEnErrorSinAbrirEmpresa()
    {
        // Arrange
        _funcionesSdk.CodigoInicializar = 7;
        var sesion = CrearSesion();

        // Act
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        // Assert
        Assert.Equal(EstadoSesion.Error, sesion.Estado.Estado);
        Assert.Contains("fInicializaSDK = 7: mensaje del SDK 7", sesion.Estado.Mensaje);
        Assert.DoesNotContain(_funcionesSdk.Llamadas, llamada => llamada.StartsWith("AbrirEmpresa"));
    }

    [Fact]
    public async Task Iniciar_FallaAbrirEmpresa_TerminaElSdkYQuedaEnError()
    {
        // Arrange
        _funcionesSdk.CodigosApertura.Enqueue(120115);
        var sesion = CrearSesion();

        // Act
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        // Assert
        Assert.Equal(EstadoSesion.Error, sesion.Estado.Estado);
        Assert.Equal("TerminarSdk", _funcionesSdk.Llamadas[^1]);
    }

    [Fact]
    public async Task Iniciar_ElSdkLanzaUnaExcepcion_QuedaEnErrorYLiberaElSdk()
    {
        // Arrange
        _funcionesSdk.ExcepcionAlInicializar = new DllNotFoundException("MGWServicios.dll");
        var sesion = CrearSesion();

        // Act
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        // Assert
        Assert.Equal(EstadoSesion.Error, sesion.Estado.Estado);
        Assert.Contains("DllNotFoundException", sesion.Estado.Mensaje);
        Assert.Equal(["CerrarEmpresa", "TerminarSdk"], _funcionesSdk.Llamadas.TakeLast(2));
    }

    [Fact]
    public async Task Iniciar_DespuesDeUnError_PermiteReintentar()
    {
        // Arrange
        _funcionesSdk.CodigoInicializar = 7;
        var sesion = CrearSesion();
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);
        _funcionesSdk.CodigoInicializar = 0;

        // Act
        sesion.Iniciar();
        await EsperarAsync(() => sesion.Estado.Estado == EstadoSesion.Lista);

        // Assert
        Assert.Equal(EstadoSesion.Lista, sesion.Estado.Estado);
    }

    [Fact]
    public async Task Iniciar_ConCredencialesSinProveedorDeClave_QuedaEnError()
    {
        // Arrange
        _opciones.Inicio = "Credenciales";
        var sesion = CrearSesion(proveedorClave: null);

        // Act
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        // Assert
        Assert.Equal(EstadoSesion.Error, sesion.Estado.Estado);
        Assert.Contains("proveedor de clave", sesion.Estado.Mensaje);
    }

    [Fact]
    public async Task Iniciar_ConCredencialesSupervisorSinClave_AbreLaEmpresaSinPedirElDialogo()
    {
        _opciones.Inicio = "Credenciales";
        _opciones.Usuario = "SUPERVISOR";
        var sesion = CrearSesion(new ClaveFija(string.Empty));

        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        Assert.Equal(EstadoSesion.Lista, sesion.Estado.Estado);
        Assert.Contains("IniciarSesion(SUPERVISOR)", _funcionesSdk.Llamadas);
        Assert.Contains($"AbrirEmpresa({RutaEmpresa})", _funcionesSdk.Llamadas);
        Assert.DoesNotContain("InicializarSdk", _funcionesSdk.Llamadas);
    }

    [Fact]
    public async Task Iniciar_ConCredenciales_NoEscribeLaClaveEnLaTraza()
    {
        // Arrange
        const string claveDePrueba = "clave-de-prueba-0000";
        _opciones.Inicio = "Credenciales";
        _opciones.Usuario = "USUARIO";
        var sesion = CrearSesion(new ClaveFija(claveDePrueba));

        // Act
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        // Assert
        Assert.Equal(EstadoSesion.Lista, sesion.Estado.Estado);
        Assert.Contains("fInicioSesionSDK('USUARIO','***')", _registro.Texto);
        Assert.DoesNotContain(claveDePrueba, _registro.Texto);
    }

    [Fact]
    public async Task Iniciar_ConCredencialesYNombreDePaqRechazado_TerminaElSdkYQuedaEnError()
    {
        // Arrange
        _opciones.Inicio = "Credenciales";
        _funcionesSdk.CodigoNombrePaq = 9;
        var sesion = CrearSesion(new ClaveFija("clave-de-prueba-0000"));

        // Act
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        // Assert
        Assert.Equal(EstadoSesion.Error, sesion.Estado.Estado);
        Assert.Contains("fSetNombrePAQ = 9", sesion.Estado.Mensaje);
        Assert.Equal("TerminarSdk", _funcionesSdk.Llamadas[^1]);
    }

    [Fact]
    public void EjecutarAsync_SesionSinIniciar_LanzaSdkNoListoException()
    {
        // Arrange
        var sesion = CrearSesion();

        // Act
        var excepcion = Assert.Throws<SdkNoListoException>(() => { _ = sesion.EjecutarAsync(() => 1); });

        // Assert
        Assert.Equal(EstadoSesion.NoIniciada, excepcion.Estado);
    }

    [Fact]
    public async Task EjecutarAsync_SesionLista_EjecutaElTrabajoEnElHiloDelSdk()
    {
        // Arrange
        var sesion = CrearSesion();
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        // Act
        var nombreDelHilo = await sesion.EjecutarAsync(() => Thread.CurrentThread.Name);

        // Assert
        Assert.Equal("Acusa.Sdk", nombreDelHilo);
    }

    [Fact]
    public async Task EjecutarAsync_ElTrabajoLanzaUnaExcepcion_ElSiguienteTrabajoSeEjecuta()
    {
        // Arrange
        var sesion = CrearSesion();
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => sesion.EjecutarAsync<int>(() => throw new InvalidOperationException("falla del SDK")));
        var resultado = await sesion.EjecutarAsync(() => 42);

        // Assert
        Assert.Equal(42, resultado);
    }

    [Fact]
    public async Task CambiarEmpresaAsync_EmpresaNueva_CierraLaActualYAbreLaNueva()
    {
        // Arrange
        var sesion = CrearSesion();
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);
        const string rutaNueva = @"C:\Compac\Empresas\adOTRA";

        // Act
        var fallo = await sesion.CambiarEmpresaAsync(rutaNueva);

        // Assert
        Assert.Null(fallo);
        Assert.Equal(EstadoSesion.Lista, sesion.Estado.Estado);
        Assert.Equal(rutaNueva, sesion.Estado.Empresa);
        Assert.Equal(["CerrarEmpresa", $"AbrirEmpresa({rutaNueva})"], _funcionesSdk.Llamadas.TakeLast(2));
    }

    [Fact]
    public async Task CambiarEmpresaAsync_LaMismaEmpresa_NoLlamaAlSdk()
    {
        // Arrange
        var sesion = CrearSesion();
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);
        var llamadasAntes = _funcionesSdk.Llamadas.Count;

        // Act
        var fallo = await sesion.CambiarEmpresaAsync(RutaEmpresa + "/");

        // Assert
        Assert.Null(fallo);
        Assert.Equal(llamadasAntes, _funcionesSdk.Llamadas.Count);
    }

    [Fact]
    public async Task CambiarEmpresaAsync_FallaLaNuevaPeroVuelveALaAnterior_QuedaListaConLaEmpresaAnterior()
    {
        // Arrange
        var sesion = CrearSesion();
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);
        _funcionesSdk.CodigosApertura.Enqueue(120115);

        // Act
        var fallo = await sesion.CambiarEmpresaAsync(@"C:\Compac\Empresas\adNOEXISTE");

        // Assert
        Assert.Contains("fAbreEmpresa = 120115", fallo);
        Assert.Equal(EstadoSesion.Lista, sesion.Estado.Estado);
        Assert.Equal(RutaEmpresa, sesion.Estado.Empresa);
    }

    [Fact]
    public async Task CambiarEmpresaAsync_FallanLaNuevaYLaAnterior_TerminaElSdkYQuedaEnError()
    {
        // Arrange
        var sesion = CrearSesion();
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);
        _funcionesSdk.CodigosApertura.Enqueue(120115);
        _funcionesSdk.CodigosApertura.Enqueue(120115);

        // Act
        await sesion.CambiarEmpresaAsync(@"C:\Compac\Empresas\adNOEXISTE");

        // Assert
        Assert.Equal(EstadoSesion.Error, sesion.Estado.Estado);
        Assert.Equal("TerminarSdk", _funcionesSdk.Llamadas[^1]);
    }

    [Fact]
    public async Task CambiarEmpresaAsync_SesionSinIniciar_DevuelveElMotivo()
    {
        // Arrange
        var sesion = CrearSesion();

        // Act
        var fallo = await sesion.CambiarEmpresaAsync(@"C:\Compac\Empresas\adOTRA");

        // Assert
        Assert.Contains("no está lista", fallo);
    }

    [Fact]
    public async Task CerrarAsync_SesionLista_CierraLaEmpresaYTerminaElSdk()
    {
        // Arrange
        var sesion = CrearSesion();
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);

        // Act
        await sesion.CerrarAsync();

        // Assert
        Assert.Equal(EstadoSesion.NoIniciada, sesion.Estado.Estado);
        Assert.Equal(["CerrarEmpresa", "TerminarSdk"], _funcionesSdk.Llamadas.TakeLast(2));
    }

    [Fact]
    public async Task CerrarAsync_FallaCerrarLaEmpresa_TerminaElSdkDeTodasFormas()
    {
        // Arrange
        var sesion = CrearSesion();
        sesion.Iniciar();
        await EsperarFinDeInicioAsync(sesion);
        _funcionesSdk.ExcepcionAlCerrarEmpresa = new InvalidOperationException("falla al cerrar");

        // Act
        await sesion.CerrarAsync();

        // Assert
        Assert.Equal(EstadoSesion.Error, sesion.Estado.Estado);
        Assert.Equal("TerminarSdk", _funcionesSdk.Llamadas[^1]);
    }

    [Fact]
    public async Task CerrarAsync_SesionSinIniciar_NoLlamaAlSdk()
    {
        // Arrange
        var sesion = CrearSesion();

        // Act
        await sesion.CerrarAsync();

        // Assert
        Assert.Empty(_funcionesSdk.Llamadas);
    }
}
