using System.Text.RegularExpressions;

namespace Acusa.Sdk.Application.Clientes;

/// <summary>
/// Reglas para escribir en la empresa. La empresa es producción: solo se crean registros
/// marcados como prueba y no existe ninguna operación de borrado ni de modificación.
/// </summary>
/// <param name="Permitida">Si es <c>false</c>, toda escritura se rechaza.</param>
/// <param name="Prefijo">Prefijo obligatorio del código y de la razón social.</param>
/// <param name="PermitidaDocumentos">
/// Candado aparte para documentos: crear uno consume un folio del concepto y un folio no se reutiliza.
/// Requiere además <paramref name="Permitida"/>.
/// </param>
/// <param name="PermitidaTimbrado">
/// Candado aparte para timbrar y cancelar: timbrar gasta un timbre del PAC y cancelar notifica al SAT; ninguna de las
/// dos se deshace. Requiere además <paramref name="Permitida"/> y <paramref name="PermitidaDocumentos"/>.
/// </param>
/// <param name="ClientesPermitidos">
/// Códigos de clientes que ya existen en Comercial y que, por excepción, pueden recibir un documento aunque su código no
/// empiece con el prefijo (un receptor real para probar el timbrado). Solo vale para el cliente del documento: los
/// productos, la referencia y todo lo que se crea siguen exigiendo el prefijo. Vacío por defecto.
/// </param>
public sealed partial record PoliticaEscritura(
    bool Permitida,
    string Prefijo = "PRUEBA",
    bool PermitidaDocumentos = false,
    bool PermitidaTimbrado = false,
    IReadOnlyCollection<string>? ClientesPermitidos = null,
    Func<bool>? EsEmpresaReal = null)
{
    /// <summary>
    /// Verdadero cuando la empresa abierta en este momento figura en <c>Contpaqi:EmpresasReales</c>: ahí se trabaja con datos
    /// reales y no se exige el prefijo de prueba. Todo lo demás (escritura, documentos, timbrado, formatos) sigue igual.
    /// </summary>
    private bool ModoReal => EsEmpresaReal?.Invoke() == true;

    public const int LongitudMaximaCodigo = 20;
    public const int LongitudMaximaRazonSocial = 100;
    public const int LongitudMaximaNombreProducto = 60;
    public const int LongitudMaximaDescripcionProducto = 255;
    public const int LongitudMaximaUnidad = 30;
    public const int LongitudMaximaReferencia = 20;
    public const int LongitudMaximaConcepto = 30;
    public const int LongitudMaximaSerie = 11;
    public const int MaximoPartidas = 50;

    /// <summary>Código en mayúsculas y sin espacios en los extremos.</summary>
    public static string NormalizarCodigo(string? codigo) => (codigo ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Devuelve el motivo del rechazo, o <c>null</c> si el alta es válida.</summary>
    public string? Validar(NuevoCliente cliente)
    {
        if (!Permitida)
        {
            return "La escritura está deshabilitada. Se activa con Contpaqi:PermitirEscritura.";
        }

        var codigo = NormalizarCodigo(cliente.Codigo);
        if (codigo.Length == 0 || codigo.Length > LongitudMaximaCodigo || !CodigoValido().IsMatch(codigo))
        {
            return $"El código debe tener letras y números, sin guiones ni espacios, hasta {LongitudMaximaCodigo} caracteres.";
        }

        if (!ModoReal && !codigo.StartsWith(Prefijo, StringComparison.Ordinal))
        {
            return $"El código debe empezar con {Prefijo}: en esta etapa solo se crean registros de prueba.";
        }

        var razon = (cliente.RazonSocial ?? string.Empty).Trim();
        if (razon.Length == 0 || razon.Length > LongitudMaximaRazonSocial)
        {
            return $"La razón social es obligatoria y admite hasta {LongitudMaximaRazonSocial} caracteres.";
        }

        if (!ModoReal && !razon.StartsWith(Prefijo, StringComparison.OrdinalIgnoreCase))
        {
            return $"La razón social debe empezar con {Prefijo} para identificar el registro como prueba.";
        }

        if (cliente.Rfc is { Length: > 0 } rfc && !RfcValido().IsMatch(rfc))
        {
            return "El RFC no tiene un formato válido (12 caracteres para persona moral, 13 para persona física).";
        }

        if (cliente.Tipo is < 1 or > 3)
        {
            return "El tipo de cliente es 1 (cliente), 2 (proveedor) o 3 (cliente y proveedor).";
        }

        if (cliente.LimiteCredito is < 0)
        {
            return "El límite de crédito no puede ser negativo.";
        }

        return null;
    }

    /// <summary>Devuelve el motivo del rechazo, o <c>null</c> si el alta del producto es válida.</summary>
    public string? Validar(Productos.NuevoProducto producto)
    {
        if (!Permitida)
        {
            return "La escritura está deshabilitada. Se activa con Contpaqi:PermitirEscritura.";
        }

        var codigo = NormalizarCodigo(producto.Codigo);
        var motivoCodigo = ValidarCodigoDePrueba(codigo, "código");
        if (motivoCodigo is not null)
        {
            return motivoCodigo;
        }

        var nombre = (producto.Nombre ?? string.Empty).Trim();
        if (nombre.Length == 0 || nombre.Length > LongitudMaximaNombreProducto)
        {
            return $"El nombre es obligatorio y admite hasta {LongitudMaximaNombreProducto} caracteres.";
        }

        if (!ModoReal && !nombre.StartsWith(Prefijo, StringComparison.OrdinalIgnoreCase))
        {
            return $"El nombre debe empezar con {Prefijo} para identificar el registro como prueba.";
        }

        var unidad = (producto.UnidadBase ?? string.Empty).Trim();
        if (unidad.Length == 0 || unidad.Length > LongitudMaximaUnidad)
        {
            return $"La unidad base es obligatoria (nombre de una unidad existente, hasta {LongitudMaximaUnidad} caracteres).";
        }

        if (producto.Precio1 < 0)
        {
            return "El precio no puede ser negativo.";
        }

        if (producto.Descripcion is { Length: > LongitudMaximaDescripcionProducto })
        {
            return $"La descripción admite hasta {LongitudMaximaDescripcionProducto} caracteres.";
        }

        if (producto.Tipo is < 1 or > 3)
        {
            return "El tipo de producto es 1, 2 o 3.";
        }

        return ValidarTextosExtra(producto.TextosExtra) ?? ValidarClaveSat(producto.ClaveSat);
    }

    public const int MaximoTextosExtra = 3;
    public const int LongitudMaximaTextoExtra = 255;

    private static string? ValidarTextosExtra(IReadOnlyList<string>? textos)
    {
        if (textos is null)
        {
            return null;
        }

        if (textos.Count > MaximoTextosExtra)
        {
            return $"Un producto admite hasta {MaximoTextosExtra} textos extra.";
        }

        return textos.Any(t => t.Length > LongitudMaximaTextoExtra)
            ? $"Cada texto extra admite hasta {LongitudMaximaTextoExtra} caracteres."
            : null;
    }

    private static string? ValidarClaveSat(string? clave) =>
        string.IsNullOrWhiteSpace(clave) || ClaveSatValida().IsMatch(clave.Trim())
            ? null
            : "La clave SAT del producto o servicio son 8 dígitos, por ejemplo 01010101 o 43232408.";

    /// <summary>Valida los cambios a un producto. Solo se modifican productos de prueba (por su código).</summary>
    public string? Validar(string codigoProducto, Productos.CambiosProducto cambios)
    {
        if (!Permitida)
        {
            return "La escritura está deshabilitada. Se activa con Contpaqi:PermitirEscritura.";
        }

        var motivoCodigo = ValidarCodigoDePrueba(NormalizarCodigo(codigoProducto), "código de producto");
        if (motivoCodigo is not null)
        {
            return motivoCodigo;
        }

        if (cambios.EstaVacio)
        {
            return "No hay nada que cambiar: indique nombre, descripcion, precio1 o claveSat.";
        }

        var nombre = (cambios.Nombre ?? string.Empty).Trim();
        if (nombre.Length > 0 && (nombre.Length > LongitudMaximaNombreProducto || (!ModoReal && !nombre.StartsWith(Prefijo, StringComparison.OrdinalIgnoreCase))))
        {
            return $"El nombre admite hasta {LongitudMaximaNombreProducto} caracteres y debe empezar con {Prefijo}.";
        }

        if (cambios.Descripcion is { Length: > LongitudMaximaDescripcionProducto })
        {
            return $"La descripción admite hasta {LongitudMaximaDescripcionProducto} caracteres.";
        }

        if (cambios.Precio1 is < 0)
        {
            return "El precio no puede ser negativo.";
        }

        return ValidarTextosExtra(cambios.TextosExtra) ?? ValidarClaveSat(cambios.ClaveSat);
    }

    public const int LongitudMaximaCalle = 100;

    /// <summary>
    /// Valida el alta o cambio de una dirección de un cliente de prueba. El código postal es obligatorio: CFDI 4.0 lo exige
    /// en el receptor. Tipo 0 es la fiscal y 1 la de envío.
    /// </summary>
    public string? Validar(Clientes.Direccion d)
    {
        if (!Permitida)
        {
            return "La escritura está deshabilitada. Se activa con Contpaqi:PermitirEscritura.";
        }

        var motivoCodigo = ValidarCodigoDePrueba(NormalizarCodigo(d.CodigoCliente), "código de cliente");
        if (motivoCodigo is not null)
        {
            return motivoCodigo;
        }

        if (d.Tipo is not (0 or 1))
        {
            return "El tipo de dirección es fiscal (0) o envío (1).";
        }

        if (!CodigoPostalValido().IsMatch((d.CodigoPostal ?? string.Empty).Trim()))
        {
            return "El código postal son 5 dígitos.";
        }

        foreach (var (valor, etiqueta) in new[]
        {
            (d.Calle, "calle"), (d.NumeroExterior, "número exterior"), (d.NumeroInterior, "número interior"), (d.Colonia, "colonia"),
            (d.Municipio, "municipio"), (d.Ciudad, "ciudad"), (d.Estado, "estado"), (d.Pais, "país"),
        })
        {
            if (valor is { Length: > LongitudMaximaCalle })
            {
                return $"El campo {etiqueta} admite hasta {LongitudMaximaCalle} caracteres.";
            }
        }

        return null;
    }

    [GeneratedRegex(@"^\d{8}$")]
    private static partial Regex ClaveSatValida();

    [GeneratedRegex(@"^\d{5}$")]
    private static partial Regex CodigoPostalValido();

    /// <summary>Devuelve el motivo del rechazo, o <c>null</c> si el borrador es válido.</summary>
    public string? Validar(Documentos.NuevoDocumento documento)
    {
        if (!Permitida)
        {
            return "La escritura está deshabilitada. Se activa con Contpaqi:PermitirEscritura.";
        }

        if (!PermitidaDocumentos)
        {
            return "Los documentos están deshabilitados: crear uno consume un folio que no se reutiliza. " +
                   "Se activa con Contpaqi:PermitirDocumentos.";
        }

        var concepto = (documento.Concepto ?? string.Empty).Trim();
        if (concepto.Length == 0 || concepto.Length > LongitudMaximaConcepto)
        {
            return $"El concepto es obligatorio (código del concepto de documento, hasta {LongitudMaximaConcepto} caracteres).";
        }

        if ((documento.Serie ?? string.Empty).Trim().Length > LongitudMaximaSerie)
        {
            return $"La serie admite hasta {LongitudMaximaSerie} caracteres.";
        }

        var codigoCliente = NormalizarCodigo(documento.ClienteCodigo);
        var excepcion = ClientesPermitidos?.Any(c => string.Equals(NormalizarCodigo(c), codigoCliente, StringComparison.Ordinal)) == true;
        var motivoCliente = excepcion ? null : ValidarCodigoDePrueba(codigoCliente, "código de cliente");
        if (motivoCliente is not null)
        {
            return motivoCliente;
        }

        var referencia = (documento.Referencia ?? string.Empty).Trim();
        if (referencia.Length > LongitudMaximaReferencia || (referencia.Length == 0 && !ModoReal))
        {
            return $"La referencia es obligatoria y admite hasta {LongitudMaximaReferencia} caracteres.";
        }

        if (!ModoReal && !referencia.StartsWith(Prefijo, StringComparison.OrdinalIgnoreCase))
        {
            return $"La referencia debe empezar con {Prefijo} para identificar el documento como prueba.";
        }

        var partidas = documento.Partidas ?? [];
        if (partidas.Count is 0 or > MaximoPartidas)
        {
            return $"El documento necesita entre 1 y {MaximoPartidas} partidas.";
        }

        for (var i = 0; i < partidas.Count; i++)
        {
            var p = partidas[i];
            var motivoProducto = ValidarCodigoDePrueba(NormalizarCodigo(p.ProductoCodigo), $"código de producto de la partida {i + 1}");
            if (motivoProducto is not null)
            {
                return motivoProducto;
            }

            if (p.Unidades <= 0)
            {
                return $"Las unidades de la partida {i + 1} deben ser mayores que cero.";
            }

            if (p.Precio < 0)
            {
                return $"El precio de la partida {i + 1} no puede ser negativo.";
            }

            foreach (var (nombre, valor) in new[]
            {
                ("IVA", p.IvaPorcentaje), ("retención de IVA", p.RetencionIvaPorcentaje), ("IEPS", p.IepsPorcentaje),
            })
            {
                if (valor is < 0 or > 100)
                {
                    return $"El porcentaje de {nombre} de la partida {i + 1} debe estar entre 0 y 100.";
                }
            }
        }

        var forma = documento.FormaPago;
        if (forma is not null && (forma.Length != 2 || !forma.All(char.IsAsciiDigit)))
        {
            return "La forma de pago es la clave SAT de dos dígitos, por ejemplo 01 o 99.";
        }

        var uso = documento.UsoCfdi;
        if (uso is not null && (uso.Length is < 3 or > 4 || !uso.All(char.IsAsciiLetterOrDigit)))
        {
            return "El uso del CFDI es la clave SAT, por ejemplo G03.";
        }

        if (documento.Fecha is { } fecha && Math.Abs(fecha.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber) > 400)
        {
            return "La fecha del documento debe estar a menos de 400 días de hoy.";
        }

        return null;
    }

    public const int LongitudMaximaEmail = 100;

    /// <summary>
    /// Valida los cambios a un cliente. Solo se modifican clientes de prueba (por su código). La razón social puede ser
    /// libre: un cliente de prueba que reciba un CFDI necesita el nombre exacto que corresponde a su RFC.
    /// Devuelve el motivo del rechazo, o <c>null</c> si es válido.
    /// </summary>
    public string? Validar(string codigoCliente, Clientes.CambiosCliente cambios)
    {
        if (!Permitida)
        {
            return "La escritura está deshabilitada. Se activa con Contpaqi:PermitirEscritura.";
        }

        var motivoCodigo = ValidarCodigoDePrueba(NormalizarCodigo(codigoCliente), "código de cliente");
        if (motivoCodigo is not null)
        {
            return motivoCodigo;
        }

        if (cambios.EstaVacio)
        {
            return "No hay nada que cambiar: indique razonSocial, rfc, regimenFiscal, usoCfdi, email o limiteCredito.";
        }

        if (cambios.LimiteCredito is < 0)
        {
            return "El límite de crédito no puede ser negativo.";
        }

        var razon = (cambios.RazonSocial ?? string.Empty).Trim();
        if (razon.Length > LongitudMaximaRazonSocial)
        {
            return $"La razón social admite hasta {LongitudMaximaRazonSocial} caracteres.";
        }

        var rfc = (cambios.Rfc ?? string.Empty).Trim().ToUpperInvariant();
        if (rfc.Length > 0 && !RfcValido().IsMatch(rfc))
        {
            return "El RFC no tiene un formato válido (12 caracteres para persona moral, 13 para persona física).";
        }

        var regimen = (cambios.RegimenFiscal ?? string.Empty).Trim();
        if (regimen.Length > 0 && !RegimenValido().IsMatch(regimen))
        {
            return "El régimen fiscal es la clave de 3 dígitos del SAT, por ejemplo 601.";
        }

        var uso = (cambios.UsoCfdi ?? string.Empty).Trim().ToUpperInvariant();
        if (uso.Length > 0 && !UsoValido().IsMatch(uso))
        {
            return "El uso de CFDI es la clave del SAT, por ejemplo G03 o S01.";
        }

        var email = (cambios.Email ?? string.Empty).Trim();
        if (email.Length > 0 && (email.Length > LongitudMaximaEmail || !EmailValido().IsMatch(email)))
        {
            return $"El correo no es válido (hasta {LongitudMaximaEmail} caracteres).";
        }

        return null;
    }

    [GeneratedRegex(@"^[A-ZÑ&]{3,4}\d{6}[A-Z0-9]{3}$")]
    private static partial Regex RfcValido();

    [GeneratedRegex(@"^\d{3}$")]
    private static partial Regex RegimenValido();

    [GeneratedRegex(@"^[A-Z0-9]{3,4}$")]
    private static partial Regex UsoValido();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailValido();

    /// <summary>Candados que deben estar abiertos para timbrar o cancelar. <c>null</c> si lo están.</summary>
    public string? ValidarTimbrado()
    {
        if (!Permitida)
        {
            return "La escritura está deshabilitada. Se activa con Contpaqi:PermitirEscritura.";
        }

        if (!PermitidaDocumentos)
        {
            return "Los documentos están deshabilitados. Se activa con Contpaqi:PermitirDocumentos.";
        }

        return PermitidaTimbrado
            ? null
            : "Timbrar y cancelar están deshabilitados: gastan un timbre del PAC y notifican al SAT, y no se deshacen. " +
              "Se activa con Contpaqi:PermitirTimbrado.";
    }

    /// <summary>Valida cómo se pide localizar el documento. <c>null</c> si es válido.</summary>
    public static string? ValidarReferencia(Documentos.ReferenciaDocumento r)
    {
        if (r.Id is { } id)
        {
            return id > 0 ? null : "El documentoId debe ser mayor que cero.";
        }

        var concepto = (r.Concepto ?? string.Empty).Trim();
        if (concepto.Length == 0 || concepto.Length > LongitudMaximaConcepto)
        {
            return "Indique documentoId, o bien concepto, serie y folio del documento.";
        }

        if ((r.Serie ?? string.Empty).Trim().Length > LongitudMaximaSerie)
        {
            return $"La serie admite hasta {LongitudMaximaSerie} caracteres.";
        }

        return r.Folio > 0 ? null : "El folio debe ser mayor que cero.";
    }

    /// <summary>Solo se timbra y se cancela lo que Acusa marcó como prueba. <c>null</c> si el documento lo es.</summary>
    public string? ValidarDocumentoDePrueba(Documentos.Documento documento) =>
        ModoReal || (documento.Referencia ?? string.Empty).StartsWith(Prefijo, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Solo se timbran y se cancelan documentos de prueba: su referencia debe empezar con {Prefijo}.";

    /// <summary>
    /// Valida el motivo de cancelación del SAT. El motivo 01 exige el UUID del documento que lo sustituye; los demás
    /// no lo admiten. <c>null</c> si es válido.
    /// </summary>
    public static string? ValidarMotivoCancelacion(string? motivo, string? uuidSustituto)
    {
        if (!Documentos.MotivosCancelacion.Valido(motivo))
        {
            return "El motivo de cancelación debe ser 01, 02, 03 o 04 (catálogo del SAT).";
        }

        var uuid = (uuidSustituto ?? string.Empty).Trim();
        if (motivo == Documentos.MotivosCancelacion.ConRelacion)
        {
            return Guid.TryParse(uuid, out _) && uuid.Length == 36
                ? null
                : "El motivo 01 requiere uuidSustituto: el UUID del documento que sustituye al cancelado.";
        }

        return uuid.Length == 0 ? null : "uuidSustituto solo aplica al motivo 01.";
    }

    private string? ValidarCodigoDePrueba(string codigo, string etiqueta)
    {
        if (codigo.Length == 0 || codigo.Length > LongitudMaximaCodigo || !CodigoValido().IsMatch(codigo))
        {
            return $"El {etiqueta} debe tener letras y números, sin guiones ni espacios, hasta {LongitudMaximaCodigo} caracteres.";
        }

        if (!ModoReal && !codigo.StartsWith(Prefijo, StringComparison.Ordinal))
        {
            return $"El {etiqueta} debe empezar con {Prefijo}: en esta etapa solo se usan registros de prueba.";
        }

        return null;
    }

    [GeneratedRegex("^[A-Z0-9]+$")]
    private static partial Regex CodigoValido();
}
