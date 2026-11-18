using System.Text.RegularExpressions;

namespace Acusa.Contrato;

/// <summary>
/// Oculta en un texto lo que nunca debe llegar a un log: llaves de API y de worker, tokens, contraseñas y datos del certificado (CSD).
/// </summary>
public static partial class Redaccion
{
    public const string Marca = "***";

    public static string Limpiar(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return texto ?? string.Empty;
        }

        var r = Llaves().Replace(texto, Marca);
        r = Bearer().Replace(r, "Bearer " + Marca);
        r = Jwt().Replace(r, Marca);
        r = Secretos().Replace(r, m => m.Groups["campo"].Value + m.Groups["sep"].Value + Marca);
        return r;
    }

    [GeneratedRegex(@"\b(?:ak|wk)_[A-Za-z0-9_\-]{8,}")]
    private static partial Regex Llaves();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-~+/=]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+")]
    private static partial Regex Jwt();

    // «campo»: «valor» / campo=valor para contraseña, clave, secreto, token, llave, password, pwd, csd, cer, key, pfx.
    [GeneratedRegex("(?<campo>\"?(?:contrase[nñ]a\\w*|password\\w*|passwd|pwd|clave\\w*|secre\\w*|token\\w*|llave\\w*|api[-_]?key|csd\\w*|certificado\\w*|pfx|\\.?key)\"?)(?<sep>\\s*[:=]\\s*)(?:\"[^\"]*\"|[^\\s,;}&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Secretos();
}
