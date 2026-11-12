using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Acusa.Worker.Desktop;

/// <summary>
/// El worker como servicio de Windows <c>AcusaWorker</c>: arranca con el servidor, corre en la sesión 0 (sin necesitar a nadie
/// conectado), se reinicia solo si falla y no admite una segunda copia. Consultar su estado puede cualquier usuario; instalarlo,
/// iniciarlo, detenerlo o quitarlo exige un administrador (aparece el aviso de permisos de Windows).
/// </summary>
public static class ServicioWindows
{
    public const string Nombre = "AcusaWorker";

    public enum Estado { NoInstalado, Detenido, Iniciando, EnEjecucion, Deteniendo, Desconocido }

    /// <summary>Estado leído con <c>sc query</c>, que Windows permite a cualquier usuario.</summary>
    public static Estado Consultar()
    {
        var salida = Ejecutar("sc.exe", $"query {Nombre}", elevar: false, out var codigo);
        if (codigo == 1060 || salida.Contains("1060")) return Estado.NoInstalado;
        if (salida.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return Estado.EnEjecucion;
        if (salida.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)) return Estado.Iniciando;
        if (salida.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase)) return Estado.Deteniendo;
        if (salida.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return Estado.Detenido;
        return Estado.Desconocido;
    }

    public static bool Instalado => Consultar() != Estado.NoInstalado;

    /// <summary>Registra el servicio: inicio automático, después de SQL Server de COMPAC, reinicio automático si se cae.</summary>
    public static string Instalar(string rutaWorker)
    {
        var guion = new StringBuilder();
        guion.AppendLine("$ErrorActionPreference = 'Stop'");
        guion.AppendLine($"$exe = '{rutaWorker.Replace("'", "''")}'");
        guion.AppendLine($"if (Get-Service {Nombre} -ErrorAction SilentlyContinue) {{ sc.exe stop {Nombre} | Out-Null; Start-Sleep 3; sc.exe delete {Nombre} | Out-Null; Start-Sleep 2 }}");
        guion.AppendLine($"New-Service -Name {Nombre} -BinaryPathName \"`\"$exe`\" --servicio\" -DisplayName 'Acusa Worker' -Description 'Ejecuta en CONTPAQi Comercial las ordenes de Acusa Central.' -StartupType Automatic | Out-Null");
        guion.AppendLine($"sc.exe config {Nombre} start= delayed-auto obj= LocalSystem | Out-Null");
        guion.AppendLine($"sc.exe failure {Nombre} reset= 86400 actions= restart/15000/restart/30000/restart/60000 | Out-Null");
        guion.AppendLine($"sc.exe failureflag {Nombre} 1 | Out-Null");
        // Si SQL Server de COMPAC existe como servicio, el worker espera a que esté arriba.
        guion.AppendLine($"if (Get-Service 'MSSQL$COMPAC' -ErrorAction SilentlyContinue) {{ sc.exe config {Nombre} depend= 'MSSQL$COMPAC' | Out-Null }}");
        return EjecutarGuion(guion.ToString());
    }

    public static string Iniciar() => EjecutarGuion($"sc.exe start {Nombre}");
    public static string Detener() => EjecutarGuion($"sc.exe stop {Nombre}");

    public static string Quitar() => EjecutarGuion($"sc.exe stop {Nombre} | Out-Null; Start-Sleep 3; sc.exe delete {Nombre}");

    /// <summary>Corre PowerShell con permisos de administrador (UAC) y espera a que termine. Devuelve un texto breve del resultado.</summary>
    private static string EjecutarGuion(string guion)
    {
        try
        {
            var codificado = Convert.ToBase64String(Encoding.Unicode.GetBytes(guion));
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {codificado}")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(60000);
            return p is { HasExited: true, ExitCode: 0 } ? "" : "Windows no completó la operación (¿se canceló el aviso de permisos?).";
        }
        catch (System.ComponentModel.Win32Exception) { return "Se canceló el aviso de permisos de Windows."; }
        catch (Exception ex) { return ex.Message; }
    }

    private static string Ejecutar(string exe, string args, bool elevar, out int codigo)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var texto = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            codigo = p.ExitCode;
            return texto;
        }
        catch { codigo = -1; return ""; }
    }
}
