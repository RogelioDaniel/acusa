using System.Globalization;
using System.Text.Json;
using Acusa.Contrato;
using Microsoft.Data.Sqlite;

namespace Acusa.Orquestador;

/// <summary>Persistencia del orquestador en SQLite: clientes, empresas, llaves, workers y la cola de tareas.</summary>
public sealed class Almacen
{
    private readonly string _cadena;

    public Almacen(string rutaBaseDatos)
    {
        var carpeta = Path.GetDirectoryName(Path.GetFullPath(rutaBaseDatos));
        if (!string.IsNullOrEmpty(carpeta))
        {
            Directory.CreateDirectory(carpeta);
        }

        _cadena = new SqliteConnectionStringBuilder { DataSource = rutaBaseDatos, Pooling = true }.ToString();
        Ejecutar("PRAGMA journal_mode=WAL;");
        Ejecutar("""
            CREATE TABLE IF NOT EXISTS clientes (id TEXT PRIMARY KEY, nombre TEXT NOT NULL, activo INTEGER NOT NULL DEFAULT 1, creado TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS empresas (cliente_id TEXT NOT NULL, nombre TEXT NOT NULL, PRIMARY KEY (cliente_id, nombre));
            CREATE TABLE IF NOT EXISTS llaves (id TEXT PRIMARY KEY, cliente_id TEXT NOT NULL, rol TEXT NOT NULL, hash TEXT NOT NULL UNIQUE, activa INTEGER NOT NULL DEFAULT 1, creada TEXT NOT NULL, nombre TEXT, prefijo TEXT, ultimo_uso TEXT);
            CREATE TABLE IF NOT EXISTS usuarios (id TEXT PRIMARY KEY, cliente_id TEXT NOT NULL, email TEXT NOT NULL UNIQUE COLLATE NOCASE, nombre TEXT NOT NULL, clave TEXT NOT NULL, activo INTEGER NOT NULL DEFAULT 1, creado TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS workers (id TEXT NOT NULL, cliente_id TEXT NOT NULL, empresa TEXT NOT NULL, ultima_vista TEXT NOT NULL, version TEXT, host TEXT, PRIMARY KEY (cliente_id, id));
            CREATE TABLE IF NOT EXISTS tareas (
                id TEXT PRIMARY KEY, cliente_id TEXT NOT NULL, empresa TEXT NOT NULL, tipo TEXT NOT NULL, payload TEXT NOT NULL,
                estado INTEGER NOT NULL, intentos INTEGER NOT NULL DEFAULT 0, creada TEXT NOT NULL, iniciada TEXT, terminada TEXT,
                worker_id TEXT, resultado TEXT, error TEXT, codigo_sdk INTEGER);
            CREATE INDEX IF NOT EXISTS ix_tareas_cola ON tareas (estado, cliente_id, empresa, creada);
            """);

        // Bases creadas antes de estas columnas: se agregan si faltan.
        foreach (var alter in new[]
        {
            "ALTER TABLE llaves ADD COLUMN nombre TEXT", "ALTER TABLE llaves ADD COLUMN prefijo TEXT", "ALTER TABLE llaves ADD COLUMN ultimo_uso TEXT",
            "ALTER TABLE workers ADD COLUMN version TEXT", "ALTER TABLE workers ADD COLUMN host TEXT",
            "ALTER TABLE tareas ADD COLUMN llave TEXT", "ALTER TABLE tareas ADD COLUMN huella TEXT",
        })
        {
            try
            {
                Ejecutar(alter);
            }
            catch (SqliteException)
            {
                // La columna ya existe.
            }
        }

        // Una llave de idempotencia identifica una orden de un cliente: la base misma impide registrarla dos veces.
        Ejecutar("CREATE UNIQUE INDEX IF NOT EXISTS ux_tareas_llave ON tareas (cliente_id, llave) WHERE llave IS NOT NULL");
    }

    // ---- Administración -------------------------------------------------------------------

    public string CrearCliente(string nombre)
    {
        var id = Guid.NewGuid().ToString("N");
        Ejecutar("INSERT INTO clientes (id, nombre, creado) VALUES ($id, $n, $t)", ("$id", id), ("$n", nombre), ("$t", Ahora()));
        return id;
    }

    public bool ClienteExiste(string clienteId) =>
        Escalar("SELECT COUNT(*) FROM clientes WHERE id=$id AND activo=1", ("$id", clienteId)) > 0;

    public bool AgregarEmpresa(string clienteId, string empresa) =>
        Ejecutar("INSERT OR IGNORE INTO empresas (cliente_id, nombre) VALUES ($c, $e)", ("$c", clienteId), ("$e", empresa)) > 0;

    public bool EmpresaDelCliente(string clienteId, string empresa) =>
        Escalar("SELECT COUNT(*) FROM empresas WHERE cliente_id=$c AND nombre=$e COLLATE NOCASE", ("$c", clienteId), ("$e", empresa)) > 0;

    public IReadOnlyList<string> EmpresasDelCliente(string clienteId)
    {
        using var cn = Abrir();
        using var cmd = Comando(cn, "SELECT nombre FROM empresas WHERE cliente_id=$c ORDER BY nombre", ("$c", clienteId));
        using var r = cmd.ExecuteReader();
        var lista = new List<string>();
        while (r.Read())
        {
            lista.Add(r.GetString(0));
        }

        return lista;
    }

    /// <summary>Registra una llave cuyo valor ya se conoce (por ejemplo, la que viene de una variable de entorno). Guarda solo el hash.</summary>
    public void CrearLlaveConocida(string clienteId, string rol, string llave, string? nombre = null) =>
        Ejecutar(
            "INSERT INTO llaves (id, cliente_id, rol, hash, creada, nombre, prefijo) VALUES ($id, $c, $r, $h, $t, $n, $p)",
            ("$id", Guid.NewGuid().ToString("N")), ("$c", clienteId), ("$r", rol), ("$h", Seguridad.HashLlave(llave)), ("$t", Ahora()),
            ("$n", string.IsNullOrWhiteSpace(nombre) ? null : nombre.Trim()), ("$p", llave[..7]));

    /// <summary>Guarda solo el hash. La llave en claro se entrega una vez y no se puede recuperar.</summary>
    public string CrearLlave(string clienteId, string rol, string? nombre = null)
    {
        var llave = Seguridad.NuevaLlave(rol);
        Ejecutar(
            "INSERT INTO llaves (id, cliente_id, rol, hash, creada, nombre, prefijo) VALUES ($id, $c, $r, $h, $t, $n, $p)",
            ("$id", Guid.NewGuid().ToString("N")), ("$c", clienteId), ("$r", rol), ("$h", Seguridad.HashLlave(llave)), ("$t", Ahora()),
            ("$n", string.IsNullOrWhiteSpace(nombre) ? null : nombre.Trim()), ("$p", llave[..7]));
        return llave;
    }

    public IReadOnlyList<LlaveDto> ListarLlaves(string clienteId)
    {
        using var cn = Abrir();
        using var cmd = Comando(cn, "SELECT id, nombre, rol, prefijo, activa, creada, ultimo_uso FROM llaves WHERE cliente_id=$c ORDER BY creada DESC", ("$c", clienteId));
        using var r = cmd.ExecuteReader();
        var lista = new List<LlaveDto>();
        while (r.Read())
        {
            lista.Add(new LlaveDto(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt32(4) == 1, Fecha(r.GetString(5)), r.IsDBNull(6) ? null : Fecha(r.GetString(6))));
        }

        return lista;
    }

    public bool RevocarLlave(string clienteId, string llaveId) =>
        Ejecutar("UPDATE llaves SET activa=0 WHERE id=$id AND cliente_id=$c AND activa=1", ("$id", llaveId), ("$c", clienteId)) > 0;

    public bool QuitarEmpresa(string clienteId, string empresa) =>
        Ejecutar("DELETE FROM empresas WHERE cliente_id=$c AND nombre=$e COLLATE NOCASE", ("$c", clienteId), ("$e", empresa)) > 0;

    // ---- Usuarios del portal ---------------------------------------------------------------

    public long ContarUsuarios() => Escalar("SELECT COUNT(*) FROM usuarios");

    public string CrearUsuario(string clienteId, string email, string nombre, string claveHash)
    {
        var id = Guid.NewGuid().ToString("N");
        Ejecutar(
            "INSERT INTO usuarios (id, cliente_id, email, nombre, clave, creado) VALUES ($id, $c, $e, $n, $k, $t)",
            ("$id", id), ("$c", clienteId), ("$e", email.Trim()), ("$n", nombre.Trim()), ("$k", claveHash), ("$t", Ahora()));
        return id;
    }

    public UsuarioRegistro? BuscarUsuarioPorEmail(string email) => BuscarUsuario("u.email=$v", (email ?? string.Empty).Trim());

    public UsuarioRegistro? BuscarUsuarioPorId(string usuarioId) => BuscarUsuario("u.id=$v", usuarioId);

    private UsuarioRegistro? BuscarUsuario(string condicion, string valor)
    {
        using var cn = Abrir();
        using var cmd = Comando(
            cn,
            "SELECT u.id, u.cliente_id, u.email, u.nombre, u.clave, c.nombre FROM usuarios u JOIN clientes c ON c.id=u.cliente_id " +
            $"WHERE {condicion} AND u.activo=1 AND c.activo=1",
            ("$v", valor));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new UsuarioRegistro(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5))
            : null;
    }

    public bool CambiarClave(string usuarioId, string claveHash) =>
        Ejecutar("UPDATE usuarios SET clave=$k WHERE id=$i", ("$k", claveHash), ("$i", usuarioId)) > 0;

    public string? NombreCliente(string clienteId)
    {
        using var cn = Abrir();
        using var cmd = Comando(cn, "SELECT nombre FROM clientes WHERE id=$c", ("$c", clienteId));
        return cmd.ExecuteScalar() as string;
    }

    // ---- Resumen del portal ----------------------------------------------------------------

    public ResumenPortal Resumen(string clienteId, TimeSpan ventanaWorker)
    {
        var desde = DateTime.UtcNow.AddHours(-24).ToString("O", CultureInfo.InvariantCulture);
        long Contar(EstadoTarea e) =>
            Escalar("SELECT COUNT(*) FROM tareas WHERE cliente_id=$c AND estado=$e AND creada >= $d", ("$c", clienteId), ("$e", (int)e), ("$d", desde));

        var workers = ListarWorkers(clienteId, ventanaWorker);
        DateTime? ultima = null;
        using (var cn = Abrir())
        {
            using var cmd = Comando(cn, "SELECT MAX(creada) FROM tareas WHERE cliente_id=$c", ("$c", clienteId));
            if (cmd.ExecuteScalar() is string t)
            {
                ultima = Fecha(t);
            }
        }

        return new ResumenPortal(
            Contar(EstadoTarea.Pendiente), Contar(EstadoTarea.EnProceso), Contar(EstadoTarea.Completada), Contar(EstadoTarea.Fallida),
            workers.Count(w => w.EnLinea), workers.Count, EmpresasDelCliente(clienteId).Count,
            Escalar("SELECT COUNT(*) FROM llaves WHERE cliente_id=$c AND activa=1", ("$c", clienteId)), ultima);
    }

    public Identidad? BuscarLlave(string llave)
    {
        using var cn = Abrir();
        using var cmd = Comando(
            cn,
            "SELECT l.id, l.cliente_id, l.rol FROM llaves l JOIN clientes c ON c.id=l.cliente_id WHERE l.hash=$h AND l.activa=1 AND c.activo=1",
            ("$h", Seguridad.HashLlave(llave)));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }

        var identidad = new Identidad(r.GetString(0), r.GetString(1), r.GetString(2));
        r.Close();
        Ejecutar("UPDATE llaves SET ultimo_uso=$t WHERE id=$i", ("$t", Ahora()), ("$i", identidad.LlaveId));
        return identidad;
    }

    public bool LlaveVigente(string llaveId) =>
        Escalar(
            "SELECT COUNT(*) FROM llaves l JOIN clientes c ON c.id=l.cliente_id WHERE l.id=$id AND l.activa=1 AND c.activo=1",
            ("$id", llaveId)) > 0;

    // ---- Cola -----------------------------------------------------------------------------

    public string EncolarTarea(string clienteId, string empresa, string tipo, string payload)
    {
        var id = Guid.NewGuid().ToString("N");
        Ejecutar(
            "INSERT INTO tareas (id, cliente_id, empresa, tipo, payload, estado, creada) VALUES ($id, $c, $e, $t, $p, 0, $ahora)",
            ("$id", id), ("$c", clienteId), ("$e", empresa), ("$t", tipo), ("$p", payload), ("$ahora", Ahora()));
        return id;
    }

    /// <summary>
    /// Encola una orden con llave de idempotencia. Si el cliente ya envió esa llave con la misma orden, no crea otra tarea:
    /// devuelve la que ya existe (<see cref="ResultadoEncolar.Repetida"/>). Si la llave ya se usó con una orden distinta,
    /// devuelve el conflicto sin encolar nada. Así un reintento de la red o del sistema que llama no duplica una factura.
    /// </summary>
    public ResultadoEncolar EncolarIdempotente(string clienteId, string empresa, string tipo, string payload, string llave)
    {
        var huella = Huella(empresa, tipo, payload);

        // Dos solicitudes simultáneas con la misma llave: la segunda choca con el índice único y vuelve a leer la primera.
        for (var intento = 0; intento < 3; intento++)
        {
            if (BuscarPorLlave(clienteId, llave) is { } previa)
            {
                return new ResultadoEncolar(previa.Id, previa.Estado, Repetida: true, Conflicto: previa.Huella != huella);
            }

            var id = Guid.NewGuid().ToString("N");
            try
            {
                Ejecutar(
                    "INSERT INTO tareas (id, cliente_id, empresa, tipo, payload, estado, creada, llave, huella) VALUES ($id, $c, $e, $t, $p, 0, $ahora, $l, $h)",
                    ("$id", id), ("$c", clienteId), ("$e", empresa), ("$t", tipo), ("$p", payload), ("$ahora", Ahora()), ("$l", llave), ("$h", huella));
                return new ResultadoEncolar(id, EstadoTarea.Pendiente, Repetida: false, Conflicto: false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // Otra solicitud registró la misma llave entre la lectura y la inserción.
            }
        }

        throw new InvalidOperationException("No se pudo registrar la llave de idempotencia.");
    }

    private (string Id, EstadoTarea Estado, string? Huella)? BuscarPorLlave(string clienteId, string llave)
    {
        using var cn = Abrir();
        using var cmd = Comando(cn, "SELECT id, estado, huella FROM tareas WHERE cliente_id=$c AND llave=$l", ("$c", clienteId), ("$l", llave));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), (EstadoTarea)r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2)) : null;
    }

    /// <summary>Resumen de la orden: empresa, tipo y datos. Igual orden da igual huella aunque cambien espacios o saltos de línea.</summary>
    private static string Huella(string empresa, string tipo, string payload)
    {
        string normalizado;
        try
        {
            normalizado = JsonSerializer.Serialize(JsonDocument.Parse(payload).RootElement);
        }
        catch (JsonException)
        {
            normalizado = payload;
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{empresa.ToLowerInvariant()}\n{tipo}\n{normalizado}"));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Entrega al worker la tarea pendiente más antigua de su empresa. Es atómico: dos workers no reciben la misma.</summary>
    public TareaAsignada? TomarTarea(string clienteId, string empresa, string workerId, string? version = null, string? host = null)
    {
        RegistrarWorker(clienteId, workerId, empresa, version, host);
        using var cn = Abrir();
        using var cmd = Comando(
            cn,
            """
            UPDATE tareas SET estado=1, intentos=intentos+1, iniciada=$ahora, worker_id=$w
            WHERE id = (SELECT id FROM tareas WHERE cliente_id=$c AND empresa=$e COLLATE NOCASE AND estado=0 ORDER BY creada, rowid LIMIT 1)
            RETURNING id, tipo, empresa, payload
            """,
            ("$ahora", Ahora()), ("$w", workerId), ("$c", clienteId), ("$e", empresa));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }

        return new TareaAsignada(r.GetString(0), r.GetString(1), r.GetString(2), JsonDocument.Parse(r.GetString(3)).RootElement.Clone());
    }

    /// <summary>Registra el resultado, solo si la tarea está en proceso y la tiene este worker.</summary>
    public bool RegistrarResultado(string clienteId, string tareaId, string workerId, ResultadoTarea resultado)
    {
        var filas = Ejecutar(
            """
            UPDATE tareas SET estado=$estado, terminada=$ahora, resultado=$res, error=$err, codigo_sdk=$cod
            WHERE id=$id AND cliente_id=$c AND worker_id=$w AND estado=1
            """,
            ("$estado", (int)(resultado.Exito ? EstadoTarea.Completada : EstadoTarea.Fallida)),
            ("$ahora", Ahora()),
            ("$res", resultado.Datos is { } d ? d.GetRawText() : null),
            ("$err", resultado.Error),
            ("$cod", resultado.CodigoSdk),
            ("$id", tareaId), ("$c", clienteId), ("$w", workerId));
        return filas > 0;
    }

    public TareaDto? ObtenerTarea(string clienteId, string tareaId)
    {
        using var cn = Abrir();
        using var cmd = Comando(cn, SeleccionTarea + " WHERE id=$id AND cliente_id=$c", ("$id", tareaId), ("$c", clienteId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? LeerTarea(r) : null;
    }

    public IReadOnlyList<TareaDto> ListarTareas(string clienteId, EstadoTarea? estado, int limite)
    {
        using var cn = Abrir();
        using var cmd = Comando(
            cn,
            SeleccionTarea + " WHERE cliente_id=$c AND ($e IS NULL OR estado=$e) ORDER BY creada DESC, rowid DESC LIMIT $l",
            ("$c", clienteId), ("$e", estado is null ? null : (int)estado), ("$l", Math.Clamp(limite, 1, 200)));
        using var r = cmd.ExecuteReader();
        var lista = new List<TareaDto>();
        while (r.Read())
        {
            lista.Add(LeerTarea(r));
        }

        return lista;
    }

    /// <summary>
    /// Tareas en proceso que el worker dejó sin responder. Las lecturas vuelven a la cola (hasta <paramref name="maxIntentos"/>);
    /// una escritura se marca fallida con resultado incierto, porque pudo aplicarse en CONTPAQi y repetirla duplicaría el registro.
    /// </summary>
    public int RecuperarColgadas(TimeSpan espera, int maxIntentos)
    {
        var limite = DateTime.UtcNow.Subtract(espera).ToString("O", CultureInfo.InvariantCulture);
        var lecturas = Ejecutar(
            "UPDATE tareas SET estado=0, worker_id=NULL WHERE estado=1 AND iniciada < $l AND tipo LIKE '%.leer' AND intentos < $m",
            ("$l", limite), ("$m", maxIntentos));
        var fallidas = Ejecutar(
            """
            UPDATE tareas SET estado=3, terminada=$ahora, worker_id=NULL,
                error=CASE WHEN tipo LIKE '%.leer' THEN 'El worker no respondió tras varios intentos.'
                           ELSE 'El worker dejó de responder durante la operación. El resultado es incierto: verifique en CONTPAQi antes de reintentar.' END
            WHERE estado=1 AND iniciada < $l
            """,
            ("$ahora", Ahora()), ("$l", limite));
        return lecturas + fallidas;
    }

    // ---- Workers --------------------------------------------------------------------------

    public void RegistrarWorker(string clienteId, string workerId, string empresa, string? version = null, string? host = null) =>
        Ejecutar(
            """
            INSERT INTO workers (id, cliente_id, empresa, ultima_vista, version, host) VALUES ($id, $c, $e, $t, $v, $h)
            ON CONFLICT (cliente_id, id) DO UPDATE SET empresa=excluded.empresa, ultima_vista=excluded.ultima_vista,
                version=COALESCE(excluded.version, version), host=COALESCE(excluded.host, host)
            """,
            ("$id", workerId), ("$c", clienteId), ("$e", empresa), ("$t", Ahora()), ("$v", version), ("$h", host));

    public IReadOnlyList<WorkerDto> ListarWorkers(string clienteId, TimeSpan ventanaEnLinea)
    {
        using var cn = Abrir();
        using var cmd = Comando(cn, "SELECT id, empresa, ultima_vista, version, host FROM workers WHERE cliente_id=$c ORDER BY empresa, id", ("$c", clienteId));
        using var r = cmd.ExecuteReader();
        var lista = new List<WorkerDto>();
        while (r.Read())
        {
            var vista = Fecha(r.GetString(2));
            lista.Add(new WorkerDto(
                r.GetString(0), r.GetString(1), vista, DateTime.UtcNow - vista <= ventanaEnLinea,
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        }

        return lista;
    }

    // ---- Utilidades -----------------------------------------------------------------------

    private const string SeleccionTarea =
        "SELECT id, tipo, empresa, estado, intentos, creada, iniciada, terminada, resultado, error, codigo_sdk FROM tareas";

    private static TareaDto LeerTarea(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        (EstadoTarea)r.GetInt32(3),
        r.GetInt32(4),
        Fecha(r.GetString(5)),
        r.IsDBNull(6) ? null : Fecha(r.GetString(6)),
        r.IsDBNull(7) ? null : Fecha(r.GetString(7)),
        r.IsDBNull(8) ? null : JsonDocument.Parse(r.GetString(8)).RootElement.Clone(),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetInt32(10));

    private static string Ahora() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Fecha(string texto) =>
        DateTime.Parse(texto, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private SqliteConnection Abrir()
    {
        var cn = new SqliteConnection(_cadena);
        cn.Open();
        using var espera = cn.CreateCommand();
        espera.CommandText = "PRAGMA busy_timeout=5000;";
        espera.ExecuteNonQuery();
        return cn;
    }

    private static SqliteCommand Comando(SqliteConnection cn, string sql, params (string Nombre, object? Valor)[] parametros)
    {
        var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (nombre, valor) in parametros)
        {
            cmd.Parameters.AddWithValue(nombre, valor ?? DBNull.Value);
        }

        return cmd;
    }

    private int Ejecutar(string sql, params (string Nombre, object? Valor)[] parametros)
    {
        using var cn = Abrir();
        using var cmd = Comando(cn, sql, parametros);
        return cmd.ExecuteNonQuery();
    }

    private long Escalar(string sql, params (string Nombre, object? Valor)[] parametros)
    {
        using var cn = Abrir();
        using var cmd = Comando(cn, sql, parametros);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

/// <summary>Llave de API sin su valor: solo el prefijo, para reconocerla en el portal.</summary>
public sealed record LlaveDto(string Id, string? Nombre, string Rol, string? Prefijo, bool Activa, DateTime Creada, DateTime? UltimoUso);

/// <summary>Usuario del portal.</summary>
/// <summary>Resultado de encolar con llave de idempotencia.</summary>
/// <param name="Repetida">La llave ya existía: no se creó una tarea nueva.</param>
/// <param name="Conflicto">La llave ya se usó con una orden distinta.</param>
public sealed record ResultadoEncolar(string Id, EstadoTarea Estado, bool Repetida, bool Conflicto);

public sealed record UsuarioRegistro(string Id, string ClienteId, string Email, string Nombre, string Clave, string ClienteNombre);

/// <summary>Cifras del dashboard (las tareas son de las últimas 24 horas).</summary>
public sealed record ResumenPortal(
    long Pendientes, long EnProceso, long Completadas, long Fallidas,
    int WorkersEnLinea, int WorkersTotal, int Empresas, long LlavesActivas, DateTime? UltimaTarea);
