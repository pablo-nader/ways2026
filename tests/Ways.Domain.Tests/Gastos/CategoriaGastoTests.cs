using Ways.Domain.Gastos;

namespace Ways.Domain.Tests.Gastos;

/// <summary>
/// stage-6-turnos-caja, Slice 3 (task 3.4, spec: gastos / No Magic Tipo Encodes A Retiro As A
/// Gasto): prueba de reflexión — el legacy's <c>tipo = 95</c> (retiro de efectivo disfrazado de
/// gasto) no tiene representación posible en <see cref="CategoriaGasto"/>. Un retiro SIEMPRE se
/// escribe en <c>movimientos_caja</c> (<c>TipoMovimientoCaja.Retiro</c>), nunca acá.
/// </summary>
public class CategoriaGastoTests
{
    [Fact]
    public void CategoriaGastoSoloContieneLosValoresDelWhitelistYNingunoRepresentaUnRetiro()
    {
        // Whitelist exacta (no blacklist de "retiro"): pinea el conjunto completo de miembros
        // para que un valor nuevo agregado al enum deba pasar explícitamente por este test.
        var nombresEsperados = new[]
        {
            nameof(CategoriaGasto.Proveedor),
            nameof(CategoriaGasto.Sueldos),
            nameof(CategoriaGasto.Viaticos),
            nameof(CategoriaGasto.Impuestos),
            nameof(CategoriaGasto.Servicios),
            nameof(CategoriaGasto.Otros)
        };

        var nombres = Enum.GetNames<CategoriaGasto>();

        Assert.Equal(nombresEsperados.OrderBy(n => n), nombres.OrderBy(n => n));
    }
}
