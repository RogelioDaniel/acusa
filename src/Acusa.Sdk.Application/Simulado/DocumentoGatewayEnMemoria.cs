using System.Collections.Concurrent;
using System.Globalization;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Documentos;
using Acusa.Sdk.Application.Productos;

namespace Acusa.Sdk.Application.Simulado;

/// <summary>
/// Documentos en memoria. Como en CONTPAQi, el cliente y los productos deben existir y cada documento
/// consume el siguiente folio del concepto y la serie. El IVA del 16 % es una suposición de la simulación:
/// el SDK real calcula los impuestos con la configuración de cada producto.
/// </summary>
public sealed class DocumentoGatewayEnMemoria(IClienteGateway clientes, IProductoGateway productos) : IDocumentoGateway
{
    private const decimal TasaIva = 0.16m;

    private readonly ConcurrentDictionary<(string Concepto, string Serie, long Folio), Documento> _documentos = new();
    private readonly ConcurrentDictionary<(string Concepto, string Serie), long> _folios = new();
    private int _ultimoId;

    public Task<Documento?> BuscarAsync(string concepto, string serie, long folio, CancellationToken ct) =>
        Task.FromResult(_documentos.TryGetValue((concepto, serie, folio), out var d) ? d : null);

    public Task<Documento?> BuscarPorIdAsync(int id, CancellationToken ct) =>
        Task.FromResult(_documentos.Values.FirstOrDefault(d => d.Id == id));

    /// <summary>Simula el PAC: asigna un UUID nuevo. No hay certificado ni SAT en la simulación.</summary>
    public Task<ResultadoDocumento> EmitirAsync(Documento documento, string claveCertificado, CancellationToken ct)
    {
        var timbrado = documento with { Uuid = Guid.NewGuid().ToString().ToUpperInvariant() };
        Guardar(timbrado);
        return Task.FromResult(new ResultadoDocumento(TipoResultadoDocumento.Hecho, timbrado));
    }

    /// <summary>Simula la entrega: un XML mínimo y un PDF ficticio, sin tocar el disco.</summary>
    public Task<ResultadoArchivos> EntregarArchivosAsync(Documento documento, FormatoArchivo formato, bool incluirContenido, CancellationToken ct)
    {
        var archivos = new List<ArchivoEntregado>();
        if (formato is FormatoArchivo.Xml or FormatoArchivo.Ambos)
        {
            var xml = System.Text.Encoding.UTF8.GetBytes($"<cfdi:Comprobante Folio=\"{documento.Folio}\" Serie=\"{documento.Serie}\"><TimbreFiscalDigital UUID=\"{documento.Uuid}\"/></cfdi:Comprobante>");
            archivos.Add(new ArchivoEntregado("xml", $"simulado\\{documento.Uuid}.xml", xml.Length, incluirContenido ? Convert.ToBase64String(xml) : null));
        }

        if (formato is FormatoArchivo.Pdf or FormatoArchivo.Ambos)
        {
            var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 simulado");
            archivos.Add(new ArchivoEntregado("pdf", $"simulado\\{documento.Uuid}.pdf", pdf.Length, incluirContenido ? Convert.ToBase64String(pdf) : null));
        }

        return Task.FromResult(new ResultadoArchivos(TipoResultadoDocumento.Hecho, documento, archivos));
    }

    public Task<ResultadoDocumento> CancelarAsync(Documento documento, string? motivo, string? uuidSustituto, CancellationToken ct)
    {
        var cancelado = documento with { Cancelado = true };
        Guardar(cancelado);
        return Task.FromResult(new ResultadoDocumento(TipoResultadoDocumento.Hecho, cancelado));
    }

    private void Guardar(Documento d) => _documentos[(d.Concepto, d.Serie ?? string.Empty, d.Folio)] = d;

    public async Task<ResultadoCrearDocumento> CrearAsync(NuevoDocumento documento, CancellationToken ct)
    {
        if (await clientes.BuscarAsync(documento.ClienteCodigo, ct) is null)
        {
            return new ResultadoCrearDocumento(TipoResultadoCrear.Invalido, Mensaje: $"No existe el cliente {documento.ClienteCodigo}.");
        }

        foreach (var partida in documento.Partidas)
        {
            if (await productos.BuscarAsync(partida.ProductoCodigo, ct) is null)
            {
                return new ResultadoCrearDocumento(TipoResultadoCrear.Invalido, Mensaje: $"No existe el producto {partida.ProductoCodigo}.");
            }
        }

        var serie = documento.Serie ?? string.Empty;
        var folio = _folios.AddOrUpdate((documento.Concepto, serie), 1, (_, actual) => actual + 1);
        var neto = documento.Partidas.Sum(p => p.Unidades * p.Precio);
        var impuestos = documento.Partidas.Sum(p =>
        {
            var importe = p.Unidades * p.Precio;
            return importe * ((p.IvaPorcentaje ?? 0m) + (p.IepsPorcentaje ?? 0m) - (p.RetencionIvaPorcentaje ?? 0m)) / 100m;
        });
        var hayImpuestos = documento.Partidas.Any(p => p.IvaPorcentaje is not null || p.IepsPorcentaje is not null || p.RetencionIvaPorcentaje is not null);
        var fecha = (documento.Fecha?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Now).ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) + " 00:00:00:000";

        var creado = new Documento(
            Id: Interlocked.Increment(ref _ultimoId),
            documento.Concepto,
            string.IsNullOrEmpty(serie) ? null : serie,
            folio,
            fecha,
            documento.ClienteCodigo,
            documento.Referencia,
            neto,
            Total: Math.Round(hayImpuestos ? neto + impuestos : neto * (1 + TasaIva), 2),
            Cancelado: false,
            Uuid: null);

        _documentos[(documento.Concepto, serie, folio)] = creado;
        return new ResultadoCrearDocumento(TipoResultadoCrear.Creado, creado);
    }
}
