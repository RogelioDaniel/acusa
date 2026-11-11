using Acusa.Contrato;
using Acusa.Orquestador;

namespace Acusa.Sdk.Tests;

public sealed class SeguridadTests
{
    private static readonly Seguridad Seg = new("secreto-de-prueba-con-mas-de-32-caracteres!!", 3600);
    private static readonly Identidad Quien = new("llave1", "cli1", Roles.Api);

    [Fact]
    public void Token_valido_devuelve_la_identidad()
    {
        var ahora = DateTimeOffset.UtcNow;
        Assert.Equal(Quien, Seg.ValidarToken(Seg.CrearToken(Quien, ahora), ahora));
    }

    [Fact]
    public void Token_vencido_se_rechaza()
    {
        var token = Seg.CrearToken(Quien, DateTimeOffset.UtcNow.AddHours(-2));
        Assert.Null(Seg.ValidarToken(token, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Token_alterado_se_rechaza()
    {
        var ahora = DateTimeOffset.UtcNow;
        var partes = Seg.CrearToken(Quien, ahora).Split('.');
        var otro = Seg.CrearToken(Quien with { Rol = Roles.Worker }, ahora).Split('.');
        Assert.Null(Seg.ValidarToken($"{partes[0]}.{otro[1]}.{partes[2]}", ahora));
    }

    [Fact]
    public void Token_firmado_con_otro_secreto_se_rechaza()
    {
        var ajeno = new Seguridad("otro-secreto-distinto-con-mas-de-32-caracteres", 3600);
        var ahora = DateTimeOffset.UtcNow;
        Assert.Null(Seg.ValidarToken(ajeno.CrearToken(Quien, ahora), ahora));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("basura")]
    [InlineData("a.b.c")]
    public void Texto_que_no_es_token_se_rechaza(string? texto) =>
        Assert.Null(Seg.ValidarToken(texto, DateTimeOffset.UtcNow));

    [Fact]
    public void Las_llaves_se_distinguen_por_rol_y_no_se_repiten()
    {
        Assert.StartsWith("wk_", Seguridad.NuevaLlave(Roles.Worker));
        Assert.StartsWith("ak_", Seguridad.NuevaLlave(Roles.Api));
        Assert.NotEqual(Seguridad.NuevaLlave(Roles.Api), Seguridad.NuevaLlave(Roles.Api));
    }
}

public sealed class AlmacenTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"orq-{Guid.NewGuid():N}.db");
    private readonly Almacen _almacen;

    public AlmacenTests() => _almacen = new Almacen(_ruta);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_ruta)!, Path.GetFileName(_ruta) + "*"))
        {
            File.Delete(f);
        }
    }

    private string ClienteConEmpresa(string empresa = "adEMPRESA")
    {
        var c = _almacen.CrearCliente("Cliente");
        _almacen.AgregarEmpresa(c, empresa);
        return c;
    }

    [Fact]
    public void La_llave_se_guarda_solo_como_hash_y_se_puede_revocar_con_el_cliente()
    {
        var c = ClienteConEmpresa();
        var llave = _almacen.CrearLlave(c, Roles.Api);
        var identidad = _almacen.BuscarLlave(llave);
        Assert.NotNull(identidad);
        Assert.Equal(c, identidad.ClienteId);
        Assert.Null(_almacen.BuscarLlave(llave + "x"));
    }

    [Fact]
    public void Una_tarea_solo_se_entrega_una_vez_y_en_orden()
    {
        var c = ClienteConEmpresa();
        var primera = _almacen.EncolarTarea(c, "adEMPRESA", "cliente.leer", "{}");
        var segunda = _almacen.EncolarTarea(c, "adEMPRESA", "cliente.leer", "{}");

        Assert.Equal(primera, _almacen.TomarTarea(c, "adEMPRESA", "w1")!.Id);
        Assert.Equal(segunda, _almacen.TomarTarea(c, "adEMPRESA", "w2")!.Id);
        Assert.Null(_almacen.TomarTarea(c, "adEMPRESA", "w1"));
    }

    [Fact]
    public void Un_worker_solo_recibe_tareas_de_su_empresa_y_su_cliente()
    {
        var a = ClienteConEmpresa("adUNO");
        var b = ClienteConEmpresa("adUNO");
        _almacen.AgregarEmpresa(a, "adDOS");
        var tarea = _almacen.EncolarTarea(a, "adDOS", "cliente.leer", "{}");

        Assert.Null(_almacen.TomarTarea(a, "adUNO", "w"));
        Assert.Null(_almacen.TomarTarea(b, "adDOS", "w"));
        Assert.Equal(tarea, _almacen.TomarTarea(a, "adDOS", "w")!.Id);
    }

    [Fact]
    public void Un_cliente_no_ve_las_tareas_de_otro()
    {
        var a = ClienteConEmpresa();
        var b = ClienteConEmpresa();
        var id = _almacen.EncolarTarea(a, "adEMPRESA", "cliente.leer", "{}");
        Assert.NotNull(_almacen.ObtenerTarea(a, id));
        Assert.Null(_almacen.ObtenerTarea(b, id));
        Assert.Empty(_almacen.ListarTareas(b, null, 50));
    }

    [Fact]
    public void Solo_el_worker_que_tiene_la_tarea_puede_informar_el_resultado()
    {
        var c = ClienteConEmpresa();
        var id = _almacen.EncolarTarea(c, "adEMPRESA", "cliente.leer", "{}");
        _almacen.TomarTarea(c, "adEMPRESA", "w1");

        var ok = new ResultadoTarea(true, null, null, null);
        Assert.False(_almacen.RegistrarResultado(c, id, "w2", ok));
        Assert.True(_almacen.RegistrarResultado(c, id, "w1", ok));
        Assert.False(_almacen.RegistrarResultado(c, id, "w1", ok));
        Assert.Equal(EstadoTarea.Completada, _almacen.ObtenerTarea(c, id)!.Estado);
    }

    [Fact]
    public void Una_lectura_colgada_vuelve_a_la_cola_y_una_escritura_se_marca_incierta()
    {
        var c = ClienteConEmpresa();
        var lectura = _almacen.EncolarTarea(c, "adEMPRESA", "cliente.leer", "{}");
        var escritura = _almacen.EncolarTarea(c, "adEMPRESA", "cliente.crear", "{}");
        _almacen.TomarTarea(c, "adEMPRESA", "w1");
        _almacen.TomarTarea(c, "adEMPRESA", "w1");

        _almacen.RecuperarColgadas(TimeSpan.FromSeconds(-1), maxIntentos: 3);

        Assert.Equal(EstadoTarea.Pendiente, _almacen.ObtenerTarea(c, lectura)!.Estado);
        var incierta = _almacen.ObtenerTarea(c, escritura)!;
        Assert.Equal(EstadoTarea.Fallida, incierta.Estado);
        Assert.Contains("incierto", incierta.Error);
    }
}

public sealed class ContrasenasTests
{
    [Fact]
    public void Una_contrasena_correcta_se_verifica_y_una_incorrecta_no()
    {
        var guardada = Contrasenas.Crear("Una-clave-larga-1");
        Assert.True(Contrasenas.Verificar("Una-clave-larga-1", guardada));
        Assert.False(Contrasenas.Verificar("Una-clave-larga-2", guardada));
    }

    [Fact]
    public void Dos_hashes_de_la_misma_contrasena_son_distintos_por_la_sal()
    {
        Assert.NotEqual(Contrasenas.Crear("misma-clave-123"), Contrasenas.Crear("misma-clave-123"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("texto-sin-formato")]
    [InlineData("pbkdf2$abc$def$ghi")]
    public void Un_hash_mal_formado_nunca_valida(string guardada) =>
        Assert.False(Contrasenas.Verificar("cualquiera", guardada));
}

public sealed class PortalAlmacenTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"orq-{Guid.NewGuid():N}.db");
    private readonly Almacen _almacen;

    public PortalAlmacenTests() => _almacen = new Almacen(_ruta);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_ruta)!, Path.GetFileName(_ruta) + "*"))
        {
            File.Delete(f);
        }
    }

    [Fact]
    public void Un_usuario_se_busca_por_correo_sin_importar_mayusculas()
    {
        var c = _almacen.CrearCliente("Cliente");
        _almacen.CrearUsuario(c, "Ana@Empresa.com", "Ana", Contrasenas.Crear("clave-larga-123"));
        var u = _almacen.BuscarUsuarioPorEmail("ana@empresa.com");
        Assert.NotNull(u);
        Assert.Equal("Ana", u.Nombre);
        Assert.Equal("Cliente", u.ClienteNombre);
        Assert.Null(_almacen.BuscarUsuarioPorEmail("otra@empresa.com"));
    }

    [Fact]
    public void El_listado_de_llaves_no_expone_el_valor_y_revocar_corta_el_acceso()
    {
        var c = _almacen.CrearCliente("Cliente");
        var llave = _almacen.CrearLlave(c, Roles.Api, "ERP");
        var listada = Assert.Single(_almacen.ListarLlaves(c));
        Assert.Equal("ERP", listada.Nombre);
        Assert.Equal(llave[..7], listada.Prefijo);
        Assert.DoesNotContain(llave, System.Text.Json.JsonSerializer.Serialize(listada));

        Assert.NotNull(_almacen.BuscarLlave(llave));
        Assert.True(_almacen.RevocarLlave(c, listada.Id));
        Assert.Null(_almacen.BuscarLlave(llave));
        Assert.False(_almacen.LlaveVigente(listada.Id));
    }

    [Fact]
    public void Un_cliente_no_puede_revocar_la_llave_de_otro()
    {
        var a = _almacen.CrearCliente("A");
        var b = _almacen.CrearCliente("B");
        _almacen.CrearLlave(a, Roles.Api);
        var id = _almacen.ListarLlaves(a)[0].Id;
        Assert.False(_almacen.RevocarLlave(b, id));
    }

    [Fact]
    public void El_poll_registra_version_y_equipo_del_worker()
    {
        var c = _almacen.CrearCliente("Cliente");
        _almacen.AgregarEmpresa(c, "adEMPRESA");
        _almacen.TomarTarea(c, "adEMPRESA", "w1", "0.1.0", "SERVIDOR1");
        var w = Assert.Single(_almacen.ListarWorkers(c, TimeSpan.FromMinutes(1)));
        Assert.Equal("0.1.0", w.Version);
        Assert.Equal("SERVIDOR1", w.Host);
        Assert.True(w.EnLinea);
    }

    [Fact]
    public void El_resumen_cuenta_tareas_workers_empresas_y_llaves()
    {
        var c = _almacen.CrearCliente("Cliente");
        _almacen.AgregarEmpresa(c, "adEMPRESA");
        _almacen.CrearLlave(c, Roles.Worker);
        var t1 = _almacen.EncolarTarea(c, "adEMPRESA", "cliente.leer", "{}");
        _almacen.EncolarTarea(c, "adEMPRESA", "cliente.leer", "{}");
        _almacen.TomarTarea(c, "adEMPRESA", "w1");
        _almacen.RegistrarResultado(c, t1, "w1", new ResultadoTarea(true, null, null, null));

        var r = _almacen.Resumen(c, TimeSpan.FromMinutes(1));
        Assert.Equal(1, r.Completadas);
        Assert.Equal(1, r.Pendientes);
        Assert.Equal(1, r.WorkersEnLinea);
        Assert.Equal(1, r.Empresas);
        Assert.Equal(1, r.LlavesActivas);
        Assert.NotNull(r.UltimaTarea);
    }
}

public sealed class CatalogoTests
{
    private static readonly string[] Esperadas =
    [
        "cliente.crear", "cliente.leer", "cliente.actualizar", "producto.crear", "producto.leer",
        "documento.crear", "documento.emitir", "documento.obtener-archivo", "documento.cancelar",
    ];

    [Fact]
    public void La_api_de_v1_expone_exactamente_nueve_operaciones()
    {
        Assert.Equal(Esperadas.Order(), Operaciones.Soportadas.Order());
        Assert.Equal(Esperadas.Order(), CatalogoOperaciones.Todas.Select(o => o.TipoProceso).Order());
    }

    [Fact]
    public void Toda_operacion_expuesta_la_implementa_el_worker()
    {
        foreach (var tipo in Operaciones.Soportadas)
        {
            Assert.Contains(tipo, Operaciones.ImplementadasPorElWorker);
        }
    }

    [Fact]
    public void Los_nombres_del_catalogo_no_se_repiten_y_cada_uno_trae_ejemplo_valido()
    {
        Assert.Equal(CatalogoOperaciones.Todas.Count, CatalogoOperaciones.Todas.Select(o => o.TipoProceso).Distinct().Count());
        foreach (var o in CatalogoOperaciones.Todas)
        {
            using var _ = System.Text.Json.JsonDocument.Parse(o.Ejemplo);
        }
    }

    [Theory]
    [InlineData("producto.actualizar")]
    [InlineData("direccion.crear")]
    [InlineData("documento.leer")]
    [InlineData("documento.emitir.lote")]
    [InlineData("cliente.eliminar")]
    [InlineData("documento.borrar")]
    public void Las_operaciones_fuera_de_v1_no_estan_en_el_catalogo(string tipo)
    {
        Assert.Null(CatalogoOperaciones.Buscar(tipo));
        Assert.DoesNotContain(tipo, Operaciones.Soportadas);
    }
}
