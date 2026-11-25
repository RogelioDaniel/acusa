using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Acusa.Contrato;
using Acusa.Orquestador;
using Acusa.Sdk.Worker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acusa.Sdk.Tests;

internal sealed class FirmanteDePrueba : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string ClavePublica => Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());

    public ManifiestoActualizacion Firmar(string version, byte[] paquete)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(paquete));
        var texto = Encoding.UTF8.GetBytes(FirmaActualizaciones.TextoFirmado(version, sha256, paquete.Length));
        var firma = Convert.ToBase64String(_rsa.SignData(texto, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return new ManifiestoActualizacion(version, sha256, paquete.Length, firma, DateTime.UtcNow);
    }

    public static byte[] PaqueteCompleto()
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var nombre in CarpetaActualizaciones.ArchivosRequeridos)
            {
                using var escritor = new StreamWriter(zip.CreateEntry(nombre).Open());
                escritor.Write(nombre);
            }
        }

        return memoria.ToArray();
    }

    public void Dispose() => _rsa.Dispose();
}

public sealed class FirmaActualizacionesTests : IDisposable
{
    private readonly FirmanteDePrueba _firmante = new();

    public void Dispose() => _firmante.Dispose();

    [Fact]
    public void Verificar_FirmaDeLaLlaveAutorizada_EsValida()
    {
        var manifiesto = _firmante.Firmar("1.2.0", [1, 2, 3]);

        Assert.True(FirmaActualizaciones.Verificar(manifiesto, _firmante.ClavePublica));
    }

    [Fact]
    public void Verificar_PaqueteOVersionCambiados_EsInvalida()
    {
        var manifiesto = _firmante.Firmar("1.2.0", [1, 2, 3]);

        Assert.False(FirmaActualizaciones.Verificar(manifiesto with { Version = "9.9.9" }, _firmante.ClavePublica));
        Assert.False(FirmaActualizaciones.Verificar(manifiesto with { Tamano = 4 }, _firmante.ClavePublica));
        Assert.False(FirmaActualizaciones.Verificar(manifiesto with { Firma = "no-es-base64" }, _firmante.ClavePublica));
    }

    [Fact]
    public void Verificar_OtraLlave_EsInvalida()
    {
        using var otro = new FirmanteDePrueba();
        var manifiesto = otro.Firmar("1.2.0", [1, 2, 3]);

        Assert.False(FirmaActualizaciones.Verificar(manifiesto, _firmante.ClavePublica));
        Assert.False(FirmaActualizaciones.Verificar(manifiesto));
    }

    [Theory]
    [InlineData("1.2.0", "1.1.1", true)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("1.1.1", "1.1.1", false)]
    [InlineData("1.0.0", "1.1.1", false)]
    [InlineData("basura", "1.1.1", false)]
    public void EsMasNueva_ComparaComoVersion(string candidata, string actual, bool esperado) =>
        Assert.Equal(esperado, FirmaActualizaciones.EsMasNueva(candidata, actual));

    [Theory]
    [InlineData(14, false, false)]
    [InlineData(15, false, true)]
    [InlineData(40, true, false)]
    public void EsMomentoDeAplicar_EvitaMinutos00a14YTareasEnCurso(int minuto, bool ocupado, bool esperado) =>
        Assert.Equal(esperado, Actualizador.EsMomentoDeAplicar(new DateTimeOffset(2026, 11, 24, 10, minuto, 0, TimeSpan.Zero), ocupado));
}

public sealed class VersionesWorkerApiTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "acusa-versiones-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly FirmanteDePrueba _firmante = new();
    private readonly WebApplicationFactory<Seguridad> _fabrica;
    private readonly HttpClient _http;
    private readonly string _llaveWorker;

    public VersionesWorkerApiTests()
    {
        _fabrica = new WebApplicationFactory<Seguridad>().WithWebHostBuilder(constructor =>
        {
            constructor.UseSetting("Orquestador:BaseDatos", _base);
            constructor.UseSetting("Orquestador:SecretoJwt", "secreto-de-prueba-con-mas-de-32-caracteres!!");
            constructor.UseSetting("Orquestador:ClaveAdmin", "clave-admin-de-prueba-16+");
            constructor.UseSetting("Orquestador:ClavePublicaActualizaciones", _firmante.ClavePublica);
        });
        _http = _fabrica.CreateClient();
        var almacen = _fabrica.Services.GetRequiredService<Almacen>();
        _llaveWorker = almacen.CrearLlave(almacen.CrearCliente("Cliente"), Roles.Worker, "servidor");
    }

    public void Dispose()
    {
        _http.Dispose();
        _fabrica.Dispose();
        _firmante.Dispose();
        foreach (var archivo in Directory.GetFiles(Path.GetDirectoryName(_base)!, Path.GetFileName(_base) + "*"))
        {
            try { File.Delete(archivo); } catch (IOException) { }
        }
    }

    private Task<HttpResponseMessage> PublicarAsync(string version, byte[] paquete, string firma, string claveAdmin = "clave-admin-de-prueba-16+")
    {
        var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/admin/worker/versiones") { Content = new ByteArrayContent(paquete) };
        peticion.Headers.Add("x-admin-key", claveAdmin);
        peticion.Headers.Add("x-version", version);
        peticion.Headers.Add("x-firma", firma);
        return _http.SendAsync(peticion);
    }

    private Task<HttpResponseMessage> ComoWorkerAsync(string ruta)
    {
        var peticion = new HttpRequestMessage(HttpMethod.Get, ruta);
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _llaveWorker);
        return _http.SendAsync(peticion);
    }

    [Fact]
    public async Task Publicar_FirmaValida_ElWorkerLaVeYLaDescarga()
    {
        var paquete = FirmanteDePrueba.PaqueteCompleto();
        var manifiesto = _firmante.Firmar("1.2.0", paquete);

        var publicada = await PublicarAsync("1.2.0", paquete, manifiesto.Firma);
        var consulta = await ComoWorkerAsync("/api/worker/actualizacion?version=1.1.1");
        var descarga = await ComoWorkerAsync("/api/worker/actualizacion/1.2.0/paquete");

        Assert.Equal(HttpStatusCode.Created, publicada.StatusCode);
        Assert.Equal("1.2.0", (await consulta.Content.ReadFromJsonAsync<ManifiestoActualizacion>())!.Version);
        Assert.Equal(paquete, await descarga.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Consultar_YaTieneLaUltima_Responde204()
    {
        var paquete = FirmanteDePrueba.PaqueteCompleto();
        await PublicarAsync("1.2.0", paquete, _firmante.Firmar("1.2.0", paquete).Firma);

        var consulta = await ComoWorkerAsync("/api/worker/actualizacion?version=1.2.0");

        Assert.Equal(HttpStatusCode.NoContent, consulta.StatusCode);
    }

    [Fact]
    public async Task Publicar_FirmaDeOtraLlave_SeRechaza()
    {
        using var otro = new FirmanteDePrueba();
        var paquete = FirmanteDePrueba.PaqueteCompleto();

        var respuesta = await PublicarAsync("1.2.0", paquete, otro.Firmar("1.2.0", paquete).Firma);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ComoWorkerAsync("/api/worker/actualizacion?version=1.1.1")).StatusCode);
    }

    [Fact]
    public async Task Publicar_SinClaveAdmin_Responde401()
    {
        var paquete = FirmanteDePrueba.PaqueteCompleto();

        var respuesta = await PublicarAsync("1.2.0", paquete, _firmante.Firmar("1.2.0", paquete).Firma, claveAdmin: "incorrecta");

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }
}

public sealed class ActualizadorTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "acusa-actualizador-" + Guid.NewGuid().ToString("N"));
    private readonly FirmanteDePrueba _firmante = new();
    private readonly AplicadorEspia _aplicador = new();

    public void Dispose()
    {
        _firmante.Dispose();
        if (Directory.Exists(_carpeta)) Directory.Delete(_carpeta, recursive: true);
    }

    private sealed class AplicadorEspia : IAplicadorActualizacion
    {
        public List<(string Carpeta, string Version)> Aplicadas { get; } = [];

        public void Aplicar(string carpetaPaquete, string version) => Aplicadas.Add((carpetaPaquete, version));
    }

    private sealed class CentralFalsa(ManifiestoActualizacion? manifiesto, byte[] paquete) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ruta = request.RequestUri!.AbsolutePath;
            if (ruta.EndsWith("/api/auth/token", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new TokenRespuesta("t", 3600)) });
            }

            if (ruta.EndsWith("/paquete", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(paquete) });
            }

            return Task.FromResult(manifiesto is null
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(manifiesto) });
        }
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private Actualizador Crear(ManifiestoActualizacion? manifiesto, byte[] paquete, int minuto = 30, bool ocupado = false)
    {
        var http = new HttpClient(new CentralFalsa(manifiesto, paquete)) { BaseAddress = new Uri("http://central.local/") };
        var orquestador = new ClienteOrquestador(http, Options.Create(new OpcionesOrquestador { LlaveWorker = "wk_x" }));
        var estado = new EstadoWorker();
        if (ocupado) estado.Inicia("documento.emitir");
        return new Actualizador(orquestador, estado, new CarpetaActualizaciones(_carpeta), _aplicador, NullLogger<Actualizador>.Instance,
            new RelojFijo(new DateTimeOffset(2026, 11, 24, 10, minuto, 0, TimeSpan.Zero)), "1.1.1", _firmante.ClavePublica);
    }

    [Fact]
    public async Task RevisarAsync_VersionNuevaFirmada_DescargaVerificaYLaAplica()
    {
        var paquete = FirmanteDePrueba.PaqueteCompleto();

        var aplicada = await Crear(_firmante.Firmar("1.2.0", paquete), paquete).RevisarAsync(default);

        Assert.Equal("1.2.0", aplicada);
        var (carpeta, version) = Assert.Single(_aplicador.Aplicadas);
        Assert.Equal("1.2.0", version);
        Assert.True(File.Exists(Path.Combine(carpeta, "instalar.ps1")));
    }

    [Fact]
    public async Task RevisarAsync_FirmaDeOtraLlave_NoAplica()
    {
        using var otro = new FirmanteDePrueba();
        var paquete = FirmanteDePrueba.PaqueteCompleto();

        Assert.Null(await Crear(otro.Firmar("1.2.0", paquete), paquete).RevisarAsync(default));
        Assert.Empty(_aplicador.Aplicadas);
    }

    [Fact]
    public async Task RevisarAsync_PaqueteAlterado_NoAplica()
    {
        var paquete = FirmanteDePrueba.PaqueteCompleto();
        var manifiesto = _firmante.Firmar("1.2.0", paquete);
        var alterado = paquete.ToArray();
        alterado[^1] ^= 0xFF;

        Assert.Null(await Crear(manifiesto, alterado).RevisarAsync(default));
        Assert.Empty(_aplicador.Aplicadas);
    }

    [Fact]
    public async Task RevisarAsync_ConTareaEnCursoOMinuto00a14_Espera()
    {
        var paquete = FirmanteDePrueba.PaqueteCompleto();
        var manifiesto = _firmante.Firmar("1.2.0", paquete);

        Assert.Null(await Crear(manifiesto, paquete, ocupado: true).RevisarAsync(default));
        Assert.Null(await Crear(manifiesto, paquete, minuto: 5).RevisarAsync(default));
        Assert.Empty(_aplicador.Aplicadas);
    }

    [Fact]
    public async Task RevisarAsync_VersionQueYaFallo_NoSeReintenta()
    {
        var paquete = FirmanteDePrueba.PaqueteCompleto();
        Directory.CreateDirectory(_carpeta);
        File.WriteAllLines(Path.Combine(_carpeta, "fallidas.txt"), ["1.2.0"]);

        Assert.Null(await Crear(_firmante.Firmar("1.2.0", paquete), paquete).RevisarAsync(default));
        Assert.Empty(_aplicador.Aplicadas);
    }
}
