using System.Text.Json;
using System.Text.RegularExpressions;

namespace Acusa.Sdk.Tests;

public sealed class DocumentacionApiTests
{
    private static string Carpeta()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "Acusa.Sdk.slnx")))
        {
            directorio = directorio.Parent;
        }

        return Path.Combine(directorio!.FullName, "src", "Acusa.Orquestador", "wwwroot", "api-docs");
    }

    [Fact]
    public void OpenApiJson_RutasDelPortal_SonLasMismasQueLasDelYaml()
    {
        var carpeta = Carpeta();
        var yaml = File.ReadAllText(Path.Combine(carpeta, "openapi.yaml"));
        var rutasYaml = Regex.Matches(yaml, @"^  (/api/[^:]+):", RegexOptions.Multiline).Select(coincidencia => coincidencia.Groups[1].Value).ToHashSet();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(carpeta, "openapi.json")));
        var rutasJson = json.RootElement.GetProperty("paths").EnumerateObject().Select(propiedad => propiedad.Name).ToHashSet();

        Assert.NotEmpty(rutasYaml);
        Assert.Equal(rutasYaml.Order(), rutasJson.Order());
    }
}
