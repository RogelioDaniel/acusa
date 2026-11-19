using System.Collections.Concurrent;

namespace Acusa.Sdk.Integration.Contpaqi;

/// <summary>
/// Hilo dedicado que ejecuta, de una en una, todas las llamadas al SDK. El SDK admite una empresa por
/// proceso y una sola sesión, así que las llamadas se serializan y siempre salen del mismo hilo.
/// Una excepción del trabajo llega a quien lo pidió y no detiene el hilo.
/// </summary>
internal sealed class HiloSdk : IDisposable
{
    private readonly BlockingCollection<Action> _trabajosPendientes = new();
    private readonly Thread _hilo;

    public HiloSdk()
    {
        _hilo = new Thread(AtenderTrabajos) { Name = "Acusa.Sdk", IsBackground = true };
        if (OperatingSystem.IsWindows())
        {
            // PowerShell ejecutó las pruebas del spike en un hilo STA; se conserva esa condición.
            _hilo.SetApartmentState(ApartmentState.STA);
        }

        _hilo.Start();
    }

    public Task<TResultado> EjecutarAsync<TResultado>(Func<TResultado> trabajo)
    {
        var finalizacion = new TaskCompletionSource<TResultado>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _trabajosPendientes.Add(() =>
            {
                try
                {
                    finalizacion.SetResult(trabajo());
                }
                catch (Exception excepcion)
                {
                    finalizacion.SetException(excepcion);
                }
            });
        }
        catch (InvalidOperationException)
        {
            finalizacion.SetException(new ObjectDisposedException(nameof(HiloSdk), "El hilo del SDK ya se cerró."));
        }

        return finalizacion.Task;
    }

    private void AtenderTrabajos()
    {
        foreach (var trabajo in _trabajosPendientes.GetConsumingEnumerable())
        {
            trabajo();
        }
    }

    public void Dispose() => _trabajosPendientes.CompleteAdding();
}
