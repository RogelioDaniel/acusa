namespace Acusa.Sdk.Application.Productos;

/// <summary>Producto o servicio leído de CONTPAQi (tabla admProductos).</summary>
/// <param name="Codigo">CCODIGOPRODUCTO.</param>
/// <param name="Nombre">CNOMBREPRODUCTO.</param>
/// <param name="Descripcion">CDESCRIPCIONPRODUCTO.</param>
/// <param name="Tipo">CTIPOPRODUCTO (1 = producto).</param>
/// <param name="Estatus">CSTATUSPRODUCTO (1 = activo).</param>
/// <param name="Precio1">CPRECIO1.</param>
/// <param name="UnidadBase">CNOMBREUNIDAD, nombre de la unidad base.</param>
/// <param name="ClaveSat">CCLAVESAT: clave de producto o servicio del SAT (8 dígitos). Sin ella no se puede timbrar.</param>
public sealed record Producto(
    string Codigo,
    string Nombre,
    string? Descripcion,
    int Tipo,
    int Estatus,
    decimal Precio1,
    string? UnidadBase,
    string? ClaveSat = null);

/// <summary>Datos para dar de alta un producto de prueba.</summary>
/// <param name="Codigo">Debe empezar con el prefijo de prueba.</param>
/// <param name="Nombre">Debe empezar con el prefijo de prueba.</param>
/// <param name="UnidadBase">Nombre de una unidad que ya exista en la empresa.</param>
/// <param name="Precio1">Precio de la lista 1; no puede ser negativo.</param>
/// <param name="Descripcion">Opcional.</param>
/// <param name="ClaveSat">Opcional: clave de producto o servicio del SAT (8 dígitos). Se asigna justo después del alta.</param>
/// <param name="Tipo">CTIPOPRODUCTO: 1, 2 o 3; vacío usa 1.</param>
/// <param name="TextosExtra">Hasta tres textos adicionales para la factura (CTEXTOEXTRA1 a 3).</param>
public sealed record NuevoProducto(
    string Codigo,
    string Nombre,
    string UnidadBase,
    decimal Precio1 = 0m,
    string? Descripcion = null,
    string? ClaveSat = null,
    int? Tipo = null,
    IReadOnlyList<string>? TextosExtra = null);

/// <summary>Datos que <c>producto.actualizar</c> puede cambiar. Un campo ausente no se toca.</summary>
public sealed record CambiosProducto(string? Nombre, string? Descripcion, decimal? Precio1, string? ClaveSat, IReadOnlyList<string>? TextosExtra = null)
{
    public bool EstaVacio =>
        string.IsNullOrWhiteSpace(Nombre) && string.IsNullOrWhiteSpace(Descripcion) && Precio1 is null && string.IsNullOrWhiteSpace(ClaveSat)
        && TextosExtra is not { Count: > 0 };
}

/// <summary>Resultado de <see cref="ProductoService.ActualizarAsync"/>.</summary>
public sealed record ResultadoActualizarProducto(
    Clientes.TipoResultadoActualizar Tipo,
    Producto? Producto = null,
    string? Mensaje = null,
    int? CodigoSdk = null);

/// <summary>Resultado de <see cref="ProductoService.CrearAsync"/>.</summary>
public sealed record ResultadoCrearProducto(
    Clientes.TipoResultadoCrear Tipo,
    Producto? Producto = null,
    string? Mensaje = null,
    int? CodigoSdk = null);
