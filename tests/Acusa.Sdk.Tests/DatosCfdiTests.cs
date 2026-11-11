using System.Text.Json;
using Acusa.Contrato;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Documentos;
using Acusa.Sdk.Application.Productos;
using Acusa.Sdk.Application.Simulado;
using Acusa.Sdk.Worker;

namespace Acusa.Sdk.Tests;

/// <summary>Los datos que CFDI 4.0 exige y que faltaban: clave SAT del producto y domicilio fiscal del cliente.</summary>
public class DatosCfdiTests
{
    private static async Task<Ejecutor> CrearAsync(bool escritura = true)
    {
        var clientesGw = new ClienteGatewayEnMemoria();
        var productosGw = new ProductoGatewayEnMemoria();
        await clientesGw.CrearAsync(new NuevoCliente("PRUEBACS1", "PRUEBA CLIENTE"), default);
        await productosGw.CrearAsync(new NuevoProducto("PRUEBAPR1", "PRUEBA PRODUCTO", "PIEZA", 100m), default);
        var sesion = new SesionSimulada("EMPRESA DE PRUEBA");
        sesion.Iniciar();
        var politica = new PoliticaEscritura(escritura, "PRUEBA", true, true);
        return new Ejecutor(
            new ClienteService(clientesGw, sesion, politica),
            new ProductoService(productosGw, sesion, politica),
            new DocumentoService(new DocumentoGatewayEnMemoria(clientesGw, productosGw), sesion, politica));
    }

    private static TareaAsignada Tarea(string tipo, string json) =>
        new("t", tipo, "EMPRESA", JsonDocument.Parse(json).RootElement.Clone());

    // ---- clave SAT ----

    [Fact]
    public async Task Crear_producto_con_clave_sat_la_guarda()
    {
        var e = await CrearAsync();

        var r = await e.EjecutarAsync(Tarea(Operaciones.ProductoCrear,
            """{"codigo":"PRUEBAPR2","nombre":"PRUEBA DOS","unidadBase":"Pieza","precio1":5,"claveSat":"01010101"}"""), default);

        Assert.True(r.Exito, r.Error);
        Assert.Equal("01010101", r.Datos!.Value.GetProperty("claveSat").GetString());
    }

    [Fact]
    public async Task Montek_manda_claveSAT_en_mayusculas_y_tambien_se_acepta()
    {
        var e = await CrearAsync();

        var r = await e.EjecutarAsync(Tarea(Operaciones.ProductoCrear,
            """{"codigo":"PRUEBAPR3","nombre":"PRUEBA TRES","unidadBase":"Pieza","claveSAT":"43232408"}"""), default);

        Assert.True(r.Exito, r.Error);
        Assert.Equal("43232408", r.Datos!.Value.GetProperty("claveSat").GetString());
    }

    [Fact]
    public async Task Actualizar_producto_asigna_la_clave_sat_sin_tocar_lo_demas()
    {
        var e = await CrearAsync();

        var r = await e.EjecutarAsync(Tarea(Operaciones.ProductoActualizar, """{"codigo":"pruebapr1","claveSat":"01010101"}"""), default);

        Assert.True(r.Exito, r.Error);
        var p = r.Datos!.Value;
        Assert.Equal("01010101", p.GetProperty("claveSat").GetString());
        Assert.Equal("PRUEBA PRODUCTO", p.GetProperty("nombre").GetString());
        Assert.Equal(100m, p.GetProperty("precio1").GetDecimal());
    }

    [Theory]
    [InlineData("""{"codigo":"PRUEBAPR1","claveSat":"123"}""", "8 dígitos")]
    [InlineData("""{"codigo":"PRUEBAPR1","claveSat":"ABCDEFGH"}""", "8 dígitos")]
    [InlineData("""{"codigo":"PRUEBAPR1"}""", "nada que cambiar")]
    [InlineData("""{"codigo":"PRODUCTOREAL","claveSat":"01010101"}""", "PRUEBA")]
    [InlineData("""{"codigo":"PRUEBAPR1","precio1":-1}""", "negativo")]
    [InlineData("""{"codigo":"PRUEBAPR1","nombre":"OTRO NOMBRE"}""", "PRUEBA")]
    public async Task Actualizar_producto_rechaza_datos_invalidos(string payload, string fragmento)
    {
        var e = await CrearAsync();

        var r = await e.EjecutarAsync(Tarea(Operaciones.ProductoActualizar, payload), default);

        Assert.False(r.Exito);
        Assert.Contains(fragmento, r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Crear_producto_con_clave_sat_invalida_no_lo_crea()
    {
        var e = await CrearAsync();

        var r = await e.EjecutarAsync(Tarea(Operaciones.ProductoCrear,
            """{"codigo":"PRUEBAPR4","nombre":"PRUEBA CUATRO","unidadBase":"Pieza","claveSat":"12"}"""), default);
        var existe = await e.EjecutarAsync(Tarea(Operaciones.ProductoLeer, """{"codigo":"PRUEBAPR4"}"""), default);

        Assert.False(r.Exito);
        Assert.False(existe.Exito);
    }

    // ---- direcciones ----

    [Fact]
    public async Task Crear_direccion_fiscal_y_leerla()
    {
        var e = await CrearAsync();

        var crea = await e.EjecutarAsync(Tarea(Operaciones.DireccionCrear,
            """{"codigoCliente":"PRUEBACS1","codigoPostal":"42501","calle":"CALLE PRUEBA","pais":"MEXICO"}"""), default);
        var lee = await e.EjecutarAsync(Tarea(Operaciones.DireccionLeer, """{"codigoCliente":"PRUEBACS1","tipo":"fiscal"}"""), default);

        Assert.True(crea.Exito, crea.Error);
        Assert.True(lee.Exito, lee.Error);
        Assert.Equal("42501", lee.Datos!.Value.GetProperty("codigoPostal").GetString());
        Assert.Equal(0, lee.Datos.Value.GetProperty("tipo").GetInt32());
    }

    [Fact]
    public async Task Guardar_otra_vez_completa_la_direccion_sin_perder_lo_anterior()
    {
        var e = await CrearAsync();
        await e.EjecutarAsync(Tarea(Operaciones.DireccionCrear, """{"codigoCliente":"PRUEBACS1","codigoPostal":"42501","calle":"CALLE UNO"}"""), default);

        var r = await e.EjecutarAsync(Tarea(Operaciones.DireccionCrear, """{"codigoCliente":"PRUEBACS1","codigoPostal":"42501","colonia":"CENTRO"}"""), default);

        Assert.True(r.Exito, r.Error);
        Assert.Equal("CALLE UNO", r.Datos!.Value.GetProperty("calle").GetString());
        Assert.Equal("CENTRO", r.Datos.Value.GetProperty("colonia").GetString());
    }

    [Fact]
    public async Task La_direccion_fiscal_y_la_de_envio_son_distintas()
    {
        var e = await CrearAsync();
        await e.EjecutarAsync(Tarea(Operaciones.DireccionCrear, """{"codigoCliente":"PRUEBACS1","tipo":0,"codigoPostal":"42501"}"""), default);

        var envio = await e.EjecutarAsync(Tarea(Operaciones.DireccionLeer, """{"codigoCliente":"PRUEBACS1","tipo":1}"""), default);

        Assert.False(envio.Exito);
        Assert.Contains("no tiene dirección", envio.Error!);
    }

    [Theory]
    [InlineData("""{"codigoCliente":"PRUEBACS1","codigoPostal":"4250"}""", "5 dígitos")]
    [InlineData("""{"codigoCliente":"PRUEBACS1"}""", "5 dígitos")]
    [InlineData("""{"codigoCliente":"PRUEBACS1","codigoPostal":"42501","tipo":7}""", "fiscal")]
    [InlineData("""{"codigoCliente":"PRUEBACS1","codigoPostal":"42501","tipo":"otro"}""", "fiscal")]
    [InlineData("""{"codigoCliente":"CLIENTEREAL","codigoPostal":"42501"}""", "PRUEBA")]
    public async Task Direccion_invalida_se_rechaza(string payload, string fragmento)
    {
        var e = await CrearAsync();

        var r = await e.EjecutarAsync(Tarea(Operaciones.DireccionCrear, payload), default);

        Assert.False(r.Exito);
        Assert.Contains(fragmento, r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Direccion_de_un_cliente_inexistente_lo_dice()
    {
        var e = await CrearAsync();

        var r = await e.EjecutarAsync(Tarea(Operaciones.DireccionCrear, """{"codigoCliente":"PRUEBANADA","codigoPostal":"42501"}"""), default);

        Assert.False(r.Exito);
        Assert.Contains("No existe el cliente", r.Error!);
    }

    [Fact]
    public async Task Con_la_escritura_apagada_no_se_guarda_ninguna_direccion()
    {
        var e = await CrearAsync(escritura: false);

        var r = await e.EjecutarAsync(Tarea(Operaciones.DireccionCrear, """{"codigoCliente":"PRUEBACS1","codigoPostal":"42501"}"""), default);

        Assert.False(r.Exito);
        Assert.Contains("deshabilitada", r.Error!);
    }

    [Fact]
    public void Las_operaciones_de_datos_cfdi_las_implementa_el_worker_pero_no_se_exponen()
    {
        foreach (var tipo in new[] { Operaciones.ProductoActualizar, Operaciones.DireccionCrear, Operaciones.DireccionLeer })
        {
            // El worker las implementa, pero v1 no las expone en la API.
            Assert.Null(CatalogoOperaciones.Buscar(tipo));
            Assert.DoesNotContain(tipo, Operaciones.Soportadas);
            Assert.Contains(tipo, Operaciones.ImplementadasPorElWorker);
        }

        Assert.True(Operaciones.EsLectura(Operaciones.DireccionLeer));
        Assert.False(Operaciones.EsLectura(Operaciones.DireccionCrear));
    }
}
