import { Link } from 'react-router'
import type { DatosDeCajaDelLibro, FilaDelLibro, LibroDeTesoreria } from '../../api/tipos'
import { fechaHoraATexto } from '../../formato/fechas'
import { ConGlobo } from './ConGlobo'
import {
  claseDeImporte,
  columnasDeCuentas,
  detalleDeAjuste,
  estaConciliada,
  etiquetaDeCaja,
  hayFilasSinCuenta,
  hayRetenciones,
  importeDeCuenta,
  saldoDeCuenta,
  textoDeImporte,
} from './libro'

type Props = {
  libro: LibroDeTesoreria
  /** Solo el Admin puede abrir `/pagos`: para el resto el detalle del pago es texto plano. */
  puedeAbrirPagos: boolean
}

function Importe({ valor }: { valor: number | null | undefined }) {
  return <span className={claseDeImporte(valor)}>{textoDeImporte(valor)}</span>
}

function MarcaDeConciliado() {
  return (
    <span className="libro-candado" role="img" aria-label="Conciliado" title="Conciliado">
      <svg
        width="12"
        height="12"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="2.5"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
        focusable="false"
      >
        <rect x="5" y="11" width="14" height="10" rx="2" />
        <path d="M8 11V7a4 4 0 0 1 8 0v4" />
      </svg>
    </span>
  )
}

function DetalleDeCaja({ caja, id }: { caja: DatosDeCajaDelLibro; id: string }) {
  const globo = (
    <dl className="mb-0">
      <div>
        <dt className="d-inline fw-semibold">Apertura: </dt>
        <dd className="d-inline ms-0">{fechaHoraATexto(caja.fechaApertura)}</dd>
      </div>
      <div>
        <dt className="d-inline fw-semibold">Cierre: </dt>
        <dd className="d-inline ms-0">{fechaHoraATexto(caja.fechaCierre)}</dd>
      </div>
      <div>
        <dt className="d-inline fw-semibold">Punto de venta: </dt>
        <dd className="d-inline ms-0">{caja.puntoVenta ?? '(no disponible)'}</dd>
      </div>
    </dl>
  )

  return (
    <ConGlobo id={id} globo={globo}>
      {(accesibilidad) => (
        <Link to={`/caja/turnos/${caja.idTurnoCaja}/z`} target="_blank" rel="noopener noreferrer" {...accesibilidad}>
          {etiquetaDeCaja(caja.numero, caja.diferenciaArqueo)}
        </Link>
      )}
    </ConGlobo>
  )
}

function CeldaDeCuenta({ fila, idCuenta, id }: { fila: FilaDelLibro; idCuenta: number; id: string }) {
  const importe = importeDeCuenta(fila, idCuenta)
  const medios = fila.caja?.desglose.find((d) => d.idCuentaTesoreria === idCuenta)?.medios ?? []
  if (importe === null || medios.length === 0) return <Importe valor={importe} />

  const conRetenciones = hayRetenciones(medios)
  const globo = (
    <table className="globo-libro__tabla">
      <thead>
        <tr>
          <th scope="col">Medio</th>
          <th scope="col">Importe</th>
          <th scope="col">Comisión</th>
          {conRetenciones && <th scope="col">Retenciones</th>}
          <th scope="col">Neto</th>
        </tr>
      </thead>
      <tbody>
        {medios.map((m, i) => (
          <tr key={`${m.idMedioPago ?? 'sin-medio'}-${i}`}>
            <td>{m.medio ?? '(no disponible)'}</td>
            <td>{textoDeImporte(m.importe)}</td>
            <td>{textoDeImporte(m.comision)}</td>
            {conRetenciones && <td>{textoDeImporte(m.retenciones)}</td>}
            <td className={claseDeImporte(m.neto)}>{textoDeImporte(m.neto)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )

  return (
    <ConGlobo id={id} globo={globo}>
      {(accesibilidad) => (
        <span tabIndex={0} {...accesibilidad}>
          <Importe valor={importe} />
        </span>
      )}
    </ConGlobo>
  )
}

function Detalle({ fila, indice, puedeAbrirPagos }: { fila: FilaDelLibro; indice: number; puedeAbrirPagos: boolean }) {
  if (fila.tipo === 'Caja' && fila.caja) return <DetalleDeCaja caja={fila.caja} id={`libro-globo-caja-${indice}`} />

  if (fila.tipo === 'Pago' && fila.pago) {
    return puedeAbrirPagos ? (
      <Link to={`/pagos?id=${fila.pago.idPago}`} target="_blank" rel="noopener noreferrer">
        {fila.pago.detalle}
      </Link>
    ) : (
      <>{fila.pago.detalle}</>
    )
  }

  return <>{detalleDeAjuste(fila.ajuste?.concepto ?? '')}</>
}

/** Una columna de efectivo, una por cuenta y, si alguna fila la usa, "Sin cuenta"; la primera fila es el saldo anterior al rango. */
export function TablaDelLibro({ libro, puedeAbrirPagos }: Props) {
  const columnas = columnasDeCuentas(libro.cuentas)
  const conSinCuenta = hayFilasSinCuenta(libro)
  const saldoAnteriorCongelado = estaConciliada(libro.desde, libro.conciliadoHasta)

  return (
    <div className="table-responsive">
      <table className="table table-sm align-middle libro-tabla">
        <thead>
          <tr>
            <th scope="col">Fecha</th>
            <th scope="col">Detalle</th>
            <th scope="col" className="text-end">
              Efectivo
            </th>
            {columnas.map((c) => (
              <th scope="col" className="text-end" key={c.id}>
                {c.titulo}
              </th>
            ))}
            {conSinCuenta && (
              <th scope="col" className="text-end">
                Sin cuenta
              </th>
            )}
          </tr>
        </thead>
        <tbody>
          <tr data-fila="saldo-anterior" className={saldoAnteriorCongelado ? 'libro-fila--conciliada' : undefined}>
            <td>{saldoAnteriorCongelado && <MarcaDeConciliado />}</td>
            <td className="libro-detalle fw-semibold">Saldo anterior</td>
            <td className="text-end">
              <Importe valor={libro.saldoAnterior.efectivo} />
            </td>
            {columnas.map((c) => (
              <td className="text-end" key={c.id}>
                <Importe valor={saldoDeCuenta(libro.saldoAnterior, c.id)} />
              </td>
            ))}
            {conSinCuenta && <td />}
          </tr>
          {libro.filas.map((fila, i) => {
            const congelada = estaConciliada(fila.fecha, libro.conciliadoHasta)
            return (
              <tr key={`${fila.tipo}-${i}`} className={congelada ? 'libro-fila--conciliada' : undefined}>
                <td>
                  {congelada && <MarcaDeConciliado />}
                  {fechaHoraATexto(fila.fecha)}
                </td>
                <td className="libro-detalle">
                  <Detalle fila={fila} indice={i} puedeAbrirPagos={puedeAbrirPagos} />
                </td>
                <td className="text-end">
                  <Importe valor={fila.efectivo} />
                </td>
                {columnas.map((c) => (
                  <td className="text-end" key={c.id}>
                    <CeldaDeCuenta fila={fila} idCuenta={c.id} id={`libro-globo-cuenta-${i}-${c.id}`} />
                  </td>
                ))}
                {conSinCuenta && (
                  <td className="text-end">
                    <Importe valor={fila.sinCuenta} />
                  </td>
                )}
              </tr>
            )
          })}
          {libro.filas.length === 0 && (
            <tr>
              <td colSpan={columnas.length + 3 + (conSinCuenta ? 1 : 0)} className="text-center text-muted py-4">
                No hay movimientos en el rango elegido.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}
