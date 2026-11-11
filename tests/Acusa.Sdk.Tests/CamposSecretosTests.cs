using System.Text.Json;
using Acusa.Contrato;

namespace Acusa.Sdk.Tests;

public class CamposSecretosTests
{
    private static JsonElement Json(string texto) => JsonDocument.Parse(texto).RootElement.Clone();

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("passwordCertificado")]
    [InlineData("contrasena")]
    [InlineData("contraseña")]
    [InlineData("clave")]
    [InlineData("claveCertificado")]
    [InlineData("claveSdk")]
    [InlineData("claveAcceso")]
    [InlineData("secreto_api")]
    [InlineData("pwd")]
    public void Estos_campos_se_toman_por_contrasena(string nombre)
    {
        Assert.True(CamposSecretos.EsSecreto(nombre));
        Assert.Equal(nombre, CamposSecretos.Buscar(Json($$"""{"{{nombre}}":"x"}""")));
    }

    [Theory]
    [InlineData("claveSat")]
    [InlineData("claveSAT")]
    [InlineData("claveProdServ")]
    [InlineData("claveUnidad")]
    [InlineData("codigo")]
    [InlineData("razonSocial")]
    [InlineData("regimenFiscal")]
    [InlineData("uuidSustituto")]
    [InlineData("documentoId")]
    public void Los_datos_fiscales_y_de_negocio_no_se_bloquean(string nombre)
    {
        Assert.False(CamposSecretos.EsSecreto(nombre));
        Assert.Null(CamposSecretos.Buscar(Json($$"""{"{{nombre}}":"x"}""")));
    }

    [Fact]
    public void Un_payload_que_no_es_objeto_no_tiene_campos_secretos()
    {
        Assert.Null(CamposSecretos.Buscar(Json("[1,2]")));
        Assert.Null(CamposSecretos.Buscar(Json("\"texto\"")));
    }

    [Fact]
    public void El_ejemplo_de_cada_operacion_del_catalogo_pasa_el_filtro()
    {
        foreach (var o in CatalogoOperaciones.Todas)
        {
            Assert.Null(CamposSecretos.Buscar(Json(o.Ejemplo!)));
        }
    }
}
