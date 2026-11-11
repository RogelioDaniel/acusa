using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Documentos;
using Acusa.Sdk.Application.Productos;
using Acusa.Sdk.Application.Sesion;
using Acusa.Sdk.Application.Simulado;

namespace Acusa.Sdk.Tests;

public class DocumentoServiceTests
{
    private sealed class Escenario
    {
        public required DocumentoService Documentos { get; init; }
        public required ClienteGatewayEnMemoria Clientes { get; init; }
        public required ProductoGatewayEnMemoria Productos { get; init; }
    }

    private static async Task<Escenario> CrearAsync(bool escritura = true, bool documentos = true, bool sesionLista = true, bool conDatos = true)
    {
        var clientes = new ClienteGatewayEnMemoria();
        var productos = new ProductoGatewayEnMemoria();
        if (conDatos)
        {
            await clientes.CrearAsync(new NuevoCliente("PRUEBACS12", "PRUEBA CLIENTE"), default);
            await productos.CrearAsync(new NuevoProducto("PRUEBAPR1", "PRUEBA PRODUCTO", "PIEZA", 100m), default);
        }

        var sesion = new SesionSimulada("EMPRESA DE PRUEBA");
        if (sesionLista)
        {
            sesion.Iniciar();
        }

        return new Escenario
        {
            Clientes = clientes,
            Productos = productos,
            Documentos = new DocumentoService(
                new DocumentoGatewayEnMemoria(clientes, productos),
                sesion,
                new PoliticaEscritura(escritura, "PRUEBA", documentos)),
        };
    }

    private static NuevoDocumento Valido(string cliente = "PRUEBACS12", string producto = "PRUEBAPR1") =>
        new("FAC", "A", cliente, "PRUEBA DOC 1", [new NuevaPartida(producto, 2, 100m)]);

    [Fact]
    public async Task Crear_con_datos_validos_devuelve_el_documento_sin_timbrar()
    {
        var e = await CrearAsync();

        var r = await e.Documentos.CrearAsync(Valido(), default);

        Assert.Equal(TipoResultadoCrear.Creado, r.Tipo);
        Assert.Equal("FAC", r.Documento!.Concepto);
        Assert.Equal("A", r.Documento.Serie);
        Assert.Equal(1, r.Documento.Folio);
        Assert.Equal("PRUEBACS12", r.Documento.ClienteCodigo);
        Assert.Equal("PRUEBA DOC 1", r.Documento.Referencia);
        Assert.Equal(200m, r.Documento.Neto);
        Assert.Equal(232m, r.Documento.Total);
        Assert.False(r.Documento.Cancelado);
        Assert.Null(r.Documento.Uuid);
    }

    [Fact]
    public async Task Crear_usa_la_fecha_indicada()
    {
        var e = await CrearAsync();

        var r = await e.Documentos.CrearAsync(Valido() with { Fecha = new DateOnly(2026, 5, 14) }, default);

        Assert.Equal(TipoResultadoCrear.Creado, r.Tipo);
        Assert.StartsWith("05/14/2026", r.Documento!.Fecha);
    }

    [Fact]
    public async Task Crear_con_impuestos_por_partida_calcula_el_total_con_ellos()
    {
        var e = await CrearAsync();
        var partida = new NuevaPartida("PRUEBAPR1", 2, 100m, IvaPorcentaje: 16m, RetencionIvaPorcentaje: 10.6666m, IepsPorcentaje: 8m);

        var r = await e.Documentos.CrearAsync(Valido() with { Partidas = [partida] }, default);

        Assert.Equal(TipoResultadoCrear.Creado, r.Tipo);
        Assert.Equal(200m, r.Documento!.Neto);
        Assert.Equal(200m + 200m * (16m + 8m - 10.6666m) / 100m, r.Documento.Total, 2);
    }

    [Fact]
    public async Task Crear_con_impuesto_en_cero_no_suma_iva()
    {
        var e = await CrearAsync();
        var partida = new NuevaPartida("PRUEBAPR1", 2, 100m, IvaPorcentaje: 0m);

        var r = await e.Documentos.CrearAsync(Valido() with { Partidas = [partida] }, default);

        Assert.Equal(200m, r.Documento!.Total);
    }

    [Theory]
    [InlineData(-1, null, null)]
    [InlineData(101, null, null)]
    [InlineData(null, 150, null)]
    [InlineData(null, null, -5)]
    public async Task Crear_rechaza_porcentajes_fuera_de_rango(int? iva, int? retencion, int? ieps)
    {
        var e = await CrearAsync();
        var partida = new NuevaPartida("PRUEBAPR1", 1, 100m, IvaPorcentaje: iva, RetencionIvaPorcentaje: retencion, IepsPorcentaje: ieps);

        var r = await e.Documentos.CrearAsync(Valido() with { Partidas = [partida] }, default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains("entre 0 y 100", r.Mensaje);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("ABC")]
    [InlineData("001")]
    public async Task Crear_rechaza_una_forma_de_pago_que_no_es_clave_sat(string forma)
    {
        var e = await CrearAsync();

        var r = await e.Documentos.CrearAsync(Valido() with { FormaPago = forma }, default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains("forma de pago", r.Mensaje);
    }

    [Fact]
    public async Task Crear_acepta_forma_de_pago_y_uso_de_cfdi_validos()
    {
        var e = await CrearAsync();

        var r = await e.Documentos.CrearAsync(Valido() with { FormaPago = "03", UsoCfdi = "g03" }, default);

        Assert.Equal(TipoResultadoCrear.Creado, r.Tipo);
    }

    [Fact]
    public async Task Crear_rechaza_una_fecha_muy_lejana()
    {
        var e = await CrearAsync();

        var r = await e.Documentos.CrearAsync(Valido() with { Fecha = DateOnly.FromDateTime(DateTime.Today.AddDays(500)) }, default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains("400 días", r.Mensaje);
    }

    [Fact]
    public async Task Cada_documento_consume_el_siguiente_folio_de_su_concepto_y_serie()
    {
        var e = await CrearAsync();

        var primero = await e.Documentos.CrearAsync(Valido(), default);
        var segundo = await e.Documentos.CrearAsync(Valido(), default);
        var otraSerie = await e.Documentos.CrearAsync(Valido() with { Serie = "B" }, default);

        Assert.Equal(1, primero.Documento!.Folio);
        Assert.Equal(2, segundo.Documento!.Folio);
        Assert.Equal(1, otraSerie.Documento!.Folio);
    }

    [Fact]
    public async Task Los_documentos_estan_apagados_por_defecto_aunque_la_escritura_este_activa()
    {
        var e = await CrearAsync(documentos: false);

        var r = await e.Documentos.CrearAsync(Valido(), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains("PermitirDocumentos", r.Mensaje);
    }

    [Fact]
    public async Task Sin_escritura_se_rechaza_aunque_los_documentos_esten_activos()
    {
        var e = await CrearAsync(escritura: false);

        var r = await e.Documentos.CrearAsync(Valido(), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains("PermitirEscritura", r.Mensaje);
    }

    [Fact]
    public async Task Rechaza_clientes_y_productos_que_no_son_de_prueba()
    {
        var e = await CrearAsync();

        var cliente = await e.Documentos.CrearAsync(Valido(cliente: "CLIENTEREAL"), default);
        var producto = await e.Documentos.CrearAsync(Valido(producto: "PRODREAL"), default);

        Assert.Equal(TipoResultadoCrear.Invalido, cliente.Tipo);
        Assert.Contains("código de cliente", cliente.Mensaje);
        Assert.Equal(TipoResultadoCrear.Invalido, producto.Tipo);
        Assert.Contains("producto de la partida 1", producto.Mensaje);
    }

    [Theory]
    [InlineData("", "A", "PRUEBA X", 2, 100, "concepto es obligatorio")]
    [InlineData("FAC", "A", "", 2, 100, "referencia es obligatoria")]
    [InlineData("FAC", "A", "FACTURA REAL", 2, 100, "referencia debe empezar con PRUEBA")]
    [InlineData("FAC", "A", "PRUEBA X", 0, 100, "unidades de la partida 1")]
    [InlineData("FAC", "A", "PRUEBA X", 2, -5, "precio de la partida 1")]
    [InlineData("FAC", "SERIEMUYLARGA1", "PRUEBA X", 2, 100, "serie admite")]
    public async Task Rechaza_datos_invalidos(string concepto, string serie, string referencia, int unidades, int precio, string fragmento)
    {
        var e = await CrearAsync();
        var doc = new NuevoDocumento(concepto, serie, "PRUEBACS12", referencia, [new NuevaPartida("PRUEBAPR1", unidades, precio)]);

        var r = await e.Documentos.CrearAsync(doc, default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains(fragmento, r.Mensaje);
    }

    [Fact]
    public async Task Exige_entre_una_y_cincuenta_partidas()
    {
        var e = await CrearAsync();
        var sinPartidas = Valido() with { Partidas = [] };
        var demasiadas = Valido() with { Partidas = Enumerable.Range(0, 51).Select(_ => new NuevaPartida("PRUEBAPR1", 1, 1)).ToList() };

        Assert.Equal(TipoResultadoCrear.Invalido, (await e.Documentos.CrearAsync(sinPartidas, default)).Tipo);
        Assert.Equal(TipoResultadoCrear.Invalido, (await e.Documentos.CrearAsync(demasiadas, default)).Tipo);
    }

    [Fact]
    public async Task Un_cliente_o_producto_inexistente_es_invalido_y_no_consume_folio()
    {
        var e = await CrearAsync();

        var sinCliente = await e.Documentos.CrearAsync(Valido(cliente: "PRUEBANOEXISTE"), default);
        var sinProducto = await e.Documentos.CrearAsync(Valido(producto: "PRUEBANOEXISTE"), default);
        var bueno = await e.Documentos.CrearAsync(Valido(), default);

        Assert.Equal(TipoResultadoCrear.Invalido, sinCliente.Tipo);
        Assert.Equal(TipoResultadoCrear.Invalido, sinProducto.Tipo);
        Assert.Equal(1, bueno.Documento!.Folio);
    }

    [Fact]
    public async Task Obtener_devuelve_el_documento_creado_y_null_si_no_existe()
    {
        var e = await CrearAsync();
        var creado = (await e.Documentos.CrearAsync(Valido(), default)).Documento!;

        var leido = await e.Documentos.ObtenerAsync("fac", "a", creado.Folio, default);

        Assert.Equal(creado, leido);
        Assert.Null(await e.Documentos.ObtenerAsync("FAC", "A", 99, default));
    }

    [Fact]
    public async Task Sin_sesion_lista_lanza_sdk_no_listo_pero_la_validacion_va_primero()
    {
        var e = await CrearAsync(sesionLista: false);

        await Assert.ThrowsAsync<SdkNoListoException>(() => e.Documentos.ObtenerAsync("FAC", "A", 1, default));
        await Assert.ThrowsAsync<SdkNoListoException>(() => e.Documentos.CrearAsync(Valido(), default));
        Assert.Equal(TipoResultadoCrear.Invalido, (await e.Documentos.CrearAsync(Valido(cliente: "REAL"), default)).Tipo);
    }
}
