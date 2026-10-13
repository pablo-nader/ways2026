import { derivarSaldoParaChip } from '../api/cuentaCorriente'
import { formatearImporte } from '../formato/importes'

type Props = {
  cliente: { saldo: number; limiteCredito: number; creditoIlimitado: boolean }
}

const moneda = (valor: number) => formatearImporte(valor, { simbolo: true })

/**
 * Saldo de cuenta corriente de un cliente como una píldora con barra de progreso: lo usado sobre el
 * límite. Con crédito ilimitado no hay barra; por encima del límite se pinta con el color de peligro.
 * El disponible va en el tooltip y en el texto accesible, que reemplaza al visual para el lector de pantalla.
 */
export function ChipDeSaldo({ cliente }: Props) {
  const d = derivarSaldoParaChip(cliente)
  const disponible = d.disponible === null ? 'sin límite' : moneda(d.disponible)
  const visible = d.limite === null ? `${moneda(d.saldo)} · sin límite` : `${moneda(d.saldo)} / ${moneda(d.limite)}`

  return (
    <div
      data-testid="chip-de-saldo"
      className={`chip-saldo${d.excedido ? ' text-danger border-danger' : ''}`}
      title={d.disponible === null ? 'Disponible: sin límite' : `Disponible ${disponible}`}
    >
      <span className="visually-hidden">{`Saldo ${moneda(d.saldo)} · Disponible ${disponible}`}</span>
      <span aria-hidden="true" className="chip-saldo-texto">
        {visible}
      </span>
      {d.proporcionUsada !== null && (
        <div className="progress" aria-hidden="true">
          <div className={`progress-bar${d.excedido ? ' bg-danger' : ''}`} style={{ width: `${d.proporcionUsada * 100}%` }} />
        </div>
      )}
    </div>
  )
}
