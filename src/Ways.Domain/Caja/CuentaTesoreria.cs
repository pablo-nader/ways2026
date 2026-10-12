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

    /// <summary>Punto de partida de la cadena: el <c>Inicio</c> del primer <see cref="MovimientoCuenta"/>
    /// debe ser igual a este valor. El saldo actual es el <c>Final</c> del último movimiento, o este
    /// valor si la cuenta no tiene movimientos; nunca se suma a los movimientos. No debe modificarse
    /// una vez que la cuenta tiene movimientos: la base no lo impide, lo exige el servicio.</summary>
    public decimal SaldoInicial { get; set; }

    public bool Activo { get; set; } = true;
}
