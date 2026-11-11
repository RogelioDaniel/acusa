namespace Acusa.Sdk.Application;

/// <summary>Configuración de la sección <c>Contpaqi</c> de appsettings.</summary>
public sealed class OpcionesContpaqi
{
    public const string Seccion = "Contpaqi";

    /// <summary><c>Real</c> usa el SDK; <c>Simulado</c> usa datos en memoria para desarrollar sin CONTPAQi.</summary>
    public string Modo { get; set; } = "Real";

    /// <summary>
    /// <c>Dialogo</c> abre "Ingreso a CONTPAQi COMERCIAL" y espera a que alguien escriba la clave (comprobado).
    /// <c>Credenciales</c> inicia sesión con <c>fInicioSesionSDK(usuario, clave)</c> sin diálogo (secuencia de Montek;
    /// pendiente de comprobar). La clave se pide en la consola de la API y nunca se guarda ni se registra.
    /// </summary>
    public string Inicio { get; set; } = "Dialogo";

    /// <summary>Usuario de CONTPAQi para el inicio por credenciales (por ejemplo COMERCIAL7). No es secreto; la clave sí.</summary>
    public string Usuario { get; set; } = string.Empty;

    /// <summary>Nombre del sistema para <c>fSetNombrePAQ</c> en el inicio por credenciales.</summary>
    public string NombrePaq { get; set; } = "CONTPAQ I COMERCIAL";

    /// <summary>
    /// Además de <c>fInicioSesionSDK</c>, llama <c>fInicioSesionSDKCONTPAQi</c> con el mismo usuario y clave, como hace
    /// Montek. Apagado: solo hace falta si el SDK lo exige, y una clave rechazada cuenta como intento fallido.
    /// </summary>
    public bool IniciarSesionContabilidad { get; set; }

    /// <summary>Carpeta de CONTPAQi Comercial, donde está MGWServicios.dll (32 bits).</summary>
    public string RutaSdk { get; set; } = @"C:\Program Files (x86)\Compac\COMERCIAL";

    /// <summary>Ruta fija de la empresa. No se recorre el catálogo: el SDK ve empresas ajenas.</summary>
    public string RutaEmpresa { get; set; } = @"C:\Compac\Empresas\adLOCALSOLUTIONIT";

    /// <summary>Lista de precios que lleva un cliente nuevo (1 en PRUEBACS11).</summary>
    public int ListaPrecios { get; set; } = 1;

    /// <summary>Apagado por defecto: la empresa es producción.</summary>
    public bool PermitirEscritura { get; set; }

    /// <summary>
    /// Apagado por defecto y aparte de <see cref="PermitirEscritura"/>: crear un documento consume un folio
    /// del concepto y un folio no se reutiliza. Requiere <see cref="PermitirEscritura"/>.
    /// </summary>
    public bool PermitirDocumentos { get; set; }

    /// <summary>
    /// Apagado por defecto y aparte de los demás: timbrar gasta un timbre del PAC y cancelar notifica al SAT; ninguna se
    /// deshace. Requiere <see cref="PermitirEscritura"/> y <see cref="PermitirDocumentos"/>. La contraseña del certificado
    /// no va aquí: se guarda cifrada en el panel del worker o en <c>ACUSA_CLAVE_CERTIFICADO</c>.
    /// </summary>
    public bool PermitirTimbrado { get; set; }

    /// <summary>
    /// Carpetas de empresa (solo el nombre, por ejemplo <c>adEMPRESA_DE_PRUEBA</c>) donde se permite escribir. En modo
    /// Real, con <see cref="PermitirEscritura"/> activo, la empresa elegida debe estar aquí o la aplicación no abre.
    /// Vacía por defecto: ninguna empresa admite escritura hasta nombrarla.
    /// </summary>
    public string[] EmpresasEscritura { get; set; } = [];

    /// <summary>
    /// Carpetas de empresa donde se trabaja con datos reales: ahí no se exige el prefijo de prueba en códigos, nombres ni
    /// referencias. Cada una debe estar también en <see cref="EmpresasEscritura"/>. Vacía por defecto: toda empresa exige el
    /// prefijo. Es la decisión que convierte una empresa de pruebas en una donde se factura de verdad.
    /// </summary>
    public string[] EmpresasReales { get; set; } = [];

    /// <summary>Si la empresa, por su carpeta, es una de <see cref="EmpresasReales"/>.</summary>
    public bool EsEmpresaReal(string? carpetaEmpresa) =>
        PermitirEscritura && !string.IsNullOrWhiteSpace(carpetaEmpresa) &&
        (EmpresasReales ?? []).Any(e => string.Equals(e, carpetaEmpresa, StringComparison.OrdinalIgnoreCase));

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

    public string PrefijoPrueba { get; set; } = "PRUEBA";

    /// <summary>
    /// Códigos de clientes existentes que pueden recibir un documento aunque no empiecen con el prefijo de prueba, para
    /// timbrar contra un receptor real. Vacía por defecto; solo afecta al cliente del documento.
    /// </summary>
    public string[] ClientesPermitidos { get; set; } = [];

    /// <summary>
    /// Carpetas de las empresas que atiende este worker (solo el nombre, por ejemplo <c>adEMPRESA_DE_PRUEBA</c>), todas bajo la
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

    /// <summary>Si la empresa admite escritura: figura en <see cref="EmpresasEscritura"/> y la escritura está activa.</summary>
    public bool EscrituraEn(string carpetaEmpresa) =>
        PermitirEscritura && EmpresasEscritura.Any(e => string.Equals(e, carpetaEmpresa, StringComparison.OrdinalIgnoreCase));

    public bool EsSimulado => string.Equals(Modo, "Simulado", StringComparison.OrdinalIgnoreCase);

    public bool UsaCredenciales => string.Equals(Inicio, "Credenciales", StringComparison.OrdinalIgnoreCase);

    /// <summary>Nombre de la carpeta de la empresa elegida (último tramo de <see cref="RutaEmpresa"/>).</summary>
    public string CarpetaEmpresa => Path.GetFileName(RutaEmpresa.TrimEnd('\\', '/'));

    /// <summary>
    /// Con la escritura activa, la empresa elegida debe estar en <see cref="EmpresasEscritura"/>. Devuelve el motivo
    /// del rechazo, o <c>null</c> si es válido (o si la escritura está apagada).
    /// </summary>
    public string? ValidarEscritura()
    {
        if (!PermitirEscritura)
        {
            return null;
        }

        var sinEscritura = (EmpresasReales ?? []).FirstOrDefault(r => !EmpresasEscritura.Any(e => string.Equals(e, r, StringComparison.OrdinalIgnoreCase)));
        if (sinEscritura is not null)
        {
            return $"La empresa '{sinEscritura}' figura en Contpaqi:EmpresasReales pero no en Contpaqi:EmpresasEscritura. Una empresa real también debe tener la escritura habilitada.";
        }

        var carpeta = CarpetaEmpresa;
        return EmpresasEscritura.Any(e => string.Equals(e, carpeta, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"La escritura está habilitada, pero la empresa '{carpeta}' no figura en Contpaqi:EmpresasEscritura. " +
              "Agregue el nombre de su carpeta solo si es una empresa de pruebas, o apague Contpaqi:PermitirEscritura.";
    }

    /// <summary>
    /// El timbrado exige la escritura y los documentos activos; sin ellos no hay documento de prueba que timbrar.
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
