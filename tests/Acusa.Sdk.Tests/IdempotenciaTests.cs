using Acusa.Contrato;
using Acusa.Orquestador;

namespace Acusa.Sdk.Tests;

/// <summary>Una orden repetida con la misma llave no crea otra tarea: no se duplica una factura.</summary>
public sealed class IdempotenciaTests : IDisposable
{
    private const string Payload = """{"concepto":"FAC","clienteCodigo":"CLI1","partidas":[{"productoCodigo":"P1","unidades":1,"precio":100}]}""";

    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"orq-{Guid.NewGuid():N}.db");
    private readonly Almacen _almacen;

    public IdempotenciaTests() => _almacen = new Almacen(_ruta);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_ruta)!, Path.GetFileName(_ruta) + "*"))
        {
            File.Delete(f);
        }
    }

    [Fact]
    public void La_primera_vez_encola_y_la_repeticion_devuelve_la_misma_tarea()
    {
        var c = _almacen.CrearCliente("Cliente");

        var primera = _almacen.EncolarIdempotente(c, "adEMPRESA", "documento.crear", Payload, "orden-1");
        var segunda = _almacen.EncolarIdempotente(c, "adEMPRESA", "documento.crear", Payload, "orden-1");

        Assert.False(primera.Repetida);
        Assert.True(segunda.Repetida);
        Assert.False(segunda.Conflicto);
        Assert.Equal(primera.Id, segunda.Id);
        Assert.Single(_almacen.ListarTareas(c, null, 50));
    }

    [Fact]
    public void Los_espacios_del_json_no_cuentan_como_una_orden_distinta()
    {
        var c = _almacen.CrearCliente("Cliente");
        var compacto = """{"a":1,"b":"x"}""";
        var conEspacios = "{ \"a\": 1,\n  \"b\": \"x\" }";

        _almacen.EncolarIdempotente(c, "adEMPRESA", "cliente.crear", compacto, "k1");
        var repetida = _almacen.EncolarIdempotente(c, "adEMPRESA", "cliente.crear", conEspacios, "k1");

        Assert.True(repetida.Repetida);
        Assert.False(repetida.Conflicto);
    }

    [Fact]
    public void La_misma_llave_con_otra_orden_es_un_conflicto_y_no_encola_nada()
    {
        var c = _almacen.CrearCliente("Cliente");
        var primera = _almacen.EncolarIdempotente(c, "adEMPRESA", "documento.crear", Payload, "orden-2");

        var otra = _almacen.EncolarIdempotente(c, "adEMPRESA", "documento.crear", Payload.Replace("100", "999"), "orden-2");

        Assert.True(otra.Conflicto);
        Assert.Equal(primera.Id, otra.Id);
        Assert.Single(_almacen.ListarTareas(c, null, 50));
    }

    [Fact]
    public void La_misma_llave_en_otra_empresa_es_un_conflicto()
    {
        var c = _almacen.CrearCliente("Cliente");
        _almacen.EncolarIdempotente(c, "adUNO", "documento.crear", Payload, "orden-3");

        var otra = _almacen.EncolarIdempotente(c, "adDOS", "documento.crear", Payload, "orden-3");

        Assert.True(otra.Conflicto);
    }

    [Fact]
    public void Dos_clientes_pueden_usar_la_misma_llave()
    {
        var a = _almacen.CrearCliente("A");
        var b = _almacen.CrearCliente("B");

        var deA = _almacen.EncolarIdempotente(a, "adEMPRESA", "documento.crear", Payload, "orden-4");
        var deB = _almacen.EncolarIdempotente(b, "adEMPRESA", "documento.crear", Payload, "orden-4");

        Assert.NotEqual(deA.Id, deB.Id);
        Assert.False(deB.Repetida);
    }

    [Fact]
    public void La_repeticion_informa_el_estado_actual_de_la_tarea()
    {
        var c = _almacen.CrearCliente("Cliente");
        var primera = _almacen.EncolarIdempotente(c, "adEMPRESA", "documento.crear", Payload, "orden-5");
        var tarea = _almacen.TomarTarea(c, "adEMPRESA", "w1");
        _almacen.RegistrarResultado(c, tarea!.Id, "w1", new ResultadoTarea(true, null, null, null));

        var repetida = _almacen.EncolarIdempotente(c, "adEMPRESA", "documento.crear", Payload, "orden-5");

        Assert.Equal(primera.Id, repetida.Id);
        Assert.Equal(EstadoTarea.Completada, repetida.Estado);
    }

    [Fact]
    public void Solicitudes_simultaneas_con_la_misma_llave_crean_una_sola_tarea()
    {
        var c = _almacen.CrearCliente("Cliente");

        var resultados = Enumerable.Range(0, 12).AsParallel().WithDegreeOfParallelism(12)
            .Select(_ => _almacen.EncolarIdempotente(c, "adEMPRESA", "documento.crear", Payload, "orden-6")).ToList();

        Assert.Single(resultados.Select(r => r.Id).Distinct());
        Assert.Equal(1, resultados.Count(r => !r.Repetida));
        Assert.Single(_almacen.ListarTareas(c, null, 50));
    }

    [Fact]
    public void Sin_llave_cada_orden_sigue_creando_su_tarea()
    {
        var c = _almacen.CrearCliente("Cliente");

        _almacen.EncolarTarea(c, "adEMPRESA", "documento.crear", Payload);
        _almacen.EncolarTarea(c, "adEMPRESA", "documento.crear", Payload);

        Assert.Equal(2, _almacen.ListarTareas(c, null, 50).Count);
    }
}
