using System.Text.Json;
using Acusa.Contrato;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Documentos;
using Acusa.Sdk.Application.Productos;
using Acusa.Sdk.Application.Sesion;
using Acusa.Sdk.Application.Simulado;
using Acusa.Sdk.Worker;

namespace Acusa.Sdk.Tests;

/// <summary>Campos que el facturador externo manda al crear clientes y productos, y la orden de crear factura completa.</summary>
public class AltaClienteProductoCamposTests
{
    private sealed record Escenario(ClienteService Clientes, ProductoService Productos, DocumentoService Documentos, Ejecutor Ejecutor);

    private static Escenario Crear()
    {
        var sesion = new SesionSimulada("EMPRESA DE PRUEBA");
        sesion.Iniciar();
        var politica = new PoliticaEscritura(true, "PRUEBA", true);
        var clientesGw = new ClienteGatewayEnMemoria();
        var productosGw = new ProductoGatewayEnMemoria();
        var clientes = new ClienteService(clientesGw, sesion, politica);
        var productos = new ProductoService(productosGw, sesion, politica);
        var documentos = new DocumentoService(new DocumentoGatewayEnMemoria(clientesGw, productosGw), sesion, politica);
        return new Escenario(clientes, productos, documentos, new Ejecutor(clientes, productos, documentos));
    }

    private static TareaAsignada Tarea(string tipo, object payload) =>
        new("t1", tipo, "EMPRESA", JsonSerializer.SerializeToElement(payload));

    [Fact]
    public async Task Cliente_se_crea_con_rfc_y_tipo()
    {
        var e = Crear();

        var r = await e.Clientes.CrearAsync(new NuevoCliente("PRUEBACS40", "PRUEBA SA", "xaxx010101000", 3, 50000m), default);

        Assert.Equal(TipoResultadoCrear.Creado, r.Tipo);
        Assert.Equal("XAXX010101000", r.Cliente!.Rfc);
        Assert.Equal(3, r.Cliente.Tipo);
    }

    [Theory]
    [InlineData("RFC-MALO", 1, 10, "RFC")]
    [InlineData(null, 0, 10, "tipo de cliente")]
    [InlineData(null, 4, 10, "tipo de cliente")]
    [InlineData(null, 1, -1, "límite de crédito")]
    public async Task Cliente_rechaza_datos_invalidos(string? rfc, int tipo, int limite, string esperado)
    {
        var e = Crear();

        var r = await e.Clientes.CrearAsync(new NuevoCliente("PRUEBACS41", "PRUEBA SA", rfc, tipo, limite), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains(esperado, r.Mensaje);
    }

    [Fact]
    public async Task Producto_se_crea_con_tipo_y_textos_extra()
    {
        var e = Crear();

        var r = await e.Productos.CrearAsync(
            new NuevoProducto("PRUEBAPR40", "PRUEBA SERVICIO", "SERVICIO", 2000m, "Renta", "81112200", 3, ["Texto uno", "  ", "Texto dos"]),
            default);

        Assert.Equal(TipoResultadoCrear.Creado, r.Tipo);
        Assert.Equal(3, r.Producto!.Tipo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Producto_rechaza_un_tipo_fuera_de_rango(int tipo)
    {
        var e = Crear();

        var r = await e.Productos.CrearAsync(new NuevoProducto("PRUEBAPR41", "PRUEBA P", "PIEZA", 1m, Tipo: tipo), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
    }

    [Fact]
    public async Task Producto_rechaza_mas_de_tres_textos_extra()
    {
        var e = Crear();

        var r = await e.Productos.CrearAsync(new NuevoProducto("PRUEBAPR42", "PRUEBA P", "PIEZA", 1m, TextosExtra: ["a", "b", "c", "d"]), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains("textos extra", r.Mensaje);
    }

    [Fact]
    public async Task El_ejecutor_traduce_la_orden_de_cliente_producto_y_factura_completa()
    {
        var e = Crear();

        var cliente = await e.Ejecutor.EjecutarAsync(
            Tarea("cliente.crear", new { codigo = "PRUEBACS50", razonSocial = "PRUEBA SA", rfc = "XAXX010101000", tipoCliente = 1, limiteCredito = 50000 }), default);
        var producto = await e.Ejecutor.EjecutarAsync(
            Tarea("producto.crear", new { codigo = "PRUEBAPR50", nombre = "PRUEBA SERVICIO", unidadBase = "SERVICIO", precio1 = 2000, claveSat = "81112200", tipoProducto = 3, textosExtra = new[] { "Texto" } }), default);
        var factura = await e.Ejecutor.EjecutarAsync(
            Tarea("documento.crear", new
            {
                concepto = "FAC",
                serie = "A",
                clienteCodigo = "PRUEBACS50",
                referencia = "PRUEBA ORDEN 1",
                fecha = "2026-05-14",
                formaPago = "03",
                usoCfdi = "G03",
                partidas = new[] { new { productoCodigo = "PRUEBAPR50", unidades = 2, precio = 2000, ivaPorcentaje = 16, retencionIvaPorcentaje = 0, iepsPorcentaje = (int?)null } },
            }), default);

        Assert.True(cliente.Exito, cliente.Error);
        Assert.True(producto.Exito, producto.Error);
        Assert.True(factura.Exito, factura.Error);
        Assert.Contains("05/14/2026", factura.Datos!.Value.GetProperty("fecha").GetString());
        Assert.Equal(4640m, factura.Datos.Value.GetProperty("total").GetDecimal());
    }

    private sealed class ClaveFija : IProveedorClaveCertificado
    {
        public string? ObtenerClave() => "clave-simulada";
    }

    private static Escenario CrearConEmpresaReal(Func<bool> esReal)
    {
        var sesion = new SesionSimulada("EMPRESA REAL");
        sesion.Iniciar();
        var politica = new PoliticaEscritura(true, "PRUEBA", true, true, null, esReal);
        var clientesGw = new ClienteGatewayEnMemoria();
        var productosGw = new ProductoGatewayEnMemoria();
        var clientes = new ClienteService(clientesGw, sesion, politica);
        var productos = new ProductoService(productosGw, sesion, politica);
        var documentos = new DocumentoService(new DocumentoGatewayEnMemoria(clientesGw, productosGw), sesion, politica, new ClaveFija());
        return new Escenario(clientes, productos, documentos, new Ejecutor(clientes, productos, documentos));
    }

    [Fact]
    public async Task Fuera_de_una_empresa_real_se_sigue_exigiendo_el_prefijo_de_prueba()
    {
        var e = CrearConEmpresaReal(() => false);

        var r = await e.Clientes.CrearAsync(new NuevoCliente("COMPANY001", "EMPRESA SA DE CV"), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
        Assert.Contains("PRUEBA", r.Mensaje);
    }

    [Fact]
    public async Task En_una_empresa_real_no_se_exige_el_prefijo_en_cliente_producto_ni_referencia()
    {
        var e = CrearConEmpresaReal(() => true);

        var cliente = await e.Clientes.CrearAsync(new NuevoCliente("COMPANY001", "EMPRESA SA DE CV", "XAXX010101000"), default);
        var producto = await e.Productos.CrearAsync(new NuevoProducto("SERV001", "Servidor Pro", "SERVICIO", 2000m, ClaveSat: "81112200"), default);
        var factura = await e.Documentos.CrearAsync(
            new NuevoDocumento("FAC", "A", "COMPANY001", string.Empty, [new NuevaPartida("SERV001", 1, 2000m, IvaPorcentaje: 16m)]), default);

        Assert.Equal(TipoResultadoCrear.Creado, cliente.Tipo);
        Assert.Equal(TipoResultadoCrear.Creado, producto.Tipo);
        Assert.Equal(TipoResultadoCrear.Creado, factura.Tipo);
        Assert.Equal(2320m, factura.Documento!.Total);
    }

    [Fact]
    public async Task En_una_empresa_real_se_pueden_timbrar_documentos_sin_prefijo()
    {
        var e = CrearConEmpresaReal(() => true);
        await e.Clientes.CrearAsync(new NuevoCliente("COMPANY002", "EMPRESA SA DE CV"), default);
        await e.Productos.CrearAsync(new NuevoProducto("SERV002", "Servicio", "SERVICIO", 100m), default);
        var creada = await e.Documentos.CrearAsync(
            new NuevoDocumento("FAC", "A", "COMPANY002", "REF-1", [new NuevaPartida("SERV002", 1, 100m)]), default);

        var emitida = await e.Documentos.EmitirAsync(new ReferenciaDocumento(creada.Documento!.Id, "FAC", "A", creada.Documento.Folio), default);

        Assert.NotEqual(TipoResultadoDocumento.Invalido, emitida.Tipo);
        Assert.DoesNotContain("de prueba", emitida.Mensaje ?? string.Empty);
    }

    [Fact]
    public async Task En_una_empresa_real_sigue_rigiendo_el_formato_del_codigo()
    {
        var e = CrearConEmpresaReal(() => true);

        var r = await e.Clientes.CrearAsync(new NuevoCliente("COMPANY-001", "EMPRESA SA DE CV"), default);

        Assert.Equal(TipoResultadoCrear.Invalido, r.Tipo);
    }

    [Fact]
    public void Una_empresa_real_debe_tener_tambien_la_escritura_habilitada()
    {
        var opciones = new Acusa.Sdk.Application.OpcionesContpaqi
        {
            RutaEmpresa = @"C:\Compac\Empresas\adEMPRESA_A",
            PermitirEscritura = true,
            EmpresasEscritura = ["adEMPRESA_A"],
            EmpresasReales = ["adEMPRESA_B"],
        };

        Assert.Contains("EmpresasReales", opciones.ValidarEscritura());
        Assert.False(opciones.EsEmpresaReal("adEMPRESA_A"));
        opciones.EmpresasEscritura = ["adEMPRESA_A", "adEMPRESA_B"];
        Assert.Null(opciones.ValidarEscritura());
        Assert.True(opciones.EsEmpresaReal("adempresa_b"));
    }

    [Fact]
    public async Task El_ejecutor_actualiza_un_cliente_con_limite_de_credito()
    {
        var e = Crear();
        await e.Clientes.CrearAsync(new NuevoCliente("PRUEBACS80", "PRUEBA SA"), default);

        var r = await e.Ejecutor.EjecutarAsync(
            Tarea("cliente.actualizar", new { codigo = "PRUEBACS80", rfc = "xaxx010101000", limiteCredito = 75000 }), default);

        Assert.True(r.Exito, r.Error);
        Assert.Equal("XAXX010101000", r.Datos!.Value.GetProperty("rfc").GetString());
    }

    [Fact]
    public async Task Actualizar_rechaza_un_limite_de_credito_negativo()
    {
        var e = Crear();
        await e.Clientes.CrearAsync(new NuevoCliente("PRUEBACS81", "PRUEBA SA"), default);

        var r = await e.Ejecutor.EjecutarAsync(Tarea("cliente.actualizar", new { codigo = "PRUEBACS81", limiteCredito = -1 }), default);

        Assert.False(r.Exito);
        Assert.Contains("límite de crédito", r.Error);
    }

    [Fact]
    public async Task El_ejecutor_rechaza_una_fecha_con_formato_incorrecto()
    {
        var e = Crear();
        await e.Clientes.CrearAsync(new NuevoCliente("PRUEBACS51", "PRUEBA SA"), default);
        await e.Productos.CrearAsync(new NuevoProducto("PRUEBAPR51", "PRUEBA P", "PIEZA", 1m), default);

        var r = await e.Ejecutor.EjecutarAsync(
            Tarea("documento.crear", new
            {
                concepto = "FAC",
                clienteCodigo = "PRUEBACS51",
                referencia = "PRUEBA ORDEN 2",
                fecha = "14/05/2026",
                partidas = new[] { new { productoCodigo = "PRUEBAPR51", unidades = 1, precio = 1 } },
            }), default);

        Assert.False(r.Exito);
        Assert.Contains("año-mes-día", r.Error);
    }
}
