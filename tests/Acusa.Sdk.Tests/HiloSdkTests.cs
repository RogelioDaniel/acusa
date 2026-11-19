using Acusa.Sdk.Integration.Contpaqi;

namespace Acusa.Sdk.Integration.Contpaqi.Tests;

public sealed class HiloSdkTests : IDisposable
{
    private readonly HiloSdk _hilo = new();

    public void Dispose() => _hilo.Dispose();

    [Fact]
    public async Task EjecutarAsync_VariasLlamadasConcurrentes_NuncaSeSolapan()
    {
        // Arrange
        var enEjecucion = 0;
        var maximoSimultaneo = 0;

        int Trabajo()
        {
            var actual = Interlocked.Increment(ref enEjecucion);
            InterlockedMax(ref maximoSimultaneo, actual);
            Thread.Sleep(5);
            Interlocked.Decrement(ref enEjecucion);
            return actual;
        }

        // Act
        var tareas = Enumerable.Range(0, 20).Select(_ => Task.Run(() => _hilo.EjecutarAsync(Trabajo))).ToArray();
        await Task.WhenAll(tareas);

        // Assert
        Assert.Equal(1, maximoSimultaneo);
    }

    [Fact]
    public async Task EjecutarAsync_LlamadasSucesivas_UsanSiempreElMismoHilo()
    {
        // Act
        var primero = await _hilo.EjecutarAsync(() => Environment.CurrentManagedThreadId);
        var segundo = await _hilo.EjecutarAsync(() => Environment.CurrentManagedThreadId);

        // Assert
        Assert.Equal(primero, segundo);
        Assert.NotEqual(Environment.CurrentManagedThreadId, primero);
    }

    [Fact]
    public async Task EjecutarAsync_LasLlamadasSeAtiendenEnElOrdenEnQueLlegan()
    {
        // Arrange
        var orden = new List<int>();

        // Act
        var tareas = Enumerable.Range(0, 10).Select(numero => _hilo.EjecutarAsync(() =>
        {
            orden.Add(numero);
            return numero;
        })).ToArray();
        await Task.WhenAll(tareas);

        // Assert
        Assert.Equal(Enumerable.Range(0, 10), orden);
    }

    [Fact]
    public async Task EjecutarAsync_ElTrabajoLanzaUnaExcepcion_LaPropagaYSigueAtendiendo()
    {
        // Act
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() => _hilo.EjecutarAsync<int>(() => throw new InvalidOperationException("falla del SDK")));
        var resultado = await _hilo.EjecutarAsync(() => 42);

        // Assert
        Assert.Equal("falla del SDK", excepcion.Message);
        Assert.Equal(42, resultado);
    }

    [Fact]
    public async Task EjecutarAsync_DespuesDeDispose_FallaConObjectDisposedException()
    {
        // Arrange
        _hilo.Dispose();

        // Act and assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _hilo.EjecutarAsync(() => 1));
    }

    private static void InterlockedMax(ref int destino, int valor)
    {
        int actual;
        do
        {
            actual = Volatile.Read(ref destino);
        }
        while (valor > actual && Interlocked.CompareExchange(ref destino, valor, actual) != actual);
    }
}
