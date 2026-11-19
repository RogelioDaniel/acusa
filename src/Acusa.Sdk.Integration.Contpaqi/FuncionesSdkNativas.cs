using Acusa.Sdk.Integration.Contpaqi.Native;

namespace Acusa.Sdk.Integration.Contpaqi;

/// <summary>Implementación real de <see cref="IFuncionesSdk"/>: llama a <c>MGWServicios.dll</c> (32 bits).</summary>
public sealed class FuncionesSdkNativas : IFuncionesSdk
{
    public void PrepararEntorno(string rutaSdk)
    {
        // MGWServicios.dll depende de otros archivos de la carpeta de Comercial.
        Environment.CurrentDirectory = rutaSdk;
        Environment.SetEnvironmentVariable("PATH", rutaSdk + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
    }

    public int InicializarSdk() => SdkNative.fInicializaSDK();

    public void IniciarSesion(string usuario, string clave) => SdkNative.fInicioSesionSDK(usuario, clave);

    public void IniciarSesionContabilidad(string usuario, string clave) => SdkNative.fInicioSesionSDKCONTPAQi(usuario, clave);

    public int FijarNombrePaq(string nombrePaq) => SdkNative.fSetNombrePAQ(nombrePaq);

    public int AbrirEmpresa(string rutaEmpresa) => SdkNative.fAbreEmpresa(rutaEmpresa);

    public void CerrarEmpresa() => SdkNative.fCierraEmpresa();

    public void TerminarSdk() => SdkNative.fTerminaSDK();

    public string ObtenerMensajeDeError(int codigoError) => SdkNative.Mensaje(codigoError);
}
