using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>
/// <see cref="BilletesArgentinos.EsVueltoJustificado"/> (decisión del dueño, 2026-09-16) —
/// pura, sin base de datos. La tabla de casos espeja la que usa <c>pagos.test.ts</c>
/// (<c>casosDeVueltoJustificado</c>) para que ambas implementaciones (dominio C# / espejo TS)
/// se verifiquen contra el mismo criterio.
/// </summary>
public class BilletesArgentinosTests
{
    public static IEnumerable<object[]> CasosJustificados()
    {
        // (efectivoEntregado, vuelto, descripción)
        yield return [10000m, 4500m, "ticket 5500, un billete de 10000"];
        yield return [6000m, 500m, "ticket 5500, tres billetes de 2000"];
        yield return [10000m, 2000m, "vuelto igual a una denominación, un solo billete alcanza"];
        yield return [10000m, 4500.50m, "total con centavos, efectivo múltiplo de 10"];
        yield return [10m, 0m, "vuelto cero siempre justificado"];
    }

    public static IEnumerable<object[]> CasosNoJustificados()
    {
        // (efectivoEntregado, vuelto, descripción)
        yield return [5520m, 20m, "todo billete > 20 es múltiplo de 50; 5520 no lo es"];
        yield return [30000m, 24500m, "ningún billete supera 24500"];
        yield return [11000m, 2000m, "11000 con billetes > 2000 no arma exacto"];
        yield return [4000m, 2000m, "vuelto igual a un billete pero no alcanza para justificar 4000"];
        yield return [5500.50m, 500.50m, "efectivo con centavos nunca es formable"];
        yield return [5505m, 500m, "efectivo no múltiplo de 10"];
        yield return [0m, 500m, "sin efectivo entregado no hay nada que formar"];
        yield return [BilletesArgentinos.EfectivoMaximo + 10m, BilletesArgentinos.EfectivoMaximo + 9m, "por encima del techo acotado"];
    }

    [Theory]
    [MemberData(nameof(CasosJustificados))]
    public void CasosJustificadosDevuelvenTrue(decimal efectivoEntregado, decimal vuelto, string descripcion)
    {
        Assert.True(BilletesArgentinos.EsVueltoJustificado(efectivoEntregado, vuelto), descripcion);
    }

    [Theory]
    [MemberData(nameof(CasosNoJustificados))]
    public void CasosNoJustificadosDevuelvenFalse(decimal efectivoEntregado, decimal vuelto, string descripcion)
    {
        Assert.False(BilletesArgentinos.EsVueltoJustificado(efectivoEntregado, vuelto), descripcion);
    }

    [Fact]
    public void VueltoIgualAUnBilleteNoAlcanzaParaJustificarElEfectivo()
    {
        // Mutation target (comentado en BilletesArgentinos.EsVueltoJustificado): el filtro
        // "billete > vuelto" es estricto. 4000 solo se arma con al menos un billete <= 2000 (dos
        // de 2000, o combinaciones más chicas) — ningún billete > 2000 solo (10000/20000) suma
        // exacto 4000. Evidencia de mutación: cambiar "> vuelto" a ">= vuelto" en
        // BilletesArgentinos admite el billete de 2000 y esta prueba pasa a fallar (4000 = 2×2000
        // se aceptaría). Mutado y revertido — ver reporte de la tarea.
        Assert.False(BilletesArgentinos.EsVueltoJustificado(4000m, 2000m));
    }

    [Fact]
    public void SinBilletesValidosPorEncimaDelVueltoNuncaEsJustificado()
    {
        // Ningún billete supera un vuelto de 20000 (el billete más grande) -> jamás formable.
        Assert.False(BilletesArgentinos.EsVueltoJustificado(20000m, 20000m));
    }

    [Fact]
    public void UnSoloBilleteQueCubreExactoElEfectivoEsFormable()
    {
        Assert.True(BilletesArgentinos.EsVueltoJustificado(20000m, 19999m));
    }
}
