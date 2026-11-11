using System.Text.Json;
using System.Text.Json.Nodes;
using Acusa.Orquestador;
using Acusa.Orquestador.Facturacion;

namespace Acusa.Sdk.Tests;

/// <summary>Las rutas de Alain (compañías, productos, facturas, partidas) y cómo se traducen a órdenes para el worker.</summary>
public sealed class FacturacionRutasTests : IDisposable
{
    private sealed record Llamada(string Empresa, string Tipo, JsonElement Payload, string? Llave);

    private sealed class EjecutorFalso : IEjecutorOrdenes
    {
        public List<Llamada> Llamadas { get; } = [];
        public Dictionary<string, Func<int, ResultadoOrden>> Respuestas { get; } = [];
        private int _n;

        public Task<ResultadoOrden> EjecutarAsync(string clienteId, string empresa, string tipo, object payload, string? llave, CancellationToken ct)
        {
            var json = JsonSerializer.SerializeToElement(payload);
            Llamadas.Add(new Llamada(empresa, tipo, json, llave));
            _n++;
            if (Respuestas.TryGetValue(tipo, out var f)) return Task.FromResult(f(_n));
            JsonElement? datos = tipo switch
            {
                "documento.crear" => Json("""{"id":41,"folio":7,"serie":"A"}"""),
                "documento.emitir" => Json("""{"uuid":"AAAAAAAA-0000-4000-8000-000000000001","documento":{"folio":7}}"""),
                "documento.obtener-archivo" => Json("""{"archivos":[{"tipo":"xml","base64":"PHhtbC8+"},{"tipo":"pdf","base64":"JVBERg=="}]}"""),
                _ => Json("{}"),
            };
            return Task.FromResult(new ResultadoOrden(true, datos, null, null, false, "t" + _n));
        }

        public static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();
        public static ResultadoOrden Falla(string error) => new(false, null, error, null, false, "tf");
        public static ResultadoOrden EnCurso() => new(false, null, "tarde", null, true, "tc");
        public IEnumerable<string> Tipos => Llamadas.Select(l => l.Tipo);
    }

    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"fac-{Guid.NewGuid():N}.db");
    private readonly Almacen _almacen;
    private readonly DocStore _store;
    private readonly EjecutorFalso _ejecutor = new();
    private readonly FacturacionServicio _s;
    private readonly string _c;

    public FacturacionRutasTests()
    {
        _almacen = new Almacen(_ruta);
        _store = new DocStore(_ruta);
        _s = new FacturacionServicio(_store, _almacen, _ejecutor, new Opciones());
        _c = _almacen.CrearCliente("Cliente de prueba");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_ruta)!, Path.GetFileName(_ruta) + "*")) File.Delete(f);
    }

    private static JsonNode Cuerpo(string json) => JsonNode.Parse(json)!;
    private static string Texto(object? cuerpo, string campo) => JsonSerializer.SerializeToElement(cuerpo).GetProperty(campo).ToString();
    private static JsonElement Elemento(object? cuerpo) => JsonSerializer.SerializeToElement(cuerpo);

    /// <summary>Los mensajes de error ya decodificados (el JSON escribe el apóstrofo como ').</summary>
    private static string Errores(object? cuerpo) =>
        string.Join(" | ", Elemento(cuerpo).GetProperty("errors").EnumerateArray().Select(e => e.GetString()));

    private int Emisora(string db = "adEMPRESA_DE_PRUEBA")
    {
        var r = _s.CrearEmisora(_c, Cuerpo("{\"issuing_company\":{\"social_reason\":\"EMISORA SA\",\"rfc\":\"EKU9003173C9\",\"database_name\":\"" + db + "\"}}"));
        Assert.Equal(200, r.Status);
        return int.Parse(Texto(r.Body, "id"));
    }

    private async Task<int> Compania()
    {
        var r = await _s.CrearCompaniaAsync(_c, Cuerpo("""{"company":{"client_type":1,"credit_limit":50000,"rfc":"XAXX010101000","social_reason":"PRUEBA CLIENTE SA"}}"""), default);
        Assert.Equal(201, r.Status);
        return int.Parse(Texto(r.Body, "id"));
    }

    private async Task<int> ProductoCreado()
    {
        var r = await _s.CrearProductoAsync(_c, Cuerpo("""{"product":{"name":"PRUEBA SERVICIO","price":"2000.00","description":"Renta","unit_base":"Pieza","series_sat":"81112200","product_type":3,"extra_texts_attributes":[{"extra_text":"Texto uno"}]}}"""), default);
        Assert.Equal(201, r.Status);
        return int.Parse(Texto(r.Body, "id"));
    }

    private int Borrador(int emisora, string extra = "")
    {
        var r = _s.CrearFactura(_c, emisora, Cuerpo(
            """{"invoice":{"concept":"5","series":"A","invoice_date":"2026-05-14","client_code":"PRUEBACS001","payment_method":"03","cfdi":"G03","tax_regime":"601","movements_attributes":[{"product_code":"PRUEBAPR001","quantity":2,"vat_percentage":16}]""" + extra + """}}"""));
        Assert.Equal(201, r.Status);
        return int.Parse(Texto(r.Body, "id"));
    }

    private async Task<(int emisora, int factura)> ListoParaTimbrar()
    {
        var e = Emisora();
        await Compania();
        await ProductoCreado();
        return (e, Borrador(e));
    }

    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Crear_una_emisora_registra_su_empresa_para_poder_ejecutar_ordenes()
    {
        Emisora("adOTRA");

        Assert.True(_almacen.EmpresaDelCliente(_c, "adOTRA"));
    }

    [Fact]
    public void Emisora_sin_datos_devuelve_422_con_los_campos_que_faltan()
    {
        var r = _s.CrearEmisora(_c, Cuerpo("""{"issuing_company":{"rfc":"X"}}"""));

        Assert.Equal(422, r.Status);
        Assert.Contains("Social reason can't be blank", Errores(r.Body));
    }

    [Fact]
    public async Task Crear_compania_genera_el_codigo_y_manda_cliente_crear_con_rfc_tipo_y_limite()
    {
        Emisora();
        var r = await _s.CrearCompaniaAsync(_c, Cuerpo("""{"company":{"client_type":1,"credit_limit":50000,"rfc":"xaxx010101000","social_reason":"PRUEBA CLIENTE SA"}}"""), default);

        Assert.Equal(201, r.Status);
        Assert.Equal("PRUEBACS001", Texto(r.Body, "code"));
        var llamada = Assert.Single(_ejecutor.Llamadas);
        Assert.Equal("cliente.crear", llamada.Tipo);
        Assert.Equal("adEMPRESA_DE_PRUEBA", llamada.Empresa);
        Assert.Equal("PRUEBACS001", llamada.Payload.GetProperty("codigo").GetString());
        Assert.Equal("XAXX010101000", llamada.Payload.GetProperty("rfc").GetString());
        Assert.Equal(50000, llamada.Payload.GetProperty("limiteCredito").GetDecimal());
        Assert.Equal("completed", Texto(r.Body, "status"));
    }

    [Fact]
    public async Task Si_el_worker_rechaza_la_compania_responde_422_y_queda_marcada_como_fallida()
    {
        Emisora();
        _ejecutor.Respuestas["cliente.crear"] = _ => EjecutorFalso.Falla("La razón social debe empezar con PRUEBA.");

        var r = await _s.CrearCompaniaAsync(_c, Cuerpo("""{"company":{"client_type":1,"credit_limit":1,"rfc":"XAXX010101000","social_reason":"SIN PREFIJO"}}"""), default);

        Assert.Equal(422, r.Status);
        Assert.Contains("PRUEBA", JsonSerializer.Serialize(r.Body));
        var lista = Elemento(_s.ListarCompanias(_c, new ParametrosLista(null, null, null, null)).Body);
        Assert.Equal("failed", lista[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Compania_sin_campos_obligatorios_devuelve_422()
    {
        Emisora();

        var r = await _s.CrearCompaniaAsync(_c, Cuerpo("""{"company":{"client_type":1}}"""), default);

        Assert.Equal(422, r.Status);
        var texto = Errores(r.Body);
        Assert.Contains("Social reason can't be blank", texto);
        Assert.Contains("Credit limit can't be blank", texto);
        Assert.Empty(_ejecutor.Llamadas);
    }

    [Fact]
    public async Task Actualizar_compania_manda_cliente_actualizar_y_guarda_los_cambios()
    {
        Emisora();
        var id = await Compania();

        var r = await _s.ActualizarCompaniaAsync(_c, id, Cuerpo("""{"company":{"credit_limit":80000}}"""), default);

        Assert.Equal(200, r.Status);
        Assert.Equal(80000, Elemento(r.Body).GetProperty("credit_limit").GetDecimal());
        var u = _ejecutor.Llamadas.Last();
        Assert.Equal("cliente.actualizar", u.Tipo);
        Assert.Equal(80000, u.Payload.GetProperty("limiteCredito").GetDecimal());
    }

    [Fact]
    public async Task Eliminar_compania_la_da_de_baja_en_acusa_sin_mandar_nada_a_comercial()
    {
        Emisora();
        var id = await Compania();
        var antes = _ejecutor.Llamadas.Count;

        var r = _s.EliminarCompania(_c, id);

        Assert.Equal(200, r.Status);
        Assert.Equal(antes, _ejecutor.Llamadas.Count);
        Assert.Equal(404, _s.ObtenerCompania(_c, id).Status);
    }

    [Fact]
    public async Task Un_cliente_no_ve_ni_modifica_lo_de_otro()
    {
        Emisora();
        var id = await Compania();
        var otro = _almacen.CrearCliente("Otro cliente");

        Assert.Equal(404, _s.ObtenerCompania(otro, id).Status);
        Assert.Equal(404, (await _s.ActualizarCompaniaAsync(otro, id, Cuerpo("""{"company":{"credit_limit":1}}"""), default)).Status);
        Assert.Equal(404, _s.EliminarCompania(otro, id).Status);
        Assert.Empty(Elemento(_s.ListarCompanias(otro, new ParametrosLista(null, null, null, null)).Body).EnumerateArray());
    }

    [Fact]
    public async Task Crear_producto_manda_tipo_y_textos_extra_y_genera_el_codigo()
    {
        Emisora();
        await ProductoCreado();

        var l = _ejecutor.Llamadas.Single();
        Assert.Equal("producto.crear", l.Tipo);
        Assert.Equal("PRUEBAPR001", l.Payload.GetProperty("codigo").GetString());
        Assert.Equal(3, l.Payload.GetProperty("tipoProducto").GetInt32());
        Assert.Equal("Texto uno", l.Payload.GetProperty("textosExtra")[0].GetString());
        Assert.Equal("81112200", l.Payload.GetProperty("claveSat").GetString());
    }

    [Fact]
    public async Task Producto_incompleto_devuelve_422()
    {
        Emisora();

        var r = await _s.CrearProductoAsync(_c, Cuerpo("""{"product":{"name":"X"}}"""), default);

        Assert.Equal(422, r.Status);
        Assert.Contains("Price can't be blank", Errores(r.Body));
    }

    [Fact]
    public async Task Crear_direccion_manda_direccion_crear_con_los_campos_de_alain()
    {
        Emisora();
        var id = await Compania();

        var r = await _s.CrearDireccionAsync(_c, id, Cuerpo("""{"address":{"street":"Av. Insurgentes 1","neighborhood":"Del Valle","zip_code":"03100","city":"CDMX","state":"CDMX","country":"México","address_type":0,"catalog_type":1}}"""), default);

        Assert.Equal(201, r.Status);
        var l = _ejecutor.Llamadas.Last();
        Assert.Equal("direccion.crear", l.Tipo);
        Assert.Equal("PRUEBACS001", l.Payload.GetProperty("codigoCliente").GetString());
        Assert.Equal("03100", l.Payload.GetProperty("codigoPostal").GetString());
    }

    // ---- Facturas -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Crear_factura_la_guarda_como_borrador_sin_tocar_comercial()
    {
        var (e, f) = await ListoParaTimbrar();
        var antes = _ejecutor.Llamadas.Count;

        var detalle = Elemento(_s.ObtenerFacturaDetalle(_c, e, f).Body);

        Assert.Equal("draft", detalle.GetProperty("sat_status").GetString());
        Assert.Equal(1, detalle.GetProperty("movements").GetArrayLength());
        Assert.Equal("03 - Transferencia electrónica de fondos", detalle.GetProperty("payment_method").GetString());
        Assert.Equal(antes, _ejecutor.Llamadas.Count);
    }

    [Theory]
    [InlineData("\"client_code\":\"NOEXISTE\"", "Client code not found")]
    [InlineData("\"payment_method\":\"77\"", "Payment method is not a SAT payment method key")]
    [InlineData("\"cfdi\":\"ZZZ\"", "Cfdi is not a SAT CFDI use key")]
    [InlineData("\"tax_regime\":\"999\"", "Tax regime is not a SAT tax regime key")]
    [InlineData("\"invoice_date\":\"14/05/2026\"", "Invoice date must be yyyy-MM-dd")]
    public async Task Factura_con_datos_invalidos_devuelve_422_con_el_motivo(string campo, string esperado)
    {
        var e = Emisora();
        await Compania();
        await ProductoCreado();
        var baseJson = """{"concept":"5","series":"A","invoice_date":"2026-05-14","client_code":"PRUEBACS001","payment_method":"03","cfdi":"G03","tax_regime":"601"}""";
        var nombre = campo.Split(':')[0];
        var obj = JsonNode.Parse(baseJson)!.AsObject();
        obj.Remove(nombre.Trim('"'));
        var json = obj.ToJsonString().TrimEnd('}') + "," + campo + "}";

        var r = _s.CrearFactura(_c, e, Cuerpo("""{"invoice":""" + json + """}"""));

        Assert.Equal(422, r.Status);
        Assert.Contains(esperado, Errores(r.Body));
    }

    [Fact]
    public async Task Timbrar_actualiza_el_cliente_crea_el_documento_y_lo_timbra_en_ese_orden()
    {
        var (e, f) = await ListoParaTimbrar();
        _ejecutor.Llamadas.Clear();

        var r = await _s.TimbrarAsync(_c, e, f, default);

        Assert.Equal(200, r.Status);
        Assert.Equal(["cliente.actualizar", "documento.crear", "documento.emitir"], _ejecutor.Tipos.ToArray());
        Assert.Equal("AAAAAAAA-0000-4000-8000-000000000001", Texto(r.Body, "uuid"));
        Assert.Equal("stamped", Texto(r.Body, "sat_status"));

        var act = _ejecutor.Llamadas[0].Payload;
        Assert.Equal("601", act.GetProperty("regimenFiscal").GetString());
        Assert.Equal("G03", act.GetProperty("usoCfdi").GetString());

        var crear = _ejecutor.Llamadas[1].Payload;
        Assert.Equal("2026-05-14", crear.GetProperty("fecha").GetString());
        Assert.Equal("03", crear.GetProperty("formaPago").GetString());
        Assert.Equal("G03", crear.GetProperty("usoCfdi").GetString());
        Assert.Equal("PRUEBAFAC" + f, crear.GetProperty("referencia").GetString());
        var partida = crear.GetProperty("partidas")[0];
        Assert.Equal("PRUEBAPR001", partida.GetProperty("productoCodigo").GetString());
        Assert.Equal(2, partida.GetProperty("unidades").GetDecimal());
        Assert.Equal(2000, partida.GetProperty("precio").GetDecimal());
        Assert.Equal(16, partida.GetProperty("ivaPorcentaje").GetDecimal());

        var emitir = _ejecutor.Llamadas[2].Payload;
        Assert.Equal(7, emitir.GetProperty("folio").GetDecimal());
        Assert.Equal("A", emitir.GetProperty("serie").GetString());
    }

    [Fact]
    public async Task Timbrar_dos_veces_la_misma_factura_responde_422_sin_mandar_nada()
    {
        var (e, f) = await ListoParaTimbrar();
        await _s.TimbrarAsync(_c, e, f, default);
        var antes = _ejecutor.Llamadas.Count;

        var r = await _s.TimbrarAsync(_c, e, f, default);

        Assert.Equal(422, r.Status);
        Assert.Contains("stamped before", JsonSerializer.Serialize(r.Body));
        Assert.Equal(antes, _ejecutor.Llamadas.Count);
    }

    [Fact]
    public async Task Si_falla_el_timbrado_el_reintento_no_vuelve_a_crear_el_documento()
    {
        var (e, f) = await ListoParaTimbrar();
        var fallar = true;
        _ejecutor.Respuestas["documento.emitir"] = n => fallar
            ? EjecutorFalso.Falla("Falta la contraseña del certificado.")
            : new ResultadoOrden(true, EjecutorFalso.Json("""{"uuid":"BBBBBBBB-0000-4000-8000-000000000002"}"""), null, null, false, "t" + n);
        _ejecutor.Llamadas.Clear();

        var primero = await _s.TimbrarAsync(_c, e, f, default);
        fallar = false;
        var segundo = await _s.TimbrarAsync(_c, e, f, default);

        Assert.Equal(422, primero.Status);
        Assert.Equal(200, segundo.Status);
        Assert.Single(_ejecutor.Llamadas, l => l.Tipo == "documento.crear");
        var emisiones = _ejecutor.Llamadas.Where(l => l.Tipo == "documento.emitir").ToList();
        Assert.Equal(2, emisiones.Count);
        Assert.NotEqual(emisiones[0].Llave, emisiones[1].Llave);
    }

    [Fact]
    public async Task Si_falla_crear_el_documento_el_reintento_usa_una_llave_nueva()
    {
        var (e, f) = await ListoParaTimbrar();
        _ejecutor.Respuestas["documento.crear"] = _ => EjecutorFalso.Falla("No existe el concepto 5.");
        _ejecutor.Llamadas.Clear();

        var primero = await _s.TimbrarAsync(_c, e, f, default);
        var segundo = await _s.TimbrarAsync(_c, e, f, default);

        Assert.Equal(422, primero.Status);
        Assert.Equal(422, segundo.Status);
        var llaves = _ejecutor.Llamadas.Where(l => l.Tipo == "documento.crear").Select(l => l.Llave).ToList();
        Assert.Equal(2, llaves.Distinct().Count());
        Assert.Contains("concepto", Elemento(_s.ObtenerFacturaDetalle(_c, e, f).Body).GetProperty("error_message").GetString());
    }

    [Fact]
    public async Task Si_el_worker_tarda_responde_202_y_el_reintento_conserva_la_misma_llave()
    {
        var (e, f) = await ListoParaTimbrar();
        _ejecutor.Respuestas["cliente.actualizar"] = _ => EjecutorFalso.EnCurso();
        _ejecutor.Llamadas.Clear();

        var primero = await _s.TimbrarAsync(_c, e, f, default);
        var segundo = await _s.TimbrarAsync(_c, e, f, default);

        Assert.Equal(202, primero.Status);
        Assert.Equal(202, segundo.Status);
        Assert.Single(_ejecutor.Llamadas.Select(l => l.Llave).Distinct());
    }

    [Fact]
    public async Task Cancelar_una_factura_sin_timbrar_responde_422()
    {
        var (e, f) = await ListoParaTimbrar();

        var r = await _s.CancelarAsync(_c, e, f, Cuerpo("""{"cancellation":{"cancellation_reason":"02"}}"""), default);

        Assert.Equal(422, r.Status);
        Assert.Contains("not stamped", JsonSerializer.Serialize(r.Body));
    }

    [Fact]
    public async Task Cancelar_una_factura_timbrada_manda_el_motivo_y_la_deja_cancelada()
    {
        var (e, f) = await ListoParaTimbrar();
        await _s.TimbrarAsync(_c, e, f, default);

        var r = await _s.CancelarAsync(_c, e, f, Cuerpo("""{"cancellation":{"cancellation_reason":"02"}}"""), default);

        Assert.Equal(200, r.Status);
        var l = _ejecutor.Llamadas.Last();
        Assert.Equal("documento.cancelar", l.Tipo);
        Assert.Equal("02", l.Payload.GetProperty("motivo").GetString());
        Assert.Equal("canceled", Elemento(_s.ObtenerFacturaDetalle(_c, e, f).Body).GetProperty("sat_status").GetString());
        Assert.Equal(422, (await _s.CancelarAsync(_c, e, f, Cuerpo("""{"cancellation":{"cancellation_reason":"02"}}"""), default)).Status);
    }

    [Fact]
    public async Task Cancelar_sin_motivo_devuelve_422()
    {
        var (e, f) = await ListoParaTimbrar();
        await _s.TimbrarAsync(_c, e, f, default);

        var r = await _s.CancelarAsync(_c, e, f, Cuerpo("""{"cancellation":{}}"""), default);

        Assert.Equal(422, r.Status);
    }

    [Fact]
    public async Task El_xml_y_el_pdf_salen_en_base64_solo_si_la_factura_esta_timbrada()
    {
        var (e, f) = await ListoParaTimbrar();
        Assert.Equal(422, (await _s.ArchivoAsync(_c, e, f, "xml", default)).Status);
        await _s.TimbrarAsync(_c, e, f, default);

        var xml = await _s.ArchivoAsync(_c, e, f, "xml", default);
        var pdf = await _s.ArchivoAsync(_c, e, f, "pdf", default);

        Assert.Equal(200, xml.Status);
        Assert.Equal("PHhtbC8+", Texto(xml.Body, "content_base64"));
        Assert.Equal("JVBERg==", Texto(pdf.Body, "content_base64"));
        Assert.EndsWith(".xml", Texto(xml.Body, "file_name"));
    }

    [Fact]
    public async Task Un_borrador_se_puede_editar_y_borrar_pero_una_factura_timbrada_no()
    {
        var (e, f) = await ListoParaTimbrar();
        Assert.Equal(200, _s.ActualizarFactura(_c, e, f, Cuerpo("""{"invoice":{"series":"b"}}""")).Status);
        Assert.Equal("B", Elemento(_s.ObtenerFacturaDetalle(_c, e, f).Body).GetProperty("series").GetString());

        await _s.TimbrarAsync(_c, e, f, default);

        Assert.Equal(422, _s.ActualizarFactura(_c, e, f, Cuerpo("""{"invoice":{"series":"C"}}""")).Status);
        Assert.Equal(422, _s.EliminarFactura(_c, e, f).Status);
    }

    [Fact]
    public async Task Eliminar_un_borrador_lo_quita_de_la_lista()
    {
        var (e, f) = await ListoParaTimbrar();

        Assert.Equal(200, _s.EliminarFactura(_c, e, f).Status);

        Assert.Empty(Elemento(_s.ListarFacturas(_c, e, new ParametrosLista(null, null, null, null)).Body).EnumerateArray());
        Assert.Equal(404, _s.ObtenerFacturaDetalle(_c, e, f).Status);
    }

    // ---- Partidas -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Las_partidas_se_agregan_cambian_y_borran_solo_en_un_borrador()
    {
        var (e, f) = await ListoParaTimbrar();

        var nueva = _s.CrearMovimiento(_c, Cuerpo("""{"movement":{"product_code":"PRUEBAPR001","quantity":5,"invoice_id":""" + f + ""","ieps_percentage":8}}"""));
        Assert.Equal(201, nueva.Status);
        var id = int.Parse(Texto(nueva.Body, "id"));
        Assert.Equal(200, _s.ActualizarMovimiento(_c, id, Cuerpo("""{"movement":{"quantity":6}}""")).Status);
        Assert.Equal(2, Elemento(_s.ListarMovimientos(_c, f, new ParametrosLista(null, null, null, null)).Body).GetArrayLength());
        Assert.Equal(200, _s.EliminarMovimiento(_c, id).Status);

        await _s.TimbrarAsync(_c, e, f, default);
        Assert.Equal(422, _s.CrearMovimiento(_c, Cuerpo("""{"movement":{"product_code":"PRUEBAPR001","quantity":1,"invoice_id":""" + f + """}}""")).Status);
    }

    [Fact]
    public async Task Una_partida_con_producto_inexistente_o_porcentaje_fuera_de_rango_devuelve_422()
    {
        var (_, f) = await ListoParaTimbrar();

        var sinProducto = _s.CrearMovimiento(_c, Cuerpo("""{"movement":{"product_code":"NOEXISTE","quantity":1,"invoice_id":""" + f + """}}"""));
        var iva = _s.CrearMovimiento(_c, Cuerpo("""{"movement":{"product_code":"PRUEBAPR001","quantity":1,"invoice_id":""" + f + ""","vat_percentage":150}}"""));

        Assert.Equal(422, sinProducto.Status);
        Assert.Equal(422, iva.Status);
        Assert.Contains("between 0 and 100", JsonSerializer.Serialize(iva.Body));
    }

    // ---- Catálogos, listas y lote ---------------------------------------------------------------------------------

    [Fact]
    public async Task La_pantalla_de_nueva_factura_trae_los_catalogos()
    {
        var e = Emisora();
        await Compania();
        await ProductoCreado();

        var n = Elemento(_s.FacturaNueva(_c, e).Body);

        foreach (var campo in new[] { "clients", "products", "payment_methods", "tax_regimes", "cfdis", "concepts", "issuing_companies" })
        {
            Assert.True(n.TryGetProperty(campo, out var arr) && arr.GetArrayLength() > 0, campo);
        }
    }

    [Fact]
    public async Task Los_listados_se_paginan_y_se_ordenan()
    {
        Emisora();
        for (var i = 0; i < 5; i++) await Compania();

        var pagina = Elemento(_s.ListarCompanias(_c, new ParametrosLista(2, 1, "id", "desc")).Body);

        Assert.Equal(2, pagina.GetArrayLength());
        Assert.Equal(4, pagina[0].GetProperty("id").GetInt32());
        Assert.Equal(3, pagina[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task El_orden_numerico_pone_el_10_despues_del_9()
    {
        Emisora();
        for (var i = 0; i < 11; i++) await Compania();

        var asc = Elemento(_s.ListarCompanias(_c, new ParametrosLista(50, 0, "id", "asc")).Body);

        Assert.Equal(9, asc[8].GetProperty("id").GetInt32());
        Assert.Equal(10, asc[9].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Timbrar_en_lote_procesa_cada_factura_y_reporta_el_resultado_de_cada_una()
    {
        var (e, f1) = await ListoParaTimbrar();
        var f2 = Borrador(e);

        var r = await _s.TimbrarLoteAsync(_c, Cuerpo("""{"invoice_ids":[""" + f1 + """,""" + f2 + """,999]}"""), default);

        Assert.Equal(200, r.Status);
        var resultados = Elemento(r.Body).GetProperty("results");
        Assert.True(resultados[0].GetProperty("ok").GetBoolean());
        Assert.True(resultados[1].GetProperty("ok").GetBoolean());
        Assert.False(resultados[2].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Un_lote_vacio_o_de_mas_de_25_devuelve_422()
    {
        await Task.CompletedTask;
        Assert.Equal(422, (await _s.TimbrarLoteAsync(_c, Cuerpo("""{"invoice_ids":[]}"""), default)).Status);
        var ids = string.Join(",", Enumerable.Range(1, 26));
        Assert.Equal(422, (await _s.TimbrarLoteAsync(_c, Cuerpo("""{"invoice_ids":[""" + ids + """]}"""), default)).Status);
    }
}
