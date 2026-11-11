using System.Text.Json;
using Acusa.Contrato;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Documentos;
using Acusa.Sdk.Application.Productos;
using Acusa.Sdk.Application.Simulado;
using Acusa.Sdk.Worker;

namespace Acusa.Sdk.Tests;

public class LoteYActualizarTests
{
    private sealed class ClaveFija : IProveedorClaveCertificado
    {
        public string? ObtenerClave() => "secreto";
    }

    private sealed class Mundo
    {
        public required Ejecutor Ejecutor { get; init; }
        public required ClienteService Clientes { get; init; }
        public required DocumentoGatewayEnMemoria Documentos { get; init; }
    }

    private static async Task<Mundo> CrearAsync(bool escritura = true)
    {
        var clientesGw = new ClienteGatewayEnMemoria();
        var productosGw = new ProductoGatewayEnMemoria();
        await clientesGw.CrearAsync(new NuevoCliente("PRUEBACS1", "PRUEBA CLIENTE"), default);
        await productosGw.CrearAsync(new NuevoProducto("PRUEBAPR1", "PRUEBA PRODUCTO", "PIEZA", 100m), default);
        var sesion = new SesionSimulada("EMPRESA DE PRUEBA");
        sesion.Iniciar();
        var politica = new PoliticaEscritura(escritura, "PRUEBA", true, true);
        var documentosGw = new DocumentoGatewayEnMemoria(clientesGw, productosGw);
        var clientes = new ClienteService(clientesGw, sesion, politica);
        var documentos = new DocumentoService(documentosGw, sesion, politica, new ClaveFija());
        return new Mundo { Ejecutor = new Ejecutor(clientes, null!, documentos), Clientes = clientes, Documentos = documentosGw };
    }

    private static async Task<int[]> DocumentosAsync(Mundo m, int cuantos, string referencia = "PRUEBA DOC")
    {
        var ids = new List<int>();
        for (var i = 0; i < cuantos; i++)
        {
            var r = await m.Documentos.CrearAsync(new NuevoDocumento("FAC", "A", "PRUEBACS1", referencia, [new NuevaPartida("PRUEBAPR1", 1, 100m)]), default);
            ids.Add(r.Documento!.Id);
        }

        return [.. ids];
    }

    private static TareaAsignada Tarea(string tipo, string json) =>
        new("t", tipo, "EMPRESA", JsonDocument.Parse(json).RootElement.Clone());

    private static string Lote(IEnumerable<int> ids, string extra = "") =>
        "{\"documentos\":[" + string.Join(",", ids.Select(id => $"{{\"documentoId\":{id}{extra}}}")) + "]}";

    // ---- cliente.actualizar ----

    [Fact]
    public async Task Actualizar_cambia_solo_los_campos_con_valor()
    {
        var m = await CrearAsync();

        var r = await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.ClienteActualizar,
            """{"codigo":"pruebacs1","rfc":"eku9003173c9","regimenFiscal":"601","usoCfdi":"g03"}"""), default);

        Assert.True(r.Exito, r.Error);
        var c = r.Datos!.Value;
        Assert.Equal("EKU9003173C9", c.GetProperty("rfc").GetString());
        Assert.Equal("601", c.GetProperty("regimenFiscal").GetString());
        Assert.Equal("G03", c.GetProperty("usoCfdi").GetString());
        Assert.Equal("PRUEBA CLIENTE", c.GetProperty("razonSocial").GetString()); // no se tocó
    }

    [Fact]
    public async Task Actualizar_permite_la_razon_social_real_de_un_rfc_de_prueba()
    {
        var m = await CrearAsync();

        var r = await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.ClienteActualizar,
            """{"codigo":"PRUEBACS1","razonSocial":"ESCUELA KEMPER URGATE"}"""), default);

        Assert.True(r.Exito, r.Error);
        Assert.Equal("ESCUELA KEMPER URGATE", r.Datos!.Value.GetProperty("razonSocial").GetString());
    }

    [Theory]
    [InlineData("""{"codigo":"CLIENTEREAL","rfc":"EKU9003173C9"}""", "PRUEBA")]
    [InlineData("""{"codigo":"PRUEBACS1"}""", "nada que cambiar")]
    [InlineData("""{"codigo":"PRUEBACS1","rfc":"corto"}""", "RFC")]
    [InlineData("""{"codigo":"PRUEBACS1","regimenFiscal":"ABC"}""", "régimen")]
    [InlineData("""{"codigo":"PRUEBACS1","usoCfdi":"x"}""", "uso de CFDI")]
    [InlineData("""{"codigo":"PRUEBACS1","email":"sin-arroba"}""", "correo")]
    public async Task Actualizar_rechaza_datos_invalidos_y_clientes_que_no_son_de_prueba(string payload, string fragmento)
    {
        var m = await CrearAsync();

        var r = await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.ClienteActualizar, payload), default);

        Assert.False(r.Exito);
        Assert.Contains(fragmento, r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Actualizar_un_cliente_inexistente_lo_dice_y_con_escritura_apagada_no_modifica()
    {
        var m = await CrearAsync();
        var noExiste = await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.ClienteActualizar, """{"codigo":"PRUEBANADA","rfc":"EKU9003173C9"}"""), default);
        Assert.False(noExiste.Exito);
        Assert.Contains("No existe", noExiste.Error!);

        var solo = await CrearAsync(escritura: false);
        var apagado = await solo.Ejecutor.EjecutarAsync(Tarea(Operaciones.ClienteActualizar, """{"codigo":"PRUEBACS1","rfc":"EKU9003173C9"}"""), default);
        Assert.False(apagado.Exito);
        Assert.Contains("deshabilitada", apagado.Error!);
    }

    // ---- lotes ----

    [Fact]
    public async Task El_lote_timbra_todos_los_documentos_y_detalla_cada_uno()
    {
        var m = await CrearAsync();
        var ids = await DocumentosAsync(m, 5);

        var r = await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitirLote, Lote(ids)), default);

        Assert.True(r.Exito, r.Error);
        var d = r.Datos!.Value;
        Assert.Equal(5, d.GetProperty("total").GetInt32());
        Assert.Equal(5, d.GetProperty("correctos").GetInt32());
        Assert.Equal(0, d.GetProperty("fallidos").GetInt32());
        var uuids = d.GetProperty("resultados").EnumerateArray().Select(x => x.GetProperty("uuid").GetString()).ToList();
        Assert.Equal(5, uuids.Distinct().Count());
        Assert.All(uuids, u => Assert.False(string.IsNullOrWhiteSpace(u)));
    }

    [Fact]
    public async Task Si_uno_falla_el_lote_sigue_con_el_resto_y_avisa()
    {
        var m = await CrearAsync();
        var buenos = await DocumentosAsync(m, 2);
        var real = await DocumentosAsync(m, 1, referencia: "FACTURA REAL");
        var ids = new[] { buenos[0], real[0], buenos[1], 9999 };

        var r = await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitirLote, Lote(ids)), default);

        Assert.False(r.Exito);
        Assert.Contains("2 de 4", r.Error!);
        var d = r.Datos!.Value;
        Assert.Equal(2, d.GetProperty("correctos").GetInt32());
        var detalle = d.GetProperty("resultados").EnumerateArray().ToList();
        Assert.True(detalle[0].GetProperty("ok").GetBoolean());
        Assert.False(detalle[1].GetProperty("ok").GetBoolean()); // no es de prueba
        Assert.True(detalle[2].GetProperty("ok").GetBoolean()); // sigue después del fallo
        Assert.Contains("No existe", detalle[3].GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task Reenviar_un_lote_no_vuelve_a_timbrar_lo_ya_timbrado()
    {
        var m = await CrearAsync();
        var ids = await DocumentosAsync(m, 3);
        await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitirLote, Lote(ids)), default);

        var otra = await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitirLote, Lote(ids)), default);

        Assert.False(otra.Exito);
        Assert.Equal(0, otra.Datos!.Value.GetProperty("correctos").GetInt32());
        Assert.All(otra.Datos.Value.GetProperty("resultados").EnumerateArray(),
            x => Assert.Contains("ya está timbrado", x.GetProperty("error").GetString()!));
    }

    [Fact]
    public async Task El_lote_de_cancelacion_aplica_el_motivo_de_cada_documento()
    {
        var m = await CrearAsync();
        var ids = await DocumentosAsync(m, 3);
        await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitirLote, Lote(ids)), default);

        var r = await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoCancelarLote, Lote(ids, ",\"motivo\":\"02\"")), default);

        Assert.True(r.Exito, r.Error);
        Assert.All(r.Datos!.Value.GetProperty("resultados").EnumerateArray(), x => Assert.True(x.GetProperty("cancelado").GetBoolean()));
    }

    [Fact]
    public async Task La_cancelacion_en_lote_sin_motivo_falla_en_cada_timbrado()
    {
        var m = await CrearAsync();
        var ids = await DocumentosAsync(m, 2);
        await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitirLote, Lote(ids)), default);

        var r = await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoCancelarLote, Lote(ids)), default);

        Assert.False(r.Exito);
        Assert.Equal(0, r.Datos!.Value.GetProperty("correctos").GetInt32());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"documentos":"no es lista"}""")]
    [InlineData("""{"documentos":[]}""")]
    public async Task Un_lote_vacio_o_mal_formado_se_rechaza(string payload)
    {
        var m = await CrearAsync();

        var r = await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitirLote, payload), default);

        Assert.False(r.Exito);
        Assert.Null(r.Datos);
    }

    [Fact]
    public async Task Un_lote_mayor_al_maximo_se_rechaza_completo_sin_timbrar_nada()
    {
        var m = await CrearAsync();
        var ids = await DocumentosAsync(m, Operaciones.MaximoDocumentosPorLote + 1);

        var r = await m.Ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitirLote, Lote(ids)), default);

        Assert.False(r.Exito);
        Assert.Contains("entre 1 y", r.Error!);
        Assert.Null((await m.Documentos.BuscarPorIdAsync(ids[0], default))!.Uuid);
    }

    [Fact]
    public void Las_operaciones_de_lote_las_implementa_el_worker_pero_no_se_exponen_y_son_escrituras()
    {
        foreach (var tipo in new[] { Operaciones.DocumentoEmitirLote, Operaciones.DocumentoCancelarLote })
        {
            Assert.Null(CatalogoOperaciones.Buscar(tipo));
            Assert.DoesNotContain(tipo, Operaciones.Soportadas);
            Assert.Contains(tipo, Operaciones.ImplementadasPorElWorker);
            Assert.False(Operaciones.EsLectura(tipo));
        }
    }

    [Fact]
    public void Cliente_actualizar_se_expone_en_la_api_y_es_una_escritura()
    {
        Assert.NotNull(CatalogoOperaciones.Buscar(Operaciones.ClienteActualizar));
        Assert.Contains(Operaciones.ClienteActualizar, Operaciones.Soportadas);
        Assert.False(Operaciones.EsLectura(Operaciones.ClienteActualizar));
    }
}
