using Ways.Domain.Pagos;

namespace Ways.Domain.Tests.Pagos;

/// <summary>
/// stage-6-turnos-caja, Slice 3 (task 3.4, spec: pagos / No Magic Tipo Encodes A Retiro As A
/// Pago): prueba de reflexión — el legacy's <c>tipo = 95</c> (retiro de efectivo disfrazado de
/// pago) no tiene representación posible en <see cref="CategoriaPago"/>. Un retiro SIEMPRE se
/// escribe en <c>movimientos_caja</c> (<c>TipoMovimientoCaja.Retiro</c>), nunca acá.
/// </summary>
public class CategoriaPagoTests
{
    [Fact]
    public void CategoriaPagoSoloContieneLosValoresDelWhitelistYNingunoRepresentaUnRetiro()
    {
        // Whitelist exacta (no blacklist de "retiro"): pinea el conjunto completo de miembros
        // para que un valor nuevo agregado al enum deba pasar explícitamente por este test.
        var nombresEsperados = new[]
        {
            nameof(CategoriaPago.Proveedor),
            nameof(CategoriaPago.Sueldos),
            nameof(CategoriaPago.Viaticos),
            nameof(CategoriaPago.Impuestos),
            nameof(CategoriaPago.Servicios),
            nameof(CategoriaPago.Otros)
        };

        var nombres = Enum.GetNames<CategoriaPago>();

        Assert.Equal(nombresEsperados.OrderBy(n => n), nombres.OrderBy(n => n));
    }
}
