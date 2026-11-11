namespace Acusa.Sdk.Application.Sesion;

/// <summary>
/// Entrega la clave de CONTPAQi para el inicio por credenciales. La clave solo vive en memoria del proceso:
/// no se lee de archivos, no se registra y no pasa por ningún endpoint.
/// </summary>
public interface IProveedorClave
{
    string ObtenerClave();
}
