using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Acusa.Worker.Desktop;

/// <summary>
/// Panel del servicio de Windows «AcusaWorker»: muestra su estado y su bitácora, guarda la configuración y las contraseñas (cifradas en
/// el servidor) y lo inicia o lo detiene. El worker vive en el servicio, no en esta ventana. Quien no es el propietario ni administrador
/// ve el estado y la bitácora, sin poder cambiar nada.
/// </summary>
public partial class MainWindow : Window
{
    private const int MaximoLineas = 1500;
    private readonly bool _soloLectura;
    private readonly bool _iniciarAlAbrir;
    private bool _saliendo;
    private int _hechas;
    private int _fallos;
    private readonly DispatcherTimer _vigilante = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Queue<string> _lineas = new();
    private long _posicionLog;
    private string _archivoLogLeido = "";
    private System.Windows.Forms.NotifyIcon? _bandeja;
    private System.Windows.Forms.ToolStripMenuItem? _menuIniciar;
    private System.Windows.Forms.ToolStripMenuItem? _menuDetener;
    private Config _config = new();

    public MainWindow(bool soloLectura = false, string? aviso = null, bool iniciarAlAbrir = false)
    {
        _soloLectura = soloLectura;
        _iniciarAlAbrir = iniciarAlAbrir;
        InitializeComponent();
        CargarConfiguracion();
        CrearBandeja();
        Closing += AlCerrar;
        if (!string.IsNullOrEmpty(aviso)) Subtitulo.Text = aviso;

        if (_soloLectura)
        {
            // Solo se ve el estado: ni configuración, ni secretos, ni botones de control.
            PanelConfig.IsEnabled = false;
            BotonIniciar.IsEnabled = false; BotonDetener.IsEnabled = false; BotonPanel.IsEnabled = false; BotonAsistente.IsEnabled = false;
            if (_menuIniciar != null) { _menuIniciar.Enabled = false; _menuDetener!.Enabled = false; }
        }

        _vigilante.Tick += (_, _) => Vigilar();
        _vigilante.Start();
        Vigilar();
        if (!_soloLectura && _iniciarAlAbrir) Loaded += (_, _) => IniciarServicio();
    }

    // ------------------------------------------------------------------ configuración

    private void CargarConfiguracion()
    {
        _config = Config.Cargar();
        var c = _config;
        CajaUrl.Text = c.Url;
        CheckDocumentos.IsChecked = c.Documentos; CheckTimbrado.IsChecked = c.Timbrado; CheckCsdComercial.IsChecked = c.CsdEnComercial;
        if (_soloLectura) return;   // los secretos no se cargan para quien solo mira
        var llave = Secretos.LeerLlave();
        if (!string.IsNullOrEmpty(llave)) CajaLlave.Password = llave;
    }

    private void GuardarConfiguracion()
    {
        var c = _config;
        c.Url = CajaUrl.Text.Trim();
        c.Documentos = CheckDocumentos.IsChecked == true; c.Timbrado = CheckTimbrado.IsChecked == true; c.CsdEnComercial = CheckCsdComercial.IsChecked == true;
        c.InicioSinDialogo = true;   // sin sesión de usuario no hay diálogo de Comercial: el servicio abre el SDK con usuario y contraseña
        if (string.IsNullOrEmpty(c.PropietarioSid)) { c.PropietarioSid = Acceso.SidActual; c.PropietarioNombre = Acceso.NombreActual; }
        c.Guardar();
    }

    /// <summary>Guarda lo que el servicio necesita leer en ProgramData: contraseñas cifradas con DPAPI de la máquina.</summary>
    private void GuardarSecretosServicio()
    {
        GuardarConfiguracion();
        if (!string.IsNullOrEmpty(CajaLlave.Password)) Secretos.GuardarLlave(CajaLlave.Password);
        if (!string.IsNullOrEmpty(CajaClaveSdk.Password)) Secretos.GuardarClaveSdk(CajaClaveSdk.Password);
        if (CheckTimbrado.IsChecked == true && CheckCsdComercial.IsChecked != true)
        {
            if (!string.IsNullOrEmpty(CajaCsd.Password)) { _config.RecordarCsd = true; _config.Guardar(); Secretos.GuardarCsd(CajaCsd.Password); }
            else if (Secretos.LeerCsd() is null) Linea("Aviso: el servicio no tiene la contraseña del certificado; no podrá timbrar. Escríbala y vuelva a iniciar.");
        }
    }

    // ------------------------------------------------------------------ servicio

    private static string? BuscarWorker()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Acusa.Sdk.Worker.exe");
        return File.Exists(exe) ? exe : null;
    }

    private void Iniciar_Click(object sender, RoutedEventArgs e) => IniciarServicio();
    private void Detener_Click(object sender, RoutedEventArgs e) => DetenerServicio();

    private void IniciarServicio()
    {
        try
        {
            if (!_config.Configurado) { MessageBox.Show("Primero complete el asistente de configuración.", "Acusa Worker", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            GuardarSecretosServicio();

            // El instalador ya lo registra; si por algo falta, se registra aquí (Windows pide permisos de administrador).
            if (!ServicioWindows.Instalado)
            {
                var exe = BuscarWorker();
                if (exe is null) { MessageBox.Show("No se encontró Acusa.Sdk.Worker.exe junto a esta aplicación.", "Acusa Worker"); return; }
                var fallo = ServicioWindows.Instalar(exe);
                if (!ServicioWindows.Instalado) { MessageBox.Show("No se pudo registrar el servicio. " + fallo, "Acusa Worker", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                Linea("--- Servicio AcusaWorker registrado ---");
            }

            // Si ya corre, se reinicia para que lea la configuración nueva.
            if (ServicioWindows.Consultar() is ServicioWindows.Estado.EnEjecucion or ServicioWindows.Estado.Iniciando)
            {
                Linea("--- Reiniciando el servicio para aplicar la configuración ---");
                ServicioWindows.Detener();
                System.Threading.Thread.Sleep(3000);
            }

            var r = ServicioWindows.Iniciar();
            Linea(string.IsNullOrEmpty(r) ? "--- Servicio AcusaWorker iniciado ---" : "No se pudo iniciar el servicio: " + r);
            CajaCsd.Clear(); CajaClaveSdk.Clear();
        }
        catch (Exception ex) { Linea("No se pudo iniciar el servicio: " + ex.Message); }

        ActualizarEstadoServicio();
    }

    private void DetenerServicio()
    {
        var r = ServicioWindows.Detener();
        Linea(string.IsNullOrEmpty(r) ? "--- Servicio AcusaWorker detenido ---" : "No se pudo detener el servicio: " + r);
        ActualizarEstadoServicio();
    }

    private void ActualizarEstadoServicio()
    {
        var e = ServicioWindows.Consultar();
        switch (e)
        {
            case ServicioWindows.Estado.EnEjecucion: PonerEstado("En ejecución", "#E0EEE7", "#0B6B50"); break;
            case ServicioWindows.Estado.Iniciando: PonerEstado("Iniciando…", "#FEF3C7", "#F59E0B"); break;
            case ServicioWindows.Estado.Deteniendo: PonerEstado("Deteniéndose…", "#FEF3C7", "#F59E0B"); break;
            case ServicioWindows.Estado.NoInstalado: PonerEstado("Sin instalar", "#F3F4F6", "#9CA3AF"); break;
            default: PonerEstado("Detenido", "#F3F4F6", "#9CA3AF"); break;
        }

        DatoTiempo.Text = e == ServicioWindows.Estado.EnEjecucion ? TiempoActivo() : "—";
        if (!_soloLectura)
        {
            BotonIniciar.IsEnabled = e is ServicioWindows.Estado.Detenido or ServicioWindows.Estado.NoInstalado;
            BotonDetener.IsEnabled = e is ServicioWindows.Estado.EnEjecucion or ServicioWindows.Estado.Iniciando;
            if (_menuIniciar != null) { _menuIniciar.Enabled = BotonIniciar.IsEnabled; _menuDetener!.Enabled = BotonDetener.IsEnabled; }
        }
    }

    private static string TiempoActivo()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("Acusa.Sdk.Worker"))
            {
                var t = DateTime.Now - p.StartTime;
                return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            }
        }
        catch { /* sin permiso para leer la hora de inicio de un proceso de otra sesión */ }

        return "—";
    }

    /// <summary>Estado del servicio y últimas líneas de su bitácora, que escribe el propio worker.</summary>
    private void Vigilar()
    {
        try
        {
            ActualizarEstadoServicio();
            var archivo = Path.Combine(Config.Carpeta, "Worker", "logs", $"worker-{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(archivo)) return;
            using var fs = new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (_archivoLogLeido != archivo) { _archivoLogLeido = archivo; _posicionLog = Math.Max(0, fs.Length - 8000); }
            if (fs.Length < _posicionLog) _posicionLog = 0;
            fs.Seek(_posicionLog, SeekOrigin.Begin);
            using var lector = new StreamReader(fs, Encoding.UTF8);
            string? linea;
            while ((linea = lector.ReadLine()) != null) MostrarLinea(linea);
            _posicionLog = fs.Position;
        }
        catch { /* el archivo puede estar ocupado un instante */ }
    }

    private void Asistente_Click(object sender, RoutedEventArgs e)
    {
        new Asistente(Config.Cargar()) { Owner = this }.ShowDialog();
        CargarConfiguracion();
    }

    private void Panel_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("http://127.0.0.1:5095") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("No se pudo abrir el panel: " + ex.Message, "Acusa Worker"); }
    }

    // ------------------------------------------------------------------ bitácora

    private static readonly Regex Codigo = new(@"[Cc][oó]digo de acceso del panel:\s*(\S+)", RegexOptions.Compiled);

    /// <summary>Una línea propia del panel: va a pantalla y nunca muestra una contraseña.</summary>
    private void Linea(string texto)
    {
        var limpio = Regex.Replace(texto, @"(?i)(password|contrase[nñ]a|clave)\s*[:=]\s*\S+", "$1: ***");
        MostrarLinea($"{DateTime.Now:HH:mm:ss} {limpio}");
    }

    private void MostrarLinea(string limpio)
    {
        _lineas.Enqueue(limpio);
        while (_lineas.Count > MaximoLineas) _lineas.Dequeue();
        Bitacora.Text = string.Join(Environment.NewLine, _lineas);
        Bitacora.ScrollToEnd();

        var m = Codigo.Match(limpio);
        if (m.Success) DatoCodigo.Text = m.Groups[1].Value;
        if (limpio.Contains("completada", StringComparison.OrdinalIgnoreCase) && limpio.Contains("Tarea", StringComparison.OrdinalIgnoreCase)) { _hechas++; DatoHechas.Text = _hechas.ToString(); }
        else if (limpio.Contains("fall", StringComparison.OrdinalIgnoreCase) && limpio.Contains("Tarea", StringComparison.OrdinalIgnoreCase)) { _fallos++; DatoFallos.Text = _fallos.ToString(); }
    }

    private void PonerEstado(string texto, string fondo, string punto)
    {
        TextoEstado.Text = texto;
        PildoraEstado.Background = (Brush)new BrushConverter().ConvertFromString(fondo)!;
        PuntoEstado.Fill = (Brush)new BrushConverter().ConvertFromString(punto)!;
    }

    private void Limpiar_Click(object sender, RoutedEventArgs e) { _lineas.Clear(); Bitacora.Clear(); }

    // ------------------------------------------------------------------ bandeja del sistema

    private void CrearBandeja()
    {
        // El mismo ícono de Acusa (la «A») que usa el ejecutable, el acceso directo y la ventana.
        System.Drawing.Icon? icono = null;
        try { if (Environment.ProcessPath is { } exe) icono = System.Drawing.Icon.ExtractAssociatedIcon(exe); } catch { /* se usa el ícono por omisión */ }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Acusa Worker", null, (_, _) => Mostrar());
        _menuIniciar = new System.Windows.Forms.ToolStripMenuItem("Iniciar worker", null, (_, _) => Dispatcher.Invoke(IniciarServicio));
        _menuDetener = new System.Windows.Forms.ToolStripMenuItem("Detener worker", null, (_, _) => Dispatcher.Invoke(DetenerServicio)) { Enabled = false };
        menu.Items.Add(_menuIniciar);
        menu.Items.Add(_menuDetener);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => Dispatcher.Invoke(Salir));
        _bandeja = new System.Windows.Forms.NotifyIcon { Icon = icono ?? System.Drawing.SystemIcons.Application, Text = "Acusa Worker", Visible = true, ContextMenuStrip = menu };
        _bandeja.DoubleClick += (_, _) => Mostrar();
    }

    private void Mostrar()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void AlCerrar(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_saliendo) return;
        // Cerrar la ventana no detiene nada: el worker es un servicio de Windows. La ventana se oculta a la bandeja.
        e.Cancel = true;
        Hide();
    }

    /// <summary>Salir cierra solo este panel; el servicio sigue atendiendo órdenes.</summary>
    private void Salir()
    {
        _saliendo = true;
        _vigilante.Stop();
        _bandeja?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
