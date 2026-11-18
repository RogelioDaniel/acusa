using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Acusa.Orquestador.Facturacion;

/// <summary>
/// Almacén sencillo de documentos JSON para las rutas de facturación (compañías, productos, facturas, partidas, direcciones
/// y empresas emisoras). Cada documento pertenece a un cliente de Acusa y solo él lo ve. Los identificadores son enteros
/// por tipo Usa el mismo archivo de base de datos que Central.
/// </summary>
public sealed class DocStore
{
    private readonly string _cadena;

    public DocStore(string rutaBaseDatos)
    {
        _cadena = new SqliteConnectionStringBuilder { DataSource = rutaBaseDatos, Pooling = true }.ToString();
        Ejecutar("""
            CREATE TABLE IF NOT EXISTS docs (tipo TEXT NOT NULL, id INTEGER NOT NULL, cliente_id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY (tipo, id));
            CREATE TABLE IF NOT EXISTS docs_contador (tipo TEXT PRIMARY KEY, ultimo INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_docs_cliente ON docs (tipo, cliente_id);
            """);
    }

    /// <summary>Guarda un documento nuevo y le asigna el siguiente identificador de su tipo; también lo escribe en <c>id</c>.</summary>
    public int Insertar(string tipo, string clienteId, JsonObject doc)
    {
        using var conexion = Abrir();
        using var transaccion = conexion.BeginTransaction();
        int id;
        using (var comando = conexion.CreateCommand())
        {
            comando.Transaction = transaccion;
            comando.CommandText = "INSERT INTO docs_contador (tipo, ultimo) VALUES ($t, 1) ON CONFLICT(tipo) DO UPDATE SET ultimo = ultimo + 1 RETURNING ultimo";
            comando.Parameters.AddWithValue("$t", tipo);
            id = Convert.ToInt32(comando.ExecuteScalar());
        }

        doc["id"] = id;
        using (var comando = conexion.CreateCommand())
        {
            comando.Transaction = transaccion;
            comando.CommandText = "INSERT INTO docs (tipo, id, cliente_id, json) VALUES ($t, $i, $c, $j)";
            comando.Parameters.AddWithValue("$t", tipo);
            comando.Parameters.AddWithValue("$i", id);
            comando.Parameters.AddWithValue("$c", clienteId);
            comando.Parameters.AddWithValue("$j", doc.ToJsonString());
            comando.ExecuteNonQuery();
        }

        transaccion.Commit();
        return id;
    }

    public JsonObject? Obtener(string tipo, string clienteId, int id)
    {
        using var conexion = Abrir();
        using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT json FROM docs WHERE tipo=$t AND id=$i AND cliente_id=$c";
        comando.Parameters.AddWithValue("$t", tipo);
        comando.Parameters.AddWithValue("$i", id);
        comando.Parameters.AddWithValue("$c", clienteId);
        return comando.ExecuteScalar() is string json ? JsonNode.Parse(json) as JsonObject : null;
    }

    public bool Actualizar(string tipo, string clienteId, int id, JsonObject doc)
    {
        doc["id"] = id;
        return Ejecutar(
            "UPDATE docs SET json=$j WHERE tipo=$t AND id=$i AND cliente_id=$c",
            ("$j", doc.ToJsonString()), ("$t", tipo), ("$i", id), ("$c", clienteId)) > 0;
    }

    /// <summary>Borra el documento del almacén de Acusa (partidas de un borrador). No toca CONTPAQi.</summary>
    public bool Eliminar(string tipo, string clienteId, int id) =>
        Ejecutar("DELETE FROM docs WHERE tipo=$t AND id=$i AND cliente_id=$c", ("$t", tipo), ("$i", id), ("$c", clienteId)) > 0;

    public List<JsonObject> Listar(string tipo, string clienteId)
    {
        using var conexion = Abrir();
        using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT json FROM docs WHERE tipo=$t AND cliente_id=$c ORDER BY id";
        comando.Parameters.AddWithValue("$t", tipo);
        comando.Parameters.AddWithValue("$c", clienteId);
        using var r = comando.ExecuteReader();
        var lista = new List<JsonObject>();
        while (r.Read())
        {
            if (JsonNode.Parse(r.GetString(0)) is JsonObject o)
            {
                lista.Add(o);
            }
        }

        return lista;
    }

    private SqliteConnection Abrir()
    {
        var conexion = new SqliteConnection(_cadena);
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = "PRAGMA busy_timeout=5000;";
        comando.ExecuteNonQuery();
        return conexion;
    }

    private int Ejecutar(string sql, params (string Nombre, object? Valor)[] parametros)
    {
        using var conexion = Abrir();
        using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nombre, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nombre, valor ?? DBNull.Value);
        }

        return comando.ExecuteNonQuery();
    }
}
