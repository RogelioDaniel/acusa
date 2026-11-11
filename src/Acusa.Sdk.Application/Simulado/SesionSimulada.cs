using Acusa.Sdk.Application.Sesion;

namespace Acusa.Sdk.Application.Simulado;

/// <summary>Sesión de mentira para desarrollar y probar sin CONTPAQi. Nunca toca el SDK.</summary>
public sealed class SesionSimulada(string empresa) : ISesionSdk
{
    private volatile string _empresa = empresa;
    private volatile EstadoSesion _estado = EstadoSesion.NoIniciada;

    public EstadoSdk Estado => new("Simulado", _estado, _empresa, "Datos en memoria; no hay conexión con CONTPAQi.");

    public void Iniciar() => _estado = EstadoSesion.Lista;

    public Task CerrarAsync()
    {
        _estado = EstadoSesion.NoIniciada;
        return Task.CompletedTask;
    }

    public Task<string?> CambiarEmpresaAsync(string rutaEmpresa)
    {
        _empresa = rutaEmpresa;
        return Task.FromResult<string?>(null);
    }
}
