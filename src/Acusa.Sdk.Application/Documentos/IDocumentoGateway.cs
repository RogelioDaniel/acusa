namespace Acusa.Sdk.Application.Documentos;

/// <summary>Puerto hacia CONTPAQi para documentos. Busca, crea borradores, timbra y cancela; no borra.</summary>
public interface IDocumentoGateway
{
    /// <summary>Devuelve <c>null</c> si no existe.</summary>
    Task<Documento?> BuscarAsync(string concepto, string serie, long folio, CancellationToken ct);

    /// <summary>Devuelve <c>null</c> si no existe.</summary>
    Task<Documento?> BuscarPorIdAsync(int id, CancellationToken ct);

    /// <summary>Crea el documento ya validado. Consume un folio del concepto.</summary>
    Task<ResultadoCrearDocumento> CrearAsync(NuevoDocumento documento, CancellationToken ct);

    /// <summary>
    /// Timbra un documento que ya existe y no está timbrado. Lo valida el servicio; aquí solo se ejecuta.
    /// Timbrar consume un timbre del PAC y no se deshace: solo se puede cancelar.
    /// </summary>
    Task<ResultadoDocumento> EmitirAsync(Documento documento, string claveCertificado, CancellationToken ct);

    /// <summary>
    /// Pide a CONTPAQi que deje el XML y/o el PDF del CFDI en disco y devuelve lo que encuentre. El servicio ya comprobó
    /// que el documento está timbrado.
    /// </summary>
    Task<ResultadoArchivos> EntregarArchivosAsync(Documento documento, FormatoArchivo formato, bool incluirContenido, CancellationToken ct);

    /// <summary>
    /// Cancela un documento. Si está timbrado usa el motivo del SAT (y el UUID que lo sustituye con el motivo 01);
    /// si es un borrador sin timbrar, lo cancela en CONTPAQi sin tocar al SAT.
    /// </summary>
    Task<ResultadoDocumento> CancelarAsync(Documento documento, string? motivo, string? uuidSustituto, CancellationToken ct);
}
