using System;
using System.Linq;

namespace Acusa.Worker.Desktop;

public partial class App : System.Windows.Application
{
    private System.Threading.Mutex? _unico;

    /// <summary>Nombre global: vale para todo el servidor, no solo para la sesión de Windows de quien lo abre.</summary>
    private const string NombreMutex = @"Global\Acusa.Worker.Desktop.Control";

    private const string NombreMutexVentana = @"Global\Acusa.Worker.Desktop.Ventana";

    private System.Threading.Mutex? _ventanaUnica;

    private const string NombreAvisoMostrar = @"Global\Acusa.Worker.Desktop.Mostrar";

    private System.Threading.EventWaitHandle? _avisoMostrar;

    private MainWindow? _ventana;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        var ventanaLibre = false;
        try
        {
            _ventanaUnica = new System.Threading.Mutex(true, NombreMutexVentana, out ventanaLibre);
        }
        catch (UnauthorizedAccessException)
        {
            ventanaLibre = false;
        }

        if (!ventanaLibre)
        {
            _ventanaUnica?.Dispose();
            _ventanaUnica = null;
            var mostrada = false;
            try
            {
                using var avisoExistente = System.Threading.EventWaitHandle.OpenExisting(NombreAvisoMostrar);
                mostrada = avisoExistente.Set();
            }
            catch (Exception excepcion) when (excepcion is System.Threading.WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
                mostrada = false;
            }

            var mensaje = "Acusa Worker ya está abierto en este servidor.";
            if (mostrada)
            {
                mensaje = "Acusa Worker ya está abierto en este servidor. Se mostró la ventana que ya estaba abierta.";
            }

            System.Windows.MessageBox.Show(
                mensaje,
                "Acusa Worker",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }
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
        _ventana = ventana;
        EscucharAvisoMostrar();
        if (!automatico || !puedeControlar) ventana.Show();
    }

    private void EscucharAvisoMostrar()
    {
        try
        {
            _avisoMostrar = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, NombreAvisoMostrar);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var escucha = new System.Threading.Thread(() =>
        {
            while (_avisoMostrar!.WaitOne())
            {
                Dispatcher.Invoke(() => _ventana?.Mostrar());
            }
        })
        { IsBackground = true, Name = "Acusa.Desktop.Mostrar" };
        escucha.Start();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { _unico?.ReleaseMutex(); } catch { }
        _unico?.Dispose();
        try { _ventanaUnica?.ReleaseMutex(); } catch { }
        _ventanaUnica?.Dispose();
        base.OnExit(e);
    }
}
