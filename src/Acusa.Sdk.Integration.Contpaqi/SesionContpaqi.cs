using Acusa.Sdk.Application;
using Acusa.Sdk.Application.Sesion;
using Acusa.Sdk.Integration.Contpaqi.Native;

namespace Acusa.Sdk.Integration.Contpaqi;

/// <summary>
/// Sesión real con el SDK. <see cref="Iniciar"/> abre el diálogo "Ingreso a CONTPAQi COMERCIAL" en la
/// sesión de Windows donde corre el proceso y espera a que alguien escriba la clave; la clave nunca
/// pasa por la API.
/// </summary>
public sealed class SesionContpaqi(OpcionesContpaqi opciones, TrazaSdk traza, IProveedorClave? proveedorClave = null) : ISesionSdk, IDisposable
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

        _ = _hilo.EjecutarAsync(Inicializar);
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
                Terminar();
                Cambiar(EstadoSesion.NoIniciada, "Sesión cerrada.");
            }
            catch (Exception ex)
            {
                Cambiar(EstadoSesion.Error, $"Error al cerrar: {ex.Message}");
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

        return await _hilo.EjecutarAsync<string?>(() =>
        {
            var anterior = opciones.RutaEmpresa;
            try
            {
                traza.Llamar("fCierraEmpresa()", SdkNative.fCierraEmpresa);
                var a = traza.Llamar($"fAbreEmpresa('{Normalizar(rutaEmpresa)}')", () => SdkNative.fAbreEmpresa(Normalizar(rutaEmpresa)));
                if (a != 0)
                {
                    // Se intenta volver a la empresa anterior para no dejar el SDK sin empresa.
                    var vuelta = traza.Llamar($"fAbreEmpresa('{Normalizar(anterior)}')", () => SdkNative.fAbreEmpresa(Normalizar(anterior)));
                    if (vuelta != 0)
                    {
                        traza.Llamar("fTerminaSDK()", SdkNative.fTerminaSDK);
                        Cambiar(EstadoSesion.Error, $"No se pudo abrir {rutaEmpresa} ni volver a {anterior}.");
                    }

                    return $"fAbreEmpresa = {a}: {SdkNative.Mensaje(a)}";
                }

                opciones.RutaEmpresa = rutaEmpresa;
                Cambiar(EstadoSesion.Lista, "Empresa abierta.");
                return null;
            }
            catch (Exception ex)
            {
                Cambiar(EstadoSesion.Error, $"{ex.GetType().Name}: {ex.Message}");
                return ex.Message;
            }
        });
    }

    /// <summary>Ejecuta <paramref name="trabajo"/> en el hilo del SDK. La sesión debe estar lista.</summary>
    internal Task<T> EjecutarAsync<T>(Func<T> trabajo)
    {
        var estado = Estado.Estado;
        if (estado != EstadoSesion.Lista)
        {
            throw new SdkNoListoException(estado);
        }

        return _hilo.EjecutarAsync(trabajo);
    }

    private int Inicializar()
    {
        try
        {
            // MGWServicios.dll depende de otros archivos de la carpeta de Comercial.
            Environment.CurrentDirectory = opciones.RutaSdk;
            Environment.SetEnvironmentVariable(
                "PATH",
                opciones.RutaSdk + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));

            if (opciones.UsaCredenciales)
            {
                var fallo = IniciarConCredenciales();
                if (fallo is not null)
                {
                    return fallo.Value;
                }
            }
            else
            {
                // Se queda esperando hasta que alguien escribe la clave en el diálogo de CONTPAQi.
                var r = traza.Llamar("fInicializaSDK()", SdkNative.fInicializaSDK, anunciar: true);
                if (r != 0)
                {
                    Cambiar(EstadoSesion.Error, $"fInicializaSDK = {r}: {SdkNative.Mensaje(r)}");
                    return r;
                }
            }

            var ruta = Normalizar(opciones.RutaEmpresa);
            var a = traza.Llamar($"fAbreEmpresa('{ruta}')", () => SdkNative.fAbreEmpresa(ruta));
            if (a != 0)
            {
                traza.Llamar("fTerminaSDK()", SdkNative.fTerminaSDK);
                Cambiar(EstadoSesion.Error, $"fAbreEmpresa = {a}: {SdkNative.Mensaje(a)}");
                return a;
            }

            Cambiar(EstadoSesion.Lista, "Empresa abierta.");
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                Terminar();
            }
            catch
            {
                // Si el SDK ni siquiera carga (falta la DLL), no hay nada que terminar.
            }

            Cambiar(EstadoSesion.Error, $"{ex.GetType().Name}: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Secuencia de Montek: <c>fInicioSesionSDK</c>, opcionalmente <c>fInicioSesionSDKCONTPAQi</c>, <c>fSetNombrePAQ</c>.
    /// Devuelve <c>null</c> si todo salió bien o el código de error, ya con el estado en Error.
    /// La clave no se escribe en la traza.
    /// </summary>
    private int? IniciarConCredenciales()
    {
        if (proveedorClave is null)
        {
            Cambiar(EstadoSesion.Error, "Inicio=Credenciales sin proveedor de clave configurado.");
            return -1;
        }

        var usuario = opciones.Usuario;
        var clave = proveedorClave.ObtenerClave();

        traza.Llamar($"fInicioSesionSDK('{usuario}','***')", () => SdkNative.fInicioSesionSDK(usuario, clave));

        if (opciones.IniciarSesionContabilidad)
        {
            traza.Llamar($"fInicioSesionSDKCONTPAQi('{usuario}','***')", () => SdkNative.fInicioSesionSDKCONTPAQi(usuario, clave));
        }

        var p = traza.Llamar($"fSetNombrePAQ('{opciones.NombrePaq}')", () => SdkNative.fSetNombrePAQ(opciones.NombrePaq));
        if (p != 0)
        {
            traza.Llamar("fTerminaSDK()", SdkNative.fTerminaSDK);
            Cambiar(
                EstadoSesion.Error,
                $"fSetNombrePAQ = {p}: {SdkNative.Mensaje(p)}. Si el usuario o la clave son incorrectos, el SDK lo informa aquí o en fAbreEmpresa.");
            return p;
        }

        return null;
    }

    /// <summary>El SDK solo acepta rutas con barra invertida: una ruta con / hace fallar fAbreEmpresa.</summary>
    private static string Normalizar(string ruta) => ruta.Replace((char)47, (char)92);

    private void Terminar()
    {
        try
        {
            traza.Llamar("fCierraEmpresa()", SdkNative.fCierraEmpresa);
        }
        finally
        {
            traza.Llamar("fTerminaSDK()", SdkNative.fTerminaSDK);
        }
    }

    private void Cambiar(EstadoSesion estado, string mensaje)
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
