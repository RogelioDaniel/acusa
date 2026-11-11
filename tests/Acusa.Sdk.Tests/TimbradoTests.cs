using System.Text.Json;
using Acusa.Contrato;
using Acusa.Sdk.Application;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Documentos;
using Acusa.Sdk.Application.Productos;
using Acusa.Sdk.Application.Simulado;
using Acusa.Sdk.Worker;

namespace Acusa.Sdk.Tests;

public class TimbradoTests
{
    private const string Uuid = "0C8E2F3A-7B1D-4E55-9A60-3F2B1C4D5E6F";

    private sealed class ClaveFija(string? clave) : IProveedorClaveCertificado
    {
        public string? ObtenerClave() => clave;
    }

    /// <summary>Gateway que registra lo que recibe, para comprobar qué pasa (y qué no) hasta el SDK.</summary>
    private sealed class GatewayEspia(IDocumentoGateway interno) : IDocumentoGateway
    {
        public List<string> Llamadas { get; } = [];
        public string? ClaveRecibida { get; private set; }
        public string? MotivoRecibido { get; private set; }

        public Task<Documento?> BuscarAsync(string concepto, string serie, long folio, CancellationToken ct) => interno.BuscarAsync(concepto, serie, folio, ct);
        public Task<Documento?> BuscarPorIdAsync(int id, CancellationToken ct) => interno.BuscarPorIdAsync(id, ct);
        public Task<ResultadoCrearDocumento> CrearAsync(NuevoDocumento documento, CancellationToken ct) => interno.CrearAsync(documento, ct);

        public Task<ResultadoDocumento> EmitirAsync(Documento documento, string claveCertificado, CancellationToken ct)
        {
            Llamadas.Add("emitir");
            ClaveRecibida = claveCertificado;
            return interno.EmitirAsync(documento, claveCertificado, ct);
        }

        public Task<ResultadoArchivos> EntregarArchivosAsync(Documento documento, FormatoArchivo formato, bool incluirContenido, CancellationToken ct)
        {
            Llamadas.Add("entregar");
            return interno.EntregarArchivosAsync(documento, formato, incluirContenido, ct);
        }

        public Task<ResultadoDocumento> CancelarAsync(Documento documento, string? motivo, string? uuidSustituto, CancellationToken ct)
        {
            Llamadas.Add("cancelar");
            MotivoRecibido = motivo;
            return interno.CancelarAsync(documento, motivo, uuidSustituto, ct);
        }
    }

    private sealed class Escenario
    {
        public required DocumentoService Servicio { get; init; }
        public required GatewayEspia Gateway { get; init; }
        public required DocumentoGatewayEnMemoria Memoria { get; init; }
    }

    private static async Task<Escenario> CrearAsync(bool escritura = true, bool documentos = true, bool timbrado = true, string? clave = "secreto")
    {
        var clientes = new ClienteGatewayEnMemoria();
        var productos = new ProductoGatewayEnMemoria();
        await clientes.CrearAsync(new NuevoCliente("PRUEBACS12", "PRUEBA CLIENTE"), default);
        await productos.CrearAsync(new NuevoProducto("PRUEBAPR1", "PRUEBA PRODUCTO", "PIEZA", 100m), default);
        var sesion = new SesionSimulada("EMPRESA DE PRUEBA");
        sesion.Iniciar();

        var memoria = new DocumentoGatewayEnMemoria(clientes, productos);
        var espia = new GatewayEspia(memoria);
        return new Escenario
        {
            Memoria = memoria,
            Gateway = espia,
            Servicio = new DocumentoService(espia, sesion, new PoliticaEscritura(escritura, "PRUEBA", documentos, timbrado), new ClaveFija(clave)),
        };
    }

    private static async Task<Documento> NuevoDocumentoAsync(Escenario e, string referencia = "PRUEBA DOC 1")
    {
        var r = await e.Memoria.CrearAsync(new NuevoDocumento("FAC", "A", "PRUEBACS12", referencia, [new NuevaPartida("PRUEBAPR1", 1, 100m)]), default);
        return r.Documento!;
    }

    private static ReferenciaDocumento Ref(Documento d) => new(null, d.Concepto, d.Serie, d.Folio);

    [Fact]
    public async Task Timbrar_asigna_uuid_y_pasa_la_clave_del_worker_al_gateway()
    {
        var e = await CrearAsync(clave: "mi-clave");
        var d = await NuevoDocumentoAsync(e);

        var r = await e.Servicio.EmitirAsync(Ref(d), default);

        Assert.Equal(TipoResultadoDocumento.Hecho, r.Tipo);
        Assert.False(string.IsNullOrWhiteSpace(r.Documento!.Uuid));
        Assert.Equal("mi-clave", e.Gateway.ClaveRecibida);
        Assert.Equal(r.Documento.Uuid, (await e.Memoria.BuscarAsync("FAC", "A", d.Folio, default))!.Uuid);
    }

    [Fact]
    public async Task Timbrar_por_documentoId_funciona_igual_que_por_concepto_serie_y_folio()
    {
        var e = await CrearAsync();
        var d = await NuevoDocumentoAsync(e);

        var r = await e.Servicio.EmitirAsync(new ReferenciaDocumento(d.Id, string.Empty, null, 0), default);

        Assert.Equal(TipoResultadoDocumento.Hecho, r.Tipo);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Con_un_candado_cerrado_no_se_llega_al_gateway(bool escritura, bool documentos, bool timbrado)
    {
        var e = await CrearAsync(escritura, documentos, timbrado);
        var d = await NuevoDocumentoAsync(e);

        var timbra = await e.Servicio.EmitirAsync(Ref(d), default);
        var cancela = await e.Servicio.CancelarAsync(Ref(d), "02", null, default);

        Assert.Equal(TipoResultadoDocumento.Invalido, timbra.Tipo);
        Assert.Equal(TipoResultadoDocumento.Invalido, cancela.Tipo);
        Assert.Empty(e.Gateway.Llamadas);
    }

    [Fact]
    public async Task No_se_timbra_un_documento_que_no_es_de_prueba()
    {
        var e = await CrearAsync();
        var d = await NuevoDocumentoAsync(e, referencia: "FACTURA REAL");

        var r = await e.Servicio.EmitirAsync(Ref(d), default);

        Assert.Equal(TipoResultadoDocumento.Invalido, r.Tipo);
        Assert.Contains("prueba", r.Mensaje!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(e.Gateway.Llamadas);
    }

    [Fact]
    public async Task No_se_timbra_dos_veces_y_el_segundo_intento_no_toca_el_gateway()
    {
        var e = await CrearAsync();
        var d = await NuevoDocumentoAsync(e);
        await e.Servicio.EmitirAsync(Ref(d), default);

        var r = await e.Servicio.EmitirAsync(Ref(d), default);

        Assert.Equal(TipoResultadoDocumento.Invalido, r.Tipo);
        Assert.Contains("ya está timbrado", r.Mensaje!);
        Assert.Single(e.Gateway.Llamadas);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Sin_la_clave_del_certificado_no_se_timbra(string? clave)
    {
        var e = await CrearAsync(clave: clave);
        var d = await NuevoDocumentoAsync(e);

        var r = await e.Servicio.EmitirAsync(Ref(d), default);

        Assert.Equal(TipoResultadoDocumento.Invalido, r.Tipo);
        Assert.Contains("contraseña del certificado", r.Mensaje!);
        Assert.Empty(e.Gateway.Llamadas);
    }

    [Fact]
    public async Task Timbrar_un_documento_inexistente_devuelve_no_encontrado()
    {
        var e = await CrearAsync();

        var r = await e.Servicio.EmitirAsync(new ReferenciaDocumento(null, "FAC", "A", 99), default);

        Assert.Equal(TipoResultadoDocumento.NoEncontrado, r.Tipo);
        Assert.Empty(e.Gateway.Llamadas);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("FAC", 0)]
    [InlineData("FAC", -1)]
    public async Task La_referencia_incompleta_se_rechaza_antes_de_buscar(string concepto, long folio)
    {
        var e = await CrearAsync();

        var r = await e.Servicio.EmitirAsync(new ReferenciaDocumento(null, concepto, "A", folio), default);

        Assert.Equal(TipoResultadoDocumento.Invalido, r.Tipo);
    }

    [Fact]
    public async Task Cancelar_un_timbrado_exige_un_motivo_del_sat()
    {
        var e = await CrearAsync();
        var d = await NuevoDocumentoAsync(e);
        await e.Servicio.EmitirAsync(Ref(d), default);

        foreach (var motivo in new string?[] { null, "", "05", "1", "dos" })
        {
            var r = await e.Servicio.CancelarAsync(Ref(d), motivo, null, default);
            Assert.Equal(TipoResultadoDocumento.Invalido, r.Tipo);
        }

        Assert.Single(e.Gateway.Llamadas); // solo el timbrado
    }

    [Fact]
    public async Task El_motivo_01_pide_el_uuid_que_sustituye_y_los_demas_no_lo_admiten()
    {
        var e = await CrearAsync();
        var d = await NuevoDocumentoAsync(e);
        await e.Servicio.EmitirAsync(Ref(d), default);

        var sinUuid = await e.Servicio.CancelarAsync(Ref(d), "01", null, default);
        var uuidMalo = await e.Servicio.CancelarAsync(Ref(d), "01", "no-es-un-uuid", default);
        var uuidDeMas = await e.Servicio.CancelarAsync(Ref(d), "02", Uuid, default);

        Assert.All(new[] { sinUuid, uuidMalo, uuidDeMas }, r => Assert.Equal(TipoResultadoDocumento.Invalido, r.Tipo));
        Assert.Single(e.Gateway.Llamadas);
    }

    [Theory]
    [InlineData("02", null)]
    [InlineData("03", null)]
    [InlineData("04", null)]
    [InlineData("01", Uuid)]
    public async Task Cancelar_un_timbrado_con_motivo_valido_lo_marca_cancelado(string motivo, string? uuid)
    {
        var e = await CrearAsync();
        var d = await NuevoDocumentoAsync(e);
        await e.Servicio.EmitirAsync(Ref(d), default);

        var r = await e.Servicio.CancelarAsync(Ref(d), motivo, uuid, default);

        Assert.Equal(TipoResultadoDocumento.Hecho, r.Tipo);
        Assert.True(r.Documento!.Cancelado);
        Assert.Equal(motivo, e.Gateway.MotivoRecibido);
    }

    [Fact]
    public async Task Cancelar_un_borrador_sin_timbrar_no_pide_motivo_ni_avisa_al_sat()
    {
        var e = await CrearAsync();
        var d = await NuevoDocumentoAsync(e);

        var r = await e.Servicio.CancelarAsync(Ref(d), null, null, default);

        Assert.Equal(TipoResultadoDocumento.Hecho, r.Tipo);
        Assert.True(r.Documento!.Cancelado);
        Assert.Null(e.Gateway.MotivoRecibido);
    }

    [Fact]
    public async Task Un_documento_cancelado_no_se_cancela_ni_se_timbra_otra_vez()
    {
        var e = await CrearAsync();
        var d = await NuevoDocumentoAsync(e);
        await e.Servicio.CancelarAsync(Ref(d), null, null, default);

        var cancela = await e.Servicio.CancelarAsync(Ref(d), null, null, default);
        var timbra = await e.Servicio.EmitirAsync(Ref(d), default);

        Assert.Equal(TipoResultadoDocumento.Invalido, cancela.Tipo);
        Assert.Equal(TipoResultadoDocumento.Invalido, timbra.Tipo);
        Assert.Single(e.Gateway.Llamadas); // solo la primera cancelación
    }

    [Fact]
    public async Task No_se_cancela_un_documento_que_no_es_de_prueba()
    {
        var e = await CrearAsync();
        var d = await NuevoDocumentoAsync(e, referencia: "FACTURA REAL");

        var r = await e.Servicio.CancelarAsync(Ref(d), "02", null, default);

        Assert.Equal(TipoResultadoDocumento.Invalido, r.Tipo);
        Assert.Empty(e.Gateway.Llamadas);
    }

    // ---- Ejecutor del worker: lo que ve quien manda la orden ----

    private static async Task<(Ejecutor Ejecutor, Escenario Escenario)> EjecutorAsync(bool timbrado = true)
    {
        var e = await CrearAsync(timbrado: timbrado);
        var sesion = new SesionSimulada("EMPRESA DE PRUEBA");
        sesion.Iniciar();
        return (new Ejecutor(null!, null!, e.Servicio), e);
    }

    private static TareaAsignada Tarea(string tipo, string json) =>
        new("t1", tipo, "EMPRESA DE PRUEBA", JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public async Task El_ejecutor_timbra_y_devuelve_el_uuid_en_el_resultado()
    {
        var (ejecutor, e) = await EjecutorAsync();
        var d = await NuevoDocumentoAsync(e);

        var r = await ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitir, $$"""{"concepto":"FAC","serie":"A","folio":{{d.Folio}}}"""), default);

        Assert.True(r.Exito);
        Assert.False(string.IsNullOrWhiteSpace(r.Datos!.Value.GetProperty("uuid").GetString()));
    }

    [Fact]
    public async Task El_ejecutor_acepta_documentoId_como_montek()
    {
        var (ejecutor, e) = await EjecutorAsync();
        var d = await NuevoDocumentoAsync(e);

        var r = await ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitir, $$"""{"documentoId":{{d.Id}}}"""), default);

        Assert.True(r.Exito);
    }

    [Fact]
    public async Task El_ejecutor_cancela_con_motivo_y_uuid_sustituto()
    {
        var (ejecutor, e) = await EjecutorAsync();
        var d = await NuevoDocumentoAsync(e);
        await ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitir, $$"""{"documentoId":{{d.Id}}}"""), default);

        var r = await ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoCancelar, $$"""{"documentoId":{{d.Id}},"motivo":"01","uuidSustituto":"{{Uuid}}"}"""), default);

        Assert.True(r.Exito);
        Assert.True(r.Datos!.Value.GetProperty("documento").GetProperty("cancelado").GetBoolean());
    }

    [Fact]
    public async Task El_ejecutor_explica_por_que_no_timbra_cuando_el_candado_esta_cerrado()
    {
        var (ejecutor, e) = await EjecutorAsync(timbrado: false);
        var d = await NuevoDocumentoAsync(e);

        var r = await ejecutor.EjecutarAsync(Tarea(Operaciones.DocumentoEmitir, $$"""{"documentoId":{{d.Id}}}"""), default);

        Assert.False(r.Exito);
        Assert.Contains("PermitirTimbrado", r.Error!);
    }

    // ---- Catálogo y configuración ----

    [Fact]
    public void Emitir_y_cancelar_estan_disponibles_en_el_catalogo_con_ejemplo()
    {
        foreach (var tipo in new[] { Operaciones.DocumentoEmitir, Operaciones.DocumentoCancelar })
        {
            var info = CatalogoOperaciones.Buscar(tipo)!;
            Assert.NotNull(info.Ejemplo);
            Assert.Contains(tipo, Operaciones.Soportadas);
            Assert.False(Operaciones.EsLectura(tipo)); // son escrituras: nunca se reintentan solas
        }
    }

    [Fact]
    public void El_timbrado_exige_escritura_y_documentos_en_la_configuracion()
    {
        Assert.Null(new OpcionesContpaqi().ValidarTimbrado());
        Assert.NotNull(new OpcionesContpaqi { PermitirTimbrado = true }.ValidarTimbrado());
        Assert.NotNull(new OpcionesContpaqi { PermitirTimbrado = true, PermitirEscritura = true }.ValidarTimbrado());
        Assert.Null(new OpcionesContpaqi { PermitirTimbrado = true, PermitirEscritura = true, PermitirDocumentos = true }.ValidarTimbrado());
    }

    [Fact]
    public void El_timbrado_esta_apagado_por_defecto()
    {
        Assert.False(new OpcionesContpaqi().PermitirTimbrado);
        Assert.False(new PoliticaEscritura(true, "PRUEBA", true).PermitidaTimbrado);
    }
}

public class ClientePermitidoTests
{
    private static NuevoDocumento Doc(string cliente) =>
        new("5", "GEN", cliente, "PRUEBA-FACT", [new NuevaPartida("PRUEBAPR01", 1, 1m)]);

    [Fact]
    public void Un_cliente_real_se_rechaza_si_no_esta_en_la_lista()
    {
        var p = new Acusa.Sdk.Application.Clientes.PoliticaEscritura(true, "PRUEBA", true, true);
        Assert.Contains("PRUEBA", p.Validar(Doc("CLI0042"))!);
    }

    [Fact]
    public void Un_cliente_de_la_lista_pasa_sin_prefijo_pero_el_producto_y_la_referencia_siguen_igual()
    {
        var p = new Acusa.Sdk.Application.Clientes.PoliticaEscritura(true, "PRUEBA", true, true, ["cli0042"]);
        Assert.Null(p.Validar(Doc("CLI0042")));
        Assert.Contains("PRUEBA", p.Validar(Doc("OTRO"))!);
        Assert.Contains("producto", p.Validar(new NuevoDocumento("5", "GEN", "CLI0042", "PRUEBA-FACT", [new NuevaPartida("REAL01", 1, 1m)]))!);
        Assert.Contains("referencia", p.Validar(new NuevoDocumento("5", "GEN", "CLI0042", "FACTURA", [new NuevaPartida("PRUEBAPR01", 1, 1m)]))!);
    }
}

public class MultiempresaTests
{
    [Fact]
    public void LaPrincipalVaPrimeroYNoSeRepiten()
    {
        var o = new Acusa.Sdk.Application.OpcionesContpaqi
        {
            RutaEmpresa = @"C:\Compac\Empresas\adPRINCIPAL",
            Empresas = ["adOTRA", "ADPRINCIPAL", "adTERCERA", " "],
        };
        Assert.Equal(["adPRINCIPAL", "adOTRA", "adTERCERA"], o.EmpresasAtendidas);
        Assert.Equal(@"C:\Compac\Empresas\adOTRA", o.RutaDe("adOTRA"));
    }

    [Fact]
    public void SinListaSoloAtiendeLaPrincipal()
    {
        var o = new Acusa.Sdk.Application.OpcionesContpaqi { RutaEmpresa = @"C:\Compac\Empresas\adUNICA" };
        Assert.Equal(["adUNICA"], o.EmpresasAtendidas);
    }

    [Fact]
    public void SoloEscribeEnLasEmpresasNombradas()
    {
        var o = new Acusa.Sdk.Application.OpcionesContpaqi
        {
            RutaEmpresa = @"C:\Compac\Empresas\adEMPRESA_DE_PRUEBA",
            PermitirEscritura = true,
            EmpresasEscritura = ["adEMPRESA_DE_PRUEBA"],
        };
        Assert.True(o.EscrituraEn("adempresa_de_prueba"));
        Assert.False(o.EscrituraEn("adLOCALSOLUTIONIT"));
        o.PermitirEscritura = false;
        Assert.False(o.EscrituraEn("adEMPRESA_DE_PRUEBA"));
    }

    [Fact]
    public async Task LaSesionSimuladaCambiaDeEmpresa()
    {
        var s = new Acusa.Sdk.Application.Simulado.SesionSimulada(@"C:\Compac\Empresas\adUNO");
        s.Iniciar();
        Assert.Null(await s.CambiarEmpresaAsync(@"C:\Compac\Empresas\adDOS"));
        Assert.Equal(@"C:\Compac\Empresas\adDOS", s.Estado.Empresa);
    }
}
