using System.Text.Json.Serialization;

namespace Acusa.Contrato;

/// <summary>
/// Cuerpo de toda respuesta de error de la API. <c>Codigo</c> es estable y sirve para programar; <c>Mensaje</c> es el texto para
/// la persona; <c>Detalles</c> lista cada validación que falló. <c>IdCorrelacion</c> es el mismo valor de la cabecera
/// <c>X-Correlation-Id</c> y aparece en los logs de la petición.
/// </summary>
public sealed record ErrorApi(
    string Codigo,
    string Mensaje,
    IReadOnlyList<string> Detalles,
    string? IdCorrelacion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CodigoSdk = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TareaId = null);

/// <summary>Valores de <see cref="ErrorApi.Codigo"/>.</summary>
public static class CodigosError
{
    public const string SolicitudInvalida = "solicitud_invalida";
    public const string NoAutorizado = "no_autorizado";
    public const string NoEncontrado = "no_encontrado";
    public const string MetodoNoPermitido = "metodo_no_permitido";
    public const string Conflicto = "conflicto";
    public const string Validacion = "validacion";
    public const string OperacionFallida = "operacion_fallida";
    public const string ErrorSdk = "error_sdk";
    public const string DemasiadosIntentos = "demasiados_intentos";
    public const string NoImplementado = "no_implementado";
    public const string ErrorInterno = "error_interno";

    /// <summary>Código que corresponde a un estado HTTP cuando el error no indica uno propio.</summary>
    public static string DeEstado(int estado) => estado switch
    {
        400 => SolicitudInvalida,
        401 or 403 => NoAutorizado,
        404 => NoEncontrado,
        405 => MetodoNoPermitido,
        409 => Conflicto,
        422 => Validacion,
        429 => DemasiadosIntentos,
        >= 500 => ErrorInterno,
        _ => SolicitudInvalida,
    };
}
