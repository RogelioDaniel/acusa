using System.Collections.Concurrent;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Productos;

namespace Acusa.Sdk.Application.Simulado;

/// <summary>Productos en memoria. La unidad base debe existir, igual que en CONTPAQi (fBuscaUnidad).</summary>
public sealed class ProductoGatewayEnMemoria(IEnumerable<string>? unidades = null) : IProductoGateway
{
    private static readonly string[] UnidadesPorOmision = ["PIEZA", "SERVICIO", "KILOGRAMO", "LITRO", "METRO"];

    private readonly HashSet<string> _unidades = new(unidades ?? UnidadesPorOmision, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Producto> _productos = new(StringComparer.Ordinal);

    public Task<Producto?> BuscarAsync(string codigo, CancellationToken ct) =>
        Task.FromResult(_productos.TryGetValue(codigo, out var p) ? p : null);

    public Task<ResultadoCrearProducto> CrearAsync(NuevoProducto producto, CancellationToken ct)
    {
        if (!_unidades.Contains(producto.UnidadBase))
        {
            return Task.FromResult(new ResultadoCrearProducto(
                TipoResultadoCrear.Invalido,
                Mensaje: $"La unidad base '{producto.UnidadBase}' no existe en la empresa."));
        }

        var nuevo = new Producto(
            producto.Codigo,
            producto.Nombre,
            producto.Descripcion,
            Tipo: producto.Tipo ?? 1,
            Estatus: 1,
            producto.Precio1,
            producto.UnidadBase.ToUpperInvariant(),
            producto.ClaveSat);

        return Task.FromResult(_productos.TryAdd(producto.Codigo, nuevo)
            ? new ResultadoCrearProducto(TipoResultadoCrear.Creado, nuevo)
            : new ResultadoCrearProducto(TipoResultadoCrear.YaExiste, Mensaje: $"Ya existe el producto {producto.Codigo}."));
    }

    public Task<ResultadoActualizarProducto> ActualizarAsync(string codigo, CambiosProducto cambios, CancellationToken ct)
    {
        if (!_productos.TryGetValue(codigo, out var actual))
        {
            return Task.FromResult(new ResultadoActualizarProducto(TipoResultadoActualizar.NoEncontrado, Mensaje: $"No existe el producto {codigo}."));
        }

        var nuevo = actual with
        {
            Nombre = string.IsNullOrWhiteSpace(cambios.Nombre) ? actual.Nombre : cambios.Nombre,
            Descripcion = string.IsNullOrWhiteSpace(cambios.Descripcion) ? actual.Descripcion : cambios.Descripcion,
            Precio1 = cambios.Precio1 ?? actual.Precio1,
            ClaveSat = string.IsNullOrWhiteSpace(cambios.ClaveSat) ? actual.ClaveSat : cambios.ClaveSat,
        };
        _productos[codigo] = nuevo;
        return Task.FromResult(new ResultadoActualizarProducto(TipoResultadoActualizar.Actualizado, nuevo));
    }
}
