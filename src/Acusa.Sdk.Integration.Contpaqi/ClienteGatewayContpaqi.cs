using System.Globalization;
using System.Text;
using Acusa.Sdk.Application;
using Acusa.Sdk.Application.Clientes;
using Acusa.Sdk.Integration.Contpaqi.Native;

namespace Acusa.Sdk.Integration.Contpaqi;

/// <summary>Clientes sobre el SDK real. Busca, crea y actualiza campos con valor; no borra.</summary>
public sealed class ClienteGatewayContpaqi(SesionContpaqi sesion, OpcionesContpaqi opciones, TrazaSdk traza) : IClienteGateway
{
    public Task<Cliente?> BuscarAsync(string codigo, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Buscar(codigo));

    public Task<ResultadoCrear> CrearAsync(NuevoCliente cliente, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Crear(cliente));

    private Cliente? Buscar(string codigo)
    {
        var r = traza.Llamar($"fBuscaCteProv('{codigo}')", () => SdkNative.fBuscaCteProv(codigo));
        if (r == SdkNative.NoEncontrado)
        {
            return null;
        }

        if (r != 0)
        {
            throw new InvalidOperationException($"fBuscaCteProv = {r}: {SdkNative.Mensaje(r)}");
        }

        var rfc = Leer("CRFC");
        return new Cliente(
            Codigo: Leer("CCODIGOCLIENTE"),
            RazonSocial: Leer("CRAZONSOCIAL"),
            Rfc: string.IsNullOrWhiteSpace(rfc) ? null : rfc,
            FechaAlta: Leer("CFECHAALTA"),
            Tipo: Entero(Leer("CTIPOCLIENTE")),
            IdMoneda: Entero(Leer("CIDMONEDA")),
            ListaPrecios: Entero(Leer("CLISTAPRECIOCLIENTE")),
            Estatus: Entero(Leer("CESTATUS")),
            RegimenFiscal: Opcional(Leer("CREGIMFISC")),
            UsoCfdi: Opcional(Leer("CUSOCFDI")),
            Email: Opcional(Leer("CEMAIL1")));
    }

    public Task<Direccion?> BuscarDireccionAsync(string codigoCliente, int tipo, CancellationToken ct) =>
        sesion.EjecutarAsync(() => BuscarDireccion(codigoCliente, tipo));

    public Task<ResultadoDireccion> GuardarDireccionAsync(Direccion direccion, CancellationToken ct) =>
        sesion.EjecutarAsync(() => GuardarDireccion(direccion));

    private Direccion? BuscarDireccion(string codigo, int tipo)
    {
        // Cualquier código distinto de cero se toma como "el cliente no tiene esa dirección".
        var r = traza.Llamar($"fBuscaDireccionCteProv('{codigo}',{tipo})", () => SdkNative.fBuscaDireccionCteProv(codigo, (byte)tipo));
        return r == 0 ? LeerDireccion(codigo, tipo) : null;
    }

    private Direccion LeerDireccion(string codigo, int tipo) =>
        new(codigo, tipo, Opcional(LeerDir("CNOMBRECALLE")), Opcional(LeerDir("CNUMEROEXTERIOR")), Opcional(LeerDir("CNUMEROINTERIOR")),
            Opcional(LeerDir("CCOLONIA")), LeerDir("CCODIGOPOSTAL"), Opcional(LeerDir("CMUNICIPIO")), Opcional(LeerDir("CCIUDAD")),
            Opcional(LeerDir("CESTADO")), Opcional(LeerDir("CPAIS")));

    /// <summary>
    /// Crea o completa la dirección con <c>fInsertaDireccion</c> o <c>fEditaDireccion</c>, <c>fSetDatoDireccion</c> por campo
    /// y <c>fGuardaDireccion</c>. El alta liga la dirección al cliente con CIDCATALOGO y CTIPOCATALOGO=1. Los nombres de campo
    /// salen de los textos de Montek; la secuencia no se ha comprobado contra Comercial: si falla, cancela sin guardar.
    /// </summary>
    private ResultadoDireccion GuardarDireccion(Direccion d)
    {
        var cliente = traza.Llamar($"fBuscaCteProv('{d.CodigoCliente}')", () => SdkNative.fBuscaCteProv(d.CodigoCliente));
        if (cliente == SdkNative.NoEncontrado)
        {
            return new ResultadoDireccion(TipoResultadoDireccion.ClienteNoEncontrado, Mensaje: $"No existe el cliente {d.CodigoCliente}.");
        }

        if (cliente != 0)
        {
            return ErrorDireccion("fBuscaCteProv", cliente);
        }

        var idCliente = Leer("CIDCLIENTEPROVEEDOR");
        var existe = traza.Llamar($"fBuscaDireccionCteProv('{d.CodigoCliente}',{d.Tipo})", () => SdkNative.fBuscaDireccionCteProv(d.CodigoCliente, (byte)d.Tipo)) == 0;

        var e = existe
            ? traza.Llamar("fEditaDireccion()", SdkNative.fEditaDireccion)
            : traza.Llamar("fInsertaDireccion()", SdkNative.fInsertaDireccion);
        if (e != 0)
        {
            return ErrorDireccion(existe ? "fEditaDireccion" : "fInsertaDireccion", e);
        }

        try
        {
            var campos = new List<(string Campo, string Valor)>();
            if (!existe)
            {
                campos.Add(("CIDCATALOGO", idCliente));
                campos.Add(("CTIPOCATALOGO", "1"));
                campos.Add(("CTIPODIRECCION", d.Tipo.ToString(CultureInfo.InvariantCulture)));
            }

            void Agregar(string campo, string? valor)
            {
                if (!string.IsNullOrWhiteSpace(valor))
                {
                    campos.Add((campo, valor));
                }
            }

            Agregar("CNOMBRECALLE", d.Calle);
            Agregar("CNUMEROEXTERIOR", d.NumeroExterior);
            Agregar("CNUMEROINTERIOR", d.NumeroInterior);
            Agregar("CCOLONIA", d.Colonia);
            Agregar("CCODIGOPOSTAL", d.CodigoPostal);
            Agregar("CMUNICIPIO", d.Municipio);
            Agregar("CCIUDAD", d.Ciudad);
            Agregar("CESTADO", d.Estado);
            Agregar("CPAIS", d.Pais);

            foreach (var (campo, valor) in campos)
            {
                e = traza.Llamar($"fSetDatoDireccion('{campo}','{valor}')", () => SdkNative.fSetDatoDireccion(campo, valor));
                if (e != 0)
                {
                    traza.Llamar("fCancelarModificacionDireccion()", SdkNative.fCancelarModificacionDireccion);
                    return ErrorDireccion($"fSetDatoDireccion({campo})", e);
                }
            }

            e = traza.Llamar("fGuardaDireccion()", SdkNative.fGuardaDireccion);
            if (e != 0)
            {
                traza.Llamar("fCancelarModificacionDireccion()", SdkNative.fCancelarModificacionDireccion);
                return ErrorDireccion("fGuardaDireccion", e);
            }
        }
        catch
        {
            traza.Llamar("fCancelarModificacionDireccion()", SdkNative.fCancelarModificacionDireccion);
            throw;
        }

        var guardada = BuscarDireccion(d.CodigoCliente, d.Tipo);
        return guardada is null
            ? new ResultadoDireccion(TipoResultadoDireccion.ErrorSdk, Mensaje: "El SDK guardó sin error, pero la dirección no aparece al buscarla.")
            : new ResultadoDireccion(TipoResultadoDireccion.Guardada, guardada);
    }

    private static ResultadoDireccion ErrorDireccion(string funcion, int codigo) =>
        new(TipoResultadoDireccion.ErrorSdk, Mensaje: $"{funcion} = {codigo}: {SdkNative.Mensaje(codigo)}", CodigoSdk: codigo);

    private string LeerDir(string campo)
    {
        var sb = new StringBuilder(512);
        var r = traza.Llamar(
            $"fLeeDatoDireccion('{campo}')",
            () => SdkNative.fLeeDatoDireccion(campo, sb, sb.Capacity),
            valor: () => sb.ToString().Trim());
        return r == 0 ? sb.ToString().Trim() : string.Empty;
    }

    private static string? Opcional(string texto) => string.IsNullOrWhiteSpace(texto) ? null : texto;

    public Task<ResultadoActualizar> ActualizarAsync(string codigo, CambiosCliente cambios, CancellationToken ct) =>
        sesion.EjecutarAsync(() => Actualizar(codigo, cambios));

    /// <summary>
    /// Modifica con <c>fBuscaCteProv</c>, <c>fEditaCteProv</c>, un <c>fSetDatoCteProv</c> por campo con valor y
    /// <c>fGuardaCteProv</c>; si algo falla cancela la edición. Los campos CREGIMFISC, CUSOCFDI y CEMAIL1 salen de los
    /// textos de Montek: no se han comprobado contra Comercial (un campo que no exista falla sin guardar nada).
    /// </summary>
    private ResultadoActualizar Actualizar(string codigo, CambiosCliente cambios)
    {
        var previo = traza.Llamar($"fBuscaCteProv('{codigo}')", () => SdkNative.fBuscaCteProv(codigo));
        if (previo == SdkNative.NoEncontrado)
        {
            return new ResultadoActualizar(TipoResultadoActualizar.NoEncontrado, Mensaje: $"No existe el cliente {codigo}.");
        }

        if (previo != 0)
        {
            return ErrorActualizar("fBuscaCteProv", previo);
        }

        var e = traza.Llamar("fEditaCteProv()", SdkNative.fEditaCteProv);
        if (e != 0)
        {
            return ErrorActualizar("fEditaCteProv", e);
        }

        try
        {
            var campos = new List<(string Campo, string Valor)>();
            void Agregar(string campo, string? valor)
            {
                if (!string.IsNullOrWhiteSpace(valor))
                {
                    campos.Add((campo, valor));
                }
            }

            Agregar("CRAZONSOCIAL", cambios.RazonSocial);
            Agregar("CRFC", cambios.Rfc);
            Agregar("CREGIMFISC", cambios.RegimenFiscal);
            Agregar("CUSOCFDI", cambios.UsoCfdi);
            Agregar("CEMAIL1", cambios.Email);
            if (cambios.LimiteCredito is { } limite)
            {
                campos.Add(("CLIMITECREDITOCLIENTE", limite.ToString(CultureInfo.InvariantCulture)));
            }

            foreach (var (campo, valor) in campos)
            {
                e = traza.Llamar($"fSetDatoCteProv('{campo}','{valor}')", () => SdkNative.fSetDatoCteProv(campo, valor));
                if (e != 0)
                {
                    Cancelar();
                    return ErrorActualizar($"fSetDatoCteProv({campo})", e);
                }
            }

            e = traza.Llamar("fGuardaCteProv()", SdkNative.fGuardaCteProv);
            if (e != 0)
            {
                Cancelar();
                return ErrorActualizar("fGuardaCteProv", e);
            }
        }
        catch
        {
            Cancelar();
            throw;
        }

        var despues = Buscar(codigo);
        return despues is null
            ? new ResultadoActualizar(TipoResultadoActualizar.ErrorSdk, Mensaje: "El SDK guardó sin error, pero el cliente no aparece al buscarlo.")
            : new ResultadoActualizar(TipoResultadoActualizar.Actualizado, despues);
    }

    private static ResultadoActualizar ErrorActualizar(string funcion, int codigo) =>
        new(TipoResultadoActualizar.ErrorSdk, Mensaje: $"{funcion} = {codigo}: {SdkNative.Mensaje(codigo)}", CodigoSdk: codigo);

    private ResultadoCrear Crear(NuevoCliente cliente)
    {
        var previo = traza.Llamar($"fBuscaCteProv('{cliente.Codigo}')", () => SdkNative.fBuscaCteProv(cliente.Codigo));
        if (previo == 0)
        {
            return new ResultadoCrear(TipoResultadoCrear.YaExiste, Mensaje: $"Ya existe el cliente {cliente.Codigo}.");
        }

        if (previo != SdkNative.NoEncontrado)
        {
            return Error("fBuscaCteProv", previo);
        }

        var e = traza.Llamar("fInsertaCteProv()", SdkNative.fInsertaCteProv);
        if (e != 0)
        {
            return Error("fInsertaCteProv", e);
        }

        try
        {
            // El SDK devuelve la fecha como mes/día/año con hora en ceros (verificado leyendo PRUEBACS11).
            var fecha = DateTime.Now.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) + " 00:00:00:000";
            var campos = new List<(string Campo, string Valor)>
            {
                ("CCODIGOCLIENTE", cliente.Codigo),
                ("CRAZONSOCIAL", cliente.RazonSocial),
                ("CFECHAALTA", fecha),
                ("CTIPOCLIENTE", (cliente.Tipo ?? 1).ToString(CultureInfo.InvariantCulture)),
                ("CIDMONEDA", "1"),

                // Sin CESTATUS el cliente queda inactivo (estatus 0): verificado en Comercial con PRUEBACS12.
                ("CESTATUS", "1"),

                // Sin lista de precios el guardado falla con 120513.
                ("CLISTAPRECIOCLIENTE", opciones.ListaPrecios.ToString(CultureInfo.InvariantCulture)),
            };

            // RFC y límite de crédito solo se asignan si vienen en la orden.
            if (!string.IsNullOrWhiteSpace(cliente.Rfc))
            {
                campos.Add(("CRFC", cliente.Rfc));
            }

            if (cliente.LimiteCredito is { } limite)
            {
                campos.Add(("CLIMITECREDITOCLIENTE", limite.ToString(CultureInfo.InvariantCulture)));
            }

            foreach (var (campo, valor) in campos)
            {
                e = traza.Llamar($"fSetDatoCteProv('{campo}','{valor}')", () => SdkNative.fSetDatoCteProv(campo, valor));
                if (e != 0)
                {
                    Cancelar();
                    return Error($"fSetDatoCteProv({campo})", e);
                }
            }

            e = traza.Llamar("fGuardaCteProv()", SdkNative.fGuardaCteProv);
            if (e != 0)
            {
                Cancelar();
                return Error("fGuardaCteProv", e);
            }
        }
        catch
        {
            Cancelar();
            throw;
        }

        var creado = Buscar(cliente.Codigo);
        return creado is null
            ? new ResultadoCrear(TipoResultadoCrear.ErrorSdk, Mensaje: "El SDK guardó sin error, pero el cliente no aparece al buscarlo.")
            : new ResultadoCrear(TipoResultadoCrear.Creado, creado);
    }

    private void Cancelar() =>
        traza.Llamar("fCancelarModificacionCteProv()", SdkNative.fCancelarModificacionCteProv);

    private static ResultadoCrear Error(string funcion, int codigo) =>
        new(TipoResultadoCrear.ErrorSdk, Mensaje: $"{funcion} = {codigo}: {SdkNative.Mensaje(codigo)}", CodigoSdk: codigo);

    private string Leer(string campo)
    {
        var sb = new StringBuilder(512);
        var r = traza.Llamar(
            $"fLeeDatoCteProv('{campo}')",
            () => SdkNative.fLeeDatoCteProv(campo, sb, sb.Capacity),
            valor: () => sb.ToString().Trim());
        return r == 0 ? sb.ToString().Trim() : string.Empty;
    }

    private static int Entero(string texto) =>
        int.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
