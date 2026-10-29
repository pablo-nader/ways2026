import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { MouseEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { clienteDeCargaSemanalDeHoras } from '../api/cargaSemanalDeHoras'
import type { SemanaDeCargaDeHoras } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { formatearImporte } from '../formato/importes'
import {
  armarSolicitud,
  claveDeCelda,
  editarCelda,
  errorDeGuardadoSemanal,
  erroresDeCeldas,
  etiquetaDeDia,
  etiquetaDeSemana,
  importeDeFila,
  sumarDias,
  textoDeCelda,
  totalDeHorasDeFila,
  totalDeHorasDelDia,
  type Ediciones,
} from './semanaDeCargaDeHoras'
import { esRechazoCierto } from './cuentaDePersonalErrores'

type Aviso = { tipo: 'exito' | 'atencion'; texto: string }

/** Salida pendiente de confirmación porque hay horas editadas sin guardar. */
type Puerta = { accion: () => void; disparador: HTMLElement | null }

const SIN_EDICIONES: Ediciones = new Map()

const ESTILO_PRIMERA_COLUMNA = { position: 'sticky', left: 0, zIndex: 1, background: 'var(--bs-body-bg)' } as const

function formatearHoras(horas: number): string {
  return String(horas).replace('.', ',')
}

function plural(cantidad: number, singular: string, pluralizado: string): string {
  return `${cantidad} ${cantidad === 1 ? singular : pluralizado}`
}

function claseDeCelda(editada: boolean, precargada: boolean, conError: boolean): string {
  const clases = ['form-control', 'form-control-sm', 'text-end']
  if (conError) clases.push('is-invalid')
  else if (editada) clases.push('border-warning', 'bg-warning-subtle')
  else if (precargada) clases.push('fst-italic', 'text-muted')

  return clases.join(' ')
}

/**
 * Carga semanal de horas del personal: filas = empleados activos, columnas = lunes a domingo. El horario
 * habitual precarga los días sin confirmar, pero nada se acredita hasta "Guardar semana".
 *
 * Contrato de invalidación: cambiar de semana y recargar tras guardar acuñan una generación nueva ANTES de
 * pedir, y toda respuesta tardía de una generación anterior se descarta. Mientras una escritura está en
 * vuelo (con su refresco) la pantalla queda inerte —celdas, botones, semana y enlaces—, así que la respuesta
 * propia de guardar no necesita gate: nada puede reemplazar lo que escribe y no navega.
 */
export function CargaSemanalDeHoras() {
  const navigate = useNavigate()

  const [semana, setSemana] = useState<SemanaDeCargaDeHoras | null>(null)
  const [cargando, setCargando] = useState(true)
  const [errorDeCarga, setErrorDeCarga] = useState('')
  const [errorDeGuardado, setErrorDeGuardado] = useState('')
  const [aviso, setAviso] = useState<Aviso | null>(null)
  const [ediciones, setEdiciones] = useState<Ediciones>(SIN_EDICIONES)
  const [guardando, setGuardando] = useState(false)
  const [puerta, setPuerta] = useState<Puerta | null>(null)
  /** La relectura posterior a guardar falló: lo que se ve es anterior al guardado y no se puede editar ni guardar. */
  const [vistaVieja, setVistaVieja] = useState(false)

  const generacion = useRef(0)
  /** Espejo síncrono de "hay un guardado en vuelo": la ÚNICA guarda de re-entrancia válida. */
  const guardandoRef = useRef(false)
  /** Última semana pedida (`null` = la actual): las flechas se calculan desde ella, no desde la que se ve. */
  const desdeVigente = useRef<string | null>(null)

  const cargar = useCallback(async (desde: string | null): Promise<boolean> => {
    const token = ++generacion.current
    desdeVigente.current = desde
    setCargando(true)
    setErrorDeCarga('')
    try {
      const resultado = await clienteDeCargaSemanalDeHoras.obtener(desde)
      if (generacion.current !== token) return false
      desdeVigente.current = resultado.desde
      setSemana(resultado)
      setEdiciones(SIN_EDICIONES)
      setVistaVieja(false)

      return true
    } catch (e) {
      if (generacion.current !== token) return false
      setErrorDeCarga(e instanceof ErrorApi && e.message.trim() ? e.message : 'No se pudo cargar la semana.')

      return false
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar(null)
  }, [cargar])

  const hayCambios = ediciones.size > 0

  useEffect(() => {
    if (!hayCambios) return

    const avisar = (evento: BeforeUnloadEvent) => evento.preventDefault()
    window.addEventListener('beforeunload', avisar)

    return () => window.removeEventListener('beforeunload', avisar)
  }, [hayCambios])

  const errores = useMemo(() => (semana ? erroresDeCeldas(semana, ediciones) : new Map<string, string>()), [semana, ediciones])
  const solicitud = useMemo(() => (semana ? armarSolicitud(semana, ediciones) : { dias: [] }), [semana, ediciones])

  const bloqueado = guardando || cargando || puerta !== null
  const edicionBloqueada = bloqueado || vistaVieja
  const puedeGuardar = !edicionBloqueada && semana !== null && errores.size === 0 && solicitud.dias.length > 0

  /** Con horas editadas sin guardar, la salida pregunta antes; el disparador se captura en el evento. */
  function conConfirmacion(accion: () => void, evento: MouseEvent<HTMLElement>) {
    if (!hayCambios) {
      accion()

      return
    }

    setPuerta({ accion, disparador: evento.currentTarget })
  }

  function irASemana(desde: string | null) {
    setAviso(null)
    setErrorDeGuardado('')
    void cargar(desde)
  }

  function moverSemana(dias: number, evento: MouseEvent<HTMLElement>) {
    const base = desdeVigente.current ?? semana?.desde
    if (!base) return

    conConfirmacion(() => irASemana(sumarDias(base, dias)), evento)
  }

  function confirmarSalida() {
    if (puerta === null) return
    const { accion } = puerta
    setPuerta(null)
    setEdiciones(SIN_EDICIONES)
    accion()
  }

  function descartar() {
    setEdiciones(SIN_EDICIONES)
    setErrorDeGuardado('')
  }

  function alEditar(idPersonal: number, fecha: string, bruto: string) {
    if (!semana) return
    const dia = semana.filas.find((f) => f.idPersonal === idPersonal)?.dias.find((d) => d.fecha === fecha)
    if (!dia) return

    setErrorDeGuardado('')
    setEdiciones((prev) => editarCelda(prev, idPersonal, dia, bruto))
  }

  async function guardar() {
    if (guardandoRef.current || semana === null) return
    guardandoRef.current = true
    setGuardando(true)
    setAviso(null)
    setErrorDeGuardado('')

    try {
      let resultado
      try {
        resultado = await clienteDeCargaSemanalDeHoras.guardar(solicitud)
      } catch (e) {
        if (esRechazoCierto(e)) {
          setErrorDeGuardado(errorDeGuardadoSemanal(e))

          return
        }

        // Sin confirmación del servidor: se relee la semana y quien lee decide, no se reintenta a ciegas.
        const vistaActualizada = await cargar(semana.desde)
        if (!vistaActualizada) setVistaVieja(true)
        setAviso({
          tipo: 'atencion',
          texto: vistaActualizada
            ? 'No se pudo confirmar si se guardó la semana. Revisá las horas que se muestran antes de volver a guardar.'
            : 'No se pudo confirmar si se guardó la semana y tampoco se pudo actualizar la vista: hasta que la semana se vuelva a cargar con Reintentar no se puede editar ni guardar.',
        })

        return
      }

      const vistaActualizada = await cargar(semana.desde)
      if (!vistaActualizada) setVistaVieja(true)
      setAviso(
        vistaActualizada
          ? {
              tipo: 'exito',
              texto: `Se guardó la semana: ${plural(resultado.jornadasConfirmadas, 'jornada confirmada', 'jornadas confirmadas')} y ${plural(resultado.movimientosCreados, 'movimiento registrado', 'movimientos registrados')}.`,
            }
          : { tipo: 'atencion', texto: 'Se guardó la semana, pero no se pudo actualizar la vista: hasta que la semana se vuelva a cargar con Reintentar no se puede editar ni guardar.' },
      )
    } finally {
      guardandoRef.current = false
      setGuardando(false)
    }
  }

  const totalDeHoras = semana ? semana.filas.reduce((suma, f) => suma + totalDeHorasDeFila(f, ediciones), 0) : 0
  const totalDeImporte = semana ? semana.filas.reduce((suma, f) => suma + importeDeFila(f, ediciones), 0) : 0

  const herramientas = (
    <nav className="d-flex flex-wrap align-items-center gap-2">
      <button
        type="button"
        className="btn btn-sm btn-outline-secondary"
        onClick={(evento) => moverSemana(-7, evento)}
        disabled={semana === null || guardando || puerta !== null}
      >
        « Semana anterior
      </button>
      <strong className="px-2" aria-live="polite">
        {semana ? etiquetaDeSemana(semana.desde, semana.hasta) : '—'}
      </strong>
      <button
        type="button"
        className="btn btn-sm btn-outline-secondary"
        onClick={(evento) => moverSemana(7, evento)}
        disabled={semana === null || guardando || puerta !== null}
      >
        Semana siguiente »
      </button>
      <button
        type="button"
        className="btn btn-sm btn-outline-secondary"
        onClick={(evento) => conConfirmacion(() => irASemana(null), evento)}
        disabled={guardando || puerta !== null}
      >
        Hoy
      </button>
      <Link
        to="/personal"
        className={`btn btn-sm btn-outline-secondary${bloqueado ? ' disabled' : ''}`}
        aria-disabled={bloqueado}
        tabIndex={bloqueado ? -1 : undefined}
        onClick={(evento) => {
          if (bloqueado) evento.preventDefault()
          else if (hayCambios) {
            evento.preventDefault()
            setPuerta({ accion: () => navigate('/personal'), disparador: evento.currentTarget })
          }
        }}
      >
        Personal
      </Link>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Carga semanal de horas" variante="inverse" herramientas={herramientas}>
        {errorDeCarga && (
          <div className="alert alert-danger d-flex align-items-center justify-content-between" role="alert">
            <span>{errorDeCarga}</span>
            <button
              type="button"
              className="btn btn-sm btn-outline-danger"
              onClick={() => irASemana(desdeVigente.current)}
              disabled={bloqueado}
            >
              Reintentar
            </button>
          </div>
        )}
        {errorDeGuardado && (
          <div className="alert alert-danger" role="alert">
            {errorDeGuardado}
          </div>
        )}
        {aviso && (
          <div className={`alert ${aviso.tipo === 'exito' ? 'alert-success' : 'alert-warning'}`} role="status">
            {aviso.texto}
          </div>
        )}

        {puerta && (
          <ConfirmacionDeBaja
            titulo="las horas sin guardar de la semana"
            pregunta="Descartar"
            nota="Cancelar vuelve a la edición."
            etiquetaConfirmar="Descartar cambios"
            etiquetaEnCurso="Descartando…"
            ocupado={false}
            disparador={puerta.disparador}
            onConfirmar={confirmarSalida}
            onCancelar={() => setPuerta(null)}
          />
        )}

        {semana === null ? (
          cargando && <Cargando texto="Cargando la semana…" />
        ) : (
          <>
            <p className="small text-muted mb-2">
              <span className="fst-italic">Cursiva gris</span>: precargado desde el horario habitual, se acredita al guardar.
              Resaltado: modificado. Los días futuros no se pueden cargar. Nada se acredita hasta guardar la semana.
            </p>

            {cargando && <Cargando texto="Cargando la semana…" />}

            {semana.filas.length === 0 ? (
              <p className="text-muted mb-0">No hay personal activo.</p>
            ) : (
              <div className="table-responsive">
                <table className="table table-sm align-middle">
                  <thead>
                    <tr>
                      <th scope="col" style={ESTILO_PRIMERA_COLUMNA}>
                        Empleado
                      </th>
                      {semana.filas[0].dias.map((d) => (
                        <th key={d.fecha} scope="col" className="text-center text-nowrap">
                          {etiquetaDeDia(d.fecha)}
                          {d.fecha === semana.hoy && <span className="badge text-bg-primary ms-1">hoy</span>}
                        </th>
                      ))}
                      <th scope="col" className="text-end">
                        Horas
                      </th>
                      <th scope="col" className="text-end">
                        Importe estimado
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {semana.filas.map((f) => (
                      <tr key={f.idPersonal}>
                        <th scope="row" className="text-nowrap" style={ESTILO_PRIMERA_COLUMNA}>
                          <Link
                            to={`/personal/${f.idPersonal}/cuenta`}
                            onClick={(evento) => {
                              if (guardando) evento.preventDefault()
                              else if (hayCambios) {
                                evento.preventDefault()
                                const destino = `/personal/${f.idPersonal}/cuenta`
                                setPuerta({ accion: () => navigate(destino), disparador: evento.currentTarget })
                              }
                            }}
                          >
                            {f.nombre}
                          </Link>
                        </th>
                        {f.dias.map((d) => {
                          const clave = claveDeCelda(f.idPersonal, d.fecha)
                          const editada = ediciones.has(clave)
                          const mensaje = errores.get(clave)
                          const precargada = d.precargado && !editada

                          return (
                            <td key={d.fecha} style={{ minWidth: '5rem' }}>
                              <input
                                type="text"
                                inputMode="decimal"
                                className={claseDeCelda(editada, precargada, mensaje !== undefined)}
                                aria-label={`Horas de ${f.nombre} el ${etiquetaDeDia(d.fecha)}`}
                                aria-invalid={mensaje !== undefined}
                                title={mensaje ?? (precargada ? 'Precargado desde el horario habitual' : undefined)}
                                value={d.futuro ? '' : textoDeCelda(f.idPersonal, d, ediciones)}
                                onChange={(evento) => alEditar(f.idPersonal, d.fecha, evento.target.value)}
                                disabled={edicionBloqueada || d.futuro}
                              />
                            </td>
                          )
                        })}
                        <td className="text-end text-nowrap">{formatearHoras(totalDeHorasDeFila(f, ediciones))}</td>
                        <td className="text-end text-nowrap">
                          {formatearImporte(importeDeFila(f, ediciones), { simbolo: true })}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                  <tfoot>
                    <tr>
                      <th scope="row" style={ESTILO_PRIMERA_COLUMNA}>
                        Total del día
                      </th>
                      {semana.filas[0]?.dias.map((d, indice) => (
                        <td key={d.fecha} className="text-center">
                          {formatearHoras(totalDeHorasDelDia(semana, indice, ediciones))}
                        </td>
                      ))}
                      <td className="text-end text-nowrap">{formatearHoras(totalDeHoras)}</td>
                      <td className="text-end text-nowrap">{formatearImporte(totalDeImporte, { simbolo: true })}</td>
                    </tr>
                  </tfoot>
                </table>
              </div>
            )}

            {errores.size > 0 && (
              <ul className="text-danger small" role="alert">
                {semana.filas.flatMap((f) =>
                  f.dias.flatMap((d) => {
                    const mensaje = errores.get(claveDeCelda(f.idPersonal, d.fecha))

                    return mensaje
                      ? [<li key={`${f.idPersonal}-${d.fecha}`}>{`${f.nombre}, ${etiquetaDeDia(d.fecha)}: ${mensaje}`}</li>]
                      : []
                  }),
                )}
              </ul>
            )}

            <div className="d-flex gap-2 mt-3">
              <button type="button" className="btn btn-primary" onClick={() => void guardar()} disabled={!puedeGuardar}>
                {guardando ? 'Guardando…' : 'Guardar semana'}
              </button>
              <button type="button" className="btn btn-outline-secondary" onClick={descartar} disabled={!hayCambios || edicionBloqueada}>
                Descartar cambios
              </button>
            </div>
          </>
        )}
      </Box>
    </div>
  )
}
