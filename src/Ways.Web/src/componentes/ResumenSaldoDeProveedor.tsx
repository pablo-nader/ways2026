import { Link } from 'react-router'
import type { ProveedorListado } from '../api/tipos'

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

/** Un saldo negativo es "saldo a favor" (stage-15-cc-proveedores-ledger, design: Web Composition,
 * proposal decisión 5) — nunca clampeado a cero. Exportada para el test colocado
 * (`ResumenSaldoDeProveedor.test.tsx`, mutation target #28). */
export function esSaldoAFavor(saldo: number): boolean {
  return saldo < 0
}

/**
 * Figura de saldo de proveedor + el link al estado de cuenta completo — pieza presentacional
 * compartida entre `Proveedores.tsx` (panel completo, Admin-only) y `Compras.tsx` (header
 * agregado cuando el listado está filtrado por proveedor, decisión: la lectura del saldo sigue
 * `Politicas.OperacionDePos` igual que el resto de `/compras` — Vendedor/Supervisor/Admin tienen
 * que poder verlo, no solo Admin vía `/proveedores`, judgment-day stage-8 Slice 6).
 *
 * stage-15-cc-proveedores-ledger (Slice 6, design: Web Composition): re-apuntada al ledger — el
 * saldo ahora es la caché mantenida de `movimientos_cuenta_corriente_proveedor`
 * (`EscriturasDeCuentaCorrienteProveedor`, single-write-authority), no una aproximación derivada;
 * la caption ("compras confirmadas menos gastos ligados") y el callout ("aproximación, no
 * invariante") describían la fórmula RETIRADA por esta etapa — ambos se retiran acá. Sigue
 * puramente presentacional: sin fetch propio, `saldo`/`idProveedor`/`proveedor` son los únicos
 * inputs.
 *
 * `proveedor` (opcional): cuando el llamador ya tiene el `ProveedorListado` completo a mano
 * (`Proveedores.tsx`, `Compras.tsx` vía su índice por id), este es el ÚNICO punto de entrada real
 * a `/proveedores/:id/cuenta-corriente` — hay que pasarlo como `location.state.proveedor`, mismo
 * patrón que `Clientes.tsx` (`state={{ cliente: c }}`), para que la pantalla destino no dependa de
 * un GET Admin-only para mostrar el nombre (judgment-day stage-15 Slice 6, hallazgo CRITICAL: sin
 * esto, TODA navegación real llegaba con `state` null). Cuando el llamador no lo tiene, el Link va
 * sin `state` y la pantalla destino degrada con gracia (fallback "Proveedor #id"); nunca es un
 * bloqueo para operar.
 */
export function ResumenSaldoDeProveedor({
  saldo,
  idProveedor,
  proveedor,
}: {
  saldo: number
  idProveedor: number
  proveedor?: ProveedorListado
}) {
  return (
    <div>
      <div className="small text-muted">Saldo</div>
      <div className="fs-5">{formatearMoneda(saldo)}</div>
      {esSaldoAFavor(saldo) && <div className="small text-warning-emphasis">Saldo a favor.</div>}
      <Link
        className="small"
        to={`/proveedores/${idProveedor}/cuenta-corriente`}
        state={proveedor ? { proveedor } : undefined}
      >
        Ver estado de cuenta completo
      </Link>
    </div>
  )
}
