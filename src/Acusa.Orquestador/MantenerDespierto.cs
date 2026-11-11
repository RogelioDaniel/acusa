namespace Acusa.Orquestador;

/// <summary>
/// En el plan gratuito de Render el servicio se duerme tras unos 15 minutos sin tráfico. Si Render informa su dirección pública
/// (RENDER_EXTERNAL_URL), Central se llama a sí mismo cada 10 minutos para seguir despierto. Sin esa variable no hace nada.
/// </summary>
public sealed class MantenerDespierto(IHttpClientFactory fabrica, ILogger<MantenerDespierto> bitacora) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken parada)
    {
        var url = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var destino = url.TrimEnd('/') + "/health";
        using var cliente = fabrica.CreateClient();
        cliente.Timeout = TimeSpan.FromSeconds(30);
        while (!parada.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), parada);
                using var respuesta = await cliente.GetAsync(destino, parada);
                bitacora.LogDebug("Auto-ping a {Destino}: {Estado}", destino, (int)respuesta.StatusCode);
            }
            catch (OperationCanceledException) when (parada.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                bitacora.LogWarning("Auto-ping falló: {Mensaje}", e.Message);
            }
        }
    }
}
