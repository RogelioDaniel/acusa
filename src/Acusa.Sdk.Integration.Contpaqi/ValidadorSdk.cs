using System.Text;
using Acusa.Sdk.Integration.Contpaqi.Native;

namespace Acusa.Sdk.Integration.Contpaqi;

/// <summary>Empresa que el SDK enumera tras iniciar sesión.</summary>
public sealed record EmpresaSdk(int Id, string Nombre, string Directorio);

/// <summary>Resultado de <see cref="ValidadorSdk.Validar"/>.</summary>
public sealed record ResultadoValidacionSdk(bool Ok, string Mensaje, IReadOnlyList<EmpresaSdk> Empresas);

/// <summary>
/// Comprueba unas credenciales del SDK sin abrir ninguna empresa: inicia sesión, enumera las empresas que el
/// SDK ve (solo lectura) y termina el SDK. La clave solo se usa en la llamada y no se registra.
/// </summary>
public static class ValidadorSdk
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(90);

    public static ResultadoValidacionSdk Validar(string rutaSdk, string usuario, string clave, string nombrePaq)
    {
        ResultadoValidacionSdk? resultado = null;
        var hilo = new Thread(() => resultado = Ejecutar(rutaSdk, usuario, clave, nombrePaq)) { IsBackground = true };
        if (OperatingSystem.IsWindows())
        {
            hilo.SetApartmentState(ApartmentState.STA);
        }

        hilo.Start();

        return hilo.Join(Espera)
            ? resultado!
            : new ResultadoValidacionSdk(false, "El SDK no respondió en 90 segundos. Revise si abrió un diálogo en el servidor.", []);
    }

    private static ResultadoValidacionSdk Ejecutar(string rutaSdk, string usuario, string clave, string nombrePaq)
    {
        var iniciado = false;
        try
        {
            if (!Directory.Exists(rutaSdk))
            {
                return new ResultadoValidacionSdk(false, $"No existe la ruta del sistema: {rutaSdk}", []);
            }

            // MGWServicios.dll depende de otros archivos de la carpeta de Comercial.
            Environment.CurrentDirectory = rutaSdk;
            Environment.SetEnvironmentVariable("PATH", rutaSdk + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));

            SdkNative.fInicioSesionSDK(usuario, clave);
            iniciado = true;

            var paq = SdkNative.fSetNombrePAQ(nombrePaq);
            if (paq != 0)
            {
                return new ResultadoValidacionSdk(false, $"fSetNombrePAQ = {paq}: {SdkNative.Mensaje(paq)}", []);
            }

            var empresas = new List<EmpresaSdk>();
            var id = 0;
            var nombre = new StringBuilder(256);
            var directorio = new StringBuilder(512);
            var r = SdkNative.fPosPrimerEmpresa(ref id, nombre, directorio);
            if (r != 0)
            {
                return new ResultadoValidacionSdk(
                    false,
                    $"El SDK no pudo enumerar empresas (fPosPrimerEmpresa = {r}: {SdkNative.Mensaje(r)}). Revise el usuario y la clave.",
                    []);
            }

            while (r == 0 && empresas.Count < 500)
            {
                empresas.Add(new EmpresaSdk(id, nombre.ToString().Trim(), directorio.ToString().Trim()));
                nombre.Clear();
                directorio.Clear();
                r = SdkNative.fPosSiguienteEmpresa(ref id, nombre, directorio);
            }

            return new ResultadoValidacionSdk(true, $"SDK listo: inicio de sesión correcto y {empresas.Count} empresas visibles.", empresas);
        }
        catch (Exception ex)
        {
            return new ResultadoValidacionSdk(false, $"{ex.GetType().Name}: {ex.Message}", []);
        }
        finally
        {
            if (iniciado)
            {
                try
                {
                    SdkNative.fTerminaSDK();
                }
                catch
                {
                    // Si el SDK ya no responde no hay nada más que liberar.
                }
            }
        }
    }
}
