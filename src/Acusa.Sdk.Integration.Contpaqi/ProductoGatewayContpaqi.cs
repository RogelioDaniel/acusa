using System.Globalization;
using System.Text;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Productos;
using Acusa.Sdk.Integration.Contpaqi.Native;

namespace Acusa.Sdk.Integration.Contpaqi;

/// <summary>
/// Productos sobre el SDK real. Solo busca y crea; no borra ni modifica.
/// El alta usa <c>fAltaProducto</c> con la estructura <see cref="TProducto"/>. Pendiente de probar en la
/// empresa de pruebas: la secuencia sale del análisis de Montek, no de una ejecución propia.
/// </summary>
public sealed class ProductoGatewayContpaqi(SesionContpaqi sesion, TrazaSdk traza) : IProductoGateway
{
    public Task<Producto?> BuscarAsync(string codigo, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Buscar(codigo));

    public Task<ResultadoCrearProducto> CrearAsync(NuevoProducto producto, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Crear(producto));

    private Producto? Buscar(string codigo)
    {
        var r = traza.Llamar($"fBuscaProducto('{codigo}')", () => SdkNative.fBuscaProducto(codigo));
        if (r == SdkNative.NoEncontrado)
        {
            return null;
        }

        if (r != 0)
        {
            throw new InvalidOperationException($"fBuscaProducto = {r}: {SdkNative.Mensaje(r)}");
        }

        var descripcion = Leer("CDESCRIPCIONPRODUCTO");
        var unidad = Leer("CNOMBREUNIDAD");
        return new Producto(
            Codigo: Leer("CCODIGOPRODUCTO"),
            Nombre: Leer("CNOMBREPRODUCTO"),
            Descripcion: string.IsNullOrWhiteSpace(descripcion) ? null : descripcion,
            Tipo: Entero(Leer("CTIPOPRODUCTO")),
            Estatus: Entero(Leer("CSTATUSPRODUCTO")),
            Precio1: Decimal(Leer("CPRECIO1")),
            UnidadBase: string.IsNullOrWhiteSpace(unidad) ? null : unidad,
            ClaveSat: string.IsNullOrWhiteSpace(Leer("CCLAVESAT")) ? null : Leer("CCLAVESAT"));
    }

    public Task<ResultadoActualizarProducto> ActualizarAsync(string codigo, CambiosProducto cambios, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Actualizar(codigo, cambios));

    /// <summary>
    /// Modifica con <c>fBuscaProducto</c>, <c>fEditaProducto</c>, un <c>fSetDatoProducto</c> por campo con valor y
    /// <c>fGuardaProducto</c>; si algo falla cancela la edición. CCLAVESAT es el campo que Montek usa para la clave SAT.
    /// </summary>
    private ResultadoActualizarProducto Actualizar(string codigo, CambiosProducto cambios)
    {
        var previo = traza.Llamar($"fBuscaProducto('{codigo}')", () => SdkNative.fBuscaProducto(codigo));
        if (previo == SdkNative.NoEncontrado)
        {
            return new ResultadoActualizarProducto(TipoResultadoActualizar.NoEncontrado, Mensaje: $"No existe el producto {codigo}.");
        }

        if (previo != 0)
        {
            return ErrorActualizar("fBuscaProducto", previo);
        }

        var e = traza.Llamar("fEditaProducto()", SdkNative.fEditaProducto);
        if (e != 0)
        {
            return ErrorActualizar("fEditaProducto", e);
        }

        try
        {
            var campos = new List<(string Campo, string Valor)>();
            if (!string.IsNullOrWhiteSpace(cambios.Nombre)) campos.Add(("CNOMBREPRODUCTO", cambios.Nombre));
            if (!string.IsNullOrWhiteSpace(cambios.Descripcion)) campos.Add(("CDESCRIPCIONPRODUCTO", cambios.Descripcion));
            if (cambios.Precio1 is { } precio) campos.Add(("CPRECIO1", precio.ToString(CultureInfo.InvariantCulture)));
            if (!string.IsNullOrWhiteSpace(cambios.ClaveSat)) campos.Add(("CCLAVESAT", cambios.ClaveSat));
            for (var i = 0; i < (cambios.TextosExtra?.Count ?? 0) && i < 3; i++)
            {
                campos.Add(($"CTEXTOEXTRA{i + 1}", cambios.TextosExtra![i]));
            }

            foreach (var (campo, valor) in campos)
            {
                e = traza.Llamar($"fSetDatoProducto('{campo}','{valor}')", () => SdkNative.fSetDatoProducto(campo, valor));
                if (e != 0)
                {
                    traza.Llamar("fCancelarModificacionProducto()", SdkNative.fCancelarModificacionProducto);
                    return ErrorActualizar($"fSetDatoProducto({campo})", e);
                }
            }

            e = traza.Llamar("fGuardaProducto()", SdkNative.fGuardaProducto);
            if (e != 0)
            {
                traza.Llamar("fCancelarModificacionProducto()", SdkNative.fCancelarModificacionProducto);
                return ErrorActualizar("fGuardaProducto", e);
            }
        }
        catch
        {
            traza.Llamar("fCancelarModificacionProducto()", SdkNative.fCancelarModificacionProducto);
            throw;
        }

        var despues = Buscar(codigo);
        return despues is null
            ? new ResultadoActualizarProducto(TipoResultadoActualizar.ErrorSdk, Mensaje: "El SDK guardó sin error, pero el producto no aparece al buscarlo.")
            : new ResultadoActualizarProducto(TipoResultadoActualizar.Actualizado, despues);
    }

    private static ResultadoActualizarProducto ErrorActualizar(string funcion, int codigo) =>
        new(TipoResultadoActualizar.ErrorSdk, Mensaje: $"{funcion} = {codigo}: {SdkNative.Mensaje(codigo)}", CodigoSdk: codigo);

    private ResultadoCrearProducto Crear(NuevoProducto producto)
    {
        var previo = traza.Llamar($"fBuscaProducto('{producto.Codigo}')", () => SdkNative.fBuscaProducto(producto.Codigo));
        if (previo == 0)
        {
            return new ResultadoCrearProducto(TipoResultadoCrear.YaExiste, Mensaje: $"Ya existe el producto {producto.Codigo}.");
        }

        if (previo != SdkNative.NoEncontrado)
        {
            return Error("fBuscaProducto", previo);
        }

        var unidad = traza.Llamar($"fBuscaUnidad('{producto.UnidadBase}')", () => SdkNative.fBuscaUnidad(producto.UnidadBase));
        if (unidad != 0)
        {
            return new ResultadoCrearProducto(
                TipoResultadoCrear.Invalido,
                Mensaje: $"La unidad base '{producto.UnidadBase}' no existe en la empresa: {SdkNative.Mensaje(unidad)}",
                CodigoSdk: unidad);
        }

        var estructura = new TProducto
        {
            cCodigoProducto = producto.Codigo,
            cNombreProducto = producto.Nombre,
            cDescripcionProducto = producto.Descripcion ?? string.Empty,
            cTipoProducto = producto.Tipo ?? 1,
            cStatusProducto = 1,
            cFechaAltaProducto = DateTime.Now.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            cCodigoUnidadBase = producto.UnidadBase,
            cPrecio1 = (double)producto.Precio1,
            cControlExistencia = 1,
            cMetodoCosteo = 1,
        };

        var id = 0;
        var e = traza.Llamar($"fAltaProducto('{producto.Codigo}')", () => SdkNative.fAltaProducto(ref id, ref estructura));
        if (e != 0)
        {
            return Error("fAltaProducto", e);
        }

        if (!string.IsNullOrWhiteSpace(producto.ClaveSat) || producto.TextosExtra is { Count: > 0 })
        {
            var clave = Actualizar(producto.Codigo, new CambiosProducto(null, null, null, producto.ClaveSat, producto.TextosExtra));
            if (clave.Tipo != TipoResultadoActualizar.Actualizado)
            {
                return new ResultadoCrearProducto(
                    TipoResultadoCrear.ErrorSdk,
                    Mensaje: $"El producto {producto.Codigo} se creó, pero no se pudo asignar la clave SAT o los textos extra: {clave.Mensaje} Asígnelos con producto.actualizar.",
                    CodigoSdk: clave.CodigoSdk);
            }
        }

        var creado = Buscar(producto.Codigo);
        return creado is null
            ? new ResultadoCrearProducto(TipoResultadoCrear.ErrorSdk, Mensaje: "El SDK guardó sin error, pero el producto no aparece al buscarlo.")
            : new ResultadoCrearProducto(TipoResultadoCrear.Creado, creado);
    }

    private static ResultadoCrearProducto Error(string funcion, int codigo) =>
        new(TipoResultadoCrear.ErrorSdk, Mensaje: $"{funcion} = {codigo}: {SdkNative.Mensaje(codigo)}", CodigoSdk: codigo);

    private string Leer(string campo)
    {
        var sb = new StringBuilder(512);
        var r = traza.Llamar(
            $"fLeeDatoProducto('{campo}')",
            () => SdkNative.fLeeDatoProducto(campo, sb, sb.Capacity),
            valor: () => sb.ToString().Trim());
        return r == 0 ? sb.ToString().Trim() : string.Empty;
    }

    private static int Entero(string texto) =>
        int.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static decimal Decimal(string texto) =>
        decimal.TryParse(texto, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : 0m;
}
