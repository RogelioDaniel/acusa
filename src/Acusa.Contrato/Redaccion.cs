using System.Text.RegularExpressions;

namespace Acusa.Contrato;

public static partial class Redaccion
{
    public const string Marca = "***";

    public static string Limpiar(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return texto ?? string.Empty;
        }

        var textoLimpio = LlavesDeApi().Replace(texto, Marca);
        textoLimpio = CabeceraBearer().Replace(textoLimpio, "Bearer " + Marca);
        textoLimpio = TokenJwt().Replace(textoLimpio, Marca);
        textoLimpio = CampoSecreto().Replace(textoLimpio, coincidencia => coincidencia.Groups["campo"].Value + coincidencia.Groups["separador"].Value + Marca);
        return textoLimpio;
    }

    [GeneratedRegex(@"\b(?:ak|wk)_[A-Za-z0-9_\-]{8,}")]
    private static partial Regex LlavesDeApi();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-~+/=]+", RegexOptions.IgnoreCase)]
    private static partial Regex CabeceraBearer();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+")]
    private static partial Regex TokenJwt();

    [GeneratedRegex("(?<campo>\"?(?:contrase[nñ]a\\w*|password\\w*|passwd|pwd|clave\\w*|secre\\w*|token\\w*|llave\\w*|api[-_]?key|csd\\w*|certificado\\w*|pfx|\\.?key)\"?)(?<separador>\\s*[:=]\\s*)(?:\"[^\"]*\"|[^\\s,;}&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CampoSecreto();
}
