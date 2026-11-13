using System;
using System.Linq;

namespace Acusa.Worker.Desktop;

public partial class App : System.Windows.Application
{
    private System.Threading.Mutex? _unico;

    /// <summary>Nombre global: vale para todo el servidor, no solo para la sesión de Windows de quien lo abre.</summary>
    private const string NombreMutex = @"Global\Acusa.Worker.Desktop.Control";

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var automatico = e.Args.Contains("--auto", StringComparer.OrdinalIgnoreCase);
        var cfg = Config.Cargar();
        var puedeControlar = Acceso.PuedeControlar(cfg);
        string? aviso = null;

        // Solo una aplicación con control por servidor: el worker habla con el SDK de CONTPAQi, que admite un solo proceso.
        // Quien no puede controlar, o llega cuando ya hay otra sesión controlando, abre la ventana en modo de solo lectura.
        if (puedeControlar)
        {
            try
            {
                _unico = new System.Threading.Mutex(true, NombreMutex, out var nuevo);
                if (!nuevo)
                {
                    _unico.Dispose(); _unico = null;
                    puedeControlar = false;
                    aviso = "Acusa Worker ya está en ejecución en otra sesión de Windows de este servidor. Esta ventana es de solo lectura.";
                }
            }
            catch (UnauthorizedAccessException)
            {
                puedeControlar = false;
                aviso = "Acusa Worker ya está en ejecución en otra sesión de Windows de este servidor. Esta ventana es de solo lectura.";
            }
        }
        else
        {
            aviso = cfg.Configurado
                ? $"Solo lectura: la configuración y el control del worker son de {(string.IsNullOrEmpty(cfg.PropietarioNombre) ? "un administrador" : cfg.PropietarioNombre)}."
                : "Acusa Worker todavía no está configurado. Debe configurarlo un administrador.";
        }

        // Versiones anteriores abrían la aplicación al iniciar sesión; ahora el worker es un servicio y eso ya no hace falta.
        try { using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); run?.DeleteValue("AcusaWorker", false); } catch { }

        // Primera vez en este servidor: el asistente de configuración. Al terminar, el servicio se inicia solo.
        var iniciarServicio = false;
        if (puedeControlar && !cfg.Configurado)
        {
            var asistente = new Asistente(cfg);
            asistente.ShowDialog();
            iniciarServicio = asistente.Instalado;
        }

        var ventana = new MainWindow(!puedeControlar, aviso, iniciarServicio);
        if (!automatico || !puedeControlar) ventana.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { _unico?.ReleaseMutex(); } catch { }
        _unico?.Dispose();
        base.OnExit(e);
    }
}
