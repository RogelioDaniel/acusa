using Acusa.Sdk.Application.Sesion;
using Acusa.Sdk.Application.Simulado;

namespace Acusa.Sdk.Tests;

public class SesionSimuladaTests
{
    [Fact]
    public async Task La_sesion_pasa_de_no_iniciada_a_lista_y_vuelve_al_cerrar()
    {
        var sesion = new SesionSimulada("EMPRESA DE PRUEBA");
        Assert.Equal(EstadoSesion.NoIniciada, sesion.Estado.Estado);

        sesion.Iniciar();
        Assert.Equal(EstadoSesion.Lista, sesion.Estado.Estado);
        Assert.Equal("Simulado", sesion.Estado.Modo);

        await sesion.CerrarAsync();
        Assert.Equal(EstadoSesion.NoIniciada, sesion.Estado.Estado);
    }
}
