using Ways.Domain.Common;

namespace Ways.Domain.Pagos;

/// <summary>
/// Pago operativo capturado contra un turno abierto (doc 10 §5/§7, design: Table Shapes —
/// write path C). Entidad de tenant (documento de autoría de usuario, no un ledger derivado) —
/// gana <c>updated_at</c>/baja lógica igual que <c>Cliente</c>/<c>Proveedor</c>, a diferencia de
/// los ledgers append-only de esta misma etapa.
///
/// <see cref="IdComprobanteCompra"/> aterriza en stage-8 Slice 1 (design: Table Shapes — D):
/// columna + FK compuesta juntas, mismo patrón que <see cref="Ways.Domain.Stock.MovimientoStock.IdComprobanteCompra"/>.
/// Poblado por <c>ServicioDePagos</c> (Slice 4) bajo el guard <c>SELECT ... FOR SHARE</c> sobre
/// el header de la compra (design decisión 7).
/// </summary>
public class Pago : EntidadTenant
{
    public int Id { get; set; }

    public DateTimeOffset Fecha { get; set; }

    /// <summary>FK compuesta a <see cref="Ways.Domain.Organizacion.Empresa"/> — resuelta
    /// server-side desde <see cref="IdPuntoVenta"/> (<c>PuntoVenta.IdEmpresa</c>), nunca input de
    /// cliente. Backfill de <c>GastosOrigenFondosYTesoreriaPorEmpresa</c> para las filas
    /// preexistentes.</summary>
    public int IdEmpresa { get; set; }

    /// <summary>Punto de venta de origen (FK compuesta). Nullable en el esquema desde
    /// <c>GastosOrigenFondosYTesoreriaPorEmpresa</c>, pero <c>ServicioDePagos.RegistrarAsync</c>
    /// siempre lo puebla hoy (viene de <see cref="SolicitudDePago"/>) — CajaTurno y Tesoreria
    /// nacen por igual contra un punto de venta puntual.</summary>
    public int? IdPuntoVenta { get; set; }

    /// <summary>Resuelto server-side del turno abierto — nunca input de cliente (spec: Pago
    /// Requires An Open Turno). Nullable en el esquema: solo <see cref="OrigenFondos"/> = <see
    /// cref="OrigenFondosPago.CajaTurno"/> lo EXIGE (<c>ck_pagos_caja_turno_requiere_turno</c>).
    /// stage-gastos-origen-fondos-pos (PR2): un pago de origen <see cref="OrigenFondosPago.Tesoreria"/>
    /// también lo trae poblado — sigue atado al turno abierto para trazabilidad — pero la CHECK no
    /// lo exige porque ese origen no descuenta el cajón.</summary>
    public int? IdTurnoCaja { get; set; }

    public int IdEmpleado { get; set; }

    public CategoriaPago Categoria { get; set; }

    /// <summary>De dónde salen los fondos (doc 10 §5/§7). <c>ServicioDePagos.RegistrarAsync</c>
    /// persiste el valor que trae <c>SolicitudDePago.OrigenFondos</c> (default <see
    /// cref="OrigenFondosPago.CajaTurno"/> por retrocompatibilidad) — <see
    /// cref="OrigenFondosPago.Tesoreria"/> excluye el pago de todo arqueo (<see
    /// cref="Ways.Application.Caja.LectorDeMovimientosDelTurno"/>) y le escribe su propio <see
    /// cref="Ways.Domain.Caja.MovimientoTesoreria"/>.</summary>
    public OrigenFondosPago OrigenFondos { get; set; }

    public int? IdProveedor { get; set; }
    public int? IdArea { get; set; }

    public required string Concepto { get; set; }
    public string? Detalle { get; set; }

    public int IdMedioPago { get; set; }

    public string? NumeroFactura { get; set; }

    /// <summary>Siempre <c>&gt; 0</c> (spec: Importe Must Be Positive, <c>ck_pagos_importe_positivo</c>).</summary>
    public decimal Importe { get; set; }

    /// <summary>Vincula el pago a la compra que paga (design decisión 7) — solo válido cuando
    /// <see cref="Categoria"/> es <see cref="CategoriaPago.Proveedor"/> y la compra está
    /// <c>confirmada</c>; el vínculo es historia, nunca bloquea la anulación de la compra
    /// (design decisión 6, la regla invertida).</summary>
    public int? IdComprobanteCompra { get; set; }

    /// <summary>Pago de personal que generó este pago (el movimiento <c>pago</c> del libro de personal es la
    /// cabecera del pago). Se escribe solo al insertar y nunca cambia: un pago con vínculo no se edita, no se
    /// da de baja ni se liga a una compra por sí solo, porque desalinearía los libros que el pago movió.</summary>
    public int? IdMovimientoPersonal { get; set; }
}
