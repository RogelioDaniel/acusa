namespace Acusa.Sdk.Application.Productos;

/// <summary>Puerto hacia CONTPAQi para productos. Solo busca y crea.</summary>
public interface IProductoGateway
{
    /// <summary>Devuelve <c>null</c> si no existe.</summary>
    Task<Producto?> BuscarAsync(string codigo, CancellationToken ct);

    /// <summary>Crea el producto ya validado. Devuelve <c>Invalido</c> si la unidad no existe.</summary>
    Task<ResultadoCrearProducto> CrearAsync(NuevoProducto producto, CancellationToken ct);

    /// <summary>Cambia solo los campos con valor de un producto que ya existe. Si algo falla no deja cambios a medias.</summary>
    Task<ResultadoActualizarProducto> ActualizarAsync(string codigo, CambiosProducto cambios, CancellationToken ct);
}
