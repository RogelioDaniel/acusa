using Acusa.Sdk.Application;

namespace Acusa.Sdk.Tests;

public class OpcionesContpaqiTests
{
    [Fact]
    public void Por_defecto_el_inicio_es_con_dialogo_y_es_valido()
    {
        var o = new OpcionesContpaqi();

        Assert.Equal("Dialogo", o.Inicio);
        Assert.False(o.UsaCredenciales);
        Assert.False(o.IniciarSesionContabilidad);
        Assert.Null(o.ValidarInicio());
    }

    [Fact]
    public void El_inicio_por_credenciales_exige_usuario()
    {
        var sinUsuario = new OpcionesContpaqi { Inicio = "credenciales" };
        var conUsuario = new OpcionesContpaqi { Inicio = "Credenciales", Usuario = "COMERCIAL7" };

        Assert.True(sinUsuario.UsaCredenciales);
        Assert.Contains("Contpaqi:Usuario", sinUsuario.ValidarInicio());
        Assert.Null(conUsuario.ValidarInicio());
    }

    [Fact]
    public void Un_modo_de_inicio_desconocido_se_rechaza()
    {
        var o = new OpcionesContpaqi { Inicio = "Magia" };

        Assert.Contains("Magia", o.ValidarInicio());
    }

    [Fact]
    public void La_configuracion_no_tiene_ningun_campo_para_la_clave()
    {
        var propiedades = typeof(OpcionesContpaqi).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain(propiedades, n => n.Contains("clave", StringComparison.OrdinalIgnoreCase)
            || n.Contains("password", StringComparison.OrdinalIgnoreCase)
            || n.Contains("contrase", StringComparison.OrdinalIgnoreCase));
    }
}
