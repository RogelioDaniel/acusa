using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Acusa.Orquestador;

/// <summary>Quién llama, según el token.</summary>
/// <param name="LlaveId">Llave con la que se pidió el token.</param>
/// <param name="ClienteId">Cliente dueño de la llave.</param>
/// <param name="Rol"><c>api</c> (sistema del cliente) o <c>worker</c>.</param>
public sealed record Identidad(string LlaveId, string ClienteId, string Rol);

public static class Roles
{
    public const string Api = "api";
    public const string Worker = "worker";

    /// <summary>Persona que entró al portal con correo y contraseña.</summary>
    public const string Portal = "portal";
}

/// <summary>Llaves y tokens. Las llaves se guardan solo como hash; el token es un JWT HS256 de corta vida.</summary>
public sealed class Seguridad(string secretoJwt, int duracionTokenSegundos)
{
    private readonly byte[] _secreto = Encoding.UTF8.GetBytes(secretoJwt);

    public int DuracionTokenSegundos { get; } = duracionTokenSegundos;

    public static string NuevaLlave(string rol) =>
        (rol == Roles.Worker ? "wk_" : "ak_") + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string HashLlave(string llave) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(llave))).ToLowerInvariant();

    public static bool IgualesEnTiempoConstante(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public string CrearToken(Identidad identidad, DateTimeOffset ahora)
    {
        var cabecera = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var cuerpo = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = identidad.LlaveId,
            ["cli"] = identidad.ClienteId,
            ["rol"] = identidad.Rol,
            ["iat"] = ahora.ToUnixTimeSeconds(),
            ["exp"] = ahora.AddSeconds(DuracionTokenSegundos).ToUnixTimeSeconds(),
        }));
        return $"{cabecera}.{cuerpo}.{Firmar($"{cabecera}.{cuerpo}")}";
    }

    /// <summary>Devuelve la identidad si la firma es válida y el token no venció; si no, <c>null</c>.</summary>
    public Identidad? ValidarToken(string? token, DateTimeOffset ahora)
    {
        var partes = (token ?? string.Empty).Split('.');
        if (partes.Length != 3 || !IgualesEnTiempoConstante(partes[2], Firmar($"{partes[0]}.{partes[1]}")))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(DesdeBase64Url(partes[1]));
            var raiz = doc.RootElement;
            if (raiz.GetProperty("exp").GetInt64() <= ahora.ToUnixTimeSeconds())
            {
                return null;
            }

            return new Identidad(
                raiz.GetProperty("sub").GetString()!,
                raiz.GetProperty("cli").GetString()!,
                raiz.GetProperty("rol").GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private string Firmar(string datos) => Base64Url(HMACSHA256.HashData(_secreto, Encoding.UTF8.GetBytes(datos)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DesdeBase64Url(string texto)
    {
        var s = texto.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>Contraseñas de los usuarios del portal: PBKDF2-SHA256 con sal propia, formato <c>pbkdf2$iteraciones$sal$hash</c>.</summary>
public static class Contrasenas
{
    private const int Iteraciones = 210_000;

    public static string Crear(string contrasena)
    {
        var sal = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(contrasena), sal, Iteraciones, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iteraciones}${Convert.ToBase64String(sal)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verificar(string contrasena, string guardada)
    {
        var partes = guardada.Split('$');
        if (partes.Length != 4 || partes[0] != "pbkdf2" || !int.TryParse(partes[1], out var iteraciones))
        {
            return false;
        }

        var esperado = Convert.FromBase64String(partes[3]);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(contrasena), Convert.FromBase64String(partes[2]), iteraciones, HashAlgorithmName.SHA256, esperado.Length);
        return CryptographicOperations.FixedTimeEquals(hash, esperado);
    }

    /// <summary>Hash falso para gastar el mismo tiempo cuando el correo no existe y no revelar qué cuentas hay.</summary>
    public static readonly string Señuelo = Crear("señuelo-sin-uso");
}
