using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Acusa.Worker.Desktop;

/// <summary>
/// Asistente de primera configuración del worker, con los mismos pasos que el de Montek: bienvenida, autenticación con la cuenta
/// administradora, base de datos, SDK, licencia (equipo), llave de API, empresas, revisión e instalación.
/// Las contraseñas solo viven en memoria durante el asistente; a disco va la configuración sin secretos y la llave cifrada.
/// </summary>
public partial class Asistente : Window
{
    // Los diez pasos originales siguen existiendo por dentro (los números de caso de la validación no cambian), pero solo se muestran
    // los que el worker necesita: base de datos, Contabilidad y el identificador del equipo se resuelven solos.
    private static readonly string[] Pasos =
    {
        "Bienvenida", "Iniciar sesión", "Base de datos", "CONTPAQi Comercial", "Contabilidad", "Equipo",
        "Llave del worker", "Empresas", "Revisión", "Listo",
    };

    private static readonly int[] Visibles = { 0, 1, 3, 6, 7, 8, 9 };
    private static int PosicionVisible(int paso) => Array.IndexOf(Visibles, paso);

    private readonly Config _cfg;
    private readonly List<UIElement> _paginas = new();
    private readonly List<(Border Circulo, TextBlock Titulo)> _marcas = new();
    private int _paso;
    private string? _token;
    private string _resumenFinal = "";
    private bool _instalado;

    // controles
    private readonly TextBox _url = new(), _correo = new(), _sqlServidor = new(), _sqlUsuario = new(), _usuarioSdk = new(), _nombrePaq = new(), _rutaSdk = new();
    private readonly PasswordBox _contrasena = new(), _sqlClave = new(), _sdkClave = new(), _llaveManual = new();
    private readonly CheckBox _sqlWindows = new(), _sqlOmitir = new(), _sinDialogo = new(), _omitirConta = new();
    private readonly RadioButton _llaveAuto = new(), _llavePegada = new();
    private readonly TextBlock _estadoSql = new(), _estadoSdk = new(), _estadoEmpresas = new(), _resumen = new(), _resultado = new(), _equipo = new();
    private readonly StackPanel _listaEmpresas = new();
    private readonly CheckBox _detComercial = new(), _detNominas = new(), _detConta = new();
    private readonly List<(CheckBox Marca, RadioButton Principal, string Carpeta, CheckBox Real)> _empresas = new();

    public Asistente(Config cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        if (string.IsNullOrEmpty(_cfg.EquipoId)) _cfg.EquipoId = Guid.NewGuid().ToString("N")[..16];
        ConstruirPasos();
        ConstruirPaginas();
        Mostrar(int.TryParse(Environment.GetEnvironmentVariable("ACUSA_ASISTENTE_PASO"), out var n) && PosicionVisible(n) >= 0 ? n : 0);
    }

    // ------------------------------------------------------------------ construcción

    private void ConstruirPasos()
    {
        for (var n = 0; n < Visibles.Length; n++)
        {
            var circulo = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.White, BorderBrush = (Brush)FindResource("Borde"), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 10, 0) };
            circulo.Child = new TextBlock { Text = (n + 1).ToString(), FontSize = 11.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Suave") };
            var titulo = new TextBlock { Text = Pasos[Visibles[n]], FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var fila = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
            fila.Children.Add(circulo);
            fila.Children.Add(titulo);
            ListaPasos.Children.Add(fila);
            _marcas.Add((circulo, titulo));
        }
    }

    private StackPanel ArmarPagina(string titulo, string sub, params UIElement[] hijos)
    {
        var raiz = new StackPanel { MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center };
        raiz.Children.Add(new TextBlock { Text = titulo, FontSize = 26, FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center });
        if (!string.IsNullOrEmpty(sub)) raiz.Children.Add(new TextBlock { Text = sub, Foreground = (Brush)FindResource("Suave"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 18) });
        else raiz.Children.Add(new Border { Height = 18 });
        foreach (var h in hijos) raiz.Children.Add(h);
        return raiz;
    }

    private TextBlock Etiqueta(string t) => new() { Text = t, Style = (Style)FindResource("Etiqueta") };
    private static TextBlock Nota(string t) => new() { Text = t, Foreground = Brushes.Gray, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private Button Boton(string t, RoutedEventHandler clic) { var b = new Button { Content = t, Style = (Style)FindResource("BotonSuave"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) }; b.Click += clic; return b; }

    private void ConstruirPaginas()
    {
        _url.Text = _cfg.Url; _correo.Text = _cfg.Correo;
        _sqlServidor.Text = _cfg.SqlServidor; _sqlUsuario.Text = _cfg.SqlUsuario; _sqlWindows.IsChecked = _cfg.SqlWindows; _sqlOmitir.IsChecked = _cfg.SqlOmitido;
        _usuarioSdk.Text = _cfg.UsuarioSdk; _nombrePaq.Text = _cfg.NombrePaq; _rutaSdk.Text = _cfg.RutaSdk; _sinDialogo.IsChecked = _cfg.InicioSinDialogo;
        _omitirConta.IsChecked = true;
        _llavePegada.IsChecked = true;
        _llaveAuto.IsChecked = false;

        // 1 Bienvenida
        var bien = new TextBlock { Text = "Conecta el worker de este servidor con Acusa Central.", TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = (Brush)FindResource("Tinta"), TextAlignment = TextAlignment.Center };
        _paginas.Add(ArmarPagina("Acusa Worker", "", bien));

        // 2 Autenticación
        _paginas.Add(ArmarPagina("Iniciar sesión", "",
            Etiqueta("Dirección de Acusa Central"), _url, Etiqueta("Usuario (correo)"), _correo, Etiqueta("Contraseña"), _contrasena));

        // 3 Base de datos: sin pantalla; el worker opera con el SDK y no necesita SQL
        _sqlOmitir.IsChecked = true;
        _paginas.Add(new StackPanel());

        // 4 CONTPAQi Comercial: el servicio abre el SDK con usuario y contraseña (sin sesión de usuario no hay diálogo)
        _sinDialogo.IsChecked = true;
        _paginas.Add(ArmarPagina("CONTPAQi Comercial", "",
            Etiqueta("Usuario del SDK"), _usuarioSdk, Etiqueta("Contraseña del SDK (vacía si el usuario no tiene)"), _sdkClave));

        // 5 Contabilidad y 6 Equipo: sin pantalla
        _paginas.Add(new StackPanel());
        _paginas.Add(new StackPanel());

        // 7 Llave del worker
        _paginas.Add(ArmarPagina("Llave del worker", "",
            Etiqueta("Llave del worker (empieza con wk_)"), _llaveManual));

        // 8 Empresas
        _detComercial.IsChecked = true;
        var barra = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        barra.Children.Add(Boton("↻ Actualizar", (_, _) => CargarEmpresas())); ((Button)barra.Children[0]).Margin = new Thickness(0, 0, 8, 6); ((Button)barra.Children[0]).Padding = new Thickness(12, 6, 12, 6);
        var todas = Boton("Seleccionar todas", (_, _) => { foreach (var e in _empresas) e.Marca.IsChecked = true; ActualizarContador(); }); todas.Margin = new Thickness(0, 0, 8, 6); todas.Padding = new Thickness(12, 6, 12, 6);
        var ninguna = Boton("Deseleccionar", (_, _) => { foreach (var e in _empresas) e.Marca.IsChecked = false; ActualizarContador(); }); ninguna.Margin = new Thickness(0, 0, 8, 6); ninguna.Padding = new Thickness(12, 6, 12, 6);
        barra.Children.Add(todas); barra.Children.Add(ninguna);
        var caja = new Border { BorderBrush = (Brush)FindResource("Borde"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Height = 270, Child = new ScrollViewer { Content = _listaEmpresas, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        var p8 = new StackPanel { MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Center };
        p8.Children.Add(new TextBlock { Text = "Seleccionar empresas", FontSize = 26, FontWeight = FontWeights.ExtraBold });
        p8.Children.Add(new Border { Height = 14 });
        p8.Children.Add(barra); _estadoEmpresas.HorizontalAlignment = HorizontalAlignment.Right; _estadoEmpresas.Foreground = (Brush)FindResource("Suave"); _estadoEmpresas.FontSize = 12; _estadoEmpresas.Margin = new Thickness(0, 0, 0, 6);
        p8.Children.Add(_estadoEmpresas); p8.Children.Add(caja);
        _paginas.Add(p8);

        // 9 Revisión
        _resumen.TextWrapping = TextWrapping.Wrap; _resumen.FontSize = 13.5; _resumen.LineHeight = 24;
        _paginas.Add(ArmarPagina("Revisión", "", _resumen));

        // 10 Listo
        _resultado.TextWrapping = TextWrapping.Wrap; _resultado.FontSize = 13.5; _resultado.LineHeight = 24;
        _paginas.Add(ArmarPagina("Listo", "", _resultado));
    }

    // ------------------------------------------------------------------ navegación

    private void Mostrar(int i)
    {
        _paso = i;
        Pagina.Content = _paginas[i];
        Mensaje.Text = "";
        var pos = PosicionVisible(i);
        for (var k = 0; k < _marcas.Count; k++)
        {
            var (c, t) = _marcas[k];
            var hecho = k < pos;
            var actual = k == pos;
            c.Background = hecho ? new SolidColorBrush(Color.FromRgb(0x0B, 0x6B, 0x50)) : actual ? (Brush)FindResource("Acento") : Brushes.White;
            c.BorderBrush = hecho ? c.Background : actual ? c.Background : (Brush)FindResource("Borde");
            ((TextBlock)c.Child).Foreground = hecho || actual ? Brushes.White : (Brush)FindResource("Suave");
            t.Foreground = actual ? (Brush)FindResource("Acento") : (Brush)FindResource("Tinta");
        }

        var pct = (int)Math.Round(100.0 * pos / (Visibles.Length - 1));
        Barra.Value = pct; TextoProgreso.Text = pct + "%";
        BotonAnterior.Visibility = i is 0 or 9 ? Visibility.Hidden : Visibility.Visible;
        BotonSiguiente.Content = i switch { 8 => "Instalar", 9 => "Finalizar", _ => "Siguiente →" };
        if (i == 7 && _empresas.Count == 0) CargarEmpresas();
        if (i == 8) ArmarResumen();
    }

    private void Anterior_Click(object sender, RoutedEventArgs e) { var pos = PosicionVisible(_paso); if (pos > 0 && _paso != 9) Mostrar(Visibles[pos - 1]); }

    private async void Siguiente_Click(object sender, RoutedEventArgs e)
    {
        if (_paso == 9) { DialogResult = true; Close(); return; }
        BotonSiguiente.IsEnabled = false; BotonAnterior.IsEnabled = false;
        try
        {
            var error = await Validar(_paso);
            if (error is not null) { Mensaje.Text = error; return; }
            if (_paso == 8)
            {
                var fallo = await Instalar();
                if (fallo is not null) { Mensaje.Text = fallo; return; }
            }

            Mostrar(Visibles[PosicionVisible(_paso) + 1]);
        }
        finally { BotonSiguiente.IsEnabled = true; BotonAnterior.IsEnabled = true; }
    }

    private async Task<string?> Validar(int paso)
    {
        switch (paso)
        {
            case 1:
                if (!Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out _)) return "Escriba la dirección de Acusa Central.";
                if (string.IsNullOrWhiteSpace(_correo.Text) || _contrasena.Password.Length == 0) return "Escriba el usuario y la contraseña.";
                Mensaje.Foreground = Brushes.Gray; Mensaje.Text = "Iniciando sesión…";
                try
                {
                    using var http = Cliente();
                    var r = await http.PostAsJsonAsync("/api/auth/login", new { correo = _correo.Text.Trim(), contrasena = _contrasena.Password });
                    Mensaje.Foreground = new SolidColorBrush(Color.FromRgb(0xBD, 0x2F, 0x24));
                    if (!r.IsSuccessStatusCode) return await MensajeError(r, "No se pudo iniciar sesión.");
                    var doc = await r.Content.ReadFromJsonAsync<JsonElement>();
                    _token = doc.GetProperty("token").GetString();
                    _cfg.Url = _url.Text.Trim().TrimEnd('/'); _cfg.Correo = _correo.Text.Trim();
                }
                catch (Exception ex) { Mensaje.Foreground = new SolidColorBrush(Color.FromRgb(0xBD, 0x2F, 0x24)); return "No se pudo conectar con Acusa Central: " + ex.Message; }

                return null;
            case 2:
                if (_sqlOmitir.IsChecked != true && string.IsNullOrWhiteSpace(_sqlServidor.Text)) return "Escriba el servidor de SQL o marque Omitir.";
                _cfg.SqlServidor = _sqlServidor.Text.Trim(); _cfg.SqlUsuario = _sqlUsuario.Text.Trim(); _cfg.SqlWindows = _sqlWindows.IsChecked == true; _cfg.SqlOmitido = _sqlOmitir.IsChecked == true;
                return null;
            case 3:
                if (string.IsNullOrWhiteSpace(_usuarioSdk.Text)) return "Escriba el usuario del SDK.";
                if (!File.Exists(Path.Combine(_rutaSdk.Text.Trim(), "MGWServicios.dll"))) return "No se encontró MGWServicios.dll en esa ruta. Revise la ruta del sistema CONTPAQi.";
                _cfg.UsuarioSdk = _usuarioSdk.Text.Trim(); _cfg.NombrePaq = _nombrePaq.Text.Trim(); _cfg.RutaSdk = _rutaSdk.Text.Trim(); _cfg.InicioSinDialogo = true;
                return null;
            case 6:
                if (!_llaveManual.Password.Trim().StartsWith("wk_", StringComparison.Ordinal)) return "La llave de un worker empieza con wk_. Revise lo que pegó.";
                return null;
            case 7:
                var marcadas = _empresas.Where(x => x.Marca.IsChecked == true).ToList();
                if (marcadas.Count == 0) return "Marque al menos una empresa.";
                if (!_empresas.Any(x => x.Principal.IsChecked == true && x.Marca.IsChecked == true)) return "Marque una empresa como Principal.";
                return null;
            default: return null;
        }
    }

    // ------------------------------------------------------------------ pruebas de conexión

    private async void ProbarSql_Click(object sender, RoutedEventArgs e)
    {
        _estadoSql.Foreground = Brushes.Gray; _estadoSql.Text = "Probando…";
        try
        {
            var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder { DataSource = _sqlServidor.Text.Trim(), ConnectTimeout = 6, TrustServerCertificate = true, Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional };
            if (_sqlWindows.IsChecked == true) b.IntegratedSecurity = true; else { b.UserID = _sqlUsuario.Text.Trim(); b.Password = _sqlClave.Password; }
            await using var cn = new Microsoft.Data.SqlClient.SqlConnection(b.ConnectionString);
            await cn.OpenAsync();
            _estadoSql.Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x6B, 0x50)); _estadoSql.Text = "✔ Conexión correcta (SQL Server " + cn.ServerVersion + ").";
        }
        catch (Exception ex) { _estadoSql.Foreground = new SolidColorBrush(Color.FromRgb(0xBD, 0x2F, 0x24)); _estadoSql.Text = "✖ " + ex.Message; }
    }

    private void ProbarSdk_Click(object sender, RoutedEventArgs e)
    {
        var dll = Path.Combine(_rutaSdk.Text.Trim(), "MGWServicios.dll");
        var ok = File.Exists(dll);
        _estadoSdk.Foreground = ok ? new SolidColorBrush(Color.FromRgb(0x0B, 0x6B, 0x50)) : new SolidColorBrush(Color.FromRgb(0xBD, 0x2F, 0x24));
        _estadoSdk.Text = ok ? "✔ SDK encontrado. El inicio de sesión real se comprueba al arrancar el worker." : "✖ No se encontró MGWServicios.dll en esa ruta.";
    }

    // ------------------------------------------------------------------ empresas

    private void CargarEmpresas()
    {
        var anteriores = _empresas.Where(x => x.Marca.IsChecked == true).Select(x => x.Carpeta).ToHashSet();
        var principal = _empresas.FirstOrDefault(x => x.Principal.IsChecked == true).Carpeta ?? _cfg.Empresa;
        _empresas.Clear(); _listaEmpresas.Children.Clear();
        if (anteriores.Count == 0) foreach (var n in _cfg.Empresas) anteriores.Add(n);
        var raiz = @"C:\Compac\Empresas";
        if (!Directory.Exists(raiz)) { _estadoEmpresas.Text = "No existe " + raiz; return; }

        var prefijos = new List<string>();
        if (_detComercial.IsChecked == true) prefijos.Add("ad");
        if (_detNominas.IsChecked == true) prefijos.Add("nm");
        if (_detConta.IsChecked == true) prefijos.Add("ct");
        foreach (var dir in Directory.GetDirectories(raiz).Select(Path.GetFileName).Where(n => n is not null && prefijos.Any(p => n!.StartsWith(p, StringComparison.OrdinalIgnoreCase))).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var carpeta = dir!;
            var marca = new CheckBox { IsChecked = anteriores.Contains(carpeta), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            marca.Checked += (_, _) => ActualizarContador(); marca.Unchecked += (_, _) => ActualizarContador();
            var radio = new RadioButton { Content = "Principal", GroupName = "principal", IsChecked = string.Equals(carpeta, principal, StringComparison.OrdinalIgnoreCase), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var fila = new Grid { Margin = new Thickness(10, 5, 10, 5) };
            fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fila.ColumnDefinitions.Add(new ColumnDefinition());
            fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fila.Children.Add(marca);
            var texto = new TextBlock { Text = carpeta, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
            Grid.SetColumn(texto, 1); fila.Children.Add(texto);
            var real = new CheckBox { Content = "Producción", IsChecked = _cfg.EmpresasReales.Contains(carpeta, StringComparer.OrdinalIgnoreCase), FontSize = 12, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Empresa real: admite escritura y no exige el prefijo PRUEBA." };
            Grid.SetColumn(real, 2); fila.Children.Add(real);
            Grid.SetColumn(radio, 3); fila.Children.Add(radio);
            _listaEmpresas.Children.Add(fila);
            _empresas.Add((marca, radio, carpeta, real));
        }

        ActualizarContador();
    }

    private void ActualizarContador() => _estadoEmpresas.Text = $"{_empresas.Count(x => x.Marca.IsChecked == true)} de {_empresas.Count} empresas seleccionadas";

    // ------------------------------------------------------------------ revisión e instalación

    private void ArmarResumen()
    {
        var sel = _empresas.Where(x => x.Marca.IsChecked == true).Select(x => x.Carpeta).ToList();
        var principal = _empresas.FirstOrDefault(x => x.Principal.IsChecked == true && x.Marca.IsChecked == true).Carpeta ?? "—";
        _resumen.Text =
            $"Acusa Central:  {_url.Text.Trim()}\n" +
            $"Usuario:  {_correo.Text.Trim()}\n" +
            $"Usuario del SDK:  {_usuarioSdk.Text.Trim()}\n" +
            $"Empresas:  {sel.Count} seleccionadas · principal {principal}";
    }

    private async Task<string?> Instalar()
    {
        Mensaje.Foreground = Brushes.Gray; Mensaje.Text = "Instalando…";
        Mensaje.Foreground = new SolidColorBrush(Color.FromRgb(0xBD, 0x2F, 0x24));
        try
        {
            using var http = Cliente(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            string llave;
            if (_llaveAuto.IsChecked == true)
            {
                var r = await http.PostAsJsonAsync("/api/portal/llaves", new { rol = "worker", nombre = "Worker " + Environment.MachineName });
                if (!r.IsSuccessStatusCode) return await MensajeError(r, "No se pudo crear la llave del worker.");
                llave = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("llave").GetString()!;
            }
            else llave = _llaveManual.Password.Trim();

            var sel = _empresas.Where(x => x.Marca.IsChecked == true).Select(x => x.Carpeta).ToList();
            var nuevas = 0;
            foreach (var n in sel)
            {
                var r = await http.PostAsJsonAsync("/api/portal/empresas", new { nombre = n });
                if (r.IsSuccessStatusCode) nuevas++;
                else if ((int)r.StatusCode != 409) return await MensajeError(r, $"No se pudo registrar la empresa {n}.");
            }

            Secretos.GuardarLlave(llave);
            if (_sdkClave.Password.Length > 0) Secretos.GuardarClaveSdk(_sdkClave.Password);
            _cfg.Empresas = sel;
            _cfg.EmpresasReales = _empresas.Where(x => x.Marca.IsChecked == true && x.Real.IsChecked == true).Select(x => x.Carpeta).ToList();
            _cfg.Empresa = _empresas.First(x => x.Principal.IsChecked == true && x.Marca.IsChecked == true).Carpeta;
            _cfg.InicioSinDialogo = true;
            _cfg.Configurado = true;
            if (string.IsNullOrEmpty(_cfg.PropietarioSid)) { _cfg.PropietarioSid = Acceso.SidActual; _cfg.PropietarioNombre = Acceso.NombreActual; }
            _cfg.Guardar();
            _instalado = true;
            _resultado.Text =
                "✔ Acusa Central verificado\n" +
                "✔ Llave del worker guardada cifrada en este servidor\n" +
                $"✔ {sel.Count} empresas configuradas\n\n" +
                "El worker se inicia al cerrar el asistente.";
            return null;
        }
        catch (Exception ex) { return "No se pudo instalar: " + ex.Message; }
    }

    // ------------------------------------------------------------------ utilidades

    private HttpClient Cliente() => new() { BaseAddress = new Uri(_url.Text.Trim().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };

    private static async Task<string> MensajeError(HttpResponseMessage r, string porDefecto)
    {
        try
        {
            var doc = await r.Content.ReadFromJsonAsync<JsonElement>();
            if (doc.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } t) return t;
        }
        catch { /* sin cuerpo legible */ }

        return porDefecto;
    }

    public bool Instalado => _instalado;
}
