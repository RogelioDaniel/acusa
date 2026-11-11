using System.Diagnostics;
using Acusa.Contrato;
using Acusa.Orquestador;

namespace Acusa.Sdk.Tests;

/// <summary>La petición puede esperar el resultado y recibirlo en la misma respuesta, sin consultar el estado.</summary>
public sealed class EsperaResultadoTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"orq-{Guid.NewGuid():N}.db");
    private readonly Almacen _almacen;
    private readonly Notificador _notificador = new();

    public EsperaResultadoTests() => _almacen = new Almacen(_ruta);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_ruta)!, Path.GetFileName(_ruta) + "*"))
        {
            File.Delete(f);
        }
    }

    private static ResultadoTarea Ok() => new(true, null, null, null);

    [Fact]
    public async Task Una_tarea_ya_terminada_se_devuelve_de_inmediato()
    {
        var c = _almacen.CrearCliente("Cliente");
        var id = _almacen.EncolarTarea(c, "adEMPRESA", "cliente.leer", "{}");
        _almacen.TomarTarea(c, "adEMPRESA", "w1");
        _almacen.RegistrarResultado(c, id, "w1", Ok());

        var reloj = Stopwatch.StartNew();
        var tarea = await _notificador.EsperarResultadoAsync(_almacen, c, id, TimeSpan.FromSeconds(30), default);

        Assert.Equal(EstadoTarea.Completada, tarea!.Estado);
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Espera_y_recibe_el_resultado_apenas_el_worker_lo_informa()
    {
        var c = _almacen.CrearCliente("Cliente");
        var id = _almacen.EncolarTarea(c, "adEMPRESA", "documento.crear", "{}");
        _almacen.TomarTarea(c, "adEMPRESA", "w1");

        var espera = _notificador.EsperarResultadoAsync(_almacen, c, id, TimeSpan.FromSeconds(30), default);
        await Task.Delay(300);
        Assert.False(espera.IsCompleted);

        var reloj = Stopwatch.StartNew();
        _almacen.RegistrarResultado(c, id, "w1", new ResultadoTarea(true, System.Text.Json.JsonSerializer.SerializeToElement(new { folio = 7 }), null, null));
        _notificador.TareaTerminada(id);
        var tarea = await espera;

        Assert.Equal(EstadoTarea.Completada, tarea!.Estado);
        Assert.Equal(7, tarea.Resultado!.Value.GetProperty("folio").GetInt32());
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(1), "El aviso debe despertar la espera sin esperar al siguiente sondeo.");
    }

    [Fact]
    public async Task Una_tarea_fallida_tambien_termina_la_espera()
    {
        var c = _almacen.CrearCliente("Cliente");
        var id = _almacen.EncolarTarea(c, "adEMPRESA", "documento.crear", "{}");
        _almacen.TomarTarea(c, "adEMPRESA", "w1");

        var espera = _notificador.EsperarResultadoAsync(_almacen, c, id, TimeSpan.FromSeconds(30), default);
        _almacen.RegistrarResultado(c, id, "w1", new ResultadoTarea(false, null, "No existe el concepto.", 120146));
        _notificador.TareaTerminada(id);
        var tarea = await espera;

        Assert.Equal(EstadoTarea.Fallida, tarea!.Estado);
        Assert.Equal(120146, tarea.CodigoSdk);
    }

    [Fact]
    public async Task Si_pasa_el_tiempo_devuelve_la_tarea_pendiente_para_responder_202()
    {
        var c = _almacen.CrearCliente("Cliente");
        var id = _almacen.EncolarTarea(c, "adEMPRESA", "documento.crear", "{}");

        var tarea = await _notificador.EsperarResultadoAsync(_almacen, c, id, TimeSpan.FromMilliseconds(400), default);

        Assert.Equal(EstadoTarea.Pendiente, tarea!.Estado);
    }

    [Fact]
    public async Task Una_tarea_que_no_existe_devuelve_nulo()
    {
        var c = _almacen.CrearCliente("Cliente");

        Assert.Null(await _notificador.EsperarResultadoAsync(_almacen, c, "no-existe", TimeSpan.FromSeconds(5), default));
    }

    [Fact]
    public async Task Un_cliente_no_ve_la_tarea_de_otro()
    {
        var a = _almacen.CrearCliente("A");
        var b = _almacen.CrearCliente("B");
        var id = _almacen.EncolarTarea(a, "adEMPRESA", "cliente.leer", "{}");

        Assert.Null(await _notificador.EsperarResultadoAsync(_almacen, b, id, TimeSpan.FromSeconds(5), default));
    }

    [Fact]
    public async Task El_aviso_de_orden_nueva_despierta_al_worker_que_espera()
    {
        var espera = _notificador.SiguienteEncolada();
        Assert.False(espera.IsCompleted);

        _notificador.TareaEncolada();

        await espera.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(_notificador.SiguienteEncolada().IsCompleted, "Cada aviso es de un solo uso: la siguiente espera es nueva.");
    }

    [Fact]
    public async Task Una_espera_cancelada_no_deja_avisos_pendientes()
    {
        var c = _almacen.CrearCliente("Cliente");
        var id = _almacen.EncolarTarea(c, "adEMPRESA", "documento.crear", "{}");
        using var cancelar = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _notificador.EsperarResultadoAsync(_almacen, c, id, TimeSpan.FromSeconds(30), cancelar.Token));

        // La tarea sigue en la cola: cancelar la espera no la cancela.
        Assert.Equal(EstadoTarea.Pendiente, _almacen.ObtenerTarea(c, id)!.Estado);
    }
}
