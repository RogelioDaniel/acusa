namespace Acusa.Orquestador.Facturacion;

/// <summary>
/// La petición REST que está atendiendo este hilo lógico (por ejemplo <c>POST /issuing_companies/1/invoices/5/stamps</c>). Las
/// órdenes que esa petición manda al worker la guardan en la tarea, para que el portal muestre la petición y no solo la operación.
/// </summary>
public static class PeticionActual
{
    private static readonly AsyncLocal<string?> Valor = new();

    public static string? Texto { get => Valor.Value; set => Valor.Value = value; }
}
