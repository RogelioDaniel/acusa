using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acusa.Contrato;

[JsonConverter(typeof(ConvertidorErrorApi))]
public sealed record ErrorApi(
    string Codigo,
    string Mensaje,
    IReadOnlyList<string> Detalles,
    string? IdCorrelacion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CodigoSdk = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TareaId = null);

public static class CodigosError
{
    public const string SolicitudInvalida = "solicitud_invalida";
    public const string NoAutorizado = "no_autorizado";
    public const string NoEncontrado = "no_encontrado";
    public const string MetodoNoPermitido = "metodo_no_permitido";
    public const string Conflicto = "conflicto";
    public const string Validacion = "validacion";
    public const string OperacionFallida = "operacion_fallida";
    public const string ErrorSdk = "error_sdk";
    public const string DemasiadosIntentos = "demasiados_intentos";
    public const string NoImplementado = "no_implementado";
    public const string ErrorInterno = "error_interno";

    public static string DeEstado(int estado) => estado switch
    {
        400 => SolicitudInvalida,
        401 or 403 => NoAutorizado,
        404 => NoEncontrado,
        405 => MetodoNoPermitido,
        409 => Conflicto,
        422 => Validacion,
        429 => DemasiadosIntentos,
        >= 500 => ErrorInterno,
        _ => SolicitudInvalida,
    };
}

public sealed class ConvertidorErrorApi : JsonConverter<ErrorApi>
{
    public override ErrorApi Read(ref Utf8JsonReader lector, Type tipo, JsonSerializerOptions opciones)
    {
        using var documento = JsonDocument.ParseValue(ref lector);
        var raiz = documento.RootElement;
        var detalles = new List<string>();
        if (raiz.TryGetProperty("detalles", out var lista) && lista.ValueKind == JsonValueKind.Array)
        {
            foreach (var detalle in lista.EnumerateArray())
            {
                detalles.Add(detalle.GetString() ?? string.Empty);
            }
        }

        string? idCorrelacion = null;
        if (raiz.TryGetProperty("idCorrelacion", out var correlacion) && correlacion.ValueKind == JsonValueKind.String)
        {
            idCorrelacion = correlacion.GetString();
        }

        int? codigoSdk = null;
        if (raiz.TryGetProperty("codigoSdk", out var sdk) && sdk.ValueKind == JsonValueKind.Number)
        {
            codigoSdk = sdk.GetInt32();
        }

        string? tareaId = null;
        if (raiz.TryGetProperty("tareaId", out var tarea) && tarea.ValueKind == JsonValueKind.String)
        {
            tareaId = tarea.GetString();
        }

        var codigo = string.Empty;
        if (raiz.TryGetProperty("codigo", out var valor) && valor.ValueKind == JsonValueKind.String)
        {
            codigo = valor.GetString() ?? string.Empty;
        }

        return new ErrorApi(codigo, string.Join(" ", detalles), detalles, idCorrelacion, codigoSdk, tareaId);
    }

    public override void Write(Utf8JsonWriter escritor, ErrorApi error, JsonSerializerOptions opciones)
    {
        escritor.WriteStartObject();
        escritor.WriteString("codigo", error.Codigo);
        escritor.WriteStartArray("detalles");
        var soloDetalles = error.Codigo == CodigosError.Validacion && error.Detalles.Count > 0;
        if (!soloDetalles && !string.IsNullOrWhiteSpace(error.Mensaje))
        {
            escritor.WriteStringValue(error.Mensaje);
        }

        foreach (var detalle in error.Detalles)
        {
            escritor.WriteStringValue(detalle);
        }

        escritor.WriteEndArray();
        escritor.WriteString("idCorrelacion", error.IdCorrelacion);
        if (error.CodigoSdk is { } codigoSdk)
        {
            escritor.WriteNumber("codigoSdk", codigoSdk);
        }

        if (error.TareaId is { } tareaId)
        {
            escritor.WriteString("tareaId", tareaId);
        }

        escritor.WriteEndObject();
    }
}
