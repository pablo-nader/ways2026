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
    public void NingunValorDeCategoriaGastoRepresentaUnRetiro()
    {
        var nombres = Enum.GetNames<CategoriaGasto>();

        Assert.All(nombres, nombre => Assert.DoesNotContain("retiro", nombre, StringComparison.OrdinalIgnoreCase));
    }
}
