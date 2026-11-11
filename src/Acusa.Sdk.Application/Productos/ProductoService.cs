using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Sesion;

namespace Acusa.Sdk.Application.Productos;

/// <summary>Casos de uso de productos. No conoce el SDK: depende solo de <see cref="IProductoGateway"/>.</summary>
public sealed class ProductoService(IProductoGateway gateway, ISesionSdk sesion, PoliticaEscritura politica)
{
    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public Task<Producto?> ObtenerAsync(string codigo, CancellationToken ct)
    {
        ExigirSesion();
        return gateway.BuscarAsync(PoliticaEscritura.NormalizarCodigo(codigo), ct);
    }

    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public async Task<ResultadoCrearProducto> CrearAsync(NuevoProducto solicitud, CancellationToken ct)
    {
        var producto = new NuevoProducto(
            PoliticaEscritura.NormalizarCodigo(solicitud.Codigo),
            (solicitud.Nombre ?? string.Empty).Trim(),
            (solicitud.UnidadBase ?? string.Empty).Trim(),
            solicitud.Precio1,
            string.IsNullOrWhiteSpace(solicitud.Descripcion) ? null : solicitud.Descripcion.Trim(),
            string.IsNullOrWhiteSpace(solicitud.ClaveSat) ? null : solicitud.ClaveSat.Trim(),
            solicitud.Tipo,
            LimpiarTextos(solicitud.TextosExtra));

        var motivo = politica.Validar(producto);
        if (motivo is not null)
        {
            return new ResultadoCrearProducto(TipoResultadoCrear.Invalido, Mensaje: motivo);
        }

        ExigirSesion();
        return await gateway.CrearAsync(producto, ct);
    }

    /// <summary>Cambia nombre, descripción, precio 1 o clave SAT de un producto de prueba que ya existe.</summary>
    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public async Task<ResultadoActualizarProducto> ActualizarAsync(string codigo, CambiosProducto solicitud, CancellationToken ct)
    {
        codigo = PoliticaEscritura.NormalizarCodigo(codigo);
        var cambios = new CambiosProducto(
            string.IsNullOrWhiteSpace(solicitud.Nombre) ? null : solicitud.Nombre.Trim(),
            string.IsNullOrWhiteSpace(solicitud.Descripcion) ? null : solicitud.Descripcion.Trim(),
            solicitud.Precio1,
            string.IsNullOrWhiteSpace(solicitud.ClaveSat) ? null : solicitud.ClaveSat.Trim(),
            LimpiarTextos(solicitud.TextosExtra));

        var motivo = politica.Validar(codigo, cambios);
        if (motivo is not null)
        {
            return new ResultadoActualizarProducto(TipoResultadoActualizar.Invalido, Mensaje: motivo);
        }

        ExigirSesion();
        return await gateway.ActualizarAsync(codigo, cambios, ct);
    }

    private static List<string>? LimpiarTextos(IReadOnlyList<string>? textos) =>
        textos?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList() is { Count: > 0 } lista ? lista : null;

    private void ExigirSesion()
    {
        var estado = sesion.Estado.Estado;
        if (estado != EstadoSesion.Lista)
        {
            throw new SdkNoListoException(estado);
        }
    }
}
