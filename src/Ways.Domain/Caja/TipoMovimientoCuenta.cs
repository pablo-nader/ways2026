namespace Ways.Domain.Caja;

/// <summary>
/// Tipo de un <see cref="MovimientoCuenta"/> (doc 10 §7). Enum nativo de Postgres
/// (<c>tipo_movimiento_cuenta</c>). Los valores se mapean por nombre a snake_case, así que el
/// orden de declaración no es significativo para el esquema.
/// </summary>
public enum TipoMovimientoCuenta
{
    Cobro,
    Comision,
    RetencionIibb,
    RetencionIva,
    RetencionGanancias,
    Ajuste,
    Transferencia,
    Deposito,
    Retiro
}
