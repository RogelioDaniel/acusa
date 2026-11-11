using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Sesion;
using Acusa.Sdk.Application.Simulado;

namespace Acusa.Sdk.Tests;

public class ClienteServiceTests
{
    private static ClienteService Crear(bool escritura = true, bool sesionLista = true, ClienteGatewayEnMemoria? gateway = null)
    {
        var sesion = new SesionSimulada("EMPRESA DE PRUEBA");
        if (sesionLista)
        {
            sesion.Iniciar();
        }

        return new ClienteService(
            gateway ?? new ClienteGatewayEnMemoria(),
            sesion,
            new PoliticaEscritura(escritura));
    }

    [Fact]
    public async Task Crear_con_datos_validos_devuelve_el_cliente_leido()
    {
        var servicio = Crear();

        var r = await servicio.CrearAsync(new NuevoCliente("pruebacs12", "  PRUEBA API CS-11 SDK "), default);

        Assert.Equal(TipoResultadoCrear.Creado, r.Tipo);
        Assert.Equal("PRUEBACS12", r.Cliente!.Codigo);
        Assert.Equal("PRUEBA API CS-11 SDK", r.Cliente.RazonSocial);
        Assert.Equal(1, r.Cliente.ListaPrecios);
        Assert.Null(r.Cliente.Rfc);
    }

    [Fact]
    public async Task Crear_rechaza_toda_escritura_cuando_esta_deshabilitada()
    {
        var servicio = Crear(escritura: false);

        var r = await servicio.CrearAsync(new NuevoCliente("PRUEBACS12", "PRUEBA X"), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains("deshabilitada", r.Mensaje);
    }

    [Theory]
    [InlineData("CLI001", "PRUEBA X")]
    [InlineData("PRUEBACS12", "CLIENTE REAL SA")]
    [InlineData("PRUEBA-CS12", "PRUEBA X")]
    [InlineData("PRUEBA CS12", "PRUEBA X")]
    [InlineData("", "PRUEBA X")]
    [InlineData("PRUEBACS12", "")]
    [InlineData("PRUEBA123456789012345", "PRUEBA X")]
    public async Task Crear_rechaza_registros_que_no_estan_marcados_como_prueba(string codigo, string razon)
    {
        var servicio = Crear();

        var r = await servicio.CrearAsync(new NuevoCliente(codigo, razon), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
    }

    [Fact]
    public async Task Crear_no_pisa_un_cliente_existente()
    {
        var servicio = Crear();
        await servicio.CrearAsync(new NuevoCliente("PRUEBACS12", "PRUEBA UNO"), default);

        var r = await servicio.CrearAsync(new NuevoCliente("PRUEBACS12", "PRUEBA DOS"), default);

        Assert.Equal(TipoResultadoCrear.YaExiste, r.Tipo);
        var actual = await servicio.ObtenerAsync("PRUEBACS12", default);
        Assert.Equal("PRUEBA UNO", actual!.RazonSocial);
    }

    [Fact]
    public async Task Obtener_devuelve_null_si_no_existe_y_normaliza_el_codigo()
    {
        var servicio = Crear();
        await servicio.CrearAsync(new NuevoCliente("PRUEBACS12", "PRUEBA UNO"), default);

        Assert.NotNull(await servicio.ObtenerAsync(" pruebacs12 ", default));
        Assert.Null(await servicio.ObtenerAsync("NOEXISTE", default));
    }

    [Fact]
    public async Task Sin_sesion_lista_no_se_lee_ni_se_escribe()
    {
        var servicio = Crear(sesionLista: false);

        await Assert.ThrowsAsync<SdkNoListoException>(() => servicio.ObtenerAsync("PRUEBACS12", default));
        await Assert.ThrowsAsync<SdkNoListoException>(
            () => servicio.CrearAsync(new NuevoCliente("PRUEBACS12", "PRUEBA UNO"), default));
    }

    [Fact]
    public async Task Una_solicitud_invalida_se_rechaza_antes_de_pedir_la_sesion()
    {
        var servicio = Crear(sesionLista: false);

        var r = await servicio.CrearAsync(new NuevoCliente("CLI001", "REAL"), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
    }
}
