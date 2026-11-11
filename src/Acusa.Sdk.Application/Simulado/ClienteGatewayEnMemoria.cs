using System.Collections.Concurrent;
using System.Globalization;
using Acusa.Sdk.Application.Clientes;

namespace Acusa.Sdk.Application.Simulado;

/// <summary>Clientes en memoria con las mismas reglas observadas en CONTPAQi (lista de precios 1, tipo 1, moneda 1).</summary>
public sealed class ClienteGatewayEnMemoria(int listaPrecios = 1) : IClienteGateway
{
    private readonly ConcurrentDictionary<string, Cliente> _clientes = new(StringComparer.Ordinal);

    public Task<Cliente?> BuscarAsync(string codigo, CancellationToken ct) =>
        Task.FromResult(_clientes.TryGetValue(codigo, out var c) ? c : null);

    private readonly ConcurrentDictionary<(string Codigo, int Tipo), Direccion> _direcciones = new();

    public Task<Direccion?> BuscarDireccionAsync(string codigoCliente, int tipo, CancellationToken ct) =>
        Task.FromResult(_direcciones.TryGetValue((codigoCliente, tipo), out var d) ? d : null);

    public Task<ResultadoDireccion> GuardarDireccionAsync(Direccion direccion, CancellationToken ct)
    {
        if (!_clientes.ContainsKey(direccion.CodigoCliente))
        {
            return Task.FromResult(new ResultadoDireccion(TipoResultadoDireccion.ClienteNoEncontrado, Mensaje: $"No existe el cliente {direccion.CodigoCliente}."));
        }

        var guardada = _direcciones.AddOrUpdate(
            (direccion.CodigoCliente, direccion.Tipo),
            direccion,
            (_, actual) => actual with
            {
                Calle = direccion.Calle ?? actual.Calle,
                NumeroExterior = direccion.NumeroExterior ?? actual.NumeroExterior,
                NumeroInterior = direccion.NumeroInterior ?? actual.NumeroInterior,
                Colonia = direccion.Colonia ?? actual.Colonia,
                CodigoPostal = direccion.CodigoPostal,
                Municipio = direccion.Municipio ?? actual.Municipio,
                Ciudad = direccion.Ciudad ?? actual.Ciudad,
                Estado = direccion.Estado ?? actual.Estado,
                Pais = direccion.Pais ?? actual.Pais,
            });
        return Task.FromResult(new ResultadoDireccion(TipoResultadoDireccion.Guardada, guardada));
    }

    public Task<ResultadoActualizar> ActualizarAsync(string codigo, CambiosCliente cambios, CancellationToken ct)
    {
        if (!_clientes.TryGetValue(codigo, out var actual))
        {
            return Task.FromResult(new ResultadoActualizar(TipoResultadoActualizar.NoEncontrado, Mensaje: $"No existe el cliente {codigo}."));
        }

        var nuevo = actual with
        {
            RazonSocial = string.IsNullOrWhiteSpace(cambios.RazonSocial) ? actual.RazonSocial : cambios.RazonSocial,
            Rfc = string.IsNullOrWhiteSpace(cambios.Rfc) ? actual.Rfc : cambios.Rfc,
            RegimenFiscal = string.IsNullOrWhiteSpace(cambios.RegimenFiscal) ? actual.RegimenFiscal : cambios.RegimenFiscal,
            UsoCfdi = string.IsNullOrWhiteSpace(cambios.UsoCfdi) ? actual.UsoCfdi : cambios.UsoCfdi,
            Email = string.IsNullOrWhiteSpace(cambios.Email) ? actual.Email : cambios.Email,
        };
        _clientes[codigo] = nuevo;
        return Task.FromResult(new ResultadoActualizar(TipoResultadoActualizar.Actualizado, nuevo));
    }

    public Task<ResultadoCrear> CrearAsync(NuevoCliente cliente, CancellationToken ct)
    {
        var nuevo = new Cliente(
            cliente.Codigo,
            cliente.RazonSocial,
            Rfc: string.IsNullOrWhiteSpace(cliente.Rfc) ? null : cliente.Rfc,
            FechaAlta: DateTime.Now.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) + " 00:00:00:000",
            Tipo: cliente.Tipo ?? 1,
            IdMoneda: 1,
            ListaPrecios: listaPrecios,
            Estatus: 1);

        return Task.FromResult(_clientes.TryAdd(cliente.Codigo, nuevo)
            ? new ResultadoCrear(TipoResultadoCrear.Creado, nuevo)
            : new ResultadoCrear(TipoResultadoCrear.YaExiste, Mensaje: $"Ya existe el cliente {cliente.Codigo}."));
    }
}
