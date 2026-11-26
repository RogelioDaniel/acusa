using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acusa.Contrato;
using Acusa.Orquestador;
using Acusa.Orquestador.Facturacion;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Acusa.Sdk.Tests;

public sealed class ApiRestTests : IDisposable
{
    private sealed record Orden(string Empresa, string Tipo, JsonElement Payload, bool Encolada, string? Llave = null);

    private sealed class WorkerSimulado : IEjecutorOrdenes
    {
        private readonly object _candado = new();
        private int _tareas;

        public List<Orden> Ordenes { get; } = [];

        public Dictionary<(string Cliente, int Tipo), JsonObject> Direcciones { get; } = [];

        public HashSet<string> TiposSinRespuesta { get; } = [];

        public Task<ResultadoOrden> EncolarAsync(string clienteId, string empresa, string tipo, object payload, string? llave, CancellationToken ct)
        {
            lock (_candado)
            {
                Ordenes.Add(new Orden(empresa, tipo, JsonSerializer.SerializeToElement(payload), true));
                _tareas++;
                return Task.FromResult(new ResultadoOrden(false, null, null, null, true, $"tarea{_tareas}"));
            }
        }

        public Task<ResultadoOrden> EjecutarAsync(string clienteId, string empresa, string tipo, object payload, string? llave, CancellationToken ct)
        {
            lock (_candado)
            {
                var datos = JsonSerializer.SerializeToElement(payload);
                Ordenes.Add(new Orden(empresa, tipo, datos, false, llave));
                _tareas++;
                var tareaId = $"tarea{_tareas}";
                return Task.FromResult(Responder(tipo, datos, tareaId));
            }
        }

        private ResultadoOrden Responder(string tipo, JsonElement datos, string tareaId)
        {
            if (TiposSinRespuesta.Contains(tipo))
            {
                return new ResultadoOrden(false, null, "El worker no respondió a tiempo; la tarea sigue en la cola.", null, true, tareaId);
            }

            if (tipo == Operaciones.DireccionLeer)
            {
                var clave = (datos.GetProperty("codigoCliente").GetString()!, datos.GetProperty("tipo").GetInt32());
                if (Direcciones.TryGetValue(clave, out var direccion))
                {
                    return Correcto(JsonSerializer.SerializeToElement(direccion), tareaId);
                }

                return new ResultadoOrden(false, null, $"El cliente {clave.Item1} no tiene dirección de tipo {clave.Item2}.", null, false, tareaId);
            }

            if (tipo == Operaciones.DireccionCrear)
            {
                var direccion = JsonNode.Parse(datos.GetRawText())!.AsObject();
                Direcciones[(direccion["codigoCliente"]!.GetValue<string>(), direccion["tipo"]!.GetValue<int>())] = direccion;
                return Correcto(datos, tareaId);
            }

            if (tipo == Operaciones.ClienteCrear && datos.GetProperty("codigo").GetString() == "EXISTE")
            {
                return new ResultadoOrden(false, null, "Ya existe el cliente EXISTE.", null, false, tareaId);
            }

            if (tipo == Operaciones.DocumentoCrear)
            {
                return Correcto(JsonSerializer.SerializeToElement(new { id = 40, concepto = datos.GetProperty("concepto").GetString(), serie = "A", folio = 7 }), tareaId);
            }

            if (tipo == Operaciones.DocumentoEmitir)
            {
                return Correcto(JsonSerializer.SerializeToElement(new { uuid = "AAAAAAAA-0000-4000-8000-000000000001" }), tareaId);
            }

            if (tipo == Operaciones.DocumentoObtenerArchivo)
            {
                return Correcto(JsonSerializer.SerializeToElement(new { archivos = new[] { new { tipo = "xml", base64 = "PHhtbC8+" } } }), tareaId);
            }

            return Correcto(datos, tareaId);
        }

        private static ResultadoOrden Correcto(JsonElement datos, string tareaId) => new(true, datos, null, null, false, tareaId);
    }

    private readonly string _base = Path.Combine(Path.GetTempPath(), $"acusa-rest-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Acusa.Orquestador.Seguridad> _fabrica;
    private readonly WorkerSimulado _worker = new();
    private readonly HttpClient _http;
    private readonly Almacen _almacen;
    private readonly string _clienteId;
    private readonly Dictionary<string, long> _empresas;

    private long Empresa1 => _empresas["EMPRESA1"];

    public ApiRestTests()
    {
        _fabrica = new WebApplicationFactory<Acusa.Orquestador.Seguridad>().WithWebHostBuilder(constructor =>
        {
            constructor.UseSetting("Orquestador:BaseDatos", _base);
            constructor.UseSetting("Orquestador:SecretoJwt", "secreto-de-prueba-con-mas-de-32-caracteres!!");
            constructor.UseSetting("Orquestador:ClaveAdmin", "clave-admin-de-prueba-16+");
            constructor.UseSetting("Orquestador:MaximoDocumentosPorLote", "3");
            constructor.ConfigureTestServices(servicios => servicios.AddSingleton<IEjecutorOrdenes>(_worker));
        });
        _http = _fabrica.CreateClient();
        _almacen = _fabrica.Services.GetRequiredService<Almacen>();
        _clienteId = _almacen.CrearCliente("Cliente de prueba");
        _almacen.AgregarEmpresa(_clienteId, "EMPRESA1");
        _almacen.AgregarEmpresa(_clienteId, "EMPRESA2");
        _empresas = _almacen.EmpresasConId(_clienteId).ToDictionary(empresa => empresa.Nombre, empresa => empresa.Id);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _almacen.CrearLlave(_clienteId, Roles.Api, "prueba"));
    }

    public void Dispose()
    {
        _http.Dispose();
        _fabrica.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var archivo in Directory.GetFiles(Path.GetDirectoryName(_base)!, Path.GetFileName(_base) + "*"))
        {
            try
            {
                File.Delete(archivo);
            }
            catch (IOException)
            {
            }
        }
    }

    private static object ClienteNuevo(string codigo = "CLI1") => new
    {
        codigo,
        razonSocial = "Cliente Ejemplo SA de CV",
        rfc = "XAXX010101000",
        codigoPostal = "06600",
        pais = "México",
        email = "facturacion@ejemplo.com",
    };

    private async Task<long> CrearClienteAsync(string codigo = "CLI1", string empresa = "EMPRESA1")
    {
        var respuesta = await _http.PostAsJsonAsync($"/api/empresas/{_empresas[empresa]}/clientes/crear", ClienteNuevo(codigo));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private async Task<long> CrearDocumentoAsync()
    {
        var respuesta = await _http.PostAsJsonAsync($"/api/empresas/{Empresa1}/documentos/crear", new
        {
            concepto = "5",
            clienteCodigo = "CLI1",
            fecha = "2026-11-25",
            formaPago = "01",
            usoCfdi = "G03",
            regimenFiscal = "601",
            movimientos = new[] { new { productoCodigo = "PR1", unidades = 1, precio = 10 } },
        });
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static async Task<ErrorApi> LeerErrorAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<ErrorApi>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    [Fact]
    public async Task TimbrarVarios_ConMaximoPorLoteDelCliente_RechazaLoQueLoExcede()
    {
        _almacen.GuardarAjusteEntero(_clienteId, Almacen.AjusteMaximoPorLote, 2);

        var respuesta = await _http.PostAsJsonAsync("/api/documentos/timbrar", new { documentos = new[] { 1, 2, 3 } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, respuesta.StatusCode);
        Assert.Contains((await LeerErrorAsync(respuesta)).Detalles, detalle => detalle.Contains("entre 1 y 2"));
    }

    [Fact]
    public async Task ListarEmpresas_DevuelveElIdDeCadaEmpresa()
    {
        var empresas = await _http.GetFromJsonAsync<JsonElement>("/api/empresas/listar");

        var primera = empresas.EnumerateArray().Single(empresa => empresa.GetProperty("nombre").GetString() == "EMPRESA1");
        Assert.Equal(Empresa1, primera.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task CrearCliente_ConElNombreDeLaEmpresaEnLaRuta_Responde404()
    {
        var respuesta = await _http.PostAsJsonAsync("/api/empresas/EMPRESA1/clientes/crear", ClienteNuevo());

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Empty(_worker.Ordenes);
    }

    [Fact]
    public async Task CrearCliente_ConElIdDeLaEmpresa_MandaLaOrdenAEsaEmpresa()
    {
        await CrearClienteAsync("CLI7", "EMPRESA2");

        var creacion = Assert.Single(_worker.Ordenes, orden => orden.Tipo == Operaciones.ClienteCrear);
        Assert.Equal("EMPRESA2", creacion.Empresa);
    }

    [Fact]
    public async Task CrearCliente_SinPais_Responde422()
    {
        var respuesta = await _http.PostAsJsonAsync($"/api/empresas/{Empresa1}/clientes/crear", new { codigo = "CLI1", razonSocial = "X", rfc = "XAXX010101000", codigoPostal = "06600" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, respuesta.StatusCode);
        Assert.Contains((await LeerErrorAsync(respuesta)).Detalles, detalle => detalle.Contains("Pais"));
    }

    [Fact]
    public async Task CrearCliente_CuerpoConUtf8Invalido_Responde400()
    {
        var bytes = Encoding.Latin1.GetBytes("{\"codigo\":\"CLI1\",\"razonSocial\":\"X\",\"rfc\":\"XAXX010101000\",\"codigoPostal\":\"06600\",\"pais\":\"México\"}");
        using var contenido = new ByteArrayContent(bytes);
        contenido.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var respuesta = await _http.PostAsync($"/api/empresas/{Empresa1}/clientes/crear", contenido);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Empty(_worker.Ordenes);
    }

    [Fact]
    public async Task CrearCliente_CuerpoQueNoEsObjeto_Responde400()
    {
        using var contenido = new StringContent("[1,2]", Encoding.UTF8, "application/json");

        var respuesta = await _http.PostAsync($"/api/empresas/{Empresa1}/clientes/crear", contenido);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    [Fact]
    public async Task CrearCliente_ConRegimenYUso_LosGuardaConUnaActualizacion()
    {
        var respuesta = await _http.PostAsJsonAsync($"/api/empresas/{Empresa1}/clientes/crear", new { codigo = "CLI1", razonSocial = "X", rfc = "XAXX010101000", codigoPostal = "06600", pais = "México", regimenFiscal = "601", usoCfdi = "G03" });

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var actualizacion = Assert.Single(_worker.Ordenes, orden => orden.Tipo == Operaciones.ClienteActualizar);
        Assert.Equal("601", actualizacion.Payload.GetProperty("regimenFiscal").GetString());
        Assert.Equal("G03", actualizacion.Payload.GetProperty("usoCfdi").GetString());
        Assert.Single(_worker.Ordenes, orden => orden.Tipo == Operaciones.DireccionCrear);
    }

    [Fact]
    public async Task CrearCliente_SinRespuestaATiempo_Responde202SinCrearLaDireccion()
    {
        _worker.TiposSinRespuesta.Add(Operaciones.ClienteCrear);

        var respuesta = await _http.PostAsJsonAsync($"/api/empresas/{Empresa1}/clientes/crear", ClienteNuevo());

        Assert.Equal(HttpStatusCode.Accepted, respuesta.StatusCode);
        Assert.DoesNotContain(_worker.Ordenes, orden => orden.Tipo == Operaciones.DireccionCrear);
        Assert.Contains("direcciones/crear", (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mensaje").GetString());
    }

    [Fact]
    public async Task CrearDocumento_ActualizacionDelClienteSinRespuesta_EncolaLaFactura()
    {
        await CrearClienteAsync();
        _worker.TiposSinRespuesta.Add(Operaciones.ClienteActualizar);

        var respuesta = await _http.PostAsJsonAsync($"/api/empresas/{Empresa1}/documentos/crear", new
        {
            concepto = "5",
            clienteCodigo = "CLI1",
            fecha = "2026-11-25",
            formaPago = "01",
            usoCfdi = "G03",
            regimenFiscal = "601",
            movimientos = new[] { new { productoCodigo = "PR1", unidades = 1, precio = 10 } },
        });

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.Single(_worker.Ordenes, orden => orden.Tipo == Operaciones.DocumentoCrear);
    }

    [Fact]
    public async Task CrearCliente_ConCorreo_NoEnviaElCorreoAlWorker()
    {
        await CrearClienteAsync();

        var creacion = Assert.Single(_worker.Ordenes, orden => orden.Tipo == Operaciones.ClienteCrear);
        Assert.False(creacion.Payload.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task CrearCliente_DatosCompletos_GuardaLaDireccionFiscalConTipoCeroYPais()
    {
        var id = await CrearClienteAsync();

        Assert.True(id > 0);
        var direccion = Assert.Single(_worker.Ordenes, orden => orden.Tipo == Operaciones.DireccionCrear).Payload;
        Assert.Equal(0, direccion.GetProperty("tipo").GetInt32());
        Assert.Equal("México", direccion.GetProperty("pais").GetString());
    }

    [Fact]
    public async Task LeerCliente_PorId_ConsultaElCodigoEnSuEmpresa()
    {
        var id = await CrearClienteAsync("CLI9", "EMPRESA2");

        var respuesta = await _http.GetAsync($"/api/clientes/{id}/consultar");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var lectura = _worker.Ordenes[^1];
        Assert.Equal(Operaciones.ClienteLeer, lectura.Tipo);
        Assert.Equal("EMPRESA2", lectura.Empresa);
        Assert.Equal("CLI9", lectura.Payload.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task LeerCliente_IdDeOtroCliente_Responde404()
    {
        var otroCliente = _almacen.CrearCliente("Otro");
        _almacen.AgregarEmpresa(otroCliente, "EMPRESA1");
        var idAjeno = _almacen.RegistrarRecurso(otroCliente, "cliente", "EMPRESA1", "AJENO");

        var respuesta = await _http.GetAsync($"/api/clientes/{idAjeno}/consultar");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task BuscarCliente_PorCodigo_DevuelveElMismoIdQueAlCrearlo()
    {
        var id = await CrearClienteAsync();

        var respuesta = await _http.GetFromJsonAsync<JsonElement>($"/api/empresas/{Empresa1}/clientes/buscar?codigo=CLI1");

        Assert.Equal(id, respuesta.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task ActualizarCliente_ConCorreo_SoloEnviaLosDatosPermitidos()
    {
        var id = await CrearClienteAsync();

        var respuesta = await _http.PatchAsJsonAsync($"/api/clientes/{id}/actualizar", new { razonSocial = "Nuevo Nombre", email = "x@ejemplo.com" });

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var actualizacion = _worker.Ordenes[^1].Payload;
        Assert.Equal("Nuevo Nombre", actualizacion.GetProperty("razonSocial").GetString());
        Assert.False(actualizacion.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task LeerDirecciones_SoloFiscal_DevuelveUnaLista()
    {
        var id = await CrearClienteAsync();

        var respuesta = await _http.GetFromJsonAsync<JsonElement>($"/api/clientes/{id}/direcciones/consultar");

        var direcciones = respuesta.GetProperty("resultado");
        Assert.Equal(1, direcciones.GetArrayLength());
        Assert.Equal(0, direcciones[0].GetProperty("tipo").GetInt32());
    }

    [Fact]
    public async Task LeerDirecciones_FiltroDeTipoInvalido_Responde422()
    {
        var id = await CrearClienteAsync();

        var respuesta = await _http.GetAsync($"/api/clientes/{id}/direcciones/consultar?tipo=5");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, respuesta.StatusCode);
    }

    [Fact]
    public async Task CrearDireccion_TipoQueYaExiste_Responde409()
    {
        var id = await CrearClienteAsync();

        var respuesta = await _http.PostAsJsonAsync($"/api/clientes/{id}/direcciones/crear", new { tipo = 0, codigoPostal = "06600", pais = "México" });

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
    }

    [Fact]
    public async Task CrearDireccion_EnvioNueva_Responde201()
    {
        var id = await CrearClienteAsync();

        var respuesta = await _http.PostAsJsonAsync($"/api/clientes/{id}/direcciones/crear", new { tipo = 1, codigoPostal = "42501", pais = "México" });

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.True(_worker.Direcciones.ContainsKey(("CLI1", 1)));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-1)]
    public async Task CrearDireccion_TipoFueraDelEnum_Responde422(int tipo)
    {
        var id = await CrearClienteAsync();

        var respuesta = await _http.PostAsJsonAsync($"/api/clientes/{id}/direcciones/crear", new { tipo, codigoPostal = "42501", pais = "México" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, respuesta.StatusCode);
    }

    [Fact]
    public async Task ActualizarDireccion_SoloCalle_ConservaLosDemasCampos()
    {
        var id = await CrearClienteAsync();

        var respuesta = await _http.PatchAsJsonAsync($"/api/clientes/{id}/direcciones/actualizar", new { calle = "Av. Reforma" });

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var guardada = _worker.Direcciones[("CLI1", 0)];
        Assert.Equal("Av. Reforma", guardada["calle"]!.GetValue<string>());
        Assert.Equal("06600", guardada["codigoPostal"]!.GetValue<string>());
        Assert.Equal("México", guardada["pais"]!.GetValue<string>());
    }

    [Fact]
    public async Task ActualizarDireccion_TipoQueNoExiste_Responde404()
    {
        var id = await CrearClienteAsync();

        var respuesta = await _http.PatchAsJsonAsync($"/api/clientes/{id}/direcciones/actualizar", new { tipo = 1, calle = "Calle Uno" });

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task CrearDocumento_Completado_DevuelveElIdDeAcusa()
    {
        var id = await CrearDocumentoAsync();

        var recurso = _almacen.BuscarRecurso(_clienteId, "documento", id);
        Assert.NotNull(recurso);
        Assert.Equal("5|A|7", recurso.Clave);
    }

    [Fact]
    public async Task CrearDocumento_ProductoPorIdDeOtraEmpresa_Responde422()
    {
        var idProducto = _almacen.RegistrarRecurso(_clienteId, "producto", "EMPRESA2", "PR2");

        var respuesta = await _http.PostAsJsonAsync($"/api/empresas/{Empresa1}/documentos/crear", new
        {
            concepto = "5",
            clienteCodigo = "CLI1",
            fecha = "2026-11-25",
            formaPago = "01",
            usoCfdi = "G03",
            regimenFiscal = "601",
            movimientos = new[] { new { productoId = idProducto, unidades = 1, precio = 10 } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, respuesta.StatusCode);
    }

    [Fact]
    public async Task Timbrar_PorId_EmiteElDocumentoYDevuelveElXml()
    {
        var id = await CrearDocumentoAsync();

        var respuesta = await _http.PostAsync($"/api/documentos/{id}/timbrar", null);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("<xml/>", cuerpo.GetProperty("xml").GetString());
        var emision = Assert.Single(_worker.Ordenes, orden => orden.Tipo == Operaciones.DocumentoEmitir).Payload;
        Assert.Equal(7, emision.GetProperty("folio").GetInt64());
    }

    [Fact]
    public async Task Cancelar_SinMotivo_Responde422()
    {
        var id = await CrearDocumentoAsync();

        var respuesta = await _http.PostAsJsonAsync($"/api/documentos/{id}/cancelar", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, respuesta.StatusCode);
    }

    [Fact]
    public async Task Cancelar_MotivoValido_EnviaLaCancelacion()
    {
        var id = await CrearDocumentoAsync();

        var respuesta = await _http.PostAsJsonAsync($"/api/documentos/{id}/cancelar", new { motivo = "02" });

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("02", _worker.Ordenes[^1].Payload.GetProperty("motivo").GetString());
    }

    [Fact]
    public async Task RutasAnteriores_TimbradoYCancelacion_YaNoExisten()
    {
        var timbrado = await _http.PostAsync("/api/empresas/EMPRESA1/documentos/5/A/7/timbrado", null);
        var cancelacion = await _http.PostAsJsonAsync("/api/empresas/EMPRESA1/documentos/cancelacion", new { documentos = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.NotFound, timbrado.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancelacion.StatusCode);
    }

    [Fact]
    public async Task TimbrarVarios_DocumentosValidos_Responde202SinEsperarYUnaTareaPorDocumento()
    {
        var id = await CrearDocumentoAsync();
        var otroId = _almacen.RegistrarRecurso(_clienteId, "documento", "EMPRESA1", "5|A|8");

        var respuesta = await _http.PostAsJsonAsync("/api/documentos/timbrar", new { documentos = new[] { id, otroId } });

        Assert.Equal(HttpStatusCode.Accepted, respuesta.StatusCode);
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, cuerpo.GetProperty("tareas").GetArrayLength());
        Assert.Equal(2, _worker.Ordenes.Count(orden => orden.Encolada && orden.Tipo == Operaciones.DocumentoEmitir));
    }

    [Fact]
    public async Task TimbrarVarios_MasDelMaximoConfigurado_Responde422()
    {
        var id = await CrearDocumentoAsync();

        var respuesta = await _http.PostAsJsonAsync("/api/documentos/timbrar", new { documentos = new[] { id, id, id, id } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, respuesta.StatusCode);
    }

    [Fact]
    public async Task CancelarVarios_DocumentoInexistente_Responde422SinEncolar()
    {
        var respuesta = await _http.PostAsJsonAsync("/api/documentos/cancelar", new { documentos = new[] { new { id = 999, motivo = "02" } } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, respuesta.StatusCode);
        Assert.DoesNotContain(_worker.Ordenes, orden => orden.Encolada);
    }

    [Fact]
    public async Task CancelarVarios_ConMotivo_EncolaCadaCancelacion()
    {
        var id = await CrearDocumentoAsync();

        var respuesta = await _http.PostAsJsonAsync("/api/documentos/cancelar", new { documentos = new[] { new { id, motivo = "02" } } });

        Assert.Equal(HttpStatusCode.Accepted, respuesta.StatusCode);
        Assert.Single(_worker.Ordenes, orden => orden.Encolada && orden.Tipo == Operaciones.DocumentoCancelar);
    }

    [Fact]
    public async Task TareasPendientes_TareasEnCola_LasLista()
    {
        _almacen.EncolarTarea(_clienteId, "EMPRESA1", Operaciones.DocumentoEmitir, "{}");

        var tareas = await _http.GetFromJsonAsync<JsonElement>("/api/tareas/pendientes");

        Assert.Equal(1, tareas.GetArrayLength());
    }
    [Fact]
    public async Task TimbrarVarios_DocumentoRepetidoEnLaLista_Responde422SinEncolar()
    {
        var id = await CrearDocumentoAsync();

        var respuesta = await _http.PostAsJsonAsync("/api/documentos/timbrar", new { documentos = new[] { id, id } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, respuesta.StatusCode);
        Assert.DoesNotContain(_worker.Ordenes, orden => orden.Encolada);
    }

    [Fact]
    public async Task CrearCliente_CodigoQueYaExiste_Responde409()
    {
        var respuesta = await _http.PostAsJsonAsync($"/api/empresas/{Empresa1}/clientes/crear", ClienteNuevo("EXISTE"));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal(CodigosError.Conflicto, (await LeerErrorAsync(respuesta)).Codigo);
    }

    [Fact]
    public async Task CrearDocumento_ConReferenciaSinIdempotencyKey_UsaLaReferenciaComoLlave()
    {
        await _http.PostAsJsonAsync($"/api/empresas/{Empresa1}/documentos/crear", new
        {
            concepto = "5",
            clienteCodigo = "CLI1",
            fecha = "2026-11-25",
            formaPago = "01",
            usoCfdi = "G03",
            regimenFiscal = "601",
            referencia = "FAC-0001",
            movimientos = new[] { new { productoCodigo = "PR1", unidades = 1, precio = 10 } },
        });

        var creacion = Assert.Single(_worker.Ordenes, orden => orden.Tipo == Operaciones.DocumentoCrear);
        Assert.Equal("referencia:EMPRESA1:FAC-0001:documento", creacion.Llave);
    }

    [Fact]
    public async Task CrearDireccion_ConTipoDireccion_AceptaElNombreDeMontek()
    {
        var id = await CrearClienteAsync();

        var respuesta = await _http.PostAsJsonAsync($"/api/clientes/{id}/direcciones/crear", new { tipoDireccion = 1, codigoPostal = "42501", pais = "México" });

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.True(_worker.Direcciones.ContainsKey(("CLI1", 1)));
    }

    [Fact]
    public void EncolarSinDuplicar_MismaOrdenPendiente_DevuelveLaMismaTarea()
    {
        var primera = _almacen.EncolarSinDuplicar(_clienteId, "EMPRESA1", Operaciones.DocumentoEmitir, """{"folio":1}""");

        var segunda = _almacen.EncolarSinDuplicar(_clienteId, "EMPRESA1", Operaciones.DocumentoEmitir, """{ "folio": 1 }""");

        Assert.Equal(primera.Id, segunda.Id);
        Assert.True(segunda.Repetida);
    }

    [Fact]
    public void EncolarSinDuplicar_MismaOrdenYaTerminada_CreaOtraTarea()
    {
        var primera = _almacen.EncolarSinDuplicar(_clienteId, "EMPRESA1", Operaciones.DocumentoEmitir, """{"folio":2}""");
        var tomada = _almacen.TomarTarea(_clienteId, "EMPRESA1", "worker1");
        _almacen.RegistrarResultado(_clienteId, tomada!.Id, "worker1", new ResultadoTarea(true, null, null, null));

        var segunda = _almacen.EncolarSinDuplicar(_clienteId, "EMPRESA1", Operaciones.DocumentoEmitir, """{"folio":2}""");

        Assert.NotEqual(primera.Id, segunda.Id);
        Assert.False(segunda.Repetida);
    }
}
