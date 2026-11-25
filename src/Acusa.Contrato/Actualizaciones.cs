using System.Security.Cryptography;
using System.Text;

namespace Acusa.Contrato;

/// <summary>
/// Versión publicada del worker. <see cref="Firma"/> es la firma RSA-PSS (SHA-256) de <see cref="FirmaActualizaciones.TextoFirmado"/>,
/// hecha con la llave privada de quien publica; el worker la verifica con la llave pública que trae compilada.
/// </summary>
public sealed record ManifiestoActualizacion(string Version, string Sha256, long Tamano, string Firma, DateTime Publicada, string? Notas = null);

/// <summary>Verificación de los paquetes de actualización del worker. Central solo los distribuye: no puede firmarlos.</summary>
public static class FirmaActualizaciones
{
    /// <summary>Llave pública (SubjectPublicKeyInfo, RSA 3072) de quien publica las versiones del worker.</summary>
    public const string ClavePublica = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA3Aq3F3BQMkznngcJ9imy/7Z1MgOmFZe2EoOiAqLZCYaqO/bxD31kNpCDe1shHcwlAtm2nueGDEaY+N/bWSdj9gQttjBdQtYx/rMSEi4Lm4If9WactbBE3RxgoNYkAbf1f1PNs9/9oz8UWt1rmidGm077en2S2Jk31y8x6rRrpb6J2eTJ0nDJ8ATyThp8ZcpuLeRQ8oX+9IILQaIsXhnppXukCtpa56fHk8dHAfH20InpoOvgoFgvhlrKmeJ1cZDV2G8tEznYl7jgiq3jgTLRoBxGan4XR7RhD3aTAguxnc2ZbAMzlLYwIGYsw1dTRGFI/DEK1y7ILa8VDsyEjl9grVhyczvFkeFcaGqBzRzU4Daiy3sZLgnqVJhXiQQUvTy8uIzMEJm82CSRVlwI7YuEojeKA3WegVxOyr69Nevl/RItynR8WqsE8/paboTjQVR8pW9zhmdLoaCOiPQQpDWWgVqgsJqLYvxrYvQWlpp8dvegI5FIxQUMRkraBOtdndm1AgMBAAE=";

    /// <summary>Texto que se firma: amarra la versión al contenido exacto del paquete.</summary>
    public static string TextoFirmado(string version, string sha256, long tamano) =>
        $"acusa-worker|{version}|{sha256.ToLowerInvariant()}|{tamano}";

    public static bool Verificar(ManifiestoActualizacion manifiesto) => Verificar(manifiesto, ClavePublica);

    public static bool Verificar(ManifiestoActualizacion manifiesto, string clavePublica)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(clavePublica), out _);
            var datos = Encoding.UTF8.GetBytes(TextoFirmado(manifiesto.Version, manifiesto.Sha256, manifiesto.Tamano));
            return rsa.VerifyData(datos, Convert.FromBase64String(manifiesto.Firma), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>SHA-256 en hexadecimal minúsculo.</summary>
    public static string Sha256De(Stream contenido) => Convert.ToHexStringLower(SHA256.HashData(contenido));

    /// <summary>Verdadero si <paramref name="candidata"/> es una versión mayor que <paramref name="actual"/> (formato 1.2.3).</summary>
    public static bool EsMasNueva(string candidata, string actual) =>
        Version.TryParse(candidata, out var nueva) && Version.TryParse(actual, out var instalada) && nueva > instalada;
}
