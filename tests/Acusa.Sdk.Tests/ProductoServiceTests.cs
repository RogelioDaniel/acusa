using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Productos;
using Acusa.Sdk.Application.Sesion;
using Acusa.Sdk.Application.Simulado;

namespace Acusa.Sdk.Tests;

public class ProductoServiceTests
{
    private static ProductoService Crear(bool escritura = true, bool sesionLista = true, ProductoGatewayEnMemoria? gateway = null)
    {
        var sesion = new SesionSimulada("EMPRESA DE PRUEBA");
        if (sesionLista)
        {
            sesion.Iniciar();
        }

        return new ProductoService(gateway ?? new ProductoGatewayEnMemoria(), sesion, new PoliticaEscritura(escritura));
    }

    private static NuevoProducto Valido(string codigo = "PRUEBAPR1") =>
        new(codigo, "PRUEBA PRODUCTO", "PIEZA", 10m);

    [Fact]
    public async Task Crear_con_datos_validos_devuelve_el_producto_leido()
    {
        var r = await Crear().CrearAsync(Valido("pruebapr1") with { Descripcion = "  prueba  " }, default);

        Assert.Equal(TipoResultadoCrear.Creado, r.Tipo);
        Assert.Equal("PRUEBAPR1", r.Producto!.Codigo);
        Assert.Equal("prueba", r.Producto.Descripcion);
        Assert.Equal(1, r.Producto.Estatus);
        Assert.Equal(10m, r.Producto.Precio1);
        Assert.Equal("PIEZA", r.Producto.UnidadBase);
    }

    [Fact]
    public async Task Crear_rechaza_toda_escritura_cuando_esta_deshabilitada()
    {
        var r = await Crear(escritura: false).CrearAsync(Valido(), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains("PermitirEscritura", r.Mensaje);
    }

    [Theory]
    [InlineData("REAL001", "PRUEBA X", "PIEZA", 1, "debe empezar con PRUEBA")]
    [InlineData("PRUEBA-1", "PRUEBA X", "PIEZA", 1, "sin guiones ni espacios")]
    [InlineData("PRUEBAPR1", "PRODUCTO REAL", "PIEZA", 1, "El nombre debe empezar con PRUEBA")]
    [InlineData("PRUEBAPR1", "PRUEBA X", "", 1, "unidad base es obligatoria")]
    [InlineData("PRUEBAPR1", "PRUEBA X", "PIEZA", -1, "precio no puede ser negativo")]
    public async Task Crear_rechaza_datos_que_no_son_de_prueba_o_estan_mal(string codigo, string nombre, string unidad, int precio, string fragmento)
    {
        var r = await Crear().CrearAsync(new NuevoProducto(codigo, nombre, unidad, precio), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains(fragmento, r.Mensaje);
    }

    [Fact]
    public async Task Crear_rechaza_un_nombre_mas_largo_que_el_del_sdk()
    {
        var nombre = "PRUEBA " + new string('X', PoliticaEscritura.LongitudMaximaNombreProducto);

        var r = await Crear().CrearAsync(new NuevoProducto("PRUEBAPR1", nombre, "PIEZA"), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
    }

    [Fact]
    public async Task Crear_con_una_unidad_inexistente_es_invalido()
    {
        var r = await Crear().CrearAsync(Valido() with { UnidadBase = "FURLONG" }, default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains("FURLONG", r.Mensaje);
    }

    [Fact]
    public async Task Crear_dos_veces_el_mismo_codigo_devuelve_ya_existe()
    {
        var servicio = Crear();
        await servicio.CrearAsync(Valido(), default);

        var r = await servicio.CrearAsync(Valido(), default);

        Assert.Equal(TipoResultadoCrear.YaExiste, r.Tipo);
    }

    [Fact]
    public async Task Obtener_un_producto_inexistente_devuelve_null()
    {
        Assert.Null(await Crear().ObtenerAsync("NOEXISTE", default));
    }

    [Fact]
    public async Task Sin_sesion_lista_lanza_sdk_no_listo_pero_la_validacion_va_primero()
    {
        var servicio = Crear(sesionLista: false);

        await Assert.ThrowsAsync<SdkNoListoException>(() => servicio.ObtenerAsync("PRUEBAPR1", default));
        await Assert.ThrowsAsync<SdkNoListoException>(() => servicio.CrearAsync(Valido(), default));

        var invalido = await servicio.CrearAsync(new NuevoProducto("REAL001", "X", "PIEZA"), default);
        Assert.Equal(TipoResultadoCrear.Invalido, invalido.Tipo);
    }
}
