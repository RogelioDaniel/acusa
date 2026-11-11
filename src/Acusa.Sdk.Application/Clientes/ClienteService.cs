using Acusa.Sdk.Application.Sesion;

namespace Acusa.Sdk.Application.Clientes;

/// <summary>Casos de uso de clientes. No conoce el SDK: depende solo de <see cref="IClienteGateway"/>.</summary>
public sealed class ClienteService(IClienteGateway gateway, ISesionSdk sesion, PoliticaEscritura politica)
{
    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public Task<Cliente?> ObtenerAsync(string codigo, CancellationToken ct)
    {
        ExigirSesion();
        return gateway.BuscarAsync(PoliticaEscritura.NormalizarCodigo(codigo), ct);
    }

    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public async Task<ResultadoCrear> CrearAsync(NuevoCliente solicitud, CancellationToken ct)
    {
        var cliente = new NuevoCliente(
            PoliticaEscritura.NormalizarCodigo(solicitud.Codigo),
            (solicitud.RazonSocial ?? string.Empty).Trim(),
            Limpio(solicitud.Rfc)?.ToUpperInvariant(),
            solicitud.Tipo,
            solicitud.LimiteCredito);

        var motivo = politica.Validar(cliente);
        if (motivo is not null)
        {
            return new ResultadoCrear(TipoResultadoCrear.Invalido, Mensaje: motivo);
        }

        ExigirSesion();
        return await gateway.CrearAsync(cliente, ct);
    }

    /// <summary>Cambia datos de un cliente de prueba que ya existe (RFC, razón social, régimen, uso de CFDI, correo).</summary>
    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public async Task<ResultadoActualizar> ActualizarAsync(string codigo, CambiosCliente solicitud, CancellationToken ct)
    {
        codigo = PoliticaEscritura.NormalizarCodigo(codigo);
        var cambios = new CambiosCliente(
            Limpio(solicitud.RazonSocial),
            Limpio(solicitud.Rfc)?.ToUpperInvariant(),
            Limpio(solicitud.RegimenFiscal),
            Limpio(solicitud.UsoCfdi)?.ToUpperInvariant(),
            Limpio(solicitud.Email),
            solicitud.LimiteCredito);

        var motivo = politica.Validar(codigo, cambios);
        if (motivo is not null)
        {
            return new ResultadoActualizar(TipoResultadoActualizar.Invalido, Mensaje: motivo);
        }

        ExigirSesion();
        return await gateway.ActualizarAsync(codigo, cambios, ct);
    }

    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public Task<Direccion?> ObtenerDireccionAsync(string codigoCliente, int tipo, CancellationToken ct)
    {
        ExigirSesion();
        return gateway.BuscarDireccionAsync(PoliticaEscritura.NormalizarCodigo(codigoCliente), tipo, ct);
    }

    /// <summary>Crea o completa la dirección (fiscal o de envío) de un cliente de prueba.</summary>
    /// <exception cref="SdkNoListoException">La sesión del SDK no está iniciada.</exception>
    public async Task<ResultadoDireccion> GuardarDireccionAsync(Direccion solicitud, CancellationToken ct)
    {
        var d = new Direccion(
            PoliticaEscritura.NormalizarCodigo(solicitud.CodigoCliente),
            solicitud.Tipo,
            Limpio(solicitud.Calle), Limpio(solicitud.NumeroExterior), Limpio(solicitud.NumeroInterior), Limpio(solicitud.Colonia),
            (solicitud.CodigoPostal ?? string.Empty).Trim(),
            Limpio(solicitud.Municipio), Limpio(solicitud.Ciudad), Limpio(solicitud.Estado), Limpio(solicitud.Pais));

        var motivo = politica.Validar(d);
        if (motivo is not null)
        {
            return new ResultadoDireccion(TipoResultadoDireccion.Invalido, Mensaje: motivo);
        }

        ExigirSesion();
        return await gateway.GuardarDireccionAsync(d, ct);
    }

    private static string? Limpio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    private void ExigirSesion()
    {
        var estado = sesion.Estado.Estado;
        if (estado != EstadoSesion.Lista)
        {
            throw new SdkNoListoException(estado);
        }
    }
}
