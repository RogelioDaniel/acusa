using System.Text.Json;
using Acusa.Contrato;

namespace Acusa.Orquestador.Facturacion;

/// <summary>Resultado de una orden mandada al worker y esperada en la misma llamada.</summary>
/// <param name="Ok">La operación se completó.</param>
/// <param name="EnCurso">Pasó el tiempo de espera sin respuesta: la tarea sigue en la cola.</param>
public sealed record ResultadoOrden(bool Ok, JsonElement? Datos, string? Error, int? CodigoSdk, bool EnCurso, string? TareaId);

/// <summary>Manda una orden a un worker y espera su resultado. Se separa para poder probar el servicio sin una cola real.</summary>
public interface IEjecutorOrdenes
{
    Task<ResultadoOrden> EjecutarAsync(string clienteId, string empresa, string tipo, object payload, string? llave, CancellationToken ct);
}

/// <summary>Ejecuta las órdenes por la cola de Central: las encola, avisa al worker y espera el resultado.</summary>
public sealed class EjecutorCola(Almacen almacen, Notificador notificador, Opciones opciones) : IEjecutorOrdenes
{
    public async Task<ResultadoOrden> EjecutarAsync(string clienteId, string empresa, string tipo, object payload, string? llave, CancellationToken ct)
    {
        if (!almacen.EmpresaDelCliente(clienteId, empresa))
        {
            return new ResultadoOrden(false, null, $"La empresa '{empresa}' no está registrada para este cliente.", null, false, null);
        }

        var json = JsonSerializer.Serialize(payload);
        if (json.Length > opciones.MaxPayloadBytes)
        {
            return new ResultadoOrden(false, null, $"La orden excede {opciones.MaxPayloadBytes} caracteres.", null, false, null);
        }

        string id;
        if (!string.IsNullOrEmpty(llave))
        {
            var r = almacen.EncolarIdempotente(clienteId, empresa, tipo, json, llave, PeticionActual.Texto);
            if (r.Conflicto)
            {
                return new ResultadoOrden(false, null, "La orden cambió respecto a un intento anterior con la misma llave.", null, false, r.Id);
            }

            id = r.Id;
            if (!r.Repetida)
            {
                notificador.TareaEncolada();
            }
        }
        else
        {
            id = almacen.EncolarTarea(clienteId, empresa, tipo, json, PeticionActual.Texto);
            notificador.TareaEncolada();
        }

        var tarea = await notificador.EsperarResultadoAsync(almacen, clienteId, id, TimeSpan.FromSeconds(Math.Max(1, opciones.FacturacionEsperaSegundos)), ct);
        return tarea?.Estado switch
        {
            EstadoTarea.Completada => new ResultadoOrden(true, tarea.Resultado, null, null, false, id),
            EstadoTarea.Fallida => new ResultadoOrden(false, null, tarea.Error ?? "La operación no se completó.", tarea.CodigoSdk, false, id),
            _ => new ResultadoOrden(false, null, "El worker no respondió a tiempo; la tarea sigue en la cola.", null, true, id),
        };
    }
}
