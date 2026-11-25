namespace Acusa.Sdk.Application;

/// <summary>Configuración de la sección <c>Contpaqi</c> de appsettings.</summary>
public sealed class OpcionesContpaqi
{
    public const string Seccion = "Contpaqi";

    /// <summary><c>Real</c> usa el SDK; <c>Simulado</c> usa datos en memoria para desarrollar sin CONTPAQi.</summary>
    public string Modo { get; set; } = "Real";

    /// <summary>
    /// <c>Dialogo</c> abre "Ingreso a CONTPAQi COMERCIAL" y espera a que alguien escriba la clave (comprobado).
    /// <c>Credenciales</c> inicia sesión con <c>fInicioSesionSDK(usuario, clave)</c> sin diálogo (comprobado en el servidor). La clave se pide en la consola de la API y nunca se guarda ni se registra.
    /// </summary>
    public string Inicio { get; set; } = "Dialogo";

    /// <summary>Usuario de CONTPAQi para el inicio por credenciales (por ejemplo COMERCIAL7). No es secreto; la clave sí.</summary>
    public string Usuario { get; set; } = string.Empty;

    /// <summary>Nombre del sistema para <c>fSetNombrePAQ</c> en el inicio por credenciales.</summary>
    public string NombrePaq { get; set; } = "CONTPAQ I COMERCIAL";

    /// <summary>
    /// Además de <c>fInicioSesionSDK</c>, llama <c>fInicioSesionSDKCONTPAQi</c> con el mismo usuario y clave.
    /// Apagado: solo hace falta si el SDK lo exige, y una clave rechazada cuenta como intento fallido.
    /// </summary>
    public bool IniciarSesionContabilidad { get; set; }

    /// <summary>Carpeta de CONTPAQi Comercial, donde está MGWServicios.dll (32 bits).</summary>
    public string RutaSdk { get; set; } = @"C:\Program Files (x86)\Compac\COMERCIAL";

    /// <summary>Ruta fija de la empresa. No se recorre el catálogo: el SDK ve empresas ajenas.</summary>
    public string RutaEmpresa { get; set; } = @"C:\Compac\Empresas\adLOCALSOLUTIONIT";

    /// <summary>Lista de precios que lleva un cliente nuevo.</summary>
    public int ListaPrecios { get; set; } = 1;

    /// <summary>Crear y modificar clientes, productos y direcciones en las empresas que atiende el worker.</summary>
    public bool PermitirEscritura { get; set; } = true;

    /// <summary>
    /// Crear documentos. Aparte de <see cref="PermitirEscritura"/> porque crear un documento consume un folio del concepto
    /// y un folio no se reutiliza. Requiere <see cref="PermitirEscritura"/>.
    /// </summary>
    public bool PermitirDocumentos { get; set; } = true;

    /// <summary>
    /// Timbrar y cancelar. Aparte de los demás porque timbrar gasta un timbre del PAC y cancelar notifica al SAT; ninguna se
    /// deshace. Requiere <see cref="PermitirEscritura"/> y <see cref="PermitirDocumentos"/>. La contraseña del certificado
    /// no va aquí: se guarda cifrada en el panel del worker o en <c>ACUSA_CLAVE_CERTIFICADO</c>.
    /// </summary>
    public bool PermitirTimbrado { get; set; } = true;

    /// <summary>
    /// Carpeta donde CONTPAQi deja el XML y el PDF (la "entrega de archivos en disco" del concepto). Opcional: si está vacía,
    /// <c>documento.obtener-archivo</c> los busca en la carpeta de la empresa, en Documentos, en el Escritorio y en la
    /// carpeta temporal del usuario.
    /// </summary>
    public string CarpetaEntrega { get; set; } = string.Empty;

    /// <summary>
    /// El worker no guarda la contraseña del certificado: se la pasa vacía al SDK para que Comercial use la que ya tenga
    /// guardada en su configuración. Si Comercial no la tiene, el timbrado falla sin consecuencias.
    /// </summary>
    public bool CertificadoGuardadoEnComercial { get; set; }

    /// <summary>
    /// Carpetas de las empresas que atiende este worker (solo el nombre, por ejemplo <c>adMIEMPRESA</c>), todas bajo la
    /// misma carpeta que <see cref="RutaEmpresa"/>. Vacía: el worker atiende solo la de <see cref="RutaEmpresa"/>.
    /// </summary>
    public string[] Empresas { get; set; } = [];

    /// <summary>Empresas que atiende el worker: la principal (<see cref="RutaEmpresa"/>) primero y sin repetidas.</summary>
    public IReadOnlyList<string> EmpresasAtendidas =>
        new[] { CarpetaEmpresa }.Concat(Empresas ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Ruta completa de una empresa atendida: misma carpeta base que la principal.</summary>
    public string RutaDe(string carpetaEmpresa) =>
        Path.Combine(Path.GetDirectoryName(RutaEmpresa.TrimEnd('\\', '/')) ?? string.Empty, carpetaEmpresa);

    public bool EsSimulado => string.Equals(Modo, "Simulado", StringComparison.OrdinalIgnoreCase);

    public bool UsaCredenciales => string.Equals(Inicio, "Credenciales", StringComparison.OrdinalIgnoreCase);

    /// <summary>Nombre de la carpeta de la empresa elegida (último tramo de <see cref="RutaEmpresa"/>).</summary>
    public string CarpetaEmpresa => Path.GetFileName(RutaEmpresa.TrimEnd('\\', '/'));

    /// <summary>
    /// El timbrado exige la escritura y los documentos activos; sin ellos no hay documento que timbrar.
    /// Devuelve el motivo del rechazo, o <c>null</c> si es válido (o si el timbrado está apagado).
    /// </summary>
    public string? ValidarTimbrado() =>
        PermitirTimbrado && !(PermitirEscritura && PermitirDocumentos)
            ? "Contpaqi:PermitirTimbrado requiere también Contpaqi:PermitirEscritura y Contpaqi:PermitirDocumentos."
            : null;

    /// <summary>Motivo por el que la configuración de inicio no es válida, o <c>null</c> si lo es.</summary>
    public string? ValidarInicio()
    {
        if (UsaCredenciales)
        {
            return string.IsNullOrWhiteSpace(Usuario)
                ? "Inicio=Credenciales requiere Contpaqi:Usuario (la clave se pide en la consola)."
                : null;
        }

        return string.Equals(Inicio, "Dialogo", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Contpaqi:Inicio '{Inicio}' no es válido. Use Dialogo o Credenciales.";
    }
}
