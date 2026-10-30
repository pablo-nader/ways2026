namespace Ways.Domain.Caja;

/// <summary>
/// Ledger encadenado de tesorería (doc 10 §7, design: Table Shapes — write path D; ex
/// <c>cajaz</c> del legacy): el cierre escribe automáticamente una fila
/// <see cref="TipoMovimientoTesoreria.RetiroCaja"/> por turno (design: The Cierre Transaction),
/// encadenando <see cref="Inicio"/> desde el <see cref="Final"/> de la última fila de la MISMA
/// EMPRESA — <c>GastosOrigenFondosYTesoreriaPorEmpresa</c> re-ancla la cadena de
/// <see cref="IdPuntoVenta"/> a <see cref="IdEmpresa"/>: varios puntos de venta de la misma
/// empresa comparten un solo fondo de tesorería.
///
/// A propósito NO hereda de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>
/// — mismo criterio que <see cref="Ways.Domain.Stock.MovimientoStock"/>, con filtro de tenant
/// escrito a mano en <c>WaysDbContext.AplicarFiltroDeTenantEnMovimientoTesoreria</c>.
/// </summary>
public class MovimientoTesoreria
{
    public int Id { get; set; }
    public int IdTenant { get; set; }

    /// <summary>FK compuesta a <see cref="Ways.Domain.Organizacion.Empresa"/> — la cadena
    /// inicio→final (<c>GastosOrigenFondosYTesoreriaPorEmpresa</c>) pasa a encadenarse por
    /// <c>(id_tenant, id_empresa)</c>, no ya por <see cref="IdPuntoVenta"/>: varios puntos de venta
    /// de la misma empresa comparten UN solo fondo de tesorería.</summary>
    public int IdEmpresa { get; set; }

    /// <summary>De qué punto de venta se originó el movimiento (información de origen, ya no
    /// determina el encadenado). Nullable: el cierre y un pago de origen
    /// <see cref="Ways.Domain.Pagos.OrigenFondosPago.Tesoreria"/> (<c>ServicioDePagos</c>) lo
    /// pueblan los dos hoy; queda nullable para futuras entradas manuales de tesorería sin punto de
    /// venta (decisión 4, todavía fuera de alcance).</summary>
    public int? IdPuntoVenta { get; set; }
    public DateTimeOffset Fecha { get; set; }
    public TipoMovimientoTesoreria Tipo { get; set; }

    /// <summary>Turno que originó la fila. El cierre siempre lo puebla; desde
    /// stage-gastos-origen-fondos-pos (PR2), un pago de origen
    /// <see cref="Ways.Domain.Pagos.OrigenFondosPago.Tesoreria"/> también lo puebla (el turno
    /// abierto contra el que se registró el pago — trazabilidad). Nullable en el esquema para
    /// futuras entradas manuales de tesorería sin turno (decisión 4, todavía fuera de alcance).</summary>
    public int? IdTurnoCaja { get; set; }

    /// <summary>Pago que originó este movimiento cuando
    /// <see cref="Ways.Domain.Pagos.OrigenFondosPago.Tesoreria"/> paga directo desde tesorería
    /// (stage-gastos-origen-fondos-pos, PR2: <c>ServicioDePagos.EscribirMovimientoDeTesoreriaAsync</c>
    /// es el único escritor) — <c>null</c> para cualquier otro tipo de movimiento (cierre, ajuste
    /// manual, depósito). FK compuesta a <c>pagos.ak_pagos_id_pago_id_tenant</c>; único por fila
    /// vía <c>ux_movimientos_tesoreria_id_pago</c> (parcial, solo cuando no es nulo) — un pago
    /// nunca puede originar dos movimientos de tesorería.</summary>
    public int? IdPago { get; set; }

    public required string Concepto { get; set; }

    public decimal Inicio { get; set; }
    public decimal Ingreso { get; set; }
    public decimal Egreso { get; set; }

    /// <summary><c>inicio + ingreso − egreso</c> (design decisión 6, backstop de esquema
    /// <c>ck_movimientos_tesoreria_cadena</c>) — a diferencia de
    /// <see cref="ArqueoTurno.Diferencia"/>, se calcula en C# y se inserta: la CHECK es defensa
    /// en profundidad, no la fuente de verdad.</summary>
    public decimal Final { get; set; }

    public int IdEmpleado { get; set; }
}
