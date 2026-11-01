import type { LibroDeTesoreria } from '../../api/tipos'
import { claseDeImporte, columnasDeCuentas, saldoDeCuenta, textoDeImporte } from './libro'

type CuentaAAjustar = { id: number; nombre: string; saldoActual: number }

type Props = {
  libro: LibroDeTesoreria
  /** Solo el Admin ve los botones de ajuste. */
  esAdmin: boolean
  deshabilitado: boolean
  onAjustarEfectivo: () => void
  onAjustarCuenta: (cuenta: CuentaAAjustar) => void
}

type AccionDeTarjeta = { etiqueta: string; onClick: () => void; deshabilitado: boolean }

function Tarjeta({ titulo, monto, accion }: { titulo: string; monto: number; accion?: AccionDeTarjeta }) {
  return (
    <div className="libro-tarjeta" role="group" aria-label={titulo}>
      <div className="small text-muted">{titulo}</div>
      <div className={`libro-tarjeta__monto ${claseDeImporte(monto)}`}>{textoDeImporte(monto)}</div>
      <div className="small text-muted">Disponible ahora</div>
      {accion && (
        <button type="button" className="btn btn-sm btn-outline-secondary mt-2" disabled={accion.deshabilitado} onClick={accion.onClick}>
          {accion.etiqueta}
        </button>
      )}
    </div>
  )
}

/** Liquidez al día de hoy: el efectivo y cada cuenta, con su saldo actual (no el del final del rango). */
export function ResumenDeLiquidez({ libro, esAdmin, deshabilitado, onAjustarEfectivo, onAjustarCuenta }: Props) {
  return (
    <div className="d-flex flex-wrap gap-3 mb-3" role="region" aria-label="Liquidez">
      <Tarjeta
        titulo="Efectivo"
        monto={libro.saldoActual.efectivo}
        accion={esAdmin ? { etiqueta: 'Ajustar efectivo', deshabilitado, onClick: onAjustarEfectivo } : undefined}
      />
      {columnasDeCuentas(libro.cuentas).map((c, i) => {
        const nombre = libro.cuentas[i].nombre
        const saldoActual = saldoDeCuenta(libro.saldoActual, c.id)
        return (
          <Tarjeta
            key={c.id}
            titulo={c.titulo}
            monto={saldoActual}
            accion={
              esAdmin
                ? { etiqueta: `Ajustar ${nombre}`, deshabilitado, onClick: () => onAjustarCuenta({ id: c.id, nombre, saldoActual }) }
                : undefined
            }
          />
        )
      })}
    </div>
  )
}
