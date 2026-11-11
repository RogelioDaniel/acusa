using System.Globalization;
using System.Text;
using Acusa.Sdk.Application;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Application.Documentos;
using Acusa.Sdk.Integration.Contpaqi.Native;

namespace Acusa.Sdk.Integration.Contpaqi;

/// <summary>
/// Documentos sobre el SDK real. Busca, crea borradores, timbra y cancela; no borra. El servicio ya validó candados,
/// documento de prueba y motivo: aquí solo se ejecuta.
/// Crear: <c>fAltaDocumento</c>, un <c>fAltaMovimiento</c> por partida y <c>fGuardaDocumento</c>.
/// Timbrar y cancelar: <c>fEmitirDocumento</c>, <c>fCancelaDocumentoConMotivo</c> y <c>fCancelaDocumento</c>.
/// Pendiente de probar con un PAC y certificado de pruebas: sale del análisis de Montek, no de una ejecución propia.
/// </summary>
public sealed class DocumentoGatewayContpaqi(SesionContpaqi sesion, TrazaSdk traza, OpcionesContpaqi? opciones = null) : IDocumentoGateway
{
    private const long LimiteContenido = 3 * 1024 * 1024;
    private const int Sistema = 6;

    // Campo del documento donde Comercial guarda la forma de pago. Verificado en Comercial real (empresa de pruebas, 10-nov-2026).
    // El uso del CFDI NO es un campo del documento: el SDK responde 73 «el nombre del campo es inválido» a CUSOCFDI. Vive en el
    // cliente (cliente.actualizar), de donde lo toma Comercial al timbrar.
    private const string CampoFormaPago = "CMETODOPAG";

    public Task<Documento?> BuscarAsync(string concepto, string serie, long folio, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Liberando(() => Buscar(concepto, serie, folio)));

    public Task<ResultadoCrearDocumento> CrearAsync(NuevoDocumento documento, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Liberando(() => Crear(documento)));

    public Task<Documento?> BuscarPorIdAsync(int id, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Liberando(() => BuscarPorId(id)));

    public Task<ResultadoDocumento> EmitirAsync(Documento documento, string claveCertificado, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Liberando(() => Emitir(documento, claveCertificado)));

    public Task<ResultadoDocumento> CancelarAsync(Documento documento, string? motivo, string? uuidSustituto, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Liberando(() => CancelarDocumento(documento, motivo, uuidSustituto)));

    public Task<ResultadoArchivos> EntregarArchivosAsync(Documento documento, FormatoArchivo formato, bool incluirContenido, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Liberando(() => Entregar(documento, formato, incluirContenido)));

    /// <summary>
    /// Ejecuta una operación sobre un documento y, al terminar, lo libera con <c>fDesbloqueaDocumento</c>. Sin esto el SDK deja el
    /// último documento bloqueado y Comercial lo muestra «en uso por otro usuario», sin poder imprimirlo ni abrirlo.
    /// </summary>
    private T Liberando<T>(Func<T> operacion)
    {
        try
        {
            return operacion();
        }
        finally
        {
            try
            {
                traza.Llamar("fDesbloqueaDocumento()", SdkNative.fDesbloqueaDocumento);
            }
            catch (Exception)
            {
                // Una versión del SDK sin esta función no debe romper la operación.
            }
        }
    }

    /// <summary>
    /// Pide el XML (formato 0) y/o el PDF (formato 1) con <c>fEntregEnDiscoXML</c>, que los escribe en la carpeta de entrega
    /// del concepto, y luego busca los archivos nuevos en disco. Es el mismo camino de Montek; la carpeta exacta depende de
    /// la configuración del concepto, así que se puede fijar con Contpaqi:CarpetaEntrega.
    /// </summary>
    private ResultadoArchivos Entregar(Documento documento, FormatoArchivo formato, bool incluirContenido)
    {
        var serie = documento.Serie ?? string.Empty;
        var desde = DateTime.UtcNow.AddSeconds(-3);
        var archivos = new List<ArchivoEntregado>();
        var avisos = new List<string>();

        var pedidos = new List<(string Tipo, int Codigo)>();
        if (formato is FormatoArchivo.Xml or FormatoArchivo.Ambos)
        {
            pedidos.Add(("xml", 0));
        }

        if (formato is FormatoArchivo.Pdf or FormatoArchivo.Ambos)
        {
            pedidos.Add(("pdf", 1));
        }

        foreach (var (tipo, codigo) in pedidos)
        {
            var r = traza.Llamar(
                $"fEntregEnDiscoXML('{documento.Concepto}','{serie}',{documento.Folio},{codigo},'')",
                () => SdkNative.fEntregEnDiscoXML(documento.Concepto, serie, documento.Folio, codigo, string.Empty));
            if (r != 0)
            {
                return new ResultadoArchivos(
                    TipoResultadoDocumento.ErrorSdk,
                    documento,
                    archivos,
                    $"fEntregEnDiscoXML ({tipo.ToUpperInvariant()}) = {r}: {SdkNative.Mensaje(r)}",
                    r);
            }

            var ruta = BuscarArchivo(tipo, documento.Uuid, desde);
            if (ruta is null)
            {
                avisos.Add($"El SDK entregó el {tipo.ToUpperInvariant()} sin error, pero no lo encontré en disco. Indique en Contpaqi:CarpetaEntrega la carpeta de entrega de archivos del concepto.");
                continue;
            }

            var info = new FileInfo(ruta);
            string? base64 = null;
            if (incluirContenido)
            {
                if (info.Length <= LimiteContenido)
                {
                    base64 = Convert.ToBase64String(File.ReadAllBytes(ruta));
                }
                else
                {
                    avisos.Add($"El {tipo.ToUpperInvariant()} pesa {info.Length / 1024} KB y no se incluye su contenido; tómelo de {ruta}.");
                }
            }

            archivos.Add(new ArchivoEntregado(tipo, ruta, info.Length, base64));
        }

        return new ResultadoArchivos(
            TipoResultadoDocumento.Hecho,
            documento,
            archivos,
            Aviso: avisos.Count == 0 ? null : string.Join(" ", avisos));
    }

    /// <summary>El archivo más reciente del tipo pedido que apareció después de la llamada; prefiere el que lleva el UUID.</summary>
    private string? BuscarArchivo(string tipo, string? uuid, DateTime desde)
    {
        var raices = new List<string>();
        void Agregar(string? carpeta)
        {
            if (!string.IsNullOrWhiteSpace(carpeta) && Directory.Exists(carpeta) && !raices.Contains(carpeta, StringComparer.OrdinalIgnoreCase))
            {
                raices.Add(carpeta);
            }
        }

        // Verificado en Comercial real: el SDK deja los archivos en <empresa>\XML_SDK\<serie><folio>.xml y .pdf.
        Agregar(opciones?.CarpetaEntrega);
        if (!string.IsNullOrWhiteSpace(opciones?.RutaEmpresa))
        {
            Agregar(Path.Combine(opciones.RutaEmpresa, "XML_SDK"));
        }

        Agregar(opciones?.RutaEmpresa);
        Agregar(Path.GetTempPath());
        Agregar(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Agregar(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        Agregar(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments));

        var patron = tipo == "pdf" ? "*.pdf" : "*.xml";
        string? mejor = null;
        var mejorFecha = DateTime.MinValue;
        var mejorConUuid = false;
        foreach (var raiz in raices)
        {
            var revisados = 0;
            try
            {
                foreach (var archivo in Directory.EnumerateFiles(raiz, patron, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    if (++revisados > 20000)
                    {
                        break;
                    }

                    var fecha = File.GetLastWriteTimeUtc(archivo);
                    if (fecha < desde)
                    {
                        continue;
                    }

                    var conUuid = !string.IsNullOrWhiteSpace(uuid) && Path.GetFileName(archivo).Contains(uuid, StringComparison.OrdinalIgnoreCase);
                    if ((conUuid && !mejorConUuid) || (conUuid == mejorConUuid && fecha > mejorFecha))
                    {
                        mejor = archivo;
                        mejorFecha = fecha;
                        mejorConUuid = conUuid;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Una carpeta que no se puede leer no impide revisar las demás.
            }
        }

        return mejor;
    }

    private Documento? BuscarPorId(int id)
    {
        var r = traza.Llamar($"fBuscarIdDocumento({id})", () => SdkNative.fBuscarIdDocumento(id));
        if (r == SdkNative.NoEncontrado)
        {
            return null;
        }

        if (r != 0)
        {
            throw new InvalidOperationException($"fBuscarIdDocumento = {r}: {SdkNative.Mensaje(r)}");
        }

        return Leer();
    }

    /// <summary>
    /// Timbra con <c>fEmitirDocumento</c>. La contraseña del certificado se pasa al SDK pero no se escribe en la traza.
    /// Pendiente de probar contra un PAC y un certificado de pruebas: la secuencia sale del análisis de Montek.
    /// </summary>
    private ResultadoDocumento Emitir(Documento documento, string clave)
    {
        var serie = documento.Serie ?? string.Empty;
        var r = traza.Llamar(
            $"fEmitirDocumento('{documento.Concepto}','{serie}',{documento.Folio},'***','')",
            () => SdkNative.fEmitirDocumento(documento.Concepto, serie, documento.Folio, clave, string.Empty));
        if (r != 0)
        {
            return ErrorDocumento("fEmitirDocumento", r);
        }

        var despues = Buscar(documento.Concepto, serie, documento.Folio);
        if (despues is null)
        {
            return new ResultadoDocumento(
                TipoResultadoDocumento.ErrorSdk,
                Mensaje: "El SDK timbró sin error, pero no se pudo volver a leer el documento. Revíselo en CONTPAQi antes de reintentar.");
        }

        return string.IsNullOrWhiteSpace(despues.Uuid)
            ? new ResultadoDocumento(
                TipoResultadoDocumento.Hecho,
                despues,
                Aviso: "El SDK no devolvió error, pero el documento no tiene UUID. Revise que el concepto esté configurado para CFDI.")
            : new ResultadoDocumento(TipoResultadoDocumento.Hecho, despues);
    }

    /// <summary>
    /// Con UUID cancela con <c>fCancelaDocumentoConMotivo</c> (el SAT puede pedir la aceptación del receptor); sin UUID,
    /// con <c>fCancelaDocumento</c>. Pendiente de probar contra un PAC y un certificado de pruebas.
    /// </summary>
    private ResultadoDocumento CancelarDocumento(Documento documento, string? motivo, string? uuidSustituto)
    {
        var serie = documento.Serie ?? string.Empty;
        if (Buscar(documento.Concepto, serie, documento.Folio) is null)
        {
            return new ResultadoDocumento(TipoResultadoDocumento.NoEncontrado, Mensaje: $"No existe el documento {documento.Concepto} {serie} {documento.Folio}.");
        }

        var r = motivo is null
            ? traza.Llamar("fCancelaDocumento()", SdkNative.fCancelaDocumento)
            : traza.Llamar(
                $"fCancelaDocumentoConMotivo('{motivo}','{uuidSustituto}')",
                () => SdkNative.fCancelaDocumentoConMotivo(motivo, uuidSustituto ?? string.Empty));
        if (r != 0)
        {
            return ErrorDocumento(motivo is null ? "fCancelaDocumento" : "fCancelaDocumentoConMotivo", r);
        }

        var despues = Buscar(documento.Concepto, serie, documento.Folio);
        if (despues is null)
        {
            return new ResultadoDocumento(
                TipoResultadoDocumento.ErrorSdk,
                Mensaje: "El SDK canceló sin error, pero no se pudo volver a leer el documento. Revíselo en CONTPAQi.");
        }

        return despues.Cancelado
            ? new ResultadoDocumento(TipoResultadoDocumento.Hecho, despues)
            : new ResultadoDocumento(
                TipoResultadoDocumento.Hecho,
                despues,
                Aviso: "El SDK aceptó la cancelación, pero el documento aún no figura cancelado. Con un CFDI, el SAT puede pedir la aceptación del receptor.");
    }

    private static ResultadoDocumento ErrorDocumento(string funcion, int codigo) =>
        new(TipoResultadoDocumento.ErrorSdk, Mensaje: $"{funcion} = {codigo}: {SdkNative.Mensaje(codigo)}", CodigoSdk: codigo);

    private Documento? Buscar(string concepto, string serie, long folio)
    {
        var f = folio.ToString(CultureInfo.InvariantCulture);
        var r = traza.Llamar($"fBuscarDocumento('{concepto}','{serie}','{f}')", () => SdkNative.fBuscarDocumento(concepto, serie, f));
        if (r == SdkNative.NoEncontrado)
        {
            return null;
        }

        if (r != 0)
        {
            throw new InvalidOperationException($"fBuscarDocumento = {r}: {SdkNative.Mensaje(r)}");
        }

        return Leer();
    }


    private ResultadoCrearDocumento Crear(NuevoDocumento documento)
    {
        // Validaciones previas: fallar aquí no consume folio.
        var cliente = traza.Llamar($"fBuscaCteProv('{documento.ClienteCodigo}')", () => SdkNative.fBuscaCteProv(documento.ClienteCodigo));
        if (cliente != 0)
        {
            return Invalido($"No existe el cliente {documento.ClienteCodigo}.", cliente);
        }

        foreach (var partida in documento.Partidas)
        {
            var p = traza.Llamar($"fBuscaProducto('{partida.ProductoCodigo}')", () => SdkNative.fBuscaProducto(partida.ProductoCodigo));
            if (p != 0)
            {
                return Invalido($"No existe el producto {partida.ProductoCodigo}.", p);
            }
        }

        var concepto = traza.Llamar($"fBuscaConceptoDocto('{documento.Concepto}')", () => SdkNative.fBuscaConceptoDocto(documento.Concepto));
        if (concepto != 0)
        {
            return Invalido($"No existe el concepto {documento.Concepto}.", concepto);
        }

        var estructura = new TDocumento
        {
            aCodConcepto = documento.Concepto,
            aSerie = documento.Serie ?? string.Empty,
            aFecha = (documento.Fecha?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Now).ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            aCodigoCteProv = documento.ClienteCodigo,
            aFolio = 0,
            aNumMoneda = 1,
            aTipoCambio = 1,
            aSistemaOrigen = Sistema,
        };

        var id = 0;
        var e = traza.Llamar(
            $"fAltaDocumento('{documento.Concepto}','{documento.Serie}','{documento.ClienteCodigo}')",
            () => SdkNative.fAltaDocumento(ref id, ref estructura));
        if (e != 0)
        {
            return Error("fAltaDocumento", e);
        }

        try
        {
            // En una empresa real la referencia es opcional: sin ella no se asigna el campo.
            e = string.IsNullOrEmpty(documento.Referencia)
                ? 0
                : traza.Llamar($"fSetDatoDocumento('CREFERENCIA','{documento.Referencia}')", () => SdkNative.fSetDatoDocumento("CREFERENCIA", documento.Referencia));
            if (e != 0)
            {
                Cancelar();
                return Error("fSetDatoDocumento(CREFERENCIA)", e);
            }

            if (documento.FormaPago is { } forma)
            {
                e = traza.Llamar($"fSetDatoDocumento('{CampoFormaPago}','{forma}')", () => SdkNative.fSetDatoDocumento(CampoFormaPago, forma));
                if (e != 0)
                {
                    Cancelar();
                    return Error($"fSetDatoDocumento({CampoFormaPago})", e);
                }
            }

            var indice = 0;
            foreach (var partida in documento.Partidas)
            {
                indice++;
                var movimiento = new TMovimiento
                {
                    aCodProdSer = partida.ProductoCodigo,
                    aUnidades = (double)partida.Unidades,
                    aPrecio = (double)partida.Precio,
                    aCodAlmacen = partida.Almacen ?? string.Empty,
                };

                var idMovimiento = 0;
                e = traza.Llamar(
                    $"fAltaMovimiento(doc={id},'{partida.ProductoCodigo}',x{partida.Unidades})",
                    () => SdkNative.fAltaMovimiento(id, ref idMovimiento, ref movimiento));
                if (e != 0)
                {
                    Cancelar();
                    return Error($"fAltaMovimiento(partida {indice})", e);
                }

                if (partida.Precio > 0)
                {
                    var precio = partida.Precio.ToString(CultureInfo.InvariantCulture);
                    e = traza.Llamar($"fSetDatoMovimiento('CPRECIO','{precio}')", () => SdkNative.fSetDatoMovimiento("CPRECIO", precio));
                    if (e != 0)
                    {
                        Cancelar();
                        return Error($"fSetDatoMovimiento(CPRECIO, partida {indice})", e);
                    }
                }

                // Un impuesto que no viene en la orden, o viene en cero, no se asigna: Comercial lo deja en cero.
                // CPORCENTAJEIMPUESTO4 no es un campo válido del movimiento (error 73 en la prueba real del 2026-11-10);
                // la retención de IVA se manda como CPORCENTAJERETENCION (pendiente de comprobar con un valor distinto de cero).
                foreach (var (campo, porcentaje) in new[]
                {
                    ("CPORCENTAJEIMPUESTO1", partida.IvaPorcentaje),
                    ("CPORCENTAJEIMPUESTO2", partida.IepsPorcentaje),
                    ("CPORCENTAJERETENCION", partida.RetencionIvaPorcentaje),
                })
                {
                    if (porcentaje is not { } valor || valor == 0m)
                    {
                        continue;
                    }

                    var texto = valor.ToString(CultureInfo.InvariantCulture);
                    e = traza.Llamar($"fSetDatoMovimiento('{campo}','{texto}')", () => SdkNative.fSetDatoMovimiento(campo, texto));
                    if (e != 0)
                    {
                        Cancelar();
                        return Error($"fSetDatoMovimiento({campo}, partida {indice})", e);
                    }
                }
            }

            e = traza.Llamar("fGuardaDocumento()", SdkNative.fGuardaDocumento);
            if (e != 0)
            {
                Cancelar();
                return Error("fGuardaDocumento", e);
            }
        }
        catch
        {
            Cancelar();
            throw;
        }

        var r = traza.Llamar($"fBuscarIdDocumento({id})", () => SdkNative.fBuscarIdDocumento(id));
        if (r != 0)
        {
            return new ResultadoCrearDocumento(
                TipoResultadoCrear.ErrorSdk,
                Mensaje: $"El SDK guardó el documento {id} sin error, pero no se pudo leer: fBuscarIdDocumento = {r}: {SdkNative.Mensaje(r)}",
                CodigoSdk: r);
        }

        return new ResultadoCrearDocumento(TipoResultadoCrear.Creado, Leer());
    }

    private void Cancelar() =>
        traza.Llamar("fCancelarModificacionDocumento()", SdkNative.fCancelarModificacionDocumento);

    private static ResultadoCrearDocumento Error(string funcion, int codigo) =>
        new(TipoResultadoCrear.ErrorSdk, Mensaje: $"{funcion} = {codigo}: {SdkNative.Mensaje(codigo)}", CodigoSdk: codigo);

    private static ResultadoCrearDocumento Invalido(string mensaje, int codigo) =>
        new(TipoResultadoCrear.Invalido, Mensaje: mensaje, CodigoSdk: codigo);

    /// <summary>Lee el documento en el que está posicionado el SDK.</summary>
    private Documento Leer()
    {
        // Primero todos los campos del documento; las consultas del UUID y del cliente pueden mover el cursor del SDK.
        var serie = Campo("CSERIEDOCUMENTO");
        var referencia = Campo("CREFERENCIA");
        var id = Entero(Campo("CIDDOCUMENTO"));
        var concepto = Campo("CCODIGOCONCEPTO");
        var folio = (long)Decimal(Campo("CFOLIO"));
        var fecha = Campo("CFECHA");
        var neto = Decimal(Campo("CNETO"));
        var total = Decimal(Campo("CTOTAL"));
        var cancelado = Entero(Campo("CCANCELADO")) != 0;
        var idCliente = Entero(Campo("CIDCLIENTEPROVEEDOR"));

        // fLeeDatoDocumento no tiene CUUIDCFDI ni CCODIGOCLIENTE (error 73): el UUID sale de fDocumentoUUID, como en Montek,
        // y el código del cliente de su id.
        var uuid = UuidDe(concepto, serie, folio);
        var cliente = CodigoDeCliente(idCliente);
        return new Documento(
            Id: id,
            Concepto: concepto,
            Serie: string.IsNullOrWhiteSpace(serie) ? null : serie,
            Folio: folio,
            Fecha: fecha,
            ClienteCodigo: cliente,
            Referencia: string.IsNullOrWhiteSpace(referencia) ? null : referencia,
            Neto: neto,
            Total: total,
            Cancelado: cancelado,
            Uuid: string.IsNullOrWhiteSpace(uuid) ? null : uuid);
    }

    /// <summary>UUID del CFDI con <c>fDocumentoUUID</c>; vacío si el documento no está timbrado.</summary>
    private string UuidDe(string concepto, string serie, long folio)
    {
        var c = new StringBuilder(concepto, 64);
        var s = new StringBuilder(serie, 64);
        var u = new StringBuilder(128);
        var r = traza.Llamar(
            $"fDocumentoUUID('{concepto}','{serie}',{folio})",
            () => SdkNative.fDocumentoUUID(c, s, folio, u),
            valor: () => u.ToString().Trim());
        return r == 0 ? u.ToString().Trim() : string.Empty;
    }

    private string CodigoDeCliente(int idCliente)
    {
        if (idCliente <= 0)
        {
            return string.Empty;
        }

        var r = traza.Llamar($"fBuscaIdCteProv({idCliente})", () => SdkNative.fBuscaIdCteProv(idCliente));
        if (r != 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(128);
        var l = traza.Llamar(
            "fLeeDatoCteProv('CCODIGOCLIENTE')",
            () => SdkNative.fLeeDatoCteProv("CCODIGOCLIENTE", sb, sb.Capacity),
            valor: () => sb.ToString().Trim());
        return l == 0 ? sb.ToString().Trim() : string.Empty;
    }

    private string Campo(string campo)
    {
        var sb = new StringBuilder(512);
        var r = traza.Llamar(
            $"fLeeDatoDocumento('{campo}')",
            () => SdkNative.fLeeDatoDocumento(campo, sb, sb.Capacity),
            valor: () => sb.ToString().Trim());
        return r == 0 ? sb.ToString().Trim() : string.Empty;
    }

    private static int Entero(string texto) =>
        int.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static decimal Decimal(string texto) =>
        decimal.TryParse(texto, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : 0m;
}
