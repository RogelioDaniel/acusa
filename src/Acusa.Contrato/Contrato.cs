using System.Text.Json;

namespace Acusa.Contrato;

/// <summary>Estado de una tarea en la cola del orquestador.</summary>
public enum EstadoTarea
{
    Pendiente = 0,
    EnProceso = 1,
    Completada = 2,
    Fallida = 3,
}

/// <summary>Nombres de las operaciones. El orquestador solo acepta las de <see cref="Soportadas"/>.</summary>
public static class Operaciones
{
    public const string ClienteCrear = "cliente.crear";
    public const string ClienteLeer = "cliente.leer";
    public const string ProductoCrear = "producto.crear";
    public const string ProductoLeer = "producto.leer";
    public const string DocumentoCrear = "documento.crear";
    public const string DocumentoLeer = "documento.leer";
    public const string DocumentoEmitir = "documento.emitir";
    public const string DocumentoCancelar = "documento.cancelar";
    public const string ClienteActualizar = "cliente.actualizar";
    public const string ProductoActualizar = "producto.actualizar";
    public const string DireccionCrear = "direccion.crear";
    public const string DireccionLeer = "direccion.leer";
    public const string DocumentoObtenerArchivo = "documento.obtener-archivo";

    /// <summary>Timbrado masivo: una orden con varios documentos. Es propia de Acusa, no existe con este nombre en Montek.</summary>
    public const string DocumentoEmitirLote = "documento.emitir.lote";

    public const string DocumentoCancelarLote = "documento.cancelar.lote";

    /// <summary>
    /// Documentos por orden en un lote. Una orden en proceso se considera colgada a los 10 minutos y un timbrado tarda
    /// entre 5 y 10 segundos, así que 25 deja margen.
    /// </summary>
    public const int MaximoDocumentosPorLote = 25;

    /// <summary>
    /// Operaciones que Acusa v1 expone en <c>POST /api/sdk/execute</c>: lo justo para un timbrado sencillo.
    /// El orquestador rechaza con 400 cualquier otra.
    /// </summary>
    public static readonly IReadOnlyList<string> Soportadas =
    [
        ClienteCrear, ClienteLeer, ClienteActualizar, ProductoCrear, ProductoLeer,
        DocumentoCrear, DocumentoEmitir, DocumentoObtenerArchivo, DocumentoCancelar,
    ];

    /// <summary>
    /// Todo lo que el worker sabe ejecutar. Es más que <see cref="Soportadas"/>: el worker conserva el soporte de las
    /// demás operaciones, pero el orquestador de v1 no las expone.
    /// </summary>
    public static readonly IReadOnlyList<string> ImplementadasPorElWorker =
    [
        ClienteCrear, ClienteLeer, ClienteActualizar, ProductoCrear, ProductoLeer, ProductoActualizar, DireccionCrear, DireccionLeer,
        DocumentoCrear, DocumentoLeer,
        DocumentoEmitir, DocumentoCancelar, DocumentoEmitirLote, DocumentoCancelarLote, DocumentoObtenerArchivo,
    ];

    /// <summary>
    /// Solo las lecturas se pueden repetir sin riesgo. Si el worker deja de responder a media tarea, una
    /// escritura no se reintenta sola: pudo haberse aplicado en CONTPAQi y repetirla duplicaría el registro.
    /// </summary>
    public static bool EsLectura(string tipoProceso) =>
        tipoProceso.EndsWith(".leer", StringComparison.Ordinal) || tipoProceso == DocumentoObtenerArchivo;
}

/// <summary>Cuerpo de <c>POST /api/sdk/execute</c>.</summary>
/// <param name="TipoProceso">Operación, por ejemplo <c>documento.crear</c>.</param>
/// <param name="Empresa">Nombre de la empresa de CONTPAQi (carpeta o base), por ejemplo <c>adEMPRESA_DE_PRUEBA</c>.</param>
/// <param name="DbName">Alias de <paramref name="Empresa"/>: es el nombre que usa Montek, para no cambiar integraciones existentes.</param>
/// <param name="Payload">Datos de la operación.</param>
public sealed record SolicitudEjecutar(string TipoProceso, string? Empresa, JsonElement? Payload, string? DbName = null, string? LlaveIdempotencia = null);

/// <summary>Respuesta de <c>POST /api/sdk/execute</c> (202).</summary>
public sealed record TareaCreada(string TareaId, EstadoTarea Estado);

/// <summary>Estado y resultado de una tarea.</summary>
public sealed record TareaDto(
    string Id,
    string TipoProceso,
    string Empresa,
    EstadoTarea Estado,
    int Intentos,
    DateTime Creada,
    DateTime? Iniciada,
    DateTime? Terminada,
    JsonElement? Resultado,
    string? Error,
    int? CodigoSdk);

/// <summary>Cuerpo de <c>POST /api/worker/poll</c>.</summary>
public sealed record SolicitudPoll(string WorkerId, string Empresa, string? Version = null, string? Host = null);

/// <summary>Tarea entregada a un worker.</summary>
public sealed record TareaAsignada(string Id, string TipoProceso, string Empresa, JsonElement Payload);

/// <summary>Cuerpo de <c>POST /api/worker/tasks/{id}/result</c>.</summary>
public sealed record ResultadoTarea(bool Exito, JsonElement? Datos, string? Error, int? CodigoSdk);

/// <summary>Respuesta de <c>POST /api/auth/token</c>.</summary>
public sealed record TokenRespuesta(string Token, int ExpiraEnSegundos);

/// <summary>Estado de un worker visto por el orquestador.</summary>
public sealed record WorkerDto(string WorkerId, string Empresa, DateTime UltimaVista, bool EnLinea, string? Version = null, string? Host = null);

/// <summary>Una operación del catálogo, tal como la publica el portal.</summary>
/// <param name="TipoProceso">Nombre que se manda en <c>tipoProceso</c>.</param>
/// <param name="Modulo">Agrupación en el portal: Clientes, Productos, Documentos.</param>
/// <param name="Descripcion">Qué hace.</param>
/// <param name="Ejemplo">Payload de ejemplo en JSON.</param>
public sealed record OperacionInfo(string TipoProceso, string Modulo, string Descripcion, string Ejemplo);

/// <summary>Catálogo de las operaciones expuestas en v1, con un payload de ejemplo cada una.</summary>
public static class CatalogoOperaciones
{
    public static readonly IReadOnlyList<OperacionInfo> Todas =
    [
        new(Operaciones.ClienteCrear, "Clientes", "Da de alta un cliente.", """{"codigo":"PRUEBADEMO01","razonSocial":"PRUEBA CLIENTE DEMO"}"""),
        new(Operaciones.ClienteLeer, "Clientes", "Consulta un cliente por código.", """{"codigo":"PRUEBADEMO01"}"""),
        new(Operaciones.ClienteActualizar, "Clientes", "Cambia datos de un cliente que ya existe: razón social, RFC, régimen fiscal, uso de CFDI, correo o límite de crédito. Un campo ausente no se toca.", """{"codigo":"PRUEBADEMO01","rfc":"XAXX010101000","limiteCredito":50000}"""),
        new(Operaciones.ProductoCrear, "Productos", "Da de alta un producto.","""{"codigo":"PRUEBAPR01","nombre":"PRUEBA PRODUCTO","unidadBase":"Pieza","precio1":10,"claveSat":"01010101"}"""),
        new(Operaciones.ProductoLeer, "Productos", "Consulta un producto por código.", """{"codigo":"PRUEBAPR01"}"""),
        new(Operaciones.DocumentoCrear, "Documentos", "Crea un documento sin timbrar. Devuelve concepto, serie y folio.", """{"concepto":"5","serie":"PRUEBA","clienteCodigo":"PRUEBADEMO01","referencia":"PRUEBA","partidas":[{"productoCodigo":"PRUEBAPR01","unidades":1,"precio":10,"almacen":"1"}]}"""),
        new(Operaciones.DocumentoEmitir, "Documentos", "Timbra un documento de prueba (CFDI). La contraseña del certificado vive en el worker, no se envía.", """{"concepto":"5","serie":"PRUEBA","folio":1}"""),
        new(Operaciones.DocumentoObtenerArchivo, "Documentos", "Entrega el XML y/o el PDF de un documento timbrado (formato: xml, pdf o ambos). Con incluirContenido devuelve el archivo en base64.", """{"concepto":"5","serie":"PRUEBA","folio":1,"formato":"ambos","incluirContenido":true}"""),
        new(Operaciones.DocumentoCancelar, "Documentos", "Cancela un documento de prueba. Si está timbrado exige el motivo del SAT (01 a 04); el 01 pide uuidSustituto.", """{"concepto":"5","serie":"PRUEBA","folio":1,"motivo":"02"}"""),
    ];

    public static OperacionInfo? Buscar(string tipoProceso) =>
        Todas.FirstOrDefault(o => string.Equals(o.TipoProceso, tipoProceso, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Detecta campos que parecen una contraseña en un payload. La cola guarda cada payload, así que una contraseña (la del
/// certificado, por ejemplo) no debe viajar en la orden: vive en el worker. Es una lista corta y explícita: un filtro por
/// la palabra "clave" bloqueaba <c>claveSat</c>, que es un dato fiscal y no un secreto.
/// </summary>
public static class CamposSecretos
{
    private static readonly string[] Contiene = ["password", "contrasena", "contraseña", "secret", "pwd", "passwd"];
    private static readonly string[] Empieza = ["clavecert", "clavesdk", "claveacceso", "claveprivada", "clavellave"];

    /// <summary>Nombre del primer campo del payload que parece una contraseña, o <c>null</c>.</summary>
    public static string? Buscar(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propiedad in payload.EnumerateObject())
        {
            if (EsSecreto(propiedad.Name))
            {
                return propiedad.Name;
            }
        }

        return null;
    }

    public static bool EsSecreto(string nombre)
    {
        var n = nombre.Trim().ToLowerInvariant();
        return Contiene.Any(n.Contains) || n is "clave" or "claves" || Empieza.Any(n.StartsWith);
    }
}
