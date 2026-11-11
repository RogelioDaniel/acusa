using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acusa.Contrato;
using Acusa.Sdk.Application.Sesion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acusa.Sdk.Worker;

/// <summary>Datos fijos de esta ejecución que el panel necesita mostrar.</summary>
public sealed record ContextoPanel(string WorkerId, string Empresa, string Modo, string UrlOrquestador, bool Configurado, string Codigo);

/// <summary>Cuerpo de <c>POST /panel/config</c>. Un secreto vacío significa "dejar el que ya está guardado".</summary>
public sealed record SolicitudConfig(
    string? Url,
    string? WorkerId,
    string? Empresa,
    string? RutaSdk,
    string? Inicio,
    string? UsuarioSdk,
    string? ClaveSdk,
    string? LlaveWorker,
    bool PermitirEscritura,
    bool PermitirDocumentos = false,
    bool PermitirTimbrado = false,
    string? ClaveCertificado = null);

/// <summary>Cuerpo de <c>POST /panel/probar</c>.</summary>
public sealed record SolicitudProbar(string? Url, string? Llave);

/// <summary>
/// Panel local del worker: Estado, Configuración y Bitácora, en una página servida solo en este equipo.
/// Todas las rutas /panel exigen el código de acceso local y no se contestan si el Host no es localhost,
/// para que otro usuario del servidor (o una página web abierta en el navegador) no pueda leer ni cambiar nada.
/// </summary>
public static class PanelLocal
{
    private const string BaseEmpresas = @"C:\Compac\Empresas";

    public static void Mapear(WebApplication app, ContextoPanel ctx, EstadoWorker estado, BitacoraProvider bitacora)
    {
        var panel = app.MapGroup("/panel");

        panel.AddEndpointFilter(async (contexto, siguiente) =>
        {
            var http = contexto.HttpContext;
            var host = http.Request.Host.Host;
            if (host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1"))
            {
                return Results.StatusCode(StatusCodes.Status421MisdirectedRequest);
            }

            var enviado = http.Request.Headers["X-Acusa-Codigo"].ToString();
            if (enviado.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(enviado.ToUpperInvariant()), Encoding.UTF8.GetBytes(ctx.Codigo)))
            {
                return Results.Json(new { error = "Código de acceso incorrecto." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return await siguiente(contexto);
        });

        panel.MapGet("/estado", () =>
        {
            var sdk = app.Services.GetService<ISesionSdk>()?.Estado;
            return Results.Ok(new
            {
                version = ClienteOrquestador.VersionWorker,
                ctx.WorkerId,
                ctx.Empresa,
                ctx.Modo,
                ctx.Configurado,
                ctx.UrlOrquestador,
                sdk = sdk is null ? null : new { estado = sdk.Estado.ToString(), mensaje = sdk.Mensaje, empresa = sdk.Empresa },
                estado = estado.Snapshot(),
                hora = DateTime.UtcNow,
            });
        });

        panel.MapGet("/bitacora", (long? desde) => Results.Ok(bitacora.Desde(desde ?? 0)));

        panel.MapGet("/config", () =>
        {
            var c = Leer();
            var orq = c["Orquestador"] as JsonObject;
            var cpq = c["Contpaqi"] as JsonObject;
            var prot = c["Protegidos"] as JsonObject;
            return Results.Ok(new
            {
                url = (string?)orq?["Url"] ?? ctx.UrlOrquestador,
                workerId = (string?)orq?["WorkerId"] ?? string.Empty,
                empresa = Path.GetFileName(((string?)cpq?["RutaEmpresa"] ?? string.Empty).TrimEnd('\\', '/')),
                rutaSdk = (string?)cpq?["RutaSdk"] ?? @"C:\Program Files (x86)\Compac\COMERCIAL",
                inicio = (string?)cpq?["Inicio"] ?? "Dialogo",
                usuarioSdk = (string?)cpq?["Usuario"] ?? string.Empty,
                permitirEscritura = (bool?)cpq?["PermitirEscritura"] ?? false,
                permitirDocumentos = (bool?)cpq?["PermitirDocumentos"] ?? false,
                permitirTimbrado = (bool?)cpq?["PermitirTimbrado"] ?? false,
                claveCertificadoGuardada = !string.IsNullOrEmpty((string?)prot?["ClaveCertificado"]),
                llaveGuardada = !string.IsNullOrEmpty((string?)prot?["LlaveWorker"]),
                claveSdkGuardada = !string.IsNullOrEmpty((string?)prot?["ClaveSdk"]),
                configurado = ctx.Configurado,
                carpetaDatos = DatosLocales.Carpeta,
                versionSdk = "MGWServicios.dll (32 bits)",
            });
        });

        panel.MapGet("/empresas-locales", () =>
        {
            try
            {
                var lista = Directory.Exists(BaseEmpresas)
                    ? Directory.GetDirectories(BaseEmpresas).Select(Path.GetFileName).Where(n => n is not null && n.StartsWith("ad", StringComparison.OrdinalIgnoreCase)).Order().ToArray()
                    : [];
                return Results.Ok(lista);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Ok(Array.Empty<string>());
            }
        });

        panel.MapPost("/config", (SolicitudConfig c) =>
        {
            var error = Validar(c);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            var raiz = Leer();
            var orq = raiz["Orquestador"] as JsonObject ?? new JsonObject();
            var cpq = raiz["Contpaqi"] as JsonObject ?? new JsonObject();
            var prot = raiz["Protegidos"] as JsonObject ?? new JsonObject();

            orq["Url"] = c.Url!.Trim().TrimEnd('/');
            orq["WorkerId"] = (c.WorkerId ?? string.Empty).Trim();
            cpq["RutaEmpresa"] = Path.Combine(BaseEmpresas, c.Empresa!.Trim());
            cpq["RutaSdk"] = c.RutaSdk!.Trim();
            cpq["Inicio"] = c.Inicio;
            cpq["Usuario"] = (c.UsuarioSdk ?? string.Empty).Trim();
            cpq["PermitirEscritura"] = c.PermitirEscritura;
            // La escritura solo se habilita para la empresa elegida, y el código sigue exigiendo registros de prueba.
            cpq["EmpresasEscritura"] = c.PermitirEscritura ? new JsonArray(c.Empresa.Trim()) : new JsonArray();
            // Cada candado exige los anteriores: sin escritura no hay documentos, y sin documentos no hay timbrado.
            cpq["PermitirDocumentos"] = c.PermitirEscritura && c.PermitirDocumentos;
            cpq["PermitirTimbrado"] = c.PermitirEscritura && c.PermitirDocumentos && c.PermitirTimbrado;
            if (!string.IsNullOrEmpty(c.ClaveCertificado))
            {
                prot["ClaveCertificado"] = DatosLocales.Proteger(c.ClaveCertificado);
            }

            if (!string.IsNullOrWhiteSpace(c.LlaveWorker))
            {
                prot["LlaveWorker"] = DatosLocales.Proteger(c.LlaveWorker.Trim());
            }

            if (!string.IsNullOrEmpty(c.ClaveSdk))
            {
                prot["ClaveSdk"] = DatosLocales.Proteger(c.ClaveSdk);
            }

            raiz["Orquestador"] = orq;
            raiz["Contpaqi"] = cpq;
            raiz["Protegidos"] = prot;
            Directory.CreateDirectory(DatosLocales.Carpeta);
            File.WriteAllText(DatosLocales.RutaConfig, raiz.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return Results.Ok(new { guardado = true, requiereReinicio = true });
        });

        panel.MapPost("/probar", async (SolicitudProbar p) =>
        {
            if (!Uri.TryCreate((p.Url ?? string.Empty).Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                return Results.BadRequest(new { error = "La dirección no es válida. Use una como https://central.suempresa.com" });
            }

            var llave = string.IsNullOrWhiteSpace(p.Llave)
                ? DatosLocales.Revelar((Leer()["Protegidos"] as JsonObject)?["LlaveWorker"]?.GetValue<string>())
                : p.Llave.Trim();
            if (string.IsNullOrEmpty(llave))
            {
                return Results.BadRequest(new { error = "Escriba la llave del worker para probar la conexión." });
            }

            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(url.ToString().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
                using var peticion = new HttpRequestMessage(HttpMethod.Post, "api/auth/token");
                peticion.Headers.Add("x-api-key", llave);
                using var r = await http.SendAsync(peticion);
                if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return Results.Ok(new { ok = false, mensaje = "Acusa Central respondió, pero rechazó la llave. Revise que sea una llave de worker vigente." });
                }

                if (!r.IsSuccessStatusCode)
                {
                    return Results.Ok(new { ok = false, mensaje = $"Acusa Central respondió con el error {(int)r.StatusCode}." });
                }

                var token = await r.Content.ReadFromJsonAsync<TokenRespuesta>();
                var rol = RolDelToken(token?.Token);
                return rol == "worker"
                    ? Results.Ok(new { ok = true, mensaje = "Conexión correcta. La llave es de worker y está vigente." })
                    : Results.Ok(new { ok = false, mensaje = "La llave funciona, pero no es de tipo worker. Cree una llave de worker en el portal." });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Ok(new { ok = false, mensaje = "No se pudo llegar a Acusa Central. Revise la dirección y que este equipo tenga salida a internet." });
            }
        });

        panel.MapPost("/reiniciar", (IHostApplicationLifetime vida) =>
        {
            // El servicio de Windows se vuelve a levantar solo (recuperación configurada); en consola se abre de nuevo a mano.
            _ = Task.Run(async () => { await Task.Delay(500); vida.StopApplication(); });
            return Results.Ok(new { reiniciando = true });
        });
    }

    private static string? Validar(SolicitudConfig c)
    {
        if (!Uri.TryCreate((c.Url ?? string.Empty).Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return "La dirección de Acusa Central no es válida. Use una como https://central.suempresa.com";
        }

        var empresa = (c.Empresa ?? string.Empty).Trim();
        if (empresa.Length < 3 || empresa.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-' or ' ')))
        {
            return "Elija la empresa que atenderá este worker.";
        }

        if (string.IsNullOrWhiteSpace(c.RutaSdk))
        {
            return "Falta la carpeta de CONTPAQi Comercial.";
        }

        if (c.Inicio is not ("Dialogo" or "Credenciales"))
        {
            return "El inicio del SDK debe ser Dialogo o Credenciales.";
        }

        if (c.Inicio == "Credenciales" && string.IsNullOrWhiteSpace(c.UsuarioSdk))
        {
            return "El inicio con credenciales necesita el usuario de CONTPAQi.";
        }

        var guardada = (Leer()["Protegidos"] as JsonObject)?["LlaveWorker"] is not null;
        if (string.IsNullOrWhiteSpace(c.LlaveWorker) && !guardada)
        {
            return "Falta la llave del worker.";
        }

        if (!string.IsNullOrWhiteSpace(c.LlaveWorker) && !c.LlaveWorker.Trim().StartsWith("wk_", StringComparison.Ordinal))
        {
            return "La llave de worker empieza con wk_. ¿Copió una llave de tipo sistema?";
        }

        return null;
    }

    private static JsonObject Leer()
    {
        try
        {
            return File.Exists(DatosLocales.RutaConfig) && JsonNode.Parse(File.ReadAllText(DatosLocales.RutaConfig)) is JsonObject o ? o : new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new JsonObject();
        }
    }

    private static string? RolDelToken(string? token)
    {
        var partes = (token ?? string.Empty).Split('.');
        if (partes.Length != 3)
        {
            return null;
        }

        try
        {
            var s = partes[1].Replace('-', '+').Replace('_', '/');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')));
            return doc.RootElement.GetProperty("rol").GetString();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException)
        {
            return null;
        }
    }
}
