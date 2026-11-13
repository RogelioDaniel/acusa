using Acusa.Sdk.Application.Clientes;

namespace Acusa.Sdk.Application.Documentos;

/// <summary>Documento leído de CONTPAQi (tabla admDocumentos).</summary>
/// <param name="Id">CIDDOCUMENTO.</param>
/// <param name="Concepto">CCODIGOCONCEPTO.</param>
/// <param name="Serie">CSERIEDOCUMENTO, vacía si el concepto no usa serie.</param>
/// <param name="Folio">CFOLIO.</param>
/// <param name="Fecha">CFECHA tal como la devuelve el SDK.</param>
/// <param name="ClienteCodigo">CCODIGOCLIENTE.</param>
/// <param name="Referencia">CREFERENCIA.</param>
/// <param name="Neto">CNETO.</param>
/// <param name="Total">CTOTAL.</param>
/// <param name="Cancelado">CCANCELADO distinto de 0.</param>
/// <param name="Uuid">CUUIDCFDI; vacío mientras el documento no esté timbrado.</param>
public sealed record Documento(
    int Id,
    string Concepto,
    string? Serie,
    long Folio,
    string Fecha,
    string ClienteCodigo,
    string? Referencia,
    decimal Neto,
    decimal Total,
    bool Cancelado,
    string? Uuid);

/// <summary>Una partida del documento.</summary>
/// <param name="ProductoCodigo">Producto que ya exista; en esta etapa debe ser de prueba.</param>
/// <param name="Unidades">Mayor que cero.</param>
/// <param name="Precio">Precio unitario; no puede ser negativo.</param>
/// <param name="Almacen">Código de almacén; vacío usa el predeterminado del concepto.</param>
/// <param name="IvaPorcentaje">Porcentaje de IVA (CPORCENTAJEIMPUESTO1). Vacío: no se aplica IVA.</param>
/// <param name="RetencionIvaPorcentaje">Porcentaje de retención de IVA (CPORCENTAJERETENCION1). Vacío: no se aplica.</param>
/// <param name="IepsPorcentaje">Porcentaje de IEPS (CPORCENTAJEIMPUESTO2). Vacío: no se aplica.</param>
public sealed record NuevaPartida(
    string ProductoCodigo,
    decimal Unidades,
    decimal Precio,
    string? Almacen = null,
    decimal? IvaPorcentaje = null,
    decimal? RetencionIvaPorcentaje = null,
    decimal? IepsPorcentaje = null);

/// <summary>Documento sin timbrar (borrador). Timbrar y cancelar no forman parte de esta operación.</summary>
/// <param name="Concepto">Código del concepto de documento, por ejemplo el de factura.</param>
/// <param name="Serie">Serie del concepto; vacío si no usa serie.</param>
/// <param name="ClienteCodigo">Cliente que ya exista; en esta etapa debe ser de prueba.</param>
/// <param name="Referencia">Obligatoria y con prefijo de prueba: es la marca del documento. Hasta 20 caracteres.</param>
/// <param name="Partidas">Entre una y cincuenta.</param>
/// <param name="Fecha">Fecha del documento; vacía usa la de hoy.</param>
/// <param name="FormaPago">Clave SAT de la forma de pago (dos dígitos, por ejemplo 01 o 99); vacía no la asigna.</param>
/// <param name="UsoCfdi">Clave SAT del uso del CFDI (por ejemplo G03); vacía no lo asigna.</param>
public sealed record NuevoDocumento(
    string Concepto,
    string? Serie,
    string ClienteCodigo,
    string Referencia,
    IReadOnlyList<NuevaPartida> Partidas,
    DateOnly? Fecha = null,
    string? FormaPago = null,
    string? UsoCfdi = null);

/// <summary>Resultado de <see cref="DocumentoService.CrearAsync"/>.</summary>
public sealed record ResultadoCrearDocumento(
    TipoResultadoCrear Tipo,
    Documento? Documento = null,
    string? Mensaje = null,
    int? CodigoSdk = null);

/// <summary>Cómo se localiza un documento: por su id, o por concepto, serie y folio.</summary>
/// <param name="Id">CIDDOCUMENTO; si viene, manda sobre concepto, serie y folio.</param>
public sealed record ReferenciaDocumento(int? Id, string Concepto, string? Serie, long Folio)
{
    public override string ToString() =>
        Id is { } id ? $"con id {id}" : $"{Concepto} {Serie} {Folio}".Replace("  ", " ");
}

public enum TipoResultadoDocumento
{
    /// <summary>La operación se aplicó.</summary>
    Hecho,

    /// <summary>La solicitud no cumple las reglas; no se tocó CONTPAQi.</summary>
    Invalido,

    /// <summary>El documento no existe.</summary>
    NoEncontrado,

    /// <summary>El SDK devolvió un error.</summary>
    ErrorSdk,
}

/// <summary>Resultado de timbrar o cancelar un documento.</summary>
/// <param name="Documento">El documento tal como quedó después de la operación.</param>
/// <param name="Aviso">Algo que el usuario debe saber aunque la operación se haya aplicado.</param>
public sealed record ResultadoDocumento(
    TipoResultadoDocumento Tipo,
    Documento? Documento = null,
    string? Mensaje = null,
    int? CodigoSdk = null,
    string? Aviso = null);

/// <summary>Un archivo del CFDI que CONTPAQi dejó en disco.</summary>
/// <param name="Tipo"><c>xml</c> o <c>pdf</c>.</param>
/// <param name="Ruta">Ruta en el equipo del worker.</param>
/// <param name="Bytes">Tamaño del archivo.</param>
/// <param name="Base64">Contenido, solo si se pidió y cabe en el límite.</param>
public sealed record ArchivoEntregado(string Tipo, string Ruta, long Bytes, string? Base64);

/// <summary>Qué archivos pedir: XML, PDF o los dos.</summary>
public enum FormatoArchivo
{
    Xml,
    Pdf,
    Ambos,
}

/// <summary>Resultado de <see cref="DocumentoService.ObtenerArchivosAsync"/>.</summary>
public sealed record ResultadoArchivos(
    TipoResultadoDocumento Tipo,
    Documento? Documento = null,
    IReadOnlyList<ArchivoEntregado>? Archivos = null,
    string? Mensaje = null,
    int? CodigoSdk = null,
    string? Aviso = null);

/// <summary>Motivos de cancelación del SAT (CFDI 4.0).</summary>
public static class MotivosCancelacion
{
    public const string ConRelacion = "01";
    public const string SinRelacion = "02";
    public const string NoSeLlevoAcabo = "03";
    public const string OperacionNominativa = "04";

    public static bool Valido(string? motivo) => motivo is "01" or "02" or "03" or "04";
}

/// <summary>Contraseña del certificado de sello digital (CSD). Vive solo en el worker; no viaja en las órdenes.</summary>
public interface IProveedorClaveCertificado
{
    /// <summary>Devuelve la contraseña, o <c>null</c> si no está configurada.</summary>
    string? ObtenerClave();

    /// <summary>
    /// <c>true</c> si el worker no guarda la contraseña y Comercial usa la que ya tenga en su configuración: el SDK recibe
    /// una cadena vacía. Por defecto <c>false</c>.
    /// </summary>
    bool ContrasenaGuardadaEnComercial => false;
}
