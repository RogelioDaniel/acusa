using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Acusa.Worker.Desktop;

/// <summary>Configuración del worker en este equipo. Nada secreto: las contraseñas viven cifradas en <see cref="Secretos"/>.</summary>
public sealed class Config
{
    public bool Configurado { get; set; }
    public string Url { get; set; } = "http://127.0.0.1:5090";
    public string Correo { get; set; } = "";
    public string EquipoId { get; set; } = "";

    // Conexión con CONTPAQi
    public string SqlServidor { get; set; } = @"localhost\COMPAC";
    public bool SqlWindows { get; set; }
    public string SqlUsuario { get; set; } = "sa";
    public bool SqlOmitido { get; set; }
    public string UsuarioSdk { get; set; } = "SUPERVISOR";
    public string NombrePaq { get; set; } = "CONTPAQ I COMERCIAL";
    public string RutaSdk { get; set; } = @"C:\Program Files (x86)\Compac\COMERCIAL";
    public bool InicioSinDialogo { get; set; }

    // Empresas
    public List<string> Empresas { get; set; } = new();
    public string Empresa { get; set; } = "adEMPRESA_DE_PRUEBA";   // la principal: la que atiende este worker

    // Seguridad de escritura
    public bool Simulado { get; set; }
    public bool Documentos { get; set; }
    public bool Timbrado { get; set; }
    public bool CsdEnComercial { get; set; }
    public string Clientes { get; set; } = "";

    public static readonly string Carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Acusa");
    private static readonly string Archivo = Path.Combine(Carpeta, "worker-desktop.json");
    private static readonly JsonSerializerOptions Opciones = new() { WriteIndented = true };

    public static Config Cargar()
    {
        try
        {
            if (File.Exists(Archivo)) return JsonSerializer.Deserialize<Config>(File.ReadAllText(Archivo)) ?? new Config();
        }
        catch { /* ilegible: valores por omisión */ }

        return new Config();
    }

    public void Guardar()
    {
        try
        {
            Directory.CreateDirectory(Carpeta);
            File.WriteAllText(Archivo, JsonSerializer.Serialize(this, Opciones));
        }
        catch { /* sin permiso de escritura: no es crítico */ }
    }

    public static string RutaArchivo => Archivo;
}

/// <summary>
/// Secretos del worker cifrados con DPAPI para el usuario de Windows (como guarda Montek su configuración). Solo se guarda
/// la llave del worker; las contraseñas de SQL y del SDK se piden cuando se usan y no se escriben en disco.
/// </summary>
public static class Secretos
{
    private static readonly string Archivo = Path.Combine(Config.Carpeta, "worker-llave.dat");

    public static void GuardarLlave(string llave)
    {
        Directory.CreateDirectory(Config.Carpeta);
        var cifrado = ProtectedData.Protect(Encoding.UTF8.GetBytes(llave), Encoding.UTF8.GetBytes("acusa-worker"), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Archivo, cifrado);
    }

    public static string? LeerLlave()
    {
        try
        {
            if (!File.Exists(Archivo)) return null;
            var plano = ProtectedData.Unprotect(File.ReadAllBytes(Archivo), Encoding.UTF8.GetBytes("acusa-worker"), DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plano);
        }
        catch { return null; }
    }

    public static void BorrarLlave()
    {
        try { if (File.Exists(Archivo)) File.Delete(Archivo); } catch { }
    }
}
