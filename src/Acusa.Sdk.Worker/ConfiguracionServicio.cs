using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Acusa.Sdk.Worker;

/// <summary>
/// Configuración del worker cuando corre como servicio de Windows (cuenta SYSTEM, sin sesión de usuario ni variables de entorno).
/// La escribe la aplicación Acusa Worker en <c>C:\ProgramData\Acusa</c>: <c>worker-desktop.json</c> (sin secretos) y los archivos
/// <c>worker-llave.dat</c>, <c>worker-csd.dat</c> y <c>worker-sdk.dat</c>, cifrados con DPAPI de la máquina y con permisos solo para
/// SYSTEM, Administradores y quien configuró. Aquí se traduce a los mismos valores que hoy pasa la aplicación por variables de entorno.
/// </summary>
public static class ConfiguracionServicio
{
    private static readonly string CarpetaServidor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Acusa");
    private static readonly byte[] Sal = Encoding.UTF8.GetBytes("acusa-worker");

    /// <summary>Agrega la configuración compartida y devuelve un resumen para la bitácora (sin secretos), o null si no hay configuración.</summary>
    public static string? Aplicar(IConfigurationBuilder configuracion) => Aplicar(configuracion, CarpetaServidor);

    /// <summary>Igual que <see cref="Aplicar(IConfigurationBuilder)"/>, leyendo la configuración de <paramref name="carpeta"/>.</summary>
    public static string? Aplicar(IConfigurationBuilder configuracion, string carpeta)
    {
        var archivo = Path.Combine(carpeta, "worker-desktop.json");
        if (!File.Exists(archivo)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(archivo));
        var c = doc.RootElement;
        if (!Bool(c, "Configurado")) return null;

        var v = new Dictionary<string, string?>();
        var empresa = Texto(c, "Empresa");
        var simulado = Bool(c, "Simulado");
        v["Orquestador:Url"] = Texto(c, "Url");
        v["Orquestador:LlaveWorker"] = Leer(carpeta, "worker-llave.dat");
        v["Contpaqi:RutaEmpresa"] = Path.Combine(@"C:\Compac\Empresas", empresa);

        var otras = Lista(c, "Empresas").Where(x => !string.Equals(x, empresa, StringComparison.OrdinalIgnoreCase)).ToList();
        for (var i = 0; i < otras.Count; i++) v[$"Contpaqi:Empresas:{i}"] = otras[i];

        if (simulado)
        {
            v["Contpaqi:Modo"] = "Simulado";
            v["Contpaqi:PermitirEscritura"] = "true"; v["Contpaqi:PermitirDocumentos"] = "true"; v["Contpaqi:PermitirTimbrado"] = "true";
        }
        else
        {
            v["Contpaqi:Modo"] = "Real";
            // Sin sesión de usuario no hay diálogo de Comercial: el servicio siempre abre el SDK con usuario y contraseña.
            v["Contpaqi:Inicio"] = "Credenciales";
            v["Contpaqi:Usuario"] = Texto(c, "UsuarioSdk");
            v["Contpaqi:NombrePaq"] = Texto(c, "NombrePaq");
            v["Contpaqi:RutaSdk"] = Texto(c, "RutaSdk");
            // Todas las empresas que atiende el worker admiten escritura; documentos y timbrado los decide la configuración.
            v["Contpaqi:PermitirEscritura"] = "true";
            var timbrado = Bool(c, "Timbrado");
            v["Contpaqi:PermitirDocumentos"] = (Bool(c, "Documentos") || timbrado) ? "true" : "false";
            v["Contpaqi:PermitirTimbrado"] = timbrado ? "true" : "false";
            if (timbrado)
            {
                if (Bool(c, "CsdEnComercial")) v["Contpaqi:CertificadoGuardadoEnComercial"] = "true";
                else if (Leer(carpeta, "worker-csd.dat") is { Length: > 0 } csd) Environment.SetEnvironmentVariable("ACUSA_CLAVE_CERTIFICADO", csd);
            }

            if (Leer(carpeta, "worker-sdk.dat") is { Length: > 0 } sdk) Environment.SetEnvironmentVariable("ACUSA_CLAVE_SDK", sdk);
        }

        configuracion.AddInMemoryCollection(v);
        return $"Configuración del servidor: {(simulado ? "simulado" : "empresa " + empresa)}, Central {v["Orquestador:Url"]}, " +
               $"llave {(string.IsNullOrEmpty(v["Orquestador:LlaveWorker"]) ? "AUSENTE" : "cargada")}.";
    }

    private static string Texto(JsonElement c, string nombre) => c.TryGetProperty(nombre, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    private static bool Bool(JsonElement c, string nombre) => c.TryGetProperty(nombre, out var p) && p.ValueKind == JsonValueKind.True;
    private static IEnumerable<string> Lista(JsonElement c, string nombre) =>
        c.TryGetProperty(nombre, out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];

    private static string? Leer(string carpeta, string nombre)
    {
        try
        {
            var ruta = Path.Combine(carpeta, nombre);
            if (!File.Exists(ruta)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(ruta), Sal, DataProtectionScope.LocalMachine));
        }
        catch { return null; }
    }
}
