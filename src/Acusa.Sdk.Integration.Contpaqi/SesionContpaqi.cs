using Acusa.Sdk.Application;
using Acusa.Sdk.Application.Sesion;

namespace Acusa.Sdk.Integration.Contpaqi;

/// <summary>
/// Sesión real con el SDK. <see cref="Iniciar"/> abre el diálogo "Ingreso a CONTPAQi COMERCIAL" en la
/// sesión de Windows donde corre el proceso y espera a que alguien escriba la clave; la clave nunca
/// pasa por la API. Todas las llamadas salen por un solo hilo y, si una falla, la sesión queda en
/// <see cref="EstadoSesion.Error"/> con el SDK terminado, sin afectar al resto de la aplicación.
/// </summary>
public sealed class SesionContpaqi(
    OpcionesContpaqi opciones,
    TrazaSdk traza,
    IFuncionesSdk funcionesSdk,
    IProveedorClave? proveedorClave = null) : ISesionSdk, IDisposable
{
    private readonly HiloSdk _hilo = new();
    private readonly object _candado = new();
    private EstadoSesion _estado = EstadoSesion.NoIniciada;
    private string? _mensaje;

    public EstadoSdk Estado
    {
        get
        {
            lock (_candado)
            {
                return new EstadoSdk("Real", _estado, opciones.RutaEmpresa, _mensaje);
            }
        }
    }

    public void Iniciar()
    {
        lock (_candado)
        {
            if (_estado is EstadoSesion.Iniciando or EstadoSesion.Lista)
            {
                return;
            }

            _estado = EstadoSesion.Iniciando;
            _mensaje = opciones.UsaCredenciales
                ? "Iniciando sesión con las credenciales configuradas (sin diálogo)."
                : "Esperando la clave en el diálogo de CONTPAQi del servidor.";
        }

        _ = _hilo.EjecutarAsync(InicializarYAbrirEmpresa);
    }

    public async Task CerrarAsync()
    {
        lock (_candado)
        {
            if (_estado == EstadoSesion.Iniciando)
            {
                // El hilo del SDK está bloqueado en el diálogo de acceso: encolar el cierre dejaría la
                // petición esperando. Hay que responder el diálogo (o cancelarlo) y volver a cerrar.
                _mensaje = "Hay un inicio en curso. Responda el diálogo de CONTPAQi en el servidor y vuelva a cerrar.";
                return;
            }

            if (_estado != EstadoSesion.Lista)
            {
                _estado = EstadoSesion.NoIniciada;
                _mensaje = "Sesión cerrada.";
                return;
            }
        }

        await _hilo.EjecutarAsync(() =>
        {
            try
            {
                CerrarEmpresaYTerminarSdk();
                CambiarEstado(EstadoSesion.NoIniciada, "Sesión cerrada.");
            }
            catch (Exception excepcion)
            {
                CambiarEstado(EstadoSesion.Error, $"Error al cerrar: {excepcion.Message}");
            }

            return 0;
        });
    }

    public async Task<string?> CambiarEmpresaAsync(string rutaEmpresa)
    {
        if (Estado.Estado != EstadoSesion.Lista)
        {
            return $"La sesión del SDK no está lista (estado: {Estado.Estado}).";
        }

        if (string.Equals(opciones.RutaEmpresa.TrimEnd('\\', '/'), rutaEmpresa.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await _hilo.EjecutarAsync<string?>(() => CambiarEmpresaEnElHilo(rutaEmpresa));
    }

    /// <summary>Ejecuta <paramref name="trabajo"/> en el hilo del SDK. La sesión debe estar lista.</summary>
    internal Task<TResultado> EjecutarAsync<TResultado>(Func<TResultado> trabajo)
    {
        var estado = Estado.Estado;
        if (estado != EstadoSesion.Lista)
        {
            throw new SdkNoListoException(estado);
        }

        return _hilo.EjecutarAsync(trabajo);
    }

    private string? CambiarEmpresaEnElHilo(string rutaEmpresa)
    {
        var rutaAnterior = opciones.RutaEmpresa;
        try
        {
            traza.Llamar("fCierraEmpresa()", funcionesSdk.CerrarEmpresa);
            var codigoApertura = AbrirEmpresa(rutaEmpresa);
            if (codigoApertura != 0)
            {
                // Se intenta volver a la empresa anterior para no dejar el SDK sin empresa.
                var codigoRegreso = AbrirEmpresa(rutaAnterior);
                if (codigoRegreso != 0)
                {
                    traza.Llamar("fTerminaSDK()", funcionesSdk.TerminarSdk);
                    CambiarEstado(EstadoSesion.Error, $"No se pudo abrir {rutaEmpresa} ni volver a {rutaAnterior}.");
                }

                return DescribirFallo("fAbreEmpresa", codigoApertura);
            }

            opciones.RutaEmpresa = rutaEmpresa;
            CambiarEstado(EstadoSesion.Lista, "Empresa abierta.");
            return null;
        }
        catch (Exception excepcion)
        {
            CambiarEstado(EstadoSesion.Error, $"{excepcion.GetType().Name}: {excepcion.Message}");
            return excepcion.Message;
        }
    }

    private int InicializarYAbrirEmpresa()
    {
        try
        {
            funcionesSdk.PrepararEntorno(opciones.RutaSdk);

            if (opciones.UsaCredenciales)
            {
                var codigoFallo = IniciarSesionConCredenciales();
                if (codigoFallo is not null)
                {
                    return codigoFallo.Value;
                }
            }
            else
            {
                // Se queda esperando hasta que alguien escribe la clave en el diálogo de CONTPAQi.
                var codigoInicio = traza.Llamar("fInicializaSDK()", funcionesSdk.InicializarSdk, anunciar: true);
                if (codigoInicio != 0)
                {
                    CambiarEstado(EstadoSesion.Error, DescribirFallo("fInicializaSDK", codigoInicio));
                    return codigoInicio;
                }
            }

            var codigoApertura = AbrirEmpresa(opciones.RutaEmpresa);
            if (codigoApertura != 0)
            {
                traza.Llamar("fTerminaSDK()", funcionesSdk.TerminarSdk);
                CambiarEstado(EstadoSesion.Error, DescribirFallo("fAbreEmpresa", codigoApertura));
                return codigoApertura;
            }

            CambiarEstado(EstadoSesion.Lista, "Empresa abierta.");
            return 0;
        }
        catch (Exception excepcion)
        {
            try
            {
                CerrarEmpresaYTerminarSdk();
            }
            catch
            {
                // Si el SDK ni siquiera carga (falta la DLL), no hay nada que terminar.
            }

            CambiarEstado(EstadoSesion.Error, $"{excepcion.GetType().Name}: {excepcion.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Secuencia: <c>fInicioSesionSDK</c>, opcionalmente <c>fInicioSesionSDKCONTPAQi</c>, <c>fSetNombrePAQ</c>.
    /// Devuelve <c>null</c> si todo salió bien o el código de error, ya con el estado en Error.
    /// La clave no se escribe en la traza.
    /// </summary>
    private int? IniciarSesionConCredenciales()
    {
        if (proveedorClave is null)
        {
            CambiarEstado(EstadoSesion.Error, "Inicio=Credenciales sin proveedor de clave configurado.");
            return -1;
        }

        var usuario = opciones.Usuario;
        var clave = proveedorClave.ObtenerClave();

        traza.Llamar($"fInicioSesionSDK('{usuario}','***')", () => funcionesSdk.IniciarSesion(usuario, clave));

        if (opciones.IniciarSesionContabilidad)
        {
            traza.Llamar($"fInicioSesionSDKCONTPAQi('{usuario}','***')", () => funcionesSdk.IniciarSesionContabilidad(usuario, clave));
        }

        var codigoNombrePaq = traza.Llamar($"fSetNombrePAQ('{opciones.NombrePaq}')", () => funcionesSdk.FijarNombrePaq(opciones.NombrePaq));
        if (codigoNombrePaq != 0)
        {
            traza.Llamar("fTerminaSDK()", funcionesSdk.TerminarSdk);
            CambiarEstado(
                EstadoSesion.Error,
                $"{DescribirFallo("fSetNombrePAQ", codigoNombrePaq)}. Si el usuario o la clave son incorrectos, el SDK lo informa aquí o en fAbreEmpresa.");
            return codigoNombrePaq;
        }

        return null;
    }

    private int AbrirEmpresa(string rutaEmpresa)
    {
        var rutaNormalizada = NormalizarRuta(rutaEmpresa);
        return traza.Llamar($"fAbreEmpresa('{rutaNormalizada}')", () => funcionesSdk.AbrirEmpresa(rutaNormalizada));
    }

    private string DescribirFallo(string funcion, int codigo) =>
        $"{funcion} = {codigo}: {funcionesSdk.ObtenerMensajeDeError(codigo)}";

    /// <summary>El SDK solo acepta rutas con barra invertida: una ruta con / hace fallar fAbreEmpresa.</summary>
    private static string NormalizarRuta(string ruta) => ruta.Replace('/', '\\');

    private void CerrarEmpresaYTerminarSdk()
    {
        try
        {
            traza.Llamar("fCierraEmpresa()", funcionesSdk.CerrarEmpresa);
        }
        finally
        {
            traza.Llamar("fTerminaSDK()", funcionesSdk.TerminarSdk);
        }
    }

    private void CambiarEstado(EstadoSesion estado, string mensaje)
    {
        lock (_candado)
        {
            _estado = estado;
            _mensaje = mensaje;
        }
    }

    public void Dispose()
    {
        try
        {
            CerrarAsync().Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Al apagar no hay a quién avisar.
        }

        _hilo.Dispose();
    }
}
