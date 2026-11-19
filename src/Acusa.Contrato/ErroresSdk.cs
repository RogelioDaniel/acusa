using System.Text.RegularExpressions;

namespace Acusa.Contrato;

public static partial class ErroresSdk
{
    private static readonly Dictionary<int, string> MensajesConocidos = new()
    {
        [3] = "El registro que se buscó no existe en CONTPAQi Comercial.",
        [120115] = "El almacén indicado en el movimiento no existe en la empresa. Use el código de un almacén registrado en CONTPAQi Comercial.",
        [120146] = "El concepto de documento indicado no existe en la empresa. Use el código de un concepto registrado en CONTPAQi Comercial.",
        [120513] = "CONTPAQi Comercial no pudo guardar el registro porque le falta una lista de precios. Revise la configuración de la empresa.",
    };

    public static (string Mensaje, string? Tecnico) Traducir(int? codigo, string? mensajeSdk)
    {
        string? textoOriginal = null;
        if (!string.IsNullOrWhiteSpace(mensajeSdk))
        {
            textoOriginal = mensajeSdk.Trim();
        }

        if (codigo is not { } codigoSdk || codigoSdk == 0)
        {
            return (textoOriginal ?? "La operación no se completó.", null);
        }

        if (MensajesConocidos.TryGetValue(codigoSdk, out var mensajeConocido))
        {
            return (mensajeConocido, textoOriginal);
        }

        string? textoDelSdk = null;
        if (textoOriginal is not null)
        {
            textoDelSdk = PrefijoDeFuncion().Replace(textoOriginal, string.Empty).Trim().TrimEnd('.');
        }

        if (string.IsNullOrEmpty(textoDelSdk))
        {
            return ($"CONTPAQi Comercial rechazó la operación (código {codigoSdk}).", textoOriginal);
        }

        return ($"CONTPAQi Comercial rechazó la operación (código {codigoSdk}): {textoDelSdk}.", textoOriginal);
    }

    [GeneratedRegex(@"^f\w+(\s*\([^)]*\))?\s*=\s*-?\d+\s*:\s*")]
    private static partial Regex PrefijoDeFuncion();
}
