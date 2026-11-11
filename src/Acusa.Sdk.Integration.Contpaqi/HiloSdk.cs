using System.Collections.Concurrent;

namespace Acusa.Sdk.Integration.Contpaqi;

/// <summary>
/// Hilo dedicado que ejecuta, de una en una, todas las llamadas al SDK. El SDK admite una empresa por
/// proceso y una sola sesión, así que las llamadas se serializan y siempre salen del mismo hilo.
/// </summary>
internal sealed class HiloSdk : IDisposable
{
    private readonly BlockingCollection<Action> _cola = new();
    private readonly Thread _hilo;

    public HiloSdk()
    {
        _hilo = new Thread(Bucle) { Name = "Acusa.Sdk", IsBackground = true };
        if (OperatingSystem.IsWindows())
        {
            // PowerShell ejecutó las pruebas del spike en un hilo STA; se conserva esa condición.
            _hilo.SetApartmentState(ApartmentState.STA);
        }

        _hilo.Start();
    }

    public Task<T> EjecutarAsync<T>(Func<T> trabajo)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cola.Add(() =>
        {
            try
            {
                tcs.SetResult(trabajo());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private void Bucle()
    {
        foreach (var accion in _cola.GetConsumingEnumerable())
        {
            accion();
        }
    }

    public void Dispose() => _cola.CompleteAdding();
}
