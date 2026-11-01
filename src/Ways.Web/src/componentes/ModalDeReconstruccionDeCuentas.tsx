import { useEffect, useRef, useState } from 'react'
import {
  claveDeReconstruccion,
  clienteDeReconstruccion,
  descuentosDeAcreditacion,
  errorAlReconstruir,
  nombreDeEmpresa,
  origenesDeReconstruccion,
  rangoDeReconstruccionPorDefecto,
  validarRangoDeReconstruccion,
  type AcreditacionDeReconstruccion,
  type DebitoDeReconstruccion,
  type PlanDeReconstruccion,
  type SolicitudDeReconstruccion,
} from '../api/cuentasTesoreria'
import type { EmpresaListado } from '../api/tipos'
import { fechaHoraATexto } from '../formato/fechas'
import { formatearImporte } from '../formato/importes'
import { CampoFecha } from './CampoFecha'
import { Modal } from './Modal'

type Props = {
  empresas: readonly EmpresaListado[]
  onCerrar: () => void
  /** Se llama una sola vez con el plan ya escrito; quien abrió el modal refresca los saldos. El modal queda abierto mostrando el resultado. */
  onReconstruido: (plan: PlanDeReconstruccion) => void
}

type VistaPrevia = { clave: string; solicitud: SolicitudDeReconstruccion; plan: PlanDeReconstruccion }

const importe = (valor: number) => formatearImporte(valor, { simbolo: true })

/**
 * Reconstrucción histórica del libro de las cuentas de tesorería (solo Admin): acredita, con la fecha de cierre del turno, los
 * cobros de turnos cerrados antes de que el medio tuviera cuenta, y debita, con la fecha del pago, los pagos que todavía no
 * debitaron la cuenta de su medio.
 *
 * `react-async-state`: la vista previa es una lectura con generación (cambiar empresa o fechas la invalida y descarta la
 * respuesta tardía) y solo habilita la confirmación si su clave es la de los campos actuales. La escritura lleva ref síncrona
 * de reentrancia y ventana completa inerte; su respuesta no lleva generación porque nada puede reemplazar lo que escribe, pero
 * sí se descarta si el modal se desmontó. Un fallo de la escritura descarta la vista previa: la reconstrucción es idempotente,
 * así que releer lo pendiente es lo seguro cuando no se sabe qué quedó escrito.
 */
export function ModalDeReconstruccionDeCuentas({ empresas, onCerrar, onReconstruido }: Props) {
  const [idEmpresa, setIdEmpresa] = useState(() => (empresas.length === 1 ? String(empresas[0].id) : ''))
  const [rango, setRango] = useState(() => rangoDeReconstruccionPorDefecto())
  const [vista, setVista] = useState<VistaPrevia | null>(null)
  const [leyendo, setLeyendo] = useState(false)
  const [escribiendo, setEscribiendo] = useState(false)
  const [resultado, setResultado] = useState<PlanDeReconstruccion | null>(null)
  const [error, setError] = useState('')

  const generacion = useRef(0)
  const escribiendoRef = useRef(false)
  const vigenteRef = useRef(true)

  useEffect(() => {
    vigenteRef.current = true
    return () => {
      vigenteRef.current = false
    }
  }, [])

  const clave = claveDeReconstruccion(idEmpresa, rango.desde, rango.hasta)
  const vistaVigente = vista !== null && vista.clave === clave ? vista : null
  const hayAlgoParaEscribir = vistaVigente !== null && vistaVigente.plan.filas > 0

  function cambiarCampos(cambios: { idEmpresa?: string; desde?: string; hasta?: string }) {
    // Invalida la lectura en vuelo y la vista previa: lo que se ve ya no corresponde a los campos.
    generacion.current++
    setLeyendo(false)
    setVista(null)
    setError('')
    if (cambios.idEmpresa !== undefined) setIdEmpresa(cambios.idEmpresa)
    if (cambios.desde !== undefined || cambios.hasta !== undefined) {
      setRango((previo) => ({ desde: cambios.desde ?? previo.desde, hasta: cambios.hasta ?? previo.hasta }))
    }
  }

  async function verVistaPrevia() {
    if (escribiendoRef.current) return

    const rechazo = validarRangoDeReconstruccion(idEmpresa, rango.desde, rango.hasta)
    if (rechazo) {
      setError(rechazo)
      return
    }

    const solicitud = { idEmpresa: Number(idEmpresa), desde: rango.desde, hasta: rango.hasta }
    const token = ++generacion.current
    setLeyendo(true)
    setError('')
    setVista(null)
    try {
      const plan = await clienteDeReconstruccion.vistaPrevia(solicitud)
      if (generacion.current !== token) return
      setVista({ clave, solicitud, plan })
    } catch (e) {
      if (generacion.current !== token) return
      setError(errorAlReconstruir(e, false))
    } finally {
      if (generacion.current === token) setLeyendo(false)
    }
  }

  async function confirmar() {
    if (escribiendoRef.current || !vistaVigente || !hayAlgoParaEscribir) return

    const { solicitud } = vistaVigente
    escribiendoRef.current = true
    setEscribiendo(true)
    setError('')
    try {
      const plan = await clienteDeReconstruccion.aplicar(solicitud)
      if (!vigenteRef.current) return
      setResultado(plan)
      setVista(null)
      onReconstruido(plan)
    } catch (e) {
      if (!vigenteRef.current) return
      generacion.current++
      setVista(null)
      setError(errorAlReconstruir(e, true))
    } finally {
      escribiendoRef.current = false
      if (vigenteRef.current) setEscribiendo(false)
    }
  }

  const pie = resultado ? (
    <button type="button" className="btn btn-primary" onClick={onCerrar}>
      Cerrar
    </button>
  ) : (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCerrar} disabled={escribiendo}>
        Cancelar
      </button>
      <button
        type="button"
        className="btn btn-outline-primary"
        onClick={() => void verVistaPrevia()}
        disabled={escribiendo || leyendo}
      >
        {leyendo ? 'Calculando…' : 'Vista previa'}
      </button>
      <button
        type="button"
        className="btn btn-success"
        onClick={() => void confirmar()}
        disabled={escribiendo || leyendo || !hayAlgoParaEscribir}
      >
        {escribiendo ? 'Reconstruyendo…' : 'Confirmar reconstrucción'}
      </button>
    </>
  )

  return (
    <Modal titulo="Reconstruir historial" tamano="xl" ocupado={escribiendo} pie={pie} onCerrar={onCerrar}>
      {error && (
        <div role="alert" className="alert alert-danger py-2 px-3 small">
          {error}
        </div>
      )}

      {resultado ? (
        <div role="status" className="alert alert-success">
          Reconstrucción lista: se escribieron {resultado.filas} {resultado.filas === 1 ? 'fila' : 'filas'} de{' '}
          {origenesDeReconstruccion(resultado.turnos, resultado.pagos)}. La pantalla vuelve a leer los saldos de las cuentas.
        </div>
      ) : (
        <>
          <p className="small text-muted">
            Acredita en las cuentas los cobros digitales de turnos ya cerrados que quedaron sin registrar porque el medio todavía
            no tenía cuenta. Se aplican los porcentajes de comisión y retenciones que tiene cada medio hoy, y cada fila se fecha
            el día en que se cerró el turno. También debita en la cuenta los pagos hechos con esos medios que todavía no la
            debitaron, fechados el día del pago. Correrlo de nuevo sobre el mismo rango no duplica nada.
          </p>

          <fieldset disabled={escribiendo} className="border-0 p-0 m-0">
            <div className="row g-3 mb-3">
              <div className="col-md-4">
                <label className="form-label" htmlFor="reconstruccion-empresa">
                  Empresa
                </label>
                <select
                  id="reconstruccion-empresa"
                  className="form-select"
                  value={idEmpresa}
                  onChange={(e) => cambiarCampos({ idEmpresa: e.target.value })}
                >
                  <option value="">Elegí una empresa</option>
                  {empresas.map((empresa) => (
                    <option key={empresa.id} value={empresa.id}>
                      {nombreDeEmpresa(empresa)}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-4">
                <label className="form-label" htmlFor="reconstruccion-desde">
                  Desde
                </label>
                <CampoFecha
                  id="reconstruccion-desde"
                  className="form-control"
                  value={rango.desde}
                  onChange={(valor) => cambiarCampos({ desde: valor })}
                />
              </div>
              <div className="col-md-4">
                <label className="form-label" htmlFor="reconstruccion-hasta">
                  Hasta
                </label>
                <CampoFecha
                  id="reconstruccion-hasta"
                  className="form-control"
                  value={rango.hasta}
                  onChange={(valor) => cambiarCampos({ hasta: valor })}
                />
              </div>
            </div>
          </fieldset>

          {vistaVigente && <VistaPreviaDeReconstruccion plan={vistaVigente.plan} />}
        </>
      )}
    </Modal>
  )
}

function VistaPreviaDeReconstruccion({ plan }: { plan: PlanDeReconstruccion }) {
  const hayCongelado = plan.congeladas.length > 0 || plan.debitosCongelados.length > 0

  if (plan.filas === 0) {
    if (!hayCongelado) {
      return <div className="alert alert-info">No hay turnos sin acreditar ni pagos sin debitar en ese rango.</div>
    }

    return (
      <>
        <div className="alert alert-info">
          No se puede escribir nada: lo pendiente de ese rango ya está conciliado y la conciliación lo congela.
        </div>
        <SeccionCongelada plan={plan} />
      </>
    )
  }

  return (
    <>
      <p className="small mb-2">
        Se escribirían <strong>{plan.filas}</strong> {plan.filas === 1 ? 'fila' : 'filas'} de{' '}
        {origenesDeReconstruccion(plan.turnos, plan.pagos)}.
      </p>

      {plan.acreditaciones.length > 0 && <TablaDeAcreditaciones acreditaciones={plan.acreditaciones} titulo="Acreditaciones" />}
      {plan.debitos.length > 0 && <TablaDeDebitos debitos={plan.debitos} titulo="Débitos de pagos" />}

      <h3 className="h6">Efecto por cuenta</h3>
      <div className="table-responsive">
        <table className="table table-sm table-bordered align-middle mb-0" aria-label="Efecto por cuenta">
          <thead>
            <tr>
              <th scope="col">Cuenta</th>
              <th scope="col" className="text-end">
                Filas
              </th>
              <th scope="col" className="text-end">
                Neto
              </th>
              <th scope="col" className="text-end">
                Saldo antes
              </th>
              <th scope="col" className="text-end">
                Saldo después
              </th>
            </tr>
          </thead>
          <tbody>
            {plan.cuentas.map((c) => (
              <tr key={c.idCuentaTesoreria}>
                <td>{c.nombre}</td>
                <td className="text-end">{c.filas}</td>
                <td className="text-end">{importe(c.neto)}</td>
                <td className="text-end">{importe(c.saldoAntes)}</td>
                <td className="text-end">{importe(c.saldoDespues)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {hayCongelado && <SeccionCongelada plan={plan} />}
    </>
  )
}

/** Lo que cae a la marca de conciliación o antes: se muestra para que se vea por qué no se escribe, nunca se cuenta. */
function SeccionCongelada({ plan }: { plan: PlanDeReconstruccion }) {
  const titulo = plan.conciliadoHasta
    ? `Congelado por la conciliación (hasta ${fechaHoraATexto(plan.conciliadoHasta)})`
    : 'Congelado por la conciliación'

  return (
    <section aria-label={titulo} className="mt-3">
      <h3 className="h6">{titulo}</h3>
      <p className="small text-muted">
        Turnos y pagos con fecha a la marca de conciliación o antes: la reconstrucción no los escribe.
      </p>
      {plan.congeladas.length > 0 && (
        <TablaDeAcreditaciones acreditaciones={plan.congeladas} titulo="Acreditaciones congeladas" />
      )}
      {plan.debitosCongelados.length > 0 && <TablaDeDebitos debitos={plan.debitosCongelados} titulo="Débitos congelados" />}
    </section>
  )
}

function TablaDeAcreditaciones({
  acreditaciones,
  titulo,
}: {
  acreditaciones: readonly AcreditacionDeReconstruccion[]
  titulo: string
}) {
  return (
    <div className="table-responsive mb-3">
      <table className="table table-sm table-striped table-bordered align-middle mb-0" aria-label={titulo}>
        <thead>
          <tr>
            <th scope="col">Cierre del turno</th>
            <th scope="col">Turno</th>
            <th scope="col">Medio</th>
            <th scope="col">Cuenta</th>
            <th scope="col" className="text-end">
              Cobro bruto
            </th>
            <th scope="col" className="text-end">
              Comisión
            </th>
            <th scope="col" className="text-end">
              Retenciones
            </th>
            <th scope="col" className="text-end">
              Neto
            </th>
          </tr>
        </thead>
        <tbody>
          {acreditaciones.map((a) => {
            const { comision, retenciones } = descuentosDeAcreditacion(a)

            return (
              <tr key={`${a.idTurnoCaja}-${a.idMedioPago}`}>
                <td>{fechaHoraATexto(a.fechaCierre)}</td>
                <td>#{a.idTurnoCaja}</td>
                <td>{a.nombreMedio}</td>
                <td>{a.nombreCuenta}</td>
                <td className="text-end">{importe(a.baseBruta)}</td>
                <td className="text-end">{importe(comision)}</td>
                <td className="text-end">{importe(retenciones)}</td>
                <td className="text-end">{importe(a.neto)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

function TablaDeDebitos({ debitos, titulo }: { debitos: readonly DebitoDeReconstruccion[]; titulo: string }) {
  return (
    <div className="table-responsive mb-3">
      <table className="table table-sm table-striped table-bordered align-middle mb-0" aria-label={titulo}>
        <thead>
          <tr>
            <th scope="col">Fecha del pago</th>
            <th scope="col">Pago</th>
            <th scope="col">Medio</th>
            <th scope="col">Cuenta</th>
            <th scope="col" className="text-end">
              Importe
            </th>
          </tr>
        </thead>
        <tbody>
          {debitos.map((d) => (
            <tr key={`${d.idPago}-${d.idMedioPago}`}>
              <td>{fechaHoraATexto(d.fecha)}</td>
              <td>{`#${d.idPago} · ${d.concepto}`}</td>
              <td>{d.nombreMedio}</td>
              <td>{d.nombreCuenta}</td>
              <td className="text-end">{importe(d.importe)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
