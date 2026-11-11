namespace Acusa.Worker.Desktop;

public partial class App : System.Windows.Application
{
    private System.Threading.Mutex? _unico;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // Una sola ventana por equipo: un segundo intento avisa y sale.
        _unico = new System.Threading.Mutex(true, "Acusa.Worker.Desktop.Unico", out var nuevo);
        if (!nuevo)
        {
            System.Windows.MessageBox.Show("Acusa Worker ya está abierto. Búsquelo en la bandeja del sistema.", "Acusa Worker",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        // Primera vez en este equipo: el asistente de configuración, como el de Montek.
        var cfg = Config.Cargar();
        if (!cfg.Configurado) new Asistente(cfg).ShowDialog();
        var ventana = new MainWindow();
        ventana.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _unico?.Dispose();
        base.OnExit(e);
    }
}
