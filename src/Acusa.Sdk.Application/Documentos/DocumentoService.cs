using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Sesion;

namespace Acusa.Sdk.Application.Documentos;

/// <summary>Casos de uso de documentos. No conoce el SDK: depende solo de <see cref="IDocumentoGateway"/>.</summary>
public sealed class DocumentoService(
    IDocumentoGateway gateway,
    ISesionSdk sesion,
    PoliticaEscritura politica,
    IProveedorClaveCertificado? certificado = null)
{
    /// <summary>
    /// Timbra un documento de prueba que ya existe. Antes de llamar al SDK comprueba que los candados estén abiertos,
    /// que el documento sea de prueba, que no esté cancelado ni timbrado ya (no se timbra dos veces) y que el worker
    /// tenga la contraseña del certificado.
    /// </summary>
    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public async Task<ResultadoDocumento> EmitirAsync(ReferenciaDocumento referencia, CancellationToken ct)
    {
        var motivo = politica.ValidarTimbrado();
        if (motivo is not null)
        {
            return Invalido(motivo);
        }

        referencia = Normalizar(referencia);
        motivo = PoliticaEscritura.ValidarReferencia(referencia);
        if (motivo is not null)
        {
            return Invalido(motivo);
        }

        ExigirSesion();
        var documento = await LocalizarAsync(referencia, ct);
        if (documento is null)
        {
            return NoEncontrado(referencia);
        }

        motivo = politica.ValidarDocumentoDePrueba(documento);
        if (motivo is not null)
        {
            return Invalido(motivo);
        }

        if (documento.Cancelado)
        {
            return Invalido("El documento está cancelado y no se puede timbrar.");
        }

        if (!string.IsNullOrWhiteSpace(documento.Uuid))
        {
            return Invalido($"El documento ya está timbrado (UUID {documento.Uuid}). No se timbra dos veces.", documento);
        }

        var clave = certificado?.ObtenerClave();
        var enComercial = certificado?.ContrasenaGuardadaEnComercial == true;
        if (string.IsNullOrEmpty(clave) && !enComercial)
        {
            return Invalido("Falta la contraseña del certificado. Guárdela en el panel del worker (Configuración); no se envía en la orden.");
        }

        return await gateway.EmitirAsync(documento, clave ?? string.Empty, ct);
    }

    /// <summary>
    /// Cancela un documento de prueba. Si está timbrado exige el motivo del SAT; si es un borrador sin timbrar lo cancela
    /// solo en CONTPAQi.
    /// </summary>
    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public async Task<ResultadoDocumento> CancelarAsync(ReferenciaDocumento referencia, string? motivoSat, string? uuidSustituto, CancellationToken ct)
    {
        var motivo = politica.ValidarTimbrado();
        if (motivo is not null)
        {
            return Invalido(motivo);
        }

        referencia = Normalizar(referencia);
        motivo = PoliticaEscritura.ValidarReferencia(referencia);
        if (motivo is not null)
        {
            return Invalido(motivo);
        }

        ExigirSesion();
        var documento = await LocalizarAsync(referencia, ct);
        if (documento is null)
        {
            return NoEncontrado(referencia);
        }

        motivo = politica.ValidarDocumentoDePrueba(documento);
        if (motivo is not null)
        {
            return Invalido(motivo);
        }

        if (documento.Cancelado)
        {
            return Invalido("El documento ya está cancelado.", documento);
        }

        motivoSat = (motivoSat ?? string.Empty).Trim();
        uuidSustituto = string.IsNullOrWhiteSpace(uuidSustituto) ? null : uuidSustituto.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(documento.Uuid))
        {
            motivo = PoliticaEscritura.ValidarMotivoCancelacion(motivoSat, uuidSustituto);
            if (motivo is not null)
            {
                return Invalido(motivo);
            }

            return await gateway.CancelarAsync(documento, motivoSat, uuidSustituto, ct);
        }

        return await gateway.CancelarAsync(documento, null, null, ct);
    }

    /// <summary>
    /// Entrega el XML y/o el PDF de un documento ya timbrado. Es una consulta: no pide candados ni exige documento de
    /// prueba, porque no cambia nada fiscal (solo deja archivos en el equipo del worker).
    /// </summary>
    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public async Task<ResultadoArchivos> ObtenerArchivosAsync(ReferenciaDocumento referencia, FormatoArchivo formato, bool incluirContenido, CancellationToken ct)
    {
        referencia = Normalizar(referencia);
        var motivo = PoliticaEscritura.ValidarReferencia(referencia);
        if (motivo is not null)
        {
            return new ResultadoArchivos(TipoResultadoDocumento.Invalido, Mensaje: motivo);
        }

        ExigirSesion();
        var documento = await LocalizarAsync(referencia, ct);
        if (documento is null)
        {
            return new ResultadoArchivos(TipoResultadoDocumento.NoEncontrado, Mensaje: $"No existe el documento {referencia}.");
        }

        if (string.IsNullOrWhiteSpace(documento.Uuid))
        {
            return new ResultadoArchivos(TipoResultadoDocumento.Invalido, documento, Mensaje: "El documento no está timbrado: todavía no tiene XML ni PDF de CFDI.");
        }

        return await gateway.EntregarArchivosAsync(documento, formato, incluirContenido, ct);
    }

    private static ReferenciaDocumento Normalizar(ReferenciaDocumento r) =>
        r with
        {
            Concepto = (r.Concepto ?? string.Empty).Trim().ToUpperInvariant(),
            Serie = (r.Serie ?? string.Empty).Trim().ToUpperInvariant(),
        };

    private Task<Documento?> LocalizarAsync(ReferenciaDocumento r, CancellationToken ct) =>
        r.Id is { } id
            ? gateway.BuscarPorIdAsync(id, ct)
            : gateway.BuscarAsync(r.Concepto, r.Serie ?? string.Empty, r.Folio, ct);

    private static ResultadoDocumento Invalido(string mensaje, Documento? documento = null) =>
        new(TipoResultadoDocumento.Invalido, documento, mensaje);

    private static ResultadoDocumento NoEncontrado(ReferenciaDocumento r) =>
        new(TipoResultadoDocumento.NoEncontrado, Mensaje: $"No existe el documento {r}.");

    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public Task<Documento?> ObtenerAsync(string concepto, string? serie, long folio, CancellationToken ct)
    {
        ExigirSesion();
        return gateway.BuscarAsync((concepto ?? string.Empty).Trim().ToUpperInvariant(), (serie ?? string.Empty).Trim().ToUpperInvariant(), folio, ct);
    }

    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public Task<Documento?> ObtenerPorIdAsync(int id, CancellationToken ct)
    {
        ExigirSesion();
        return gateway.BuscarPorIdAsync(id, ct);
    }

    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public async Task<ResultadoCrearDocumento> CrearAsync(NuevoDocumento solicitud, CancellationToken ct)
    {
        var documento = new NuevoDocumento(
            (solicitud.Concepto ?? string.Empty).Trim().ToUpperInvariant(),
            (solicitud.Serie ?? string.Empty).Trim().ToUpperInvariant(),
            PoliticaEscritura.NormalizarCodigo(solicitud.ClienteCodigo),
            (solicitud.Referencia ?? string.Empty).Trim().ToUpperInvariant(),
            (solicitud.Partidas ?? [])
                .Select(p => p with
                {
                    ProductoCodigo = PoliticaEscritura.NormalizarCodigo(p.ProductoCodigo),
                    Almacen = string.IsNullOrWhiteSpace(p.Almacen) ? null : p.Almacen.Trim().ToUpperInvariant(),
                })
                .ToList(),
            solicitud.Fecha,
            string.IsNullOrWhiteSpace(solicitud.FormaPago) ? null : solicitud.FormaPago.Trim(),
            string.IsNullOrWhiteSpace(solicitud.UsoCfdi) ? null : solicitud.UsoCfdi.Trim().ToUpperInvariant());

        var motivo = politica.Validar(documento);
        if (motivo is not null)
        {
            return new ResultadoCrearDocumento(TipoResultadoCrear.Invalido, Mensaje: motivo);
        }

        ExigirSesion();
        return await gateway.CrearAsync(documento, ct);
    }

    private void ExigirSesion()
    {
        var estado = sesion.Estado.Estado;
        if (estado != EstadoSesion.Lista)
        {
            throw new SdkNoListoException(estado);
        }
    }
}
