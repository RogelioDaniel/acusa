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
        var e = Errores.DeTarea("fAltaMovimiento = 120115: no existe", 120115, "t-1");

        Assert.Equal(CodigosError.ErrorSdk, e.Codigo);
        Assert.Equal(120115, e.CodigoSdk);
        Assert.Equal("t-1", e.TareaId);
        Assert.Single(e.Detalles);
    }

    [Fact]
    public void El_error_de_una_tarea_sin_codigo_del_sdk_es_operacion_fallida()
    {
        var e = Errores.DeTarea("El payload no tiene el formato esperado.", null, "t-2");

        Assert.Equal(CodigosError.OperacionFallida, e.Codigo);
        Assert.Empty(e.Detalles);
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
        _fabrica = new WebApplicationFactory<Acusa.Orquestador.Seguridad>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Orquestador:BaseDatos", _base);
            b.UseSetting("Orquestador:SecretoJwt", "secreto-de-prueba-con-mas-de-32-caracteres!!");
            b.UseSetting("Orquestador:ClaveAdmin", "clave-admin-de-prueba-16+");
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
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_base)!, Path.GetFileName(_base) + "*"))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    private HttpRequestMessage Peticion(HttpMethod metodo, string ruta, object? cuerpo = null, bool conLlave = true)
    {
        var p = new HttpRequestMessage(metodo, ruta);
        if (conLlave) p.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _llave);
        if (cuerpo is not null) p.Content = JsonContent.Create(cuerpo);
        return p;
    }

    private static async Task<(ErrorApi Error, string Crudo)> Leer(HttpResponseMessage r)
    {
        var crudo = await r.Content.ReadAsStringAsync();
        return (JsonSerializer.Deserialize<ErrorApi>(crudo, new JsonSerializerOptions(JsonSerializerDefaults.Web))!, crudo);
    }

    [Fact]
    public async Task Sin_credenciales_responde_401_con_el_formato_estandar()
    {
        var r = await _http.SendAsync(Peticion(HttpMethod.Get, "/api/empresas", conLlave: false));
        var (e, _) = await Leer(r);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(CodigosError.NoAutorizado, e.Codigo);
        Assert.False(string.IsNullOrWhiteSpace(e.Mensaje));
        Assert.NotNull(e.Detalles);
    }

    [Fact]
    public async Task El_id_de_correlacion_del_cuerpo_es_el_de_la_cabecera()
    {
        var r = await _http.SendAsync(Peticion(HttpMethod.Get, "/api/empresas", conLlave: false));
        var (e, _) = await Leer(r);

        Assert.Equal(e.IdCorrelacion, Assert.Single(r.Headers.GetValues(Correlacion.Cabecera)));
        Assert.False(string.IsNullOrEmpty(e.IdCorrelacion));
    }

    [Fact]
    public async Task Si_quien_llama_manda_un_id_de_correlacion_valido_se_conserva()
    {
        var p = Peticion(HttpMethod.Get, "/api/empresas/NOEXISTE/clientes/X");
        p.Headers.Add(Correlacion.Cabecera, "sistema-cliente-42");
        var r = await _http.SendAsync(p);
        var (e, _) = await Leer(r);

        Assert.Equal("sistema-cliente-42", e.IdCorrelacion);
        Assert.Equal("sistema-cliente-42", r.Headers.GetValues(Correlacion.Cabecera).Single());
    }

    [Fact]
    public async Task Un_id_de_correlacion_con_caracteres_raros_se_reemplaza()
    {
        var p = Peticion(HttpMethod.Get, "/api/empresas", conLlave: false);
        p.Headers.TryAddWithoutValidation(Correlacion.Cabecera, "<script>alert(1)</script>");
        var r = await _http.SendAsync(p);

        Assert.DoesNotContain("<", r.Headers.GetValues(Correlacion.Cabecera).Single());
    }

    [Fact]
    public async Task Una_empresa_que_no_es_del_cliente_responde_404()
    {
        var r = await _http.SendAsync(Peticion(HttpMethod.Get, "/api/empresas/AJENA/clientes/X"));
        var (e, _) = await Leer(r);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(CodigosError.NoEncontrado, e.Codigo);
        Assert.Contains("AJENA", e.Mensaje);
    }

    [Fact]
    public async Task Una_validacion_responde_422_con_un_detalle_por_campo()
    {
        var r = await _http.SendAsync(Peticion(HttpMethod.Post, "/api/empresas/EMPRESA1/productos", new { }));
        var (e, _) = await Leer(r);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal(CodigosError.Validacion, e.Codigo);
        Assert.Contains(e.Detalles, d => d.Contains("Codigo"));
        Assert.Contains(e.Detalles, d => d.Contains("Nombre"));
        Assert.Contains(e.Detalles, d => d.Contains("Unidad base"));
    }

    [Fact]
    public async Task Un_cuerpo_que_no_es_json_responde_400()
    {
        var p = Peticion(HttpMethod.Post, "/api/empresas/EMPRESA1/productos");
        p.Content = new StringContent("esto no es json", System.Text.Encoding.UTF8, "application/json");
        var r = await _http.SendAsync(p);
        var (e, _) = await Leer(r);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(CodigosError.SolicitudInvalida, e.Codigo);
    }

    [Fact]
    public async Task Una_contrasena_en_el_cuerpo_se_rechaza_sin_repetirla_en_la_respuesta()
    {
        var r = await _http.SendAsync(Peticion(HttpMethod.Post, "/api/empresas/EMPRESA1/productos", new { codigo = "P1", password = "claveFalsa" }));
        var (e, crudo) = await Leer(r);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(CodigosError.SolicitudInvalida, e.Codigo);
        Assert.DoesNotContain("claveFalsa", crudo);
    }

    [Fact]
    public async Task Una_operacion_que_no_existe_responde_400()
    {
        var r = await _http.SendAsync(Peticion(HttpMethod.Post, "/api/sdk/execute", new { tipoProceso = "no.existe", empresa = "EMPRESA1" }));
        var (e, _) = await Leer(r);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(CodigosError.SolicitudInvalida, e.Codigo);
        Assert.Contains(e.Detalles, d => d.Contains(Operaciones.ClienteCrear));
    }

    [Fact]
    public async Task Una_ruta_de_la_api_que_no_existe_responde_404_con_el_formato_estandar()
    {
        var r = await _http.SendAsync(Peticion(HttpMethod.Get, "/api/no-existe"));
        var (e, _) = await Leer(r);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(CodigosError.NoEncontrado, e.Codigo);
    }

    [Fact]
    public async Task El_login_incorrecto_responde_401_con_el_formato_estandar()
    {
        var r = await _http.PostAsJsonAsync("/api/auth/login", new { correo = "nadie@ejemplo.com", contrasena = "incorrecta123" });
        var (e, _) = await Leer(r);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(CodigosError.NoAutorizado, e.Codigo);
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

            public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? ex, Func<TState, Exception?, string> formato)
            {
                var ambitos = new List<string>();
                padre._ambitos.ForEachScope((a, lista) => lista.Add(string.Join(",", (a as IEnumerable<KeyValuePair<string, object>>)?.Select(p => $"{p.Key}={p.Value}") ?? [])), ambitos);
                lock (padre.Lineas)
                {
                    padre.Lineas.Add($"{nivel}|{formato(estado, ex)}|{string.Join(";", ambitos)}");
                }
            }
        }
    }

    private static async Task<(WebApplication App, Captura Logs, HttpClient Http)> Arrancar()
    {
        var logs = new Captura();
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Logging.ClearProviders();
        b.Logging.AddProvider(logs);
        Errores.SinRuido(b.Logging);
        var app = b.Build();
        Errores.Usar(app);
        app.MapGet("/api/ok", () => Results.Ok(new { ok = true }));
        app.MapGet("/api/falla", () => Results.StatusCode(404));
        app.MapGet("/api/revienta", object () => throw new InvalidOperationException("Falló con password=claveFalsa y la llave ak_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        await app.StartAsync();
        return (app, logs, app.GetTestClient());
    }

    [Fact]
    public async Task Cada_linea_de_la_peticion_lleva_el_id_de_correlacion()
    {
        var (app, logs, http) = await Arrancar();
        await using var _ = app;
        var r = await http.GetAsync("/api/ok");
        var id = r.Headers.GetValues(Correlacion.Cabecera).Single();

        var linea = Assert.Single(logs.Lineas, l => l.Contains("GET /api/ok"));
        Assert.Contains($"IdCorrelacion={id}", linea);
        Assert.Contains("-> 200", linea);
    }

    [Fact]
    public async Task El_log_no_guarda_cabeceras_cuerpo_ni_query_string()
    {
        var (app, logs, http) = await Arrancar();
        await using var _ = app;
        var p = new HttpRequestMessage(HttpMethod.Get, "/api/ok?token=claveFalsa");
        p.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "ak_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await http.SendAsync(p);

        var todo = string.Join("\n", logs.Lineas);
        Assert.DoesNotContain("claveFalsa", todo);
        Assert.DoesNotContain("ak_aaaaaaaaaa", todo);
        Assert.DoesNotContain("Bearer", todo);
    }

    [Fact]
    public async Task Una_excepcion_responde_500_estandar_y_el_log_no_contiene_secretos()
    {
        var (app, logs, http) = await Arrancar();
        await using var _ = app;
        var r = await http.GetAsync("/api/revienta");
        var crudo = await r.Content.ReadAsStringAsync();
        var e = JsonSerializer.Deserialize<ErrorApi>(crudo, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal(CodigosError.ErrorInterno, e.Codigo);
        Assert.Equal(r.Headers.GetValues(Correlacion.Cabecera).Single(), e.IdCorrelacion);
        Assert.DoesNotContain("claveFalsa", crudo);

        var todo = string.Join("\n", logs.Lineas);
        Assert.Contains("Error no controlado", todo);
        Assert.DoesNotContain("claveFalsa", todo);
        Assert.DoesNotContain("ak_aaaaaaaaaa", todo);
    }

    [Fact]
    public async Task Un_error_de_cliente_se_registra_como_aviso_y_uno_de_servidor_como_error()
    {
        var (app, logs, http) = await Arrancar();
        await using var _ = app;
        await http.GetAsync("/api/falla");
        await http.GetAsync("/api/revienta");

        Assert.Contains(logs.Lineas, l => l.StartsWith("Warning|GET /api/falla"));
        Assert.Contains(logs.Lineas, l => l.StartsWith("Error|GET /api/revienta -> 500"));
    }
}
