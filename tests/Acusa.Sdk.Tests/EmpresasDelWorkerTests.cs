using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Acusa.Contrato;
using Acusa.Orquestador;
using Acusa.Sdk.Worker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acusa.Sdk.Tests;

public sealed class EmpresasDelWorkerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "acusa-empresas-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly WebApplicationFactory<Seguridad> _fabrica;
    private readonly HttpClient _http;
    private readonly Almacen _almacen;
    private readonly string _cliente;
    private readonly string _llaveWorker;

    public EmpresasDelWorkerTests()
    {
        _fabrica = new WebApplicationFactory<Seguridad>().WithWebHostBuilder(constructor =>
        {
            constructor.UseSetting("Orquestador:BaseDatos", _base);
            constructor.UseSetting("Orquestador:SecretoJwt", "secreto-de-prueba-con-mas-de-32-caracteres!!");
            constructor.UseSetting("Orquestador:ClaveAdmin", "clave-admin-de-prueba-16+");
        });
        _http = _fabrica.CreateClient();
        _almacen = _fabrica.Services.GetRequiredService<Almacen>();
        _cliente = _almacen.CrearCliente("Cliente");
        _llaveWorker = _almacen.CrearLlave(_cliente, Roles.Worker, "servidor");
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

    private Task<HttpResponseMessage> ConsultarAsync(string empresa)
    {
        var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/worker/poll") { Content = JsonContent.Create(new SolicitudPoll("SERVIDOR-1", empresa)) };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _llaveWorker);
        return _http.SendAsync(peticion);
    }

    [Fact]
    public async Task Poll_EmpresaNoRegistrada_LaRegistraParaElClienteDelWorker()
    {
        var respuesta = await ConsultarAsync("adEMPRESA_NUEVA");

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
        Assert.True(_almacen.EmpresaDelCliente(_cliente, "adEMPRESA_NUEVA"));
    }

    [Fact]
    public async Task Poll_NombreDeEmpresaMalFormado_Responde400SinRegistrarla()
    {
        var respuesta = await ConsultarAsync("a/b");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.False(_almacen.EmpresaDelCliente(_cliente, "a/b"));
    }

    [Fact]
    public async Task ConsultarAsync_CentralRechazaLaEmpresa_LanzaEmpresaRechazada()
    {
        using var http = new HttpClient(new RespuestaFija()) { BaseAddress = new Uri("http://central.local/") };
        var orquestador = new ClienteOrquestador(http, Options.Create(new OpcionesOrquestador { LlaveWorker = "wk_x" }));

        var error = await Assert.ThrowsAsync<EmpresaRechazadaException>(() => orquestador.ConsultarAsync("SERVIDOR-1", "adOTRA", default));

        Assert.Equal("adOTRA", error.Empresa);
    }

    private sealed class RespuestaFija : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/auth/token", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new TokenRespuesta("token", 3600)) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"mensaje\":\"empresa\"}") });
        }
    }
}
