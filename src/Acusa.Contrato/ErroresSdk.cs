using System.Text.RegularExpressions;

namespace Acusa.Contrato;

/// <summary>
/// Convierte el error que regresa el SDK de CONTPAQi (<c>fFuncion = 120115: texto del SDK</c>) en un mensaje que la persona
/// que consume la API puede atender. El texto original del SDK se conserva como detalle.
/// </summary>
public static partial class ErroresSdk
{
    private static readonly Dictionary<int, string> Conocidos = new()
    {
        [3] = "El registro que se buscó no existe en CONTPAQi Comercial.",
        [120115] = "El almacén indicado en el movimiento no existe en la empresa. Use el código de un almacén registrado en CONTPAQi Comercial.",
        [120146] = "El concepto de documento indicado no existe en la empresa. Use el código de un concepto registrado en CONTPAQi Comercial.",
        [120513] = "CONTPAQi Comercial no pudo guardar el registro porque le falta una lista de precios. Revise la configuración de la empresa.",
    };

    /// <summary>Mensaje para la persona y, aparte, el texto técnico original del SDK.</summary>
    public static (string Mensaje, string? Tecnico) Traducir(int? codigo, string? mensajeSdk)
    {
        var original = string.IsNullOrWhiteSpace(mensajeSdk) ? null : mensajeSdk.Trim();
        if (codigo is not { } c || c == 0)
        {
            return (original ?? "La operación no se completó.", null);
        }

        if (Conocidos.TryGetValue(c, out var conocido))
        {
            return (conocido, original);
        }

        var texto = original is null ? null : Prefijo().Replace(original, string.Empty).Trim().TrimEnd('.');
        return (string.IsNullOrEmpty(texto)
            ? $"CONTPAQi Comercial rechazó la operación (código {c})."
            : $"CONTPAQi Comercial rechazó la operación (código {c}): {texto}.", original);
    }

    [GeneratedRegex(@"^f\w+(\s*\([^)]*\))?\s*=\s*-?\d+\s*:\s*")]
    private static partial Regex Prefijo();
}
