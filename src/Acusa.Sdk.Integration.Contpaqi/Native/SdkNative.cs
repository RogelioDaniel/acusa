using System.Runtime.InteropServices;
using System.Text;

namespace Acusa.Sdk.Integration.Contpaqi.Native;

/// <summary>
/// Funciones del SDK de CONTPAQi Comercial (MGWServicios.dll, 32 bits). Las cadenas se pasan como
/// <c>char*</c>: <see cref="string"/> para entrada y <see cref="StringBuilder"/> para salida.
/// Solo se declaran las funciones que la API usa; no hay funciones de borrado.
/// </summary>
internal static class SdkNative
{
    private const string Dll = "MGWServicios.dll";

    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fInicializaSDK();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern void fTerminaSDK();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fAbreEmpresa(string ruta);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern void fCierraEmpresa();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern void fError(int numero, StringBuilder mensaje, int longitud);

    // Inicio por credenciales (secuencia observada en Montek). Ninguna de las dos devuelve código: un usuario o
    // una clave incorrectos se notan en la llamada siguiente (fSetNombrePAQ o fAbreEmpresa).
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern void fInicioSesionSDK(string usuario, string clave);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern void fInicioSesionSDKCONTPAQi(string usuario, string clave);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fSetNombrePAQ(string sistema);

    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fBuscaCteProv(string codigo);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fLeeDatoCteProv(string campo, StringBuilder valor, int longitud);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fInsertaCteProv();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fSetDatoCteProv(string campo, string valor);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fGuardaCteProv();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fCancelarModificacionCteProv();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fEditaCteProv();

    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fBuscaProducto(string codigo);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fLeeDatoProducto(string campo, StringBuilder valor, int longitud);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fAltaProducto(ref int idProducto, ref TProducto producto);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fBuscaUnidad(string nombre);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fEditaProducto();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fSetDatoProducto(string campo, string valor);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fGuardaProducto();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fCancelarModificacionProducto();

    // Direcciones (admDomicilios). Firmas leídas de los metadatos de Montek: el tipo de dirección es un byte.
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fBuscaDireccionCteProv(string codigoCteProv, byte tipoDireccion);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fLeeDatoDireccion(string campo, StringBuilder valor, int longitud);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fInsertaDireccion();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fEditaDireccion();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fSetDatoDireccion(string campo, string valor);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fGuardaDireccion();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fCancelarModificacionDireccion();

    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fBuscaConceptoDocto(string codigo);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fAltaDocumento(ref int idDocumento, ref TDocumento documento);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fAltaMovimiento(int idDocumento, ref int idMovimiento, ref TMovimiento movimiento);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fSetDatoMovimiento(string campo, string valor);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fSetDatoDocumento(string campo, string valor);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fGuardaDocumento();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fCancelarModificacionDocumento();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fBuscarDocumento(string concepto, string serie, string folio);
    /// <summary>Libera el documento que dejó posicionado una búsqueda, para que otro usuario lo abra en Comercial (existe en el SDK que usa Montek).</summary>
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fDesbloqueaDocumento();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fBuscarIdDocumento(int idDocumento);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fLeeDatoDocumento(string campo, StringBuilder valor, int longitud);

    // Timbrado y cancelación. Firmas leídas de los metadatos de Montek (Montekapp.Installer.dll, clase SdkComercial):
    // fEmitirDocumento recibe el folio como double; fCancelaDocumento* actúan sobre el documento ya posicionado
    // con fBuscarDocumento. No se declara fCancelaDocumentoAdministrativamente: cancela sin avisar al SAT.
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fEmitirDocumento(string concepto, string serie, double folio, string password, string archivoAdicional);
    // UUID del CFDI de un documento. Los tres primeros parámetros son de entrada y salida (StringBuilder), como en Montek.
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fDocumentoUUID(StringBuilder codConcepto, StringBuilder serie, double folio, StringBuilder uuid);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fBuscaIdCteProv(int idCteProv);

    // Entrega el XML (formato 0) o el PDF (formato 1, el único valor que Montek acepta para PDF) en la carpeta de entrega.
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fEntregEnDiscoXML(string codConcepto, string serie, double folio, int formato, string formatoAmigable);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fCancelaDocumento();
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fCancelaDocumentoConMotivo(string motivoCancelacion, string uuidRemplaza);

    // Recorrido de empresas: solo lectura. Funciona justo después de iniciar sesión, sin abrir ninguna empresa.
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fPosPrimerEmpresa(ref int idEmpresa, StringBuilder nombre, StringBuilder directorio);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int fPosSiguienteEmpresa(ref int idEmpresa, StringBuilder nombre, StringBuilder directorio);

    /// <summary>Código que devuelve <c>fBuscaCteProv</c> cuando no existe el registro.</summary>
    public const int NoEncontrado = 3;

    /// <summary>Mensaje del SDK para un código de error. Funciona sin haber iniciado el SDK.</summary>
    public static string Mensaje(int codigo)
    {
        var sb = new StringBuilder(512);
        fError(codigo, sb, sb.Capacity);
        return sb.ToString();
    }
}
