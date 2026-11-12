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

    private readonly bool _soloLectura;
    private readonly bool _automatico;
    private bool _detencionManual;
    private readonly Queue<DateTime> _reinicios = new();
    private readonly DispatcherTimer _vigilante = new() { Interval = TimeSpan.FromSeconds(3) };
    private long _posicionLog;
    private string _archivoLogLeido = "";

    public MainWindow(bool soloLectura = false, string? aviso = null, bool automatico = false)
    {
        _soloLectura = soloLectura;
        _automatico = automatico;
        _servicio = ServicioWindows.Instalado;
        InitializeComponent();
        CargarConfiguracion();
        CrearBandeja();
        _reloj.Tick += (_, _) => ActualizarTiempo();
        _reloj.Start();
        var ruta = BuscarWorker();
        TextoRuta.Text = ruta is null ? "No se encontró Acusa.Sdk.Worker.exe junto a esta aplicación." : "Worker: " + ruta;
        BotonIniciar.IsEnabled = ruta is not null;
        Closing += AlCerrar;
        if (!string.IsNullOrEmpty(aviso)) Subtitulo.Text = aviso;

        if (_soloLectura)
        {
            // Solo se ve el estado: ni configuración, ni secretos, ni botones de control.
            PanelConfig.IsEnabled = false;
            BotonIniciar.IsEnabled = false; BotonDetener.IsEnabled = false; BotonPanel.IsEnabled = false; BotonAsistente.IsEnabled = false;
            BotonServicio.IsEnabled = false;
            if (_menuIniciar != null) { _menuIniciar.Enabled = false; _menuDetener!.Enabled = false; }
            _vigilante.Tick += (_, _) => Vigilar();
            _vigilante.Start();
            Vigilar();
        }
        else
        {
            if (_servicio) { _vigilante.Tick += (_, _) => Vigilar(); _vigilante.Start(); Vigilar(); }
            RefrescarBotonesServicio();
            Loaded += (_, _) => { if (_automatico && _config.Configurado) IniciarWorker(); };
        }
    }

    private bool _servicio;

    /// <summary>Con el servicio instalado, la app es solo el panel de control: el worker vive en el servicio, no en esta ventana.</summary>
    private void RefrescarBotonesServicio()
    {
        BotonServicio.Content = _servicio ? "Reinstalar servicio" : "Instalar servicio de Windows";
        BotonQuitarServicio.Visibility = _servicio ? Visibility.Visible : Visibility.Collapsed;
        if (_servicio)
        {
            TextoRuta.Text = "Worker como servicio de Windows «AcusaWorker»: arranca con el servidor y se reinicia solo.";
            BotonPanel.IsEnabled = true;
        }
    }

    private void ActualizarEstadoServicio()
    {
        var e = ServicioWindows.Consultar();
        switch (e)
        {
            case ServicioWindows.Estado.EnEjecucion: PonerEstado("Servicio en ejecución", "#E0EEE7", "#0B6B50"); break;
            case ServicioWindows.Estado.Iniciando: PonerEstado("Servicio iniciando…", "#FEF3C7", "#F59E0B"); break;
            case ServicioWindows.Estado.Deteniendo: PonerEstado("Servicio deteniéndose…", "#FEF3C7", "#F59E0B"); break;
            case ServicioWindows.Estado.NoInstalado: PonerEstado("Servicio no instalado", "#F3F4F6", "#9CA3AF"); break;
            default: PonerEstado("Servicio detenido", "#F3F4F6", "#9CA3AF"); break;
        }

        if (!_soloLectura)
        {
            BotonIniciar.IsEnabled = e == ServicioWindows.Estado.Detenido;
            BotonDetener.IsEnabled = e is ServicioWindows.Estado.EnEjecucion or ServicioWindows.Estado.Iniciando;
            if (_menuIniciar != null) { _menuIniciar.Enabled = BotonIniciar.IsEnabled; _menuDetener!.Enabled = BotonDetener.IsEnabled; }
        }
    }

    /// <summary>Guarda lo que el servicio necesita leer en ProgramData: contraseñas cifradas con DPAPI de la máquina.</summary>
    private void GuardarSecretosServicio()
    {
        GuardarConfiguracion();
        if (!string.IsNullOrEmpty(CajaLlave.Password)) Secretos.GuardarLlave(CajaLlave.Password);
        if (!string.IsNullOrEmpty(CajaClaveSdk.Password)) Secretos.GuardarClaveSdk(CajaClaveSdk.Password);
        if (CheckTimbrado.IsChecked == true && CheckCsdComercial.IsChecked != true)
        {
            if (!string.IsNullOrEmpty(CajaCsd.Password)) { _config.RecordarCsd = true; CheckRecordarCsd.IsChecked = true; _config.Guardar(); Secretos.GuardarCsd(CajaCsd.Password); }
            else if (Secretos.LeerCsd() is null) Linea("Aviso: el servicio no tiene la contraseña del certificado; no podrá timbrar. Escríbala y vuelva a iniciar.");
        }
    }

    private void IniciarServicio()
    {
        try
        {
            GuardarSecretosServicio();
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

    private void InstalarServicio_Click(object sender, RoutedEventArgs e)
    {
        var exe = BuscarWorker();
        if (exe is null) { MessageBox.Show("No se encontró Acusa.Sdk.Worker.exe.", "Acusa Worker"); return; }
        if (!_config.Configurado) { MessageBox.Show("Primero complete el asistente de configuración.", "Acusa Worker", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_proceso is { HasExited: false }) { MessageBox.Show("Detenga el worker que corre en esta ventana: el servicio lo reemplaza.", "Acusa Worker", MessageBoxButton.OK, MessageBoxImage.Information); return; }

        var r = MessageBox.Show("Se registrará el servicio «AcusaWorker»: arranca con el servidor aunque nadie inicie sesión y se reinicia solo si falla.\n\n" +
            "Sin sesión de usuario no hay diálogo de Comercial, así que el servicio abre el SDK con usuario y contraseña, y guarda las contraseñas cifradas en este servidor.\n\n¿Continuar? Windows pedirá permisos de administrador.",
            "Instalar el servicio", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        _config.InicioSinDialogo = true;
        GuardarSecretosServicio();
        var error = ServicioWindows.Instalar(exe);
        _servicio = ServicioWindows.Instalado;
        if (!_servicio) { MessageBox.Show("No se instaló el servicio. " + error, "Acusa Worker", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Linea("--- Servicio AcusaWorker instalado ---");
        if (!_vigilante.IsEnabled) { _vigilante.Tick += (_, _) => Vigilar(); _vigilante.Start(); }
        RefrescarBotonesServicio();
        IniciarServicio();
    }

    private void QuitarServicio_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Se detendrá y se quitará el servicio «AcusaWorker». El worker dejará de atender órdenes hasta que se vuelva a instalar.\n\n¿Quitarlo?",
                "Quitar el servicio", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var error = ServicioWindows.Quitar();
        _servicio = ServicioWindows.Instalado;
        Linea(_servicio ? "No se pudo quitar el servicio. " + error : "--- Servicio AcusaWorker quitado ---");
        _vigilante.Stop();
        RefrescarBotonesServicio();
        PonerEstado("Detenido", "#F3F4F6", "#9CA3AF");
        BotonIniciar.IsEnabled = BuscarWorker() is not null;
    }

    // ------------------------------------------------------------------ vista de solo lectura

    /// <summary>Otro usuario o sesión controla el worker: aquí solo se muestra si el proceso está vivo y las últimas líneas de su bitácora.</summary>
    private void Vigilar()
    {
        try
        {
            if (_servicio) ActualizarEstadoServicio();
            else
            {
                var vivo = Process.GetProcessesByName("Acusa.Sdk.Worker").Length > 0;
                if (vivo) PonerEstado("En ejecución", "#E0EEE7", "#0B6B50");
                else PonerEstado("Detenido", "#F3F4F6", "#9CA3AF");
            }

            // La bitácora del servicio la escribe el propio worker; la de una ventana de otra sesión, esa aplicación.
            var archivo = _servicio
                ? Path.Combine(Config.Carpeta, "Worker", "logs", $"worker-{DateTime.Now:yyyyMMdd}.log")
                : Path.Combine(Config.CarpetaLogs, $"worker_{DateTime.Now:yyyyMMdd}.log");
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

    // ------------------------------------------------------------------ configuración (sin secretos)

    private Config _config = new();

    private void CargarConfiguracion()
    {
        _config = Config.Cargar();
        var c = _config;
        CajaUrl.Text = c.Url; CajaEmpresa.Text = c.Empresa; CheckSimulado.IsChecked = c.Simulado;
        CheckDocumentos.IsChecked = c.Documentos; CheckTimbrado.IsChecked = c.Timbrado;
        CheckCsdComercial.IsChecked = c.CsdEnComercial; CajaClientes.Text = c.Clientes;
        CheckRecordarCsd.IsChecked = c.RecordarCsd; CheckInicioWindows.IsChecked = c.IniciarConWindows;
        if (_soloLectura) return;   // los secretos no se cargan para quien solo mira
        var llave = Secretos.LeerLlave();
        if (!string.IsNullOrEmpty(llave)) CajaLlave.Password = llave;
    }

    private void GuardarConfiguracion()
    {
        var c = _config;
        c.Url = CajaUrl.Text.Trim(); c.Empresa = CajaEmpresa.Text.Trim(); c.Simulado = CheckSimulado.IsChecked == true;
        c.Documentos = CheckDocumentos.IsChecked == true; c.Timbrado = CheckTimbrado.IsChecked == true;
        c.CsdEnComercial = CheckCsdComercial.IsChecked == true; c.Clientes = CajaClientes.Text.Trim();
        c.RecordarCsd = CheckRecordarCsd.IsChecked == true; c.IniciarConWindows = CheckInicioWindows.IsChecked == true;
        if (string.IsNullOrEmpty(c.PropietarioSid)) { c.PropietarioSid = Acceso.SidActual; c.PropietarioNombre = Acceso.NombreActual; }
        c.Guardar();
        AplicarInicioConWindows(c.IniciarConWindows);
    }

    /// <summary>Registra (o quita) el arranque automático para el usuario actual: al iniciar sesión se abre la app en la bandeja y arranca el worker.</summary>
    private static void AplicarInicioConWindows(bool activar)
    {
        try
        {
            using var clave = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (activar && Environment.ProcessPath is { } exe) clave.SetValue("AcusaWorker", $"\"{exe}\" --auto");
            else clave.DeleteValue("AcusaWorker", false);
        }
        catch { /* sin acceso al registro del usuario */ }
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
        if (_servicio) { IniciarServicio(); return; }
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

        if (!simulado && CheckTimbrado.IsChecked == true && _config.EmpresasReales.Count > 0)
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
            // Inicio con usuario y contraseña: la contraseña guardada (cifrada en este servidor) o, si SUPERVISOR no tiene, vacía.
            if (_config.InicioSinDialogo && Secretos.LeerClaveSdk() is { Length: > 0 } claveSdk) psi.Environment["ACUSA_CLAVE_SDK"] = claveSdk;
            // Escritura: las empresas marcadas como «Producción» (reales, sin prefijo PRUEBA) y las de pruebas; el resto queda de consulta.
            var atendidas = new[] { empresa }.Concat(_config.Empresas).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var reales = atendidas.Where(x => _config.EmpresasReales.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
            var escribibles = atendidas.Where(x => reales.Contains(x, StringComparer.OrdinalIgnoreCase) || EsEmpresaDePrueba(x)).ToList();
            for (var k = 0; k < escribibles.Count; k++) psi.Environment[$"Contpaqi__EmpresasEscritura__{k}"] = escribibles[k];
            for (var k = 0; k < reales.Count; k++) psi.Environment[$"Contpaqi__EmpresasReales__{k}"] = reales[k];

            if (CheckDocumentos.IsChecked == true || CheckTimbrado.IsChecked == true) psi.Environment["Contpaqi__PermitirDocumentos"] = "true";
            if (CheckTimbrado.IsChecked == true)
            {
                psi.Environment["Contpaqi__PermitirTimbrado"] = "true";
                if (CheckCsdComercial.IsChecked == true) psi.Environment["Contpaqi__CertificadoGuardadoEnComercial"] = "true";
                else
                {
                    // La que se escribió ahora, o la que el administrador pidió recordar (cifrada en este equipo).
                    var csd = !string.IsNullOrEmpty(CajaCsd.Password) ? CajaCsd.Password : (_config.RecordarCsd ? Secretos.LeerCsd() : null);
                    if (!string.IsNullOrEmpty(csd)) psi.Environment["ACUSA_CLAVE_CERTIFICADO"] = csd;
                    else Linea("Aviso: no hay contraseña del certificado; el worker no podrá timbrar.");
                    if (_config.RecordarCsd && !string.IsNullOrEmpty(CajaCsd.Password)) { try { Secretos.GuardarCsd(CajaCsd.Password); } catch { /* no se pudo recordar */ } }
                }
            }
            if (!_config.RecordarCsd) Secretos.BorrarCsd();

            var i = 0;
            foreach (var c in CajaClientes.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                psi.Environment[$"Contpaqi__ClientesPermitidos__{i++}"] = c;
            }
        }

        try
        {
            _detencionManual = false;
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
        if (_servicio) { DetenerServicio(); return; }
        try
        {
            _detencionManual = true;
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

        // Si se cayó solo (no lo detuvo una persona), se reinicia. Con 5 caídas en 10 minutos se rinde para no ciclar sin fin.
        if (_detencionManual || _saliendo || _soloLectura) return;
        var ahora = DateTime.Now;
        _reinicios.Enqueue(ahora);
        while (_reinicios.Count > 0 && ahora - _reinicios.Peek() > TimeSpan.FromMinutes(10)) _reinicios.Dequeue();
        if (_reinicios.Count > 5) { Linea("--- El worker se cayó 5 veces en 10 minutos: no se reinicia más. Revise la bitácora. ---"); PonerEstado("Detenido (error)", "#FBE4E1", "#BD2F24"); return; }
        Linea("--- El worker se detuvo solo; se reinicia en 15 s ---");
        PonerEstado("Reiniciando…", "#FEF3C7", "#F59E0B");
        var espera = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        espera.Tick += (_, _) => { espera.Stop(); if (!_detencionManual && !_saliendo && _proceso is null) IniciarWorker(); };
        espera.Start();
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

    private static readonly object CandadoLog = new();

    private void Linea(string texto)
    {
        // Nunca se muestra una contraseña aunque el proceso la escribiera.
        var limpio = Regex.Replace(texto, @"(?i)(password|contrase[nñ]a|clave)\s*[:=]\s*\S+", "$1: ***");
        // Bitácora en disco, legible por todos los usuarios del servidor (sin contraseñas).
        try
        {
            lock (CandadoLog)
            {
                Directory.CreateDirectory(Config.CarpetaLogs);
                File.AppendAllText(Path.Combine(Config.CarpetaLogs, $"worker_{DateTime.Now:yyyyMMdd}.log"), limpio + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* sin permiso de escritura */ }
        MostrarLinea(limpio);
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
