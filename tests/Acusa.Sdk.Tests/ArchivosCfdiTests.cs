using System.Text;
using System.Text.Json;
using Acusa.Contrato;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Documentos;
using Acusa.Sdk.Application.Productos;
using Acusa.Sdk.Application.Simulado;
using Acusa.Sdk.Worker;

namespace Acusa.Sdk.Tests;

/// <summary>documento.obtener-archivo: XML y PDF de un CFDI ya timbrado.</summary>
public class ArchivosCfdiTests
{
    private sealed class ClaveFija : IProveedorClaveCertificado
    {
        public string? ObtenerClave() => "secreto";
    }

    private sealed class Mundo
    {
        public required Ejecutor Ejecutor { get; init; }
        public required DocumentoGatewayEnMemoria Documentos { get; init; }
        public required DocumentoService Servicio { get; init; }
    }

    private static async Task<Mundo> CrearAsync(bool timbrado = true)
    {
        var clientes = new ClienteGatewayEnMemoria();
        var productos = new ProductoGatewayEnMemoria();
        await clientes.CrearAsync(new NuevoCliente("PRUEBACS1", "PRUEBA CLIENTE"), default);
        await productos.CrearAsync(new NuevoProducto("PRUEBAPR1", "PRUEBA PRODUCTO", "PIEZA", 100m), default);
        var sesion = new SesionSimulada("EMPRESA DE PRUEBA");
        sesion.Iniciar();
        var politica = new PoliticaEscritura(true, "PRUEBA", true, timbrado);
        var gw = new DocumentoGatewayEnMemoria(clientes, productos);
        var documentos = new DocumentoService(gw, sesion, politica, new ClaveFija());
        return new Mundo
        {
            Ejecutor = new Ejecutor(new ClienteService(clientes, sesion, politica), null!, documentos),
            Documentos = gw,
            Servicio = documentos,
        };
    }

    private static async Task<Documento> NuevoAsync(Mundo m, bool timbrar)
    {
        var r = await m.Documentos.CrearAsync(new NuevoDocumento("FAC", "A", "PRUEBACS1", "PRUEBA DOC", [new NuevaPartida("PRUEBAPR1", 1, 100m)]), default);
        var d = r.Documento!;
        if (timbrar)
        {
            d = (await m.Servicio.EmitirAsync(new ReferenciaDocumento(d.Id, string.Empty, null, 0), default)).Documento!;
        }

        return d;
    }

    private static TareaAsignada Tarea(string json) =>
        new("t", Operaciones.DocumentoObtenerArchivo, "EMPRESA", JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public async Task Pide_xml_y_pdf_de_un_documento_timbrado_con_el_contenido_en_base64()
    {
        var m = await CrearAsync();
        var d = await NuevoAsync(m, timbrar: true);

        var r = await m.Ejecutor.EjecutarAsync(Tarea($$"""{"documentoId":{{d.Id}},"formato":"ambos","incluirContenido":true}"""), default);

        Assert.True(r.Exito, r.Error);
        var archivos = r.Datos!.Value.GetProperty("archivos").EnumerateArray().ToList();
        Assert.Equal(2, archivos.Count);
        Assert.Equal(["xml", "pdf"], archivos.Select(a => a.GetProperty("tipo").GetString()).ToArray());
        var xml = Encoding.UTF8.GetString(Convert.FromBase64String(archivos[0].GetProperty("base64").GetString()!));
        Assert.Contains(d.Uuid!, xml);
        Assert.Equal(d.Uuid, r.Datos.Value.GetProperty("uuid").GetString());
    }

    [Theory]
    [InlineData("xml", new[] { "xml" })]
    [InlineData("pdf", new[] { "pdf" })]
    [InlineData("ambos", new[] { "xml", "pdf" })]
    public async Task El_formato_decide_que_archivos_se_entregan(string formato, string[] esperados)
    {
        var m = await CrearAsync();
        var d = await NuevoAsync(m, timbrar: true);

        var r = await m.Ejecutor.EjecutarAsync(Tarea($$"""{"documentoId":{{d.Id}},"formato":"{{formato}}"}"""), default);

        Assert.True(r.Exito, r.Error);
        Assert.Equal(esperados, r.Datos!.Value.GetProperty("archivos").EnumerateArray().Select(a => a.GetProperty("tipo").GetString()).ToArray());
    }

    [Fact]
    public async Task Sin_incluirContenido_solo_devuelve_la_ruta()
    {
        var m = await CrearAsync();
        var d = await NuevoAsync(m, timbrar: true);

        var r = await m.Ejecutor.EjecutarAsync(Tarea($$"""{"documentoId":{{d.Id}},"formato":"xml"}"""), default);

        Assert.True(r.Exito, r.Error);
        var a = r.Datos!.Value.GetProperty("archivos")[0];
        Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("ruta").GetString()));
        Assert.Equal(JsonValueKind.Null, a.GetProperty("base64").ValueKind);
    }

    [Fact]
    public async Task Un_documento_sin_timbrar_no_tiene_archivos()
    {
        var m = await CrearAsync();
        var d = await NuevoAsync(m, timbrar: false);

        var r = await m.Ejecutor.EjecutarAsync(Tarea($$"""{"documentoId":{{d.Id}}}"""), default);

        Assert.False(r.Exito);
        Assert.Contains("no está timbrado", r.Error!);
    }

    [Theory]
    [InlineData("""{"documentoId":1,"formato":"docx"}""", "xml, pdf o ambos")]
    [InlineData("""{"concepto":"FAC","serie":"A","folio":999}""", "No existe")]
    [InlineData("""{}""", "documentoId")]
    public async Task Pide_bien_o_explica_que_falta(string payload, string fragmento)
    {
        var m = await CrearAsync();

        var r = await m.Ejecutor.EjecutarAsync(Tarea(payload), default);

        Assert.False(r.Exito);
        Assert.Contains(fragmento, r.Error!);
    }

    [Fact]
    public async Task Obtener_archivos_no_necesita_el_candado_de_timbrado()
    {
        var m = await CrearAsync(timbrado: true);
        var d = await NuevoAsync(m, timbrar: true);
        var cerrado = await CrearAsyncSinTimbrado(m, d);

        var r = await cerrado.ObtenerArchivosAsync(new ReferenciaDocumento(d.Id, string.Empty, null, 0), FormatoArchivo.Xml, false, default);

        Assert.Equal(TipoResultadoDocumento.Hecho, r.Tipo);
    }

    // El mismo gateway (con el documento ya timbrado) pero con el timbrado deshabilitado en la política.
    private static Task<DocumentoService> CrearAsyncSinTimbrado(Mundo m, Documento _)
    {
        var sesion = new SesionSimulada("EMPRESA DE PRUEBA");
        sesion.Iniciar();
        return Task.FromResult(new DocumentoService(m.Documentos, sesion, new PoliticaEscritura(true, "PRUEBA", true, false), new ClaveFija()));
    }

    [Fact]
    public void La_operacion_esta_disponible_y_es_segura_de_repetir()
    {
        Assert.NotNull(CatalogoOperaciones.Buscar(Operaciones.DocumentoObtenerArchivo));
        Assert.Contains(Operaciones.DocumentoObtenerArchivo, Operaciones.Soportadas);
        Assert.True(Operaciones.EsLectura(Operaciones.DocumentoObtenerArchivo));
        Assert.True(Operaciones.EsLectura(Operaciones.DocumentoLeer));
        Assert.False(Operaciones.EsLectura(Operaciones.DocumentoEmitir));
    }
}
