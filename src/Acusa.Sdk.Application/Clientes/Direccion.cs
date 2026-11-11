namespace Acusa.Sdk.Application.Clientes;

/// <summary>
/// Dirección de un cliente (tabla admDomicilios). La fiscal (tipo 0) aporta el código postal del receptor de un CFDI 4.0.
/// </summary>
/// <param name="CodigoCliente">Código del cliente dueño de la dirección.</param>
/// <param name="Tipo">CTIPODIRECCION: 0 fiscal, 1 envío.</param>
/// <param name="CodigoPostal">CCODIGOPOSTAL, 5 dígitos.</param>
public sealed record Direccion(
    string CodigoCliente,
    int Tipo,
    string? Calle,
    string? NumeroExterior,
    string? NumeroInterior,
    string? Colonia,
    string CodigoPostal,
    string? Municipio,
    string? Ciudad,
    string? Estado,
    string? Pais);

public enum TipoResultadoDireccion
{
    Guardada,
    Invalido,
    ClienteNoEncontrado,
    ErrorSdk,
}

/// <summary>Resultado de <see cref="ClienteService.GuardarDireccionAsync"/>.</summary>
public sealed record ResultadoDireccion(
    TipoResultadoDireccion Tipo,
    Direccion? Direccion = null,
    string? Mensaje = null,
    int? CodigoSdk = null);
