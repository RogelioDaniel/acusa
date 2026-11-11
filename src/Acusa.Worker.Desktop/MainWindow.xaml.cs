using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Acusa.Worker.Desktop;

/// <summary>
/// Aplicación de escritorio del Acusa Worker: inicia y detiene el proceso del worker, muestra su estado y su bitácora y
/// vive en la bandeja del sistema. No guarda contraseñas: la llave y la del certificado solo pasan al proceso por entorno.
/// </summary>
public partial class MainWindow : Window
{
    private const int MaximoLineas = 1500;
    private Process? _proceso;
    private DateTime _inicio;
    private int _hechas;
    private int _fallos;
    private bool _saliendo;
    private readonly DispatcherTimer _reloj = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<string> _lineas = new();
    private System.Windows.Forms.NotifyIcon? _bandeja;
    private System.Windows.Forms.ToolStripMenuItem? _menuIniciar;
    private System.Windows.Forms.ToolStripMenuItem? _menuDetener;

    public MainWindow()
    {
        InitializeComponent();
        CargarConfiguracion();
        CrearBandeja();
        _reloj.Tick += (_, _) => ActualizarTiempo();
        _reloj.Start();
        var ruta = BuscarWorker();
        TextoRuta.Text = ruta is null ? "No se encontró Acusa.Sdk.Worker.exe junto a esta aplicación." : "Worker: " + ruta;
        BotonIniciar.IsEnabled = ruta is not null;
        Closing += AlCerrar;
    }

    // ------------------------------------------------------------------ configuración (sin secretos)

    private Config _config = new();

    private void CargarConfiguracion()
    {
        _config = Config.Cargar();
        var c = _config;
        CajaUrl.Text = c.Url; CajaEmpresa.Text = c.Empresa; CheckSimulado.IsChecked = c.Simulado;
        CheckDocumentos.IsChecked = c.Documentos; CheckTimbrado.IsChecked = c.Timbrado;
        CheckCsdComercial.IsChecked = c.CsdEnComercial; CajaClientes.Text = c.Clientes;
        var llave = Secretos.LeerLlave();
        if (!string.IsNullOrEmpty(llave)) CajaLlave.Password = llave;
    }

    private void GuardarConfiguracion()
    {
        var c = _config;
        c.Url = CajaUrl.Text.Trim(); c.Empresa = CajaEmpresa.Text.Trim(); c.Simulado = CheckSimulado.IsChecked == true;
        c.Documentos = CheckDocumentos.IsChecked == true; c.Timbrado = CheckTimbrado.IsChecked == true;
        c.CsdEnComercial = CheckCsdComercial.IsChecked == true; c.Clientes = CajaClientes.Text.Trim();
        c.Guardar();
    }

    // ------------------------------------------------------------------ proceso del worker

    private static string? BuscarWorker()
    {
        var @base = AppContext.BaseDirectory;
        foreach (var r in new[]
        {
            Path.Combine(@base, "worker", "Acusa.Sdk.Worker.exe"),
            Path.Combine(@base, "..", "worker", "Acusa.Sdk.Worker.exe"),
            Path.Combine(@base, "Acusa.Sdk.Worker.exe"),
        })
        {
            if (File.Exists(r)) return Path.GetFullPath(r);
        }

        return null;
    }

    private void Iniciar_Click(object sender, RoutedEventArgs e) => IniciarWorker();

    private void IniciarWorker()
    {
        if (_proceso is { HasExited: false }) return;
        var exe = BuscarWorker();
        if (exe is null) { MessageBox.Show("No se encontró Acusa.Sdk.Worker.exe.", "Acusa Worker"); return; }

        var simulado = CheckSimulado.IsChecked == true;
        _simulado = simulado;
        var empresa = CajaEmpresa.Text.Trim();
        var llave = CajaLlave.Password;
        if (string.IsNullOrWhiteSpace(llave) || string.IsNullOrWhiteSpace(CajaUrl.Text))
        {
            MessageBox.Show("Escriba la dirección de Acusa Central y la llave del worker.", "Acusa Worker", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!simulado && string.IsNullOrWhiteSpace(empresa))
        {
            MessageBox.Show("Escriba la carpeta de la empresa de CONTPAQi.", "Acusa Worker", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!simulado && CheckTimbrado.IsChecked == true && !EsEmpresaDePrueba(empresa))
        {
            var r = MessageBox.Show($"La empresa '{empresa}' no parece de pruebas.\n\nTimbrar y cancelar gasta timbres reales y avisa al SAT. ¿Continuar?",
                "Acusa Worker: empresa de producción", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
        }

        GuardarConfiguracion();
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["Orquestador__Url"] = CajaUrl.Text.Trim();
        psi.Environment["Orquestador__LlaveWorker"] = llave;
        psi.Environment["Contpaqi__RutaEmpresa"] = Path.Combine(@"C:\Compac\Empresas", empresa);
        var demas = _config.Empresas.Where(x => !string.Equals(x, empresa, StringComparison.OrdinalIgnoreCase)).ToList();
        for (var j = 0; j < demas.Count; j++) psi.Environment[$"Contpaqi__Empresas__{j}"] = demas[j];
        if (simulado)
        {
            psi.ArgumentList.Add("--simulado");
        }
        else
        {
            psi.ArgumentList.Add("--modo=real");
            psi.Environment["Contpaqi__Modo"] = "Real";
            psi.Environment["Contpaqi__Inicio"] = _config.InicioSinDialogo ? "Credenciales" : "Dialogo";
            if (!string.IsNullOrWhiteSpace(_config.UsuarioSdk)) psi.Environment["Contpaqi__Usuario"] = _config.UsuarioSdk;
            if (!string.IsNullOrWhiteSpace(_config.NombrePaq)) psi.Environment["Contpaqi__NombrePaq"] = _config.NombrePaq;
            if (!string.IsNullOrWhiteSpace(_config.RutaSdk)) psi.Environment["Contpaqi__RutaSdk"] = _config.RutaSdk;
            psi.Environment["Contpaqi__PermitirEscritura"] = "true";
            // Escritura: la principal (como hasta ahora) y las demás solo si son de pruebas; el resto queda de consulta.
            var otras = _config.Empresas.Where(x => !string.Equals(x, empresa, StringComparison.OrdinalIgnoreCase)).ToList();
            psi.Environment["Contpaqi__EmpresasEscritura__0"] = empresa;
            var k = 1;
            foreach (var x in otras.Where(EsEmpresaDePrueba)) psi.Environment[$"Contpaqi__EmpresasEscritura__{k++}"] = x;

            if (CheckDocumentos.IsChecked == true || CheckTimbrado.IsChecked == true) psi.Environment["Contpaqi__PermitirDocumentos"] = "true";
            if (CheckTimbrado.IsChecked == true)
            {
                psi.Environment["Contpaqi__PermitirTimbrado"] = "true";
                if (CheckCsdComercial.IsChecked == true) psi.Environment["Contpaqi__CertificadoGuardadoEnComercial"] = "true";
                else if (!string.IsNullOrEmpty(CajaCsd.Password)) psi.Environment["ACUSA_CLAVE_CERTIFICADO"] = CajaCsd.Password;
            }

            var i = 0;
            foreach (var c in CajaClientes.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                psi.Environment[$"Contpaqi__ClientesPermitidos__{i++}"] = c;
            }
        }

        try
        {
            _hechas = 0; _fallos = 0; DatoHechas.Text = "0"; DatoFallos.Text = "0"; DatoCodigo.Text = "—";
            _proceso = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proceso.OutputDataReceived += (_, a) => { if (a.Data != null) Dispatcher.BeginInvoke(() => Linea(a.Data)); };
            _proceso.ErrorDataReceived += (_, a) => { if (a.Data != null) Dispatcher.BeginInvoke(() => Linea(a.Data)); };
            _proceso.Exited += (_, _) => Dispatcher.BeginInvoke(AlTerminar);
            _proceso.Start();
            _proceso.BeginOutputReadLine();
            _proceso.BeginErrorReadLine();
            _inicio = DateTime.Now;
            Linea($"--- Worker iniciado {DateTime.Now:HH:mm:ss} ({(simulado ? "simulado" : "empresa " + empresa)}) ---");
            PonerEstado("Iniciando…", "#FEF3C7", "#F59E0B");
            BotonIniciar.IsEnabled = false; BotonDetener.IsEnabled = true; BotonPanel.IsEnabled = true;
            if (_menuIniciar != null) { _menuIniciar.Enabled = false; _menuDetener!.Enabled = true; }
            CajaCsd.Clear();
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo iniciar el worker: " + ex.Message, "Acusa Worker", MessageBoxButton.OK, MessageBoxImage.Error);
            _proceso = null;
        }
    }

    private static bool EsEmpresaDePrueba(string empresa) => empresa.Contains("PRUEBA", StringComparison.OrdinalIgnoreCase);

    private void Detener_Click(object sender, RoutedEventArgs e) => DetenerWorker();

    private void DetenerWorker()
    {
        try
        {
            if (_proceso is { HasExited: false })
            {
                Linea("--- Deteniendo el worker… ---");
                _proceso.Kill(true);
            }
        }
        catch (Exception ex) { Linea("No se pudo detener: " + ex.Message); }
    }

    private void AlTerminar()
    {
        Linea($"--- Worker detenido {DateTime.Now:HH:mm:ss} ---");
        PonerEstado("Detenido", "#F3F4F6", "#9CA3AF");
        BotonIniciar.IsEnabled = BuscarWorker() is not null; BotonDetener.IsEnabled = false; BotonPanel.IsEnabled = false;
        if (_menuIniciar != null) { _menuIniciar.Enabled = true; _menuDetener!.Enabled = false; }
        DatoTiempo.Text = "—";
        _proceso = null;
    }

    private void Asistente_Click(object sender, RoutedEventArgs e)
    {
        if (_proceso is { HasExited: false }) { MessageBox.Show("Detenga el worker antes de abrir el asistente.", "Acusa Worker", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        new Asistente(Config.Cargar()) { Owner = this }.ShowDialog();
        CargarConfiguracion();
    }

    private void Panel_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("http://127.0.0.1:5095") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("No se pudo abrir el panel: " + ex.Message, "Acusa Worker"); }
    }

    // ------------------------------------------------------------------ bitácora y estado

    private bool _simulado;
    private static readonly Regex Codigo = new(@"[Cc][oó]digo de acceso del panel:\s*(\S+)", RegexOptions.Compiled);

    private void Linea(string texto)
    {
        // Nunca se muestra una contraseña aunque el proceso la escribiera.
        var limpio = Regex.Replace(texto, @"(?i)(password|contrase[nñ]a|clave)\s*[:=]\s*\S+", "$1: ***");
        _lineas.Enqueue(limpio);
        while (_lineas.Count > MaximoLineas) _lineas.Dequeue();
        Bitacora.Text = string.Join(Environment.NewLine, _lineas);
        Bitacora.ScrollToEnd();

        var m = Codigo.Match(limpio);
        if (m.Success) DatoCodigo.Text = m.Groups[1].Value;
        if (limpio.Contains("completada", StringComparison.OrdinalIgnoreCase) && limpio.Contains("Tarea", StringComparison.OrdinalIgnoreCase)) { _hechas++; DatoHechas.Text = _hechas.ToString(); }
        else if (limpio.Contains("fall", StringComparison.OrdinalIgnoreCase) && limpio.Contains("Tarea", StringComparison.OrdinalIgnoreCase)) { _fallos++; DatoFallos.Text = _fallos.ToString(); }
        // El worker ya trabaja cuando abrió la sesión del SDK (modo real) o empezó a consultar (modo simulado).
        if (limpio.Contains("Sesión del SDK lista", StringComparison.OrdinalIgnoreCase))
        {
            PonerEstado("En ejecución", "#E0EEE7", "#0B6B50");
        }
    }

    private void PonerEstado(string texto, string fondo, string punto)
    {
        TextoEstado.Text = texto;
        PildoraEstado.Background = (Brush)new BrushConverter().ConvertFromString(fondo)!;
        PuntoEstado.Fill = (Brush)new BrushConverter().ConvertFromString(punto)!;
    }

    private void ActualizarTiempo()
    {
        if (_proceso is { HasExited: false }) DatoTiempo.Text = (DateTime.Now - _inicio).ToString(@"hh\:mm\:ss");
    }

    private void Limpiar_Click(object sender, RoutedEventArgs e) { _lineas.Clear(); Bitacora.Clear(); }

    // ------------------------------------------------------------------ bandeja del sistema

    private void CrearBandeja()
    {
        using var mapa = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(mapa))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pincel = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Rectangle(0, 0, 32, 32),
                System.Drawing.Color.FromArgb(14, 165, 233), System.Drawing.Color.FromArgb(67, 56, 202), 45f);
            g.FillEllipse(pincel, 1, 1, 30, 30);
            using var lapiz = new System.Drawing.Pen(System.Drawing.Color.White, 3f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round, StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawLines(lapiz, new[] { new System.Drawing.Point(8, 22), new System.Drawing.Point(13, 10), new System.Drawing.Point(17, 17), new System.Drawing.Point(20, 13), new System.Drawing.Point(25, 22) });
        }

        var icono = System.Drawing.Icon.FromHandle(mapa.GetHicon());
        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icono.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Acusa Worker", null, (_, _) => Mostrar());
        _menuIniciar = new System.Windows.Forms.ToolStripMenuItem("Iniciar worker", null, (_, _) => Dispatcher.Invoke(IniciarWorker));
        _menuDetener = new System.Windows.Forms.ToolStripMenuItem("Detener worker", null, (_, _) => Dispatcher.Invoke(DetenerWorker)) { Enabled = false };
        menu.Items.Add(_menuIniciar);
        menu.Items.Add(_menuDetener);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => Dispatcher.Invoke(Salir));
        _bandeja = new System.Windows.Forms.NotifyIcon { Icon = icono, Text = "Acusa Worker", Visible = true, ContextMenuStrip = menu };
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
        // Cerrar la ventana no detiene el worker: se oculta a la bandeja.
        e.Cancel = true;
        Hide();
        _bandeja?.ShowBalloonTip(2500, "Acusa Worker", _proceso is { HasExited: false } ? "Sigue ejecutándose en segundo plano." : "Está en la bandeja del sistema.", System.Windows.Forms.ToolTipIcon.Info);
    }

    private void Salir()
    {
        if (_proceso is { HasExited: false })
        {
            var r = MessageBox.Show("El worker sigue en ejecución. Al salir se detiene y dejará de atender órdenes. ¿Salir?", "Acusa Worker", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            DetenerWorker();
        }

        _saliendo = true;
        _bandeja?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
