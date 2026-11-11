namespace Acusa.Sdk.Application.Clientes;

/// <summary>Acceso a clientes. La implementación real vive en la capa de integración con CONTPAQi.</summary>
public interface IClienteGateway
{
    /// <summary>Busca por código. Devuelve <c>null</c> si no existe.</summary>
    Task<Cliente?> BuscarAsync(string codigo, CancellationToken ct);

    /// <summary>Da de alta un cliente. Si algo falla no deja registros a medias.</summary>
    Task<ResultadoCrear> CrearAsync(NuevoCliente cliente, CancellationToken ct);

    /// <summary>
    /// Cambia solo los campos con valor de un cliente que ya existe. Si algo falla no deja cambios a medias.
    /// El servicio ya validó los datos y que el cliente sea de prueba.
    /// </summary>
    Task<ResultadoActualizar> ActualizarAsync(string codigo, CambiosCliente cambios, CancellationToken ct);

    /// <summary>Devuelve la dirección del tipo pedido (0 fiscal, 1 envío), o <c>null</c> si el cliente no la tiene.</summary>
    Task<Direccion?> BuscarDireccionAsync(string codigoCliente, int tipo, CancellationToken ct);

    /// <summary>
    /// Crea la dirección del tipo indicado o, si el cliente ya la tiene, la completa con los campos que vengan con valor.
    /// El servicio ya validó los datos y que el cliente sea de prueba.
    /// </summary>
    Task<ResultadoDireccion> GuardarDireccionAsync(Direccion direccion, CancellationToken ct);
}
