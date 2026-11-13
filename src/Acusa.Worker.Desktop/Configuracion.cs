using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Acusa.Worker.Desktop;

/// <summary>
/// Configuración del worker en este servidor. Vive en <c>C:\ProgramData\Acusa</c>, no en el perfil de un usuario: así todos los
/// usuarios de Windows ven el mismo estado y la misma configuración. Nada secreto: las contraseñas viven cifradas en <see cref="Secretos"/>.
/// </summary>
public sealed class Config
{
    public bool Configurado { get; set; }
    public string Url { get; set; } = "https://acusa-i331.onrender.com";
    public string Correo { get; set; } = "";
    public string EquipoId { get; set; } = "";

    /// <summary>SID del usuario de Windows que configuró el worker. Solo él y los administradores pueden cambiarlo, iniciarlo o detenerlo.</summary>
    public string PropietarioSid { get; set; } = "";
    public string PropietarioNombre { get; set; } = "";
    /// <summary>Abrir y arrancar el worker solo al iniciar sesión el propietario.</summary>
    public bool IniciarConWindows { get; set; }
    /// <summary>Guardar la contraseña del certificado cifrada en este equipo, para que el worker timbre tras un reinicio automático.</summary>
    public bool RecordarCsd { get; set; }

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
    /// <summary>Empresas que se tratan como reales: admiten escritura y no exigen el prefijo PRUEBA. Las demás empresas marcadas son de consulta, salvo las de pruebas (nombre con PRUEBA).</summary>
    public List<string> EmpresasReales { get; set; } = new();
    public string Empresa { get; set; } = "";   // la principal: la que atiende este worker

    // Seguridad de escritura
    public bool Simulado { get; set; }
    public bool Documentos { get; set; }
    public bool Timbrado { get; set; }
    public bool CsdEnComercial { get; set; }
    public string Clientes { get; set; } = "";

    public static readonly string Carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Acusa");
    public static readonly string CarpetaLogs = Path.Combine(Carpeta, "logs");
    private static readonly string Archivo = Path.Combine(Carpeta, "worker-desktop.json");
    private static readonly string CarpetaAnterior = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Acusa");
    private static readonly JsonSerializerOptions Opciones = new() { WriteIndented = true };

    public static Config Cargar()
    {
        try
        {
            Migrar();
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

    /// <summary>Instalaciones anteriores guardaban la configuración y la llave en el perfil del usuario: se pasan a la carpeta compartida.</summary>
    private static void Migrar()
    {
        try
        {
            var viejo = Path.Combine(CarpetaAnterior, "worker-desktop.json");
            if (File.Exists(Archivo) || !File.Exists(viejo)) return;
            Directory.CreateDirectory(Carpeta);
            File.Copy(viejo, Archivo);
            var llaveVieja = Path.Combine(CarpetaAnterior, "worker-llave.dat");
            if (File.Exists(llaveVieja))
            {
                var plano = ProtectedData.Unprotect(File.ReadAllBytes(llaveVieja), Encoding.UTF8.GetBytes("acusa-worker"), DataProtectionScope.CurrentUser);
                Secretos.GuardarLlave(Encoding.UTF8.GetString(plano));
            }
        }
        catch { /* si no se puede migrar, el asistente vuelve a pedir los datos */ }
    }
}

/// <summary>Quién puede cambiar la configuración y controlar el worker. Los demás usuarios del servidor solo ven el estado.</summary>
public static class Acceso
{
    public static string SidActual => WindowsIdentity.GetCurrent().User?.Value ?? "";
    public static string NombreActual => WindowsIdentity.GetCurrent().Name;

    public static bool EsAdministrador()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    /// <summary>
    /// Sin configurar: solo un administrador puede abrir el asistente. Configurado: el propietario o un administrador. El propietario
    /// cuenta aunque la app no esté elevada (con UAC un administrador sin elevar no figura como administrador).
    /// </summary>
    public static bool PuedeControlar(Config c)
    {
        if (!c.Configurado || string.IsNullOrEmpty(c.PropietarioSid)) return EsAdministrador();
        return string.Equals(c.PropietarioSid, SidActual, StringComparison.OrdinalIgnoreCase) || EsAdministrador();
    }
}

/// <summary>
/// Secretos del worker cifrados con DPAPI a nivel de equipo y con permisos solo para SYSTEM, Administradores y el propietario, de modo
/// que el worker pueda reiniciarse solo sin que nadie escriba la contraseña, y que otros usuarios del servidor no puedan leerlos.
/// La contraseña del certificado solo se guarda si el administrador lo pide.
/// </summary>
public static class Secretos
{
    private static readonly string ArchivoLlave = Path.Combine(Config.Carpeta, "worker-llave.dat");
    private static readonly string ArchivoCsd = Path.Combine(Config.Carpeta, "worker-csd.dat");
    private static readonly byte[] Sal = Encoding.UTF8.GetBytes("acusa-worker");

    public static void GuardarLlave(string llave) => Guardar(ArchivoLlave, llave);
    public static string? LeerLlave() => Leer(ArchivoLlave);
    public static void BorrarLlave() => Borrar(ArchivoLlave);

    public static void GuardarCsd(string clave) => Guardar(ArchivoCsd, clave);
    public static string? LeerCsd() => Leer(ArchivoCsd);
    public static void BorrarCsd() => Borrar(ArchivoCsd);

    /// <summary>Contraseña del usuario del SDK de Comercial: el servicio la necesita porque, sin sesión de usuario, no hay diálogo de ingreso.</summary>
    private static readonly string ArchivoSdk = Path.Combine(Config.Carpeta, "worker-sdk.dat");
    public static void GuardarClaveSdk(string clave) => Guardar(ArchivoSdk, clave);
    public static string? LeerClaveSdk() => Leer(ArchivoSdk);
    public static void BorrarClaveSdk() => Borrar(ArchivoSdk);

    private static void Guardar(string ruta, string texto)
    {
        Directory.CreateDirectory(Config.Carpeta);
        var cifrado = ProtectedData.Protect(Encoding.UTF8.GetBytes(texto), Sal, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(ruta, cifrado);
        Restringir(ruta);
    }

    private static string? Leer(string ruta)
    {
        try
        {
            if (!File.Exists(ruta)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(ruta), Sal, DataProtectionScope.LocalMachine));
        }
        catch { return null; }
    }

    private static void Borrar(string ruta)
    {
        try { if (File.Exists(ruta)) File.Delete(ruta); } catch { }
    }

    private static void Restringir(string ruta)
    {
        try
        {
            var info = new FileInfo(ruta);
            var seguridad = info.GetAccessControl();
            seguridad.SetAccessRuleProtection(true, false);
            foreach (FileSystemAccessRule regla in seguridad.GetAccessRules(true, false, typeof(SecurityIdentifier))) seguridad.RemoveAccessRule(regla);
            foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), WindowsIdentity.GetCurrent().User! })
            {
                seguridad.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            }

            info.SetAccessControl(seguridad);
        }
        catch { /* sin permiso para cambiar permisos: el cifrado sigue protegiendo el contenido de otros equipos */ }
    }
}
