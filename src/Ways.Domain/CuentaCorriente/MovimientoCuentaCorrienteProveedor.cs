namespace Ways.Domain.CuentaCorriente;

/// <summary>
/// Ledger de movimientos de cuenta corriente de proveedores (doc 10 §8-adjacent): la tabla que
/// reconstruye y audita <c>Proveedor.Saldo</c> (doc 10 principio 7), mismo criterio que
/// <see cref="MovimientoCuentaCorriente"/> sobre <c>Cliente.Saldo</c>. Inmutable una vez
/// insertado — ningún endpoint edita ni elimina una fila.
///
/// A propósito NO hereda de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>
/// — mismo family shape que <see cref="Ways.Domain.Stock.MovimientoStock"/> y
/// <see cref="MovimientoCuentaCorriente"/>: un ledger append-only no tiene
/// <c>updated_at</c>/<c>deleted_at</c> con sentido. Filtro de tenant escrito a mano en
/// <c>WaysDbContext.AplicarFiltroDeTenantEnMovimientoCuentaCorrienteProveedor</c> —
/// <c>IdTenant</c> se escribe explícito, nunca vía <c>EstamparTenant()</c> (stage-14 decisión 7).
///
/// Sin clave alterna y sin self-FK (design decisión 16, gate §B "No alternate key on this
/// table"): la reliquidación está fuera de alcance de esta etapa, así que ninguna tabla
/// referencia este ledger.
/// </summary>
public class MovimientoCuentaCorrienteProveedor
{
    public int Id { get; set; }
    public int IdTenant { get; set; }

    public int IdProveedor { get; set; }

    /// <summary><c>IRelojDelSistema</c>, sin <c>DEFAULT now()</c> a nivel columna — un default
    /// de base silenciaría <c>RelojFijo</c> en los tests (criterio de stage 14, aplicado
    /// verbatim). Excepción aceptada: la fila `apertura` de la migración usa <c>now()</c>
    /// crudo (proposal.md:605, no hay <c>IRelojDelSistema</c> en contexto de migración).</summary>
    public DateTimeOffset Fecha { get; set; }

    /// <summary><c>NULL</c> solo en <see cref="TipoMovimientoCcProveedor.Apertura"/> — provenance
    /// del movimiento, respaldado por <c>ck_movimientos_cuenta_corriente_proveedor_apertura</c>.
    /// </summary>
    public int? IdPuntoVenta { get; set; }

    /// <summary><c>NULL</c> solo en <see cref="TipoMovimientoCcProveedor.Apertura"/>.</summary>
    public int? IdEmpleado { get; set; }

    public TipoMovimientoCcProveedor Tipo { get; set; }

    /// <summary>'compra': la compra que originó la deuda. 'pago'/'ajuste': la compra imputada
    /// (opcional). <c>NULL</c> siempre en 'apertura'.</summary>
    public int? IdComprobanteCompra { get; set; }

    /// <summary>El gasto que materializó el pago — poblado únicamente en 'pago'.</summary>
    public int? IdGasto { get; set; }

    /// <summary>Con signo: positivo aumenta la deuda ('apertura' con saldo a favor exceptuado,
    /// 'compra'), negativo la reduce ('pago', 'ajuste').</summary>
    public decimal Importe { get; set; }

    /// <summary>Snapshot de <c>Proveedor.Saldo</c> al momento del INSERT — nunca se re-deriva
    /// (spec: Saldo Is The Single-Write-Authority Cache Of The Ledger).</summary>
    public decimal SaldoResultante { get; set; }

    /// <summary>Obligatorio en el ajuste manual (regla de servicio, <c>ReglaDeAjusteDeCuenta</c>)
    /// — no una CHECK de esquema.</summary>
    public string? Detalle { get; set; }
}
