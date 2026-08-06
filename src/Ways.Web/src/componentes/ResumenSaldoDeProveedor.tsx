function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

/**
 * Figura de saldo de proveedor + el callout de saldo negativo (gasto colgante) — pieza
 * presentacional compartida entre `Proveedores.tsx` (panel completo, Admin-only) y `Compras.tsx`
 * (header agregado cuando el listado está filtrado por proveedor, decisión: la lectura del saldo
 * sigue `Politicas.OperacionDePos` igual que el resto de `/compras` — Vendedor/Supervisor/Admin
 * tienen que poder verlo, no solo Admin vía `/proveedores`, judgment-day stage-8 Slice 6).
 */
export function ResumenSaldoDeProveedor({ saldo }: { saldo: number }) {
  return (
    <div>
      <div className="small text-muted">Saldo (compras confirmadas menos gastos ligados)</div>
      <div className="fs-5">{formatearMoneda(saldo)}</div>
      {saldo < 0 && (
        <div className="small text-warning-emphasis">
          Saldo negativo: hay gastos de proveedor sin ligar a ninguna compra puntual — reducen el total, pero
          no marcan ninguna compra específica como pagada (aproximación, no invariante).
        </div>
      )}
    </div>
  )
}
