using Acusa.Sdk.Application;
using Acusa.Sdk.Worker;
using Microsoft.Extensions.Configuration;

namespace Acusa.Sdk.Tests;

public sealed class ConfiguracionServicioTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "acusa-config-" + Guid.NewGuid().ToString("N"));

    public ConfiguracionServicioTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    private OpcionesContpaqi Cargar(string json)
    {
        File.WriteAllText(Path.Combine(_carpeta, "worker-desktop.json"), json);
        var constructor = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true);
        Assert.NotNull(ConfiguracionServicio.Aplicar(constructor, _carpeta));
        return constructor.Build().GetSection(OpcionesContpaqi.Seccion).Get<OpcionesContpaqi>()!;
    }

    [Fact]
    public void Aplicar_ServidorReal_IniciaConCredencialesSinDialogo()
    {
        var opciones = Cargar("""
            {"Configurado":true,"Url":"https://central","Empresa":"adEMPRESA_A","Empresas":["adEMPRESA_A","adEMPRESA_B"],
             "UsuarioSdk":"SUPERVISOR","NombrePaq":"CONTPAQ I COMERCIAL","RutaSdk":"C:\\Program Files (x86)\\Compac\\COMERCIAL",
             "Simulado":false,"Documentos":true,"Timbrado":false}
            """);

        Assert.Equal("Real", opciones.Modo);
        Assert.True(opciones.UsaCredenciales);
        Assert.Equal("SUPERVISOR", opciones.Usuario);
        Assert.Equal(@"C:\Program Files (x86)\Compac\COMERCIAL", opciones.RutaSdk);
        Assert.Equal(["adEMPRESA_A", "adEMPRESA_B"], opciones.EmpresasAtendidas);
        Assert.True(opciones.PermitirEscritura);
        Assert.True(opciones.PermitirDocumentos);
        Assert.False(opciones.PermitirTimbrado);
    }

    [Fact]
    public void Aplicar_SinConfigurar_NoAplicaNada()
    {
        File.WriteAllText(Path.Combine(_carpeta, "worker-desktop.json"), """{"Configurado":false}""");

        Assert.Null(ConfiguracionServicio.Aplicar(new ConfigurationBuilder(), _carpeta));
    }
}
