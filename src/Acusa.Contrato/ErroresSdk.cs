using System.Text.RegularExpressions;

namespace Acusa.Contrato;

/// <summary>
/// Convierte el error que regresa el SDK de CONTPAQi (<c>fFuncion = 120115: texto del SDK</c>) en un mensaje que la persona
/// que consume la API puede atender. El texto textoOriginal del SDK se conserva como detalle.
/// </summary>
public static partial class ErroresSdk
{
    private static readonly Dictionary<int, string> MensajesConocidos = new()
    {
        [3] = "El registro que se buscó no existe en CONTPAQi Comercial.",
        [120115] = "El almacén indicado en el movimiento no existe en la empresa. Use el código de un almacén registrado en CONTPAQi Comercial.",
        [120146] = "El concepto de documento indicado no existe en la empresa. Use el código de un concepto registrado en CONTPAQi Comercial.",
        [120513] = "CONTPAQi Comercial no pudo guardar el registro porque le falta una lista de precios. Revise la configuración de la empresa.",
    };

    /// <summary>Mensaje para la persona y, aparte, el texto técnico textoOriginal del SDK.</summary>
    public static (string Mensaje, string? Tecnico) Traducir(int? codigo, string? mensajeSdk)
    {
        var textoOriginal = string.IsNullOrWhiteSpace(mensajeSdk) ? null : mensajeSdk.Trim();
        if (codigo is not { } codigoSdk || codigoSdk == 0)
        {
            return (textoOriginal ?? "La operación no se completó.", null);
        }

        if (MensajesConocidos.TryGetValue(codigoSdk, out var mensajeConocido))
        {
            return (mensajeConocido, textoOriginal);
        }

        var textoDelSdk = textoOriginal is null ? null : PrefijoDeFuncion().Replace(textoOriginal, string.Empty).Trim().TrimEnd('.');
        return (string.IsNullOrEmpty(textoDelSdk)
            ? $"CONTPAQi Comercial rechazó la operación (código {codigoSdk})."
            : $"CONTPAQi Comercial rechazó la operación (código {codigoSdk}): {textoDelSdk}.", textoOriginal);
    }

    [GeneratedRegex(@"^f\w+(\s*\([^)]*\))?\s*=\s*-?\d+\s*:\s*")]
    private static partial Regex PrefijoDeFuncion();
}
