using Ways.Domain.Common;

namespace Ways.Domain.Caja;

/// <summary>
/// Cuenta digital de una empresa (Mercado Pago, banco) con su propio saldo corriente, distinta del
/// fondo físico de <see cref="MovimientoTesoreria"/> (doc 10 §7). Siempre pertenece a una empresa:
/// a diferencia de un catálogo, <see cref="IdEmpresa"/> no admite <c>NULL</c>.
/// </summary>
public class CuentaTesoreria : EntidadTenant
{
    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    public required string Nombre { get; set; }

    /// <summary>Saldo con el que la cuenta entra al sistema; el saldo actual es este valor más lo
    /// que acumulan sus <see cref="MovimientoCuenta"/>.</summary>
    public decimal SaldoInicial { get; set; }

    public bool Activo { get; set; } = true;
}
