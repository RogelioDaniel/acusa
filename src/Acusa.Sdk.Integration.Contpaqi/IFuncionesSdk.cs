namespace Acusa.Sdk.Integration.Contpaqi;

/// <summary>
/// Funciones del SDK de CONTPAQi que administran la sesión: inicio, apertura y cierre de empresa y cierre del SDK.
/// Es la única frontera con <c>MGWServicios.dll</c> para el ciclo de vida de la sesión, de modo que la sesión se pueda
/// probar sin CONTPAQi instalado.
/// </summary>
public interface IFuncionesSdk
{
    /// <summary>Deja el proceso listo para cargar el SDK: carpeta de trabajo y ruta de búsqueda de sus archivos.</summary>
    void PrepararEntorno(string rutaSdk);

    /// <summary>Inicia el SDK y espera a que alguien escriba la clave en el diálogo de CONTPAQi.</summary>
    int InicializarSdk();

    /// <summary>Inicia sesión con usuario y clave, sin diálogo. No devuelve código: un dato incorrecto se nota en la llamada siguiente.</summary>
    void IniciarSesion(string usuario, string clave);

    void IniciarSesionContabilidad(string usuario, string clave);

    int FijarNombrePaq(string nombrePaq);

    int AbrirEmpresa(string rutaEmpresa);

    void CerrarEmpresa();

    void TerminarSdk();

    /// <summary>Texto que el SDK asocia a un código de error.</summary>
    string ObtenerMensajeDeError(int codigoError);
}
