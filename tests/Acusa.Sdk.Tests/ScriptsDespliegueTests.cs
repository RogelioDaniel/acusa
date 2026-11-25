namespace Acusa.Sdk.Tests;

public sealed class ScriptsDespliegueTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static TheoryData<string> Scripts()
    {
        var carpeta = AppContext.BaseDirectory;
        while (carpeta is not null && !Directory.Exists(Path.Combine(carpeta, "deploy")))
        {
            carpeta = Path.GetDirectoryName(carpeta);
        }

        var datos = new TheoryData<string>();
        foreach (var script in Directory.GetFiles(Path.Combine(carpeta!, "deploy"), "*.ps1", SearchOption.AllDirectories))
        {
            datos.Add(script);
        }

        return datos;
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Script_TieneComoMaximoUnaMarcaBomAlInicio(string script)
    {
        var contenido = File.ReadAllBytes(script);
        var sinPrimera = contenido.AsSpan().StartsWith(Bom) ? contenido.AsSpan(3) : contenido.AsSpan();

        Assert.False(sinPrimera.StartsWith(Bom), $"{Path.GetFileName(script)} tiene la marca BOM repetida: PowerShell no reconoce su bloque param.");
        Assert.DoesNotContain((byte)0x0C, contenido);
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Script_ConParam_LoTieneComoPrimeraInstruccion(string script)
    {
        var lineas = File.ReadAllLines(script).Select(linea => linea.Trim()).ToList();
        if (!lineas.Any(linea => linea.StartsWith("param(", StringComparison.Ordinal)))
        {
            return;
        }

        var enBloque = false;
        string? primera = null;
        foreach (var linea in lineas)
        {
            if (enBloque)
            {
                enBloque = !linea.Contains("#>", StringComparison.Ordinal);
                continue;
            }

            if (linea.StartsWith("<#", StringComparison.Ordinal))
            {
                enBloque = !linea.Contains("#>", StringComparison.Ordinal);
                continue;
            }

            if (linea.Length > 0 && !linea.StartsWith('#') && !linea.StartsWith("[CmdletBinding", StringComparison.Ordinal))
            {
                primera = linea;
                break;
            }
        }


        Assert.StartsWith("param(", primera ?? string.Empty);
    }
}
