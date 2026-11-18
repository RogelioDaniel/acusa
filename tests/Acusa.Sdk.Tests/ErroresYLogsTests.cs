using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Acusa.Contrato;
using Acusa.Orquestador;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acusa.Sdk.Tests;

public sealed class ErroresSdkTests
{
    [Theory]
    [InlineData(120115, "almacén")]
    [InlineData(120146, "concepto")]
    [InlineData(120513, "lista de precios")]
    public void Un_codigo_conocido_se_explica_y_conserva_el_texto_tecnico(int codigo, string menciona)
    {
        var (mensaje, tecnico) = ErroresSdk.Traducir(codigo, $"fAltaMovimiento = {codigo}: texto del SDK");

        Assert.Contains(menciona, mensaje);
        Assert.Equal($"fAltaMovimiento = {codigo}: texto del SDK", tecnico);
        Assert.DoesNotContain("fAltaMovimiento", mensaje);
    }

    [Fact]
    public void Un_codigo_desconocido_quita_el_nombre_de_la_funcion_y_conserva_el_texto_del_sdk()
    {
        var (mensaje, tecnico) = ErroresSdk.Traducir(999999, "fGuardaDocumento = 999999: El documento está bloqueado.");

        Assert.Equal("CONTPAQi Comercial rechazó la operación (código 999999): El documento está bloqueado.", mensaje);
        Assert.Equal("fGuardaDocumento = 999999: El documento está bloqueado.", tecnico);
    }

    [Theory]
    [InlineData(null, "Falló algo.", "Falló algo.")]
    [InlineData(0, "Falló algo.", "Falló algo.")]
    [InlineData(null, null, "La operación no se completó.")]
    public void Sin_codigo_del_sdk_el_mensaje_no_cambia(int? codigo, string? texto, string esperado)
    {
        var (mensaje, tecnico) = ErroresSdk.Traducir(codigo, texto);

        Assert.Equal(esperado, mensaje);
        Assert.Null(tecnico);
    }

    [Fact]
    public void El_error_de_una_tarea_con_codigo_del_sdk_lleva_codigo_error_sdk_y_el_id_de_la_tarea()
    {
        var error = Errores.DeTarea("fAltaMovimiento = 120115: no existe", 120115, "t-1");

        Assert.Equal(CodigosError.ErrorSdk, error.Codigo);
        Assert.Equal(120115, error.CodigoSdk);
        Assert.Equal("t-1", error.TareaId);
        Assert.Single(error.Detalles);
    }

    [Fact]
    public void El_error_de_una_tarea_sin_codigo_del_sdk_es_operacion_fallida()
    {
        var error = Errores.DeTarea("El payload no tiene el formato esperado.", null, "t-2");

        Assert.Equal(CodigosError.OperacionFallida, error.Codigo);
        Assert.Empty(error.Detalles);
    }
}

public sealed class RedaccionTests
{
    [Theory]
    [InlineData("llave ak_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa lista", "ak_aaaa")]
    [InlineData("llave wk_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb lista", "wk_bbbb")]
    [InlineData("Authorization: Bearer abc.def-ghi_123", "abc.def")]
    [InlineData("token eyJaaaaaaaa.eyJbbbbbbbb.cccccccc", "eyJaaaa")]
    [InlineData("password=claveFalsa", "claveFalsa")]
    [InlineData("{\"contrasena\":\"claveFalsa\"}", "claveFalsa")]
    [InlineData("claveCertificado: claveFalsa", "claveFalsa")]
    [InlineData("csdPassword=\"con espacios y mas\"", "con espacios")]
    public void Oculta_llaves_tokens_contrasenas_y_datos_del_certificado(string texto, string secreto)
    {
        var limpio = Redaccion.Limpiar(texto);

        Assert.DoesNotContain(secreto, limpio);
        Assert.Contains(Redaccion.Marca, limpio);
    }

    [Fact]
    public void Un_texto_sin_secretos_no_cambia()
    {
        const string texto = "Tarea 123: documento.crear (EMPRESA1) completada en 840 ms";

        Assert.Equal(texto, Redaccion.Limpiar(texto));
    }
}

public sealed class ErroresApiTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "acusa-errores-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly WebApplicationFactory<Acusa.Orquestador.Seguridad> _fabrica;
    private readonly HttpClient _http;
    private readonly string _llave;

    public ErroresApiTests()
    {
        _fabrica = new WebApplicationFactory<Acusa.Orquestador.Seguridad>().WithWebHostBuilder(constructor =>
        {
            constructor.UseSetting("Orquestador:BaseDatos", _base);
            constructor.UseSetting("Orquestador:SecretoJwt", "secreto-de-prueba-con-mas-de-32-caracteres!!");
            constructor.UseSetting("Orquestador:ClaveAdmin", "clave-admin-de-prueba-16+");
        });
        _http = _fabrica.CreateClient();
        var almacen = _fabrica.Services.GetRequiredService<Almacen>();
        var cliente = almacen.CrearCliente("Cliente de prueba");
        almacen.AgregarEmpresa(cliente, "EMPRESA1");
        _llave = almacen.CrearLlave(cliente, Roles.Api, "prueba");
    }

    public void Dispose()
    {
        _http.Dispose();
        _fabrica.Dispose();
        foreach (var archivo in Directory.GetFiles(Path.GetDirectoryName(_base)!, Path.GetFileName(_base) + "*"))
        {
            try { File.Delete(archivo); } catch (IOException) { }
        }
    }

    private HttpRequestMessage Peticion(HttpMethod metodo, string ruta, object? cuerpo = null, bool conLlave = true)
    {
        var peticion = new HttpRequestMessage(metodo, ruta);
        if (conLlave) peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _llave);
        if (cuerpo is not null) peticion.Content = JsonContent.Create(cuerpo);
        return peticion;
    }

    private static async Task<(ErrorApi Error, string Crudo)> Leer(HttpResponseMessage respuesta)
    {
        var crudo = await respuesta.Content.ReadAsStringAsync();
        return (JsonSerializer.Deserialize<ErrorApi>(crudo, new JsonSerializerOptions(JsonSerializerDefaults.Web))!, crudo);
    }

    [Fact]
    public async Task Sin_credenciales_responde_401_con_el_formato_estandar()
    {
        var respuesta = await _http.SendAsync(Peticion(HttpMethod.Get, "/api/empresas", conLlave: false));
        var (error, _) = await Leer(respuesta);

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
        Assert.Equal(CodigosError.NoAutorizado, error.Codigo);
        Assert.False(string.IsNullOrWhiteSpace(error.Mensaje));
        Assert.NotNull(error.Detalles);
    }

    [Fact]
    public async Task El_id_de_correlacion_del_cuerpo_es_el_de_la_cabecera()
    {
        var respuesta = await _http.SendAsync(Peticion(HttpMethod.Get, "/api/empresas", conLlave: false));
        var (error, _) = await Leer(respuesta);

        Assert.Equal(error.IdCorrelacion, Assert.Single(respuesta.Headers.GetValues(Correlacion.Cabecera)));
        Assert.False(string.IsNullOrEmpty(error.IdCorrelacion));
    }

    [Fact]
    public async Task Si_quien_llama_manda_un_id_de_correlacion_valido_se_conserva()
    {
        var peticion = Peticion(HttpMethod.Get, "/api/empresas/NOEXISTE/clientes/X");
        peticion.Headers.Add(Correlacion.Cabecera, "sistema-cliente-42");
        var respuesta = await _http.SendAsync(peticion);
        var (error, _) = await Leer(respuesta);

        Assert.Equal("sistema-cliente-42", error.IdCorrelacion);
        Assert.Equal("sistema-cliente-42", respuesta.Headers.GetValues(Correlacion.Cabecera).Single());
    }

    [Fact]
    public async Task Un_id_de_correlacion_con_caracteres_raros_se_reemplaza()
    {
        var peticion = Peticion(HttpMethod.Get, "/api/empresas", conLlave: false);
        peticion.Headers.TryAddWithoutValidation(Correlacion.Cabecera, "<script>alert(1)</script>");
        var respuesta = await _http.SendAsync(peticion);

        Assert.DoesNotContain("<", respuesta.Headers.GetValues(Correlacion.Cabecera).Single());
    }

    [Fact]
    public async Task Una_empresa_que_no_es_del_cliente_responde_404()
    {
        var respuesta = await _http.SendAsync(Peticion(HttpMethod.Get, "/api/empresas/AJENA/clientes/X"));
        var (error, _) = await Leer(respuesta);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal(CodigosError.NoEncontrado, error.Codigo);
        Assert.Contains("AJENA", error.Mensaje);
    }

    [Fact]
    public async Task Una_validacion_responde_422_con_un_detalle_por_campo()
    {
        var respuesta = await _http.SendAsync(Peticion(HttpMethod.Post, "/api/empresas/EMPRESA1/productos", new { }));
        var (error, _) = await Leer(respuesta);

        Assert.Equal((HttpStatusCode)422, respuesta.StatusCode);
        Assert.Equal(CodigosError.Validacion, error.Codigo);
        Assert.Contains(error.Detalles, detalle => detalle.Contains("Codigo"));
        Assert.Contains(error.Detalles, detalle => detalle.Contains("Nombre"));
        Assert.Contains(error.Detalles, detalle => detalle.Contains("Unidad base"));
    }

    [Fact]
    public async Task Un_cuerpo_que_no_es_json_responde_400()
    {
        var peticion = Peticion(HttpMethod.Post, "/api/empresas/EMPRESA1/productos");
        peticion.Content = new StringContent("esto no es json", System.Text.Encoding.UTF8, "application/json");
        var respuesta = await _http.SendAsync(peticion);
        var (error, _) = await Leer(respuesta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(CodigosError.SolicitudInvalida, error.Codigo);
    }

    [Fact]
    public async Task Una_contrasena_en_el_cuerpo_se_rechaza_sin_repetirla_en_la_respuesta()
    {
        var respuesta = await _http.SendAsync(Peticion(HttpMethod.Post, "/api/empresas/EMPRESA1/productos", new { codigo = "P1", password = "claveFalsa" }));
        var (error, crudo) = await Leer(respuesta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(CodigosError.SolicitudInvalida, error.Codigo);
        Assert.DoesNotContain("claveFalsa", crudo);
    }

    [Fact]
    public async Task Una_operacion_que_no_existe_responde_400()
    {
        var respuesta = await _http.SendAsync(Peticion(HttpMethod.Post, "/api/sdk/execute", new { tipoProceso = "no.existe", empresa = "EMPRESA1" }));
        var (error, _) = await Leer(respuesta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(CodigosError.SolicitudInvalida, error.Codigo);
        Assert.Contains(error.Detalles, detalle => detalle.Contains(Operaciones.ClienteCrear));
    }

    [Fact]
    public async Task Una_ruta_de_la_api_que_no_existe_responde_404_con_el_formato_estandar()
    {
        var respuesta = await _http.SendAsync(Peticion(HttpMethod.Get, "/api/no-existe"));
        var (error, _) = await Leer(respuesta);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal(CodigosError.NoEncontrado, error.Codigo);
    }

    [Fact]
    public async Task El_login_incorrecto_responde_401_con_el_formato_estandar()
    {
        var respuesta = await _http.PostAsJsonAsync("/api/auth/login", new { correo = "nadie@ejemplo.com", contrasena = "incorrecta123" });
        var (error, _) = await Leer(respuesta);

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
        Assert.Equal(CodigosError.NoAutorizado, error.Codigo);
    }
}

public sealed class RegistroDeLogsTests
{
    private sealed class Captura : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _ambitos = new LoggerExternalScopeProvider();
        public List<string> Lineas { get; } = [];

        public void SetScopeProvider(IExternalScopeProvider proveedor) => _ambitos = proveedor;

        public ILogger CreateLogger(string categoria) => new Registro(this);

        public void Dispose()
        {
        }

        private sealed class Registro(Captura padre) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState estado) where TState : notnull => padre._ambitos.Push(estado);

            public bool IsEnabled(LogLevel nivel) => true;

            public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? excepcion, Func<TState, Exception?, string> formato)
            {
                var ambitos = new List<string>();
                padre._ambitos.ForEachScope((ambito, lineasDeAmbito) => lineasDeAmbito.Add(string.Join(",", (ambito as IEnumerable<KeyValuePair<string, object>>)?.Select(propiedad => $"{propiedad.Key}={propiedad.Value}") ?? [])), ambitos);
                lock (padre.Lineas)
                {
                    padre.Lineas.Add($"{nivel}|{formato(estado, excepcion)}|{string.Join(";", ambitos)}");
                }
            }
        }
    }

    private static async Task<(WebApplication App, Captura Logs, HttpClient Http)> Arrancar()
    {
        var captura = new Captura();
        var constructor = WebApplication.CreateBuilder();
        constructor.WebHost.UseTestServer();
        constructor.Logging.ClearProviders();
        constructor.Logging.AddProvider(captura);
        Errores.SinRuido(constructor.Logging);
        var aplicacion = constructor.Build();
        Errores.Usar(aplicacion);
        aplicacion.MapGet("/api/ok", () => Results.Ok(new { ok = true }));
        aplicacion.MapGet("/api/falla", () => Results.StatusCode(404));
        aplicacion.MapGet("/api/revienta", object () => throw new InvalidOperationException("Falló con password=claveFalsa y la llave ak_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        await aplicacion.StartAsync();
        return (aplicacion, captura, aplicacion.GetTestClient());
    }

    [Fact]
    public async Task Cada_linea_de_la_peticion_lleva_el_id_de_correlacion()
    {
        var (aplicacion, captura, cliente) = await Arrancar();
        await using var _ = aplicacion;
        var respuesta = await cliente.GetAsync("/api/ok");
        var id = respuesta.Headers.GetValues(Correlacion.Cabecera).Single();

        var linea = Assert.Single(captura.Lineas, linea => linea.Contains("GET /api/ok"));
        Assert.Contains($"IdCorrelacion={id}", linea);
        Assert.Contains("-> 200", linea);
    }

    [Fact]
    public async Task El_log_no_guarda_cabeceras_cuerpo_ni_query_string()
    {
        var (aplicacion, captura, cliente) = await Arrancar();
        await using var _ = aplicacion;
        var peticion = new HttpRequestMessage(HttpMethod.Get, "/api/ok?token=claveFalsa");
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "ak_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await cliente.SendAsync(peticion);

        var todo = string.Join("\n", captura.Lineas);
        Assert.DoesNotContain("claveFalsa", todo);
        Assert.DoesNotContain("ak_aaaaaaaaaa", todo);
        Assert.DoesNotContain("Bearer", todo);
    }

    [Fact]
    public async Task Una_excepcion_responde_500_estandar_y_el_log_no_contiene_secretos()
    {
        var (aplicacion, captura, cliente) = await Arrancar();
        await using var _ = aplicacion;
        var respuesta = await cliente.GetAsync("/api/revienta");
        var crudo = await respuesta.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ErrorApi>(crudo, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        Assert.Equal(CodigosError.ErrorInterno, error.Codigo);
        Assert.Equal(respuesta.Headers.GetValues(Correlacion.Cabecera).Single(), error.IdCorrelacion);
        Assert.DoesNotContain("claveFalsa", crudo);

        var todo = string.Join("\n", captura.Lineas);
        Assert.Contains("Error no controlado", todo);
        Assert.DoesNotContain("claveFalsa", todo);
        Assert.DoesNotContain("ak_aaaaaaaaaa", todo);
    }

    [Fact]
    public async Task Un_error_de_cliente_se_registra_como_aviso_y_uno_de_servidor_como_error()
    {
        var (aplicacion, captura, cliente) = await Arrancar();
        await using var _ = aplicacion;
        await cliente.GetAsync("/api/falla");
        await cliente.GetAsync("/api/revienta");

        Assert.Contains(captura.Lineas, linea => linea.StartsWith("Warning|GET /api/falla"));
        Assert.Contains(captura.Lineas, linea => linea.StartsWith("Error|GET /api/revienta -> 500"));
    }
}
