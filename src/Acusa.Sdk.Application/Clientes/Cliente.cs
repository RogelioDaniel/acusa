namespace Acusa.Sdk.Application.Clientes;

/// <summary>Cliente leído de CONTPAQi (tabla admClientes).</summary>
/// <param name="Codigo">CCODIGOCLIENTE.</param>
/// <param name="RazonSocial">CRAZONSOCIAL.</param>
/// <param name="Rfc">CRFC, vacío si el cliente se guardó sin RFC.</param>
/// <param name="FechaAlta">CFECHAALTA tal como la devuelve el SDK (mes/día/año).</param>
/// <param name="Tipo">CTIPOCLIENTE (1 = cliente).</param>
/// <param name="IdMoneda">CIDMONEDA.</param>
/// <param name="ListaPrecios">CLISTAPRECIOCLIENTE.</param>
/// <param name="Estatus">CESTATUS.</param>
/// <param name="RegimenFiscal">CREGIMFISC (clave del SAT, por ejemplo 601), vacío si no se ha capturado.</param>
/// <param name="UsoCfdi">CUSOCFDI (clave del SAT, por ejemplo G03), vacío si no se ha capturado.</param>
/// <param name="Email">CEMAIL1.</param>
public sealed record Cliente(
    string Codigo,
    string RazonSocial,
    string? Rfc,
    string FechaAlta,
    int Tipo,
    int IdMoneda,
    int ListaPrecios,
    int Estatus,
    string? RegimenFiscal = null,
    string? UsoCfdi = null,
    string? Email = null);

/// <summary>
/// Datos que <c>cliente.actualizar</c> puede cambiar. Un campo vacío o ausente no se toca; no hay forma de borrar un dato.
/// </summary>
public sealed record CambiosCliente(string? RazonSocial, string? Rfc, string? RegimenFiscal, string? UsoCfdi, string? Email, decimal? LimiteCredito = null)
{
    public bool EstaVacio =>
        string.IsNullOrWhiteSpace(RazonSocial) && string.IsNullOrWhiteSpace(Rfc) && string.IsNullOrWhiteSpace(RegimenFiscal) &&
        string.IsNullOrWhiteSpace(UsoCfdi) && string.IsNullOrWhiteSpace(Email) && LimiteCredito is null;
}

public enum TipoResultadoActualizar
{
    Actualizado,
    Invalido,
    NoEncontrado,
    ErrorSdk,
}

/// <summary>Resultado de <see cref="ClienteService.ActualizarAsync"/>.</summary>
public sealed record ResultadoActualizar(
    TipoResultadoActualizar Tipo,
    Cliente? Cliente = null,
    string? Mensaje = null,
    int? CodigoSdk = null);

/// <summary>Datos para dar de alta un cliente. Solo el código y la razón social son obligatorios.</summary>
/// <param name="Rfc">RFC del cliente (12 o 13 caracteres); vacío no lo asigna.</param>
/// <param name="Tipo">CTIPOCLIENTE: 1 cliente, 2 proveedor, 3 cliente y proveedor; vacío usa 1.</param>
/// <param name="LimiteCredito">CLIMITECREDITOCLIENTE; vacío no lo asigna.</param>
public sealed record NuevoCliente(string Codigo, string RazonSocial, string? Rfc = null, int? Tipo = null, decimal? LimiteCredito = null);

public enum TipoResultadoCrear
{
    Creado,
    Invalido,
    YaExiste,
    ErrorSdk,
}

/// <summary>Resultado de <see cref="ClienteService.CrearAsync"/>.</summary>
/// <param name="Tipo">Qué pasó.</param>
/// <param name="Cliente">Cliente leído de CONTPAQi tras guardar; solo si <see cref="TipoResultadoCrear.Creado"/>.</param>
/// <param name="Mensaje">Explicación para el usuario.</param>
/// <param name="CodigoSdk">Código de error del SDK, si aplica.</param>
public sealed record ResultadoCrear(
    TipoResultadoCrear Tipo,
    Cliente? Cliente = null,
    string? Mensaje = null,
    int? CodigoSdk = null);
