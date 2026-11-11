using System.Net;
using Acusa.Sdk.Application;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Documentos;
using Acusa.Sdk.Application.Productos;
using Acusa.Sdk.Application.Sesion;
using Acusa.Sdk.Application.Simulado;
using Acusa.Sdk.Integration.Contpaqi;
using Acusa.Sdk.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Acusa Worker: servicio de Windows que atiende UNA empresa de CONTPAQi y consulta a Acusa Central.
// Trae un panel local (Estado, Configuración y Bitácora) en http://127.0.0.1:5095, protegido con un código local.
// Configuración, de menor a mayor prioridad: appsettings.json, configuracion.json (la guarda el panel) y variables de
// entorno (Orquestador__*, Contpaqi__*). Con --simulado usa datos en memoria para probar sin CONTPAQi.
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(DatosLocales.RutaConfig, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Services.AddWindowsService(o => o.ServiceName = "AcusaWorker");

var bitacora = new BitacoraProvider(DatosLocales.RutaLogs);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddProvider(bitacora);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var simulado = args.Contains("--simulado", StringComparer.OrdinalIgnoreCase);
var opciones = builder.Configuration.GetSection(OpcionesContpaqi.Seccion).Get<OpcionesContpaqi>() ?? new OpcionesContpaqi();
if (simulado)
{
    opciones.Modo = "Simulado";
    opciones.PermitirEscritura = true;
    opciones.PermitirDocumentos = true;
    opciones.PermitirTimbrado = true;
}

var orquestadorOpc = builder.Configuration.GetSection(OpcionesOrquestador.Seccion).Get<OpcionesOrquestador>() ?? new OpcionesOrquestador();

// Los secretos guardados por el panel están cifrados con DPAPI; una variable de entorno, si existe, manda.
var llave = orquestadorOpc.LlaveWorker;
if (string.IsNullOrWhiteSpace(llave))
{
    llave = DatosLocales.Revelar(builder.Configuration["Protegidos:LlaveWorker"]) ?? string.Empty;
    orquestadorOpc.LlaveWorker = llave;
}

var claveSdk = DatosLocales.Revelar(builder.Configuration["Protegidos:ClaveSdk"]);
var claveCertificado = DatosLocales.Revelar(builder.Configuration["Protegidos:ClaveCertificado"]);

// Qué falta para poder trabajar. Sin configuración el worker no se detiene: abre el panel y espera.
string? faltante = null;
if (string.IsNullOrWhiteSpace(llave))
{
    faltante = "Falta la llave del worker.";
}
else if (string.IsNullOrWhiteSpace(opciones.RutaEmpresa))
{
    faltante = "Falta elegir la empresa que atenderá este worker.";
}
else if (!opciones.EsSimulado)
{
    faltante = !OperatingSystem.IsWindows()
        ? "El modo Real solo funciona en Windows con CONTPAQi Comercial instalado."
        : opciones.ValidarInicio() ?? opciones.ValidarEscritura() ?? opciones.ValidarTimbrado();
}

var configurado = faltante is null;
var empresa = string.IsNullOrWhiteSpace(opciones.RutaEmpresa) ? "(sin elegir)" : opciones.CarpetaEmpresa;
var varias = opciones.EmpresasAtendidas.Count > 1;
var workerId = !string.IsNullOrWhiteSpace(orquestadorOpc.WorkerId) ? orquestadorOpc.WorkerId.Trim() : varias ? Environment.MachineName : $"{Environment.MachineName}-{empresa}";

var estado = new EstadoWorker();
estado.Conexion(configurado ? "iniciando" : "sin configurar", faltante);
builder.Services.AddSingleton(estado);
builder.Services.AddSingleton(bitacora);
builder.Services.Configure<OpcionesOrquestador>(o =>
{
    o.Url = orquestadorOpc.Url;
    o.LlaveWorker = llave;
    o.WorkerId = workerId;
    o.ConsultaSegundos = orquestadorOpc.ConsultaSegundos;
    o.SondeoLargoSegundos = orquestadorOpc.SondeoLargoSegundos;
});

if (configurado)
{
    builder.Services.AddSingleton(opciones);
    // El modo real se decide por la empresa abierta en cada momento: el worker puede atender varias y solo las listadas en
    // Contpaqi:EmpresasReales dejan de exigir el prefijo de prueba.
    builder.Services.AddSingleton(sp =>
    {
        var sesionActual = sp.GetRequiredService<ISesionSdk>();
        return new PoliticaEscritura(
            opciones.PermitirEscritura, opciones.PrefijoPrueba, opciones.PermitirDocumentos, opciones.PermitirTimbrado, opciones.ClientesPermitidos,
            () => opciones.EsEmpresaReal(Path.GetFileName((sesionActual.Estado.Empresa ?? string.Empty).TrimEnd('\\', '/'))));
    });
    // La contraseña del certificado nunca viaja en una orden: sale del panel (cifrada), de ACUSA_CLAVE_CERTIFICADO o, en simulación, es ficticia.
    builder.Services.AddSingleton<IProveedorClaveCertificado>(new ClaveCertificadoProtegida(opciones.EsSimulado ? "simulado" : claveCertificado, opciones.CertificadoGuardadoEnComercial));
    builder.Services.AddHttpClient<ClienteOrquestador>((sp, http) =>
    {
        var url = sp.GetRequiredService<IOptions<OpcionesOrquestador>>().Value.Url;
        http.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
        http.Timeout = TimeSpan.FromSeconds(60);
    });

    if (opciones.EsSimulado)
    {
        var clientes = new ClienteGatewayEnMemoria(opciones.ListaPrecios);
        var productos = new ProductoGatewayEnMemoria();
        builder.Services.AddSingleton<ISesionSdk>(new SesionSimulada(opciones.RutaEmpresa));
        builder.Services.AddSingleton<IClienteGateway>(clientes);
        builder.Services.AddSingleton<IProductoGateway>(productos);
        builder.Services.AddSingleton<IDocumentoGateway>(new DocumentoGatewayEnMemoria(clientes, productos));
    }
    else
    {
        if (opciones.UsaCredenciales)
        {
            // La clave del SDK sale del panel (cifrada) o de ACUSA_CLAVE_SDK. Este inicio sin diálogo aún no se ha comprobado contra CONTPAQi real.
            builder.Services.AddSingleton<IProveedorClave>(new ClaveProtegida(claveSdk));
        }

        builder.Services.AddSingleton(sp => new TrazaSdk(sp.GetRequiredService<ILoggerFactory>().CreateLogger("SDK")));
        builder.Services.AddSingleton<SesionContpaqi>();
        builder.Services.AddSingleton<ISesionSdk>(sp => sp.GetRequiredService<SesionContpaqi>());
        builder.Services.AddSingleton<IClienteGateway, ClienteGatewayContpaqi>();
        builder.Services.AddSingleton<IProductoGateway, ProductoGatewayContpaqi>();
        builder.Services.AddSingleton<IDocumentoGateway, DocumentoGatewayContpaqi>();
    }

    builder.Services.AddSingleton<ClienteService>();
    builder.Services.AddSingleton<ProductoService>();
    builder.Services.AddSingleton<DocumentoService>();
    builder.Services.AddSingleton<Ejecutor>();
    builder.Services.AddHostedService(sp => new ServicioWorker(
        sp.GetRequiredService<ClienteOrquestador>(),
        sp.GetRequiredService<Ejecutor>(),
        sp.GetRequiredService<ISesionSdk>(),
        sp.GetRequiredService<EstadoWorker>(),
        configurado ? opciones.EmpresasAtendidas : new[] { empresa },
        opciones.RutaDe,
        opciones.EsSimulado ? (_ => true) : opciones.EscrituraEn,
        workerId,
        sp.GetRequiredService<IOptions<OpcionesOrquestador>>(),
        sp.GetRequiredService<ILogger<ServicioWorker>>()));
}

// El panel solo escucha en este equipo. Un puerto explícito evita heredar la variable Urls de otro programa.
var puerto = builder.Configuration.GetValue("Panel:Puerto", 5095);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, puerto));

var codigo = DatosLocales.CodigoPanel();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
PanelLocal.Mapear(app, new ContextoPanel(workerId, empresa, opciones.EsSimulado ? "Simulado" : "Real", orquestadorOpc.Url, configurado, codigo), estado, bitacora);

var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Panel");
log.LogInformation("Acusa Worker {Version}. Panel local: http://127.0.0.1:{Puerto}", ClienteOrquestador.VersionWorker, puerto);
// El código va solo a la consola: la bitácora se guarda en un archivo que otros usuarios del equipo podrían leer.
Console.WriteLine($"Código de acceso del panel: {codigo}");
if (!configurado)
{
    log.LogWarning("{Faltante} Abra el panel para configurarlo.", faltante);
}

app.Run();

/// <summary>Contraseña del certificado en memoria: la del panel (cifrada en disco) o <c>ACUSA_CLAVE_CERTIFICADO</c>.</summary>
internal sealed class ClaveCertificadoProtegida(string? clave, bool claveEnComercial = false) : IProveedorClaveCertificado
{
    public bool ContrasenaGuardadaEnComercial { get; } = claveEnComercial;

    public string? ObtenerClave() =>
        Environment.GetEnvironmentVariable("ACUSA_CLAVE_CERTIFICADO") is { Length: > 0 } entorno ? entorno : clave;
}

/// <summary>Clave del SDK en memoria: la del panel (cifrada en disco) o la variable de entorno <c>ACUSA_CLAVE_SDK</c>.</summary>
internal sealed class ClaveProtegida(string? clave) : IProveedorClave
{
    public string ObtenerClave() =>
        Environment.GetEnvironmentVariable("ACUSA_CLAVE_SDK") is { Length: > 0 } entorno ? entorno
        : clave ?? throw new InvalidOperationException("Falta la clave del SDK: guárdela en el panel o defina ACUSA_CLAVE_SDK.");
}
