using System.Collections.Concurrent;
using Acusa.Contrato;

namespace Acusa.Orquestador;

/// <summary>
/// Avisos dentro del propio Central: despierta al worker cuando entra una orden y a quien espera cuando termina. Evita que
/// ambos tengan que preguntar cada pocos segundos. Si un aviso se pierde, la base de datos sigue siendo la fuente de verdad:
/// quien espera vuelve a leerla cada pocos segundos.
/// </summary>
public sealed class Notificador
{
    private TaskCompletionSource _cola = Nueva();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _terminadas = new();

    private static TaskCompletionSource Nueva() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Se completa cuando entra la siguiente orden a la cola. Pídala antes de revisar la cola, para no perder avisos.</summary>
    public Task SiguienteEncolada() => Volatile.Read(ref _cola).Task;

    public void TareaEncolada() => Interlocked.Exchange(ref _cola, Nueva()).TrySetResult();

    /// <summary>Se completa cuando la tarea termina (completada o fallida).</summary>
    public Task EsperarTerminada(string tareaId) => _terminadas.GetOrAdd(tareaId, _ => Nueva()).Task;

    public void TareaTerminada(string tareaId)
    {
        if (_terminadas.TryRemove(tareaId, out var aviso))
        {
            aviso.TrySetResult();
        }
    }

    public void Olvidar(string tareaId) => _terminadas.TryRemove(tareaId, out _);

    /// <summary>
    /// Espera hasta <paramref name="espera"/> a que la tarea termine y devuelve su estado en ese momento. Si ya había
    /// terminado, regresa de inmediato. Devuelve <c>null</c> si la tarea no existe.
    /// </summary>
    public async Task<TareaDto?> EsperarResultadoAsync(Almacen almacen, string clienteId, string tareaId, TimeSpan espera, CancellationToken ct)
    {
        var fin = DateTime.UtcNow + espera;
        try
        {
            while (true)
            {
                // Primero el aviso y después la lectura: si la tarea termina entre una y otra, el aviso ya está registrado.
                var aviso = EsperarTerminada(tareaId);
                var tarea = almacen.ObtenerTarea(clienteId, tareaId);
                if (tarea is null || tarea.Estado is EstadoTarea.Completada or EstadoTarea.Fallida)
                {
                    return tarea;
                }

                var resto = fin - DateTime.UtcNow;
                if (resto <= TimeSpan.Zero)
                {
                    return tarea;
                }

                await Task.WhenAny(aviso, Task.Delay(resto < TimeSpan.FromSeconds(2) ? resto : TimeSpan.FromSeconds(2), ct));
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            Olvidar(tareaId);
        }
    }
}
