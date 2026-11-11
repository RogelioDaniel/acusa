namespace Acusa.Sdk.Application.Sesion;

public enum EstadoSesion
{
    NoIniciada,

    /// <summary>Esperando la clave en el diálogo "Ingreso a CONTPAQi COMERCIAL".</summary>
    Iniciando,
    Lista,
    Error,
}

/// <summary>Estado de la sesión con el SDK, para mostrarlo en la API.</summary>
public sealed record EstadoSdk(string Modo, EstadoSesion Estado, string? Empresa, string? Mensaje);

/// <summary>Se lanza cuando se pide una operación y la sesión del SDK no está lista.</summary>
public sealed class SdkNoListoException(EstadoSesion estado)
    : InvalidOperationException($"La sesión del SDK no está lista (estado: {estado}). Inicie la sesión con POST /sdk/iniciar.")
{
    public EstadoSesion Estado { get; } = estado;
}

/// <summary>
/// Sesión única con el SDK. CONTPAQi permite una empresa por proceso y cada inicio pide la clave
/// de usuario en un diálogo, por eso la sesión se abre una vez y se reutiliza.
/// </summary>
public interface ISesionSdk
{
    EstadoSdk Estado { get; }

    /// <summary>Inicia el SDK y abre la empresa. Vuelve de inmediato; el avance se consulta en <see cref="Estado"/>.</summary>
    void Iniciar();

    /// <summary>Cierra la empresa y termina el SDK.</summary>
    Task CerrarAsync();

    /// <summary>
    /// Cambia a otra empresa sin volver a iniciar sesión: cierra la abierta y abre <paramref name="rutaEmpresa"/>.
    /// Devuelve el motivo del fallo, o <c>null</c> si quedó abierta.
    /// </summary>
    Task<string?> CambiarEmpresaAsync(string rutaEmpresa);
}
