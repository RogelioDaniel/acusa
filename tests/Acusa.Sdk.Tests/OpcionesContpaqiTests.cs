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
    public void Con_la_escritura_apagada_no_se_exige_lista_de_empresas()
    {
        var o = new OpcionesContpaqi { PermitirEscritura = false, RutaEmpresa = @"C:\Compac\Empresas\adCualquiera" };

        Assert.Null(o.ValidarEscritura());
    }

    [Fact]
    public void Con_la_escritura_activa_la_empresa_debe_estar_en_la_lista()
    {
        var o = new OpcionesContpaqi
        {
            PermitirEscritura = true,
            RutaEmpresa = @"C:\Compac\Empresas\adLOCALSOLUTIONIT",
            EmpresasEscritura = ["adEMPRESA_DE_PRUEBA"],
        };

        Assert.Contains("adLOCALSOLUTIONIT", o.ValidarEscritura());
    }

    [Theory]
    [InlineData(@"C:\Compac\Empresas\adEMPRESA_DE_PRUEBA")]
    [InlineData(@"C:\Compac\Empresas\adempresa_de_prueba\")]
    public void Una_empresa_de_la_lista_admite_escritura_sin_importar_mayusculas_ni_barra_final(string ruta)
    {
        var o = new OpcionesContpaqi
        {
            PermitirEscritura = true,
            RutaEmpresa = ruta,
            EmpresasEscritura = ["adEMPRESA_DE_PRUEBA"],
        };

        Assert.Null(o.ValidarEscritura());
    }

    [Fact]
    public void Sin_lista_ni_empresa_la_escritura_activa_se_rechaza()
    {
        var o = new OpcionesContpaqi { PermitirEscritura = true, RutaEmpresa = string.Empty };

        Assert.NotNull(o.ValidarEscritura());
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
