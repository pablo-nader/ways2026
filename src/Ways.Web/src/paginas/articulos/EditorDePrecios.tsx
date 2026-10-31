import { Fragment, useCallback, useContext, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import { ErrorApi } from '../../api/cliente'
import { clienteDePrecios } from '../../api/precios'
import type { AlcanceDeFamilia, HistorialDePrecio, ListaPrecioListado, PrecioVigente } from '../../api/tipos'
import { CampoImporte } from '../../componentes/CampoImporte'
import { Cargando } from '../../componentes/Cargando'
import { ConfirmacionDeBaja } from '../../componentes/ConfirmacionDeBaja'
import { Modal } from '../../componentes/Modal'
import { TituloDeSeccion } from '../../componentes/TituloDeSeccion'
import { formatearImporte } from '../../formato/importes'
import { PuntoVentaContext } from '../../puntoVenta/PuntoVentaContext'
import { contextoDeAlcance, descripcionDeFamilia, mensajeDePrecioParaToda, type FamiliaDelArticulo } from './familia'
import { ordenarListasParaTabla } from './ordenDeListas'
import { PreguntaDeAlcance } from './PreguntaDeAlcance'
import { fechaHoraATexto } from '../../formato/fechas'
import { toast } from '../../toasts/toasts'

type EstadoDeLista = {
  monto: string
  programado: boolean
  vigenteDesde: string
  guardando: boolean
  refrescando: boolean
  error: string
  confirmarPendiente: boolean
  /** La pregunta de alcance está abierta (artículo miembro de una familia): `contexto` es con lo que se
   * abre, la familia conocida o el texto del servidor. Mientras está abierta el panel queda inerte. */
  preguntaDeAlcance: { contexto: string } | null
  /** El alcance ya elegido, recordado por si el servidor pide confirmar el reemplazo de un precio
   * programado: el reintento lo reenvía sin volver a preguntar. */
  alcance: AlcanceDeFamilia | null
}

function estadoDeListaVacio(): EstadoDeLista {
  return {
    monto: '',
    programado: false,
    vigenteDesde: '',
    guardando: false,
    refrescando: false,
    error: '',
    confirmarPendiente: false,
    preguntaDeAlcance: null,
    alcance: null,
  }
}

/** Lo que el borrador de una lista tiene que cumplir antes de pedir nada: el precio y, si se programa,
 * su fecha de vigencia. Vacío cuando está listo. */
function errorDelBorrador(estado: EstadoDeLista): string {
  const monto = Number(estado.monto)
  if (!estado.monto.trim() || Number.isNaN(monto)) return 'Ingresá un precio válido.'
  if (estado.programado && !estado.vigenteDesde) return 'Elegí la fecha de vigencia.'
  return ''
}

/**
 * Precio por lista (design: ABM Composition) — precio vigente + badge de pendiente (dentro del
 * panel expandido, no en la fila colapsada, para no multiplicar el N+1 de historial por cada
 * lista al cargar la pantalla) + sugerencia de margen (propone, nunca aplica sola) + alta/
 * programación con el flujo de `confirmarReemplazo` en el 409 `precio_pendiente_existe` +
 * historial. Las listas `derivada` no admiten alta propia (se resuelven en lectura): solo
 * muestran el precio vigente resuelto, sin acciones. Las manuales van primero en la tabla.
 *
 * Un artículo miembro de una familia (`familia`, doc 10 §3) no escribe un precio sin que se pregunte el
 * alcance: toda la familia, o solo este artículo, que sale de ella. El precio de un miembro que el cliente no
 * sabía miembro lo frena el servidor con 409 `alcance_requerido` y se pregunta igual; `familia_cambio`
 * (la pertenencia cambió) le pide al padre que recargue el artículo.
 */
export function EditorDePrecios({
  idArticulo,
  listasPrecio,
  bloqueadoPorPadre,
  alDeEscribir,
  familia,
  alSalirDeLaFamilia,
  alEscribirPrecio,
  alCambiarLaFamilia,
}: {
  idArticulo: number
  listasPrecio: ListaPrecioListado[]
  bloqueadoPorPadre: boolean
  alDeEscribir: (enCurso: boolean) => void
  /** La familia del artículo, o `null` si no es miembro de ninguna. */
  familia: FamiliaDelArticulo | null
  /** El precio se escribió con `SoloEste`: el artículo ya no es miembro. */
  alSalirDeLaFamilia: (nombre: string | null) => void
  /** Un precio del artículo se escribió bien (con cualquier alcance). */
  alEscribirPrecio: () => void
  alCambiarLaFamilia: () => void
}) {
  // Empresa del punto de venta de la sesión: la sugerencia redondea según su parámetro. Sin punto de venta
  // elegido (o fuera de la puerta de puntos de venta) se pide sin empresa y el servidor usa el redondeo por defecto.
  const idEmpresaDeSesion = useContext(PuntoVentaContext)?.puntoVenta?.idEmpresa
  const listasOrdenadas = ordenarListasParaTabla(listasPrecio)
  const [vigentes, setVigentes] = useState<Record<number, PrecioVigente>>({})
  const [cargandoVigentes, setCargandoVigentes] = useState(true)
  const [errorVigentes, setErrorVigentes] = useState('')
  const cargaInicialHechaRef = useRef(false)
  // Espejo sincrónico de "hay un precio escribiéndose o refrescándose" en cualquiera de las listas.
  const escribiendoRef = useRef(false)
  const generacionVigentesRef = useRef(0)
  const generacionSugerenciaRef = useRef(0)
  // Generación por lista: la carga inicial de `alternarExpandida` y el refresco post-guardado
  // de `guardarPrecio` compiten por la misma lista — sin esto, la que resuelve primero pero
  // arrancó antes puede pisar el historial ya actualizado por la otra.
  const generacionHistorialRef = useRef<Record<number, number>>({})
  const [listaExpandida, setListaExpandida] = useState<number | null>(null)
  const [historiales, setHistoriales] = useState<Record<number, HistorialDePrecio[]>>({})
  const [estados, setEstados] = useState<Record<number, EstadoDeLista>>({})
  const [sugerencia, setSugerencia] = useState<number | null>(null)
  const [sinSugerencia, setSinSugerencia] = useState(false)
  const [cargandoSugerencia, setCargandoSugerencia] = useState(false)
  const [errorSugerencia, setErrorSugerencia] = useState('')

  const cargarVigentes = useCallback(
    async (opciones?: { relanzarError?: boolean }) => {
      // Generación: si mientras esta llamada está en vuelo se dispara otra (dos paneles
      // abriéndose/refrescando en simultáneo), la que llega tarde no debe pisar el estado
      // compartido con una respuesta desactualizada — solo aplica la más reciente en curso.
      const generacion = (generacionVigentesRef.current += 1)
      setCargandoVigentes(true)
      setErrorVigentes('')
      try {
        const lista = await clienteDePrecios.vigentes(idArticulo)
        if (generacionVigentesRef.current !== generacion) return
        const mapa: Record<number, PrecioVigente> = {}
        for (const p of lista) mapa[p.idListaPrecio] = p
        setVigentes(mapa)
      } catch (e) {
        if (generacionVigentesRef.current === generacion) {
          setErrorVigentes(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los precios vigentes.')
        }
        if (opciones?.relanzarError && generacionVigentesRef.current === generacion) throw e
      } finally {
        if (generacionVigentesRef.current === generacion) {
          setCargandoVigentes(false)
          cargaInicialHechaRef.current = true
        }
      }
    },
    [idArticulo],
  )

  useEffect(() => {
    void cargarVigentes()
  }, [cargarVigentes])

  function estadoDe(idLista: number): EstadoDeLista {
    return estados[idLista] ?? estadoDeListaVacio()
  }

  function actualizarEstado(idLista: number, parcial: Partial<EstadoDeLista>) {
    setEstados((prev) => ({ ...prev, [idLista]: { ...(prev[idLista] ?? estadoDeListaVacio()), ...parcial } }))
  }

  async function alternarExpandida(lista: ListaPrecioListado) {
    const abrir = listaExpandida !== lista.id
    setListaExpandida(abrir ? lista.id : null)

    if (abrir && !historiales[lista.id]) {
      const generacion = (generacionHistorialRef.current[lista.id] = (generacionHistorialRef.current[lista.id] ?? 0) + 1)
      try {
        const historial = await clienteDePrecios.historial(idArticulo, lista.id)
        if (generacionHistorialRef.current[lista.id] !== generacion) return
        setHistoriales((prev) => ({ ...prev, [lista.id]: historial }))
      } catch (e) {
        if (generacionHistorialRef.current[lista.id] !== generacion) return
        actualizarEstado(lista.id, { error: e instanceof ErrorApi ? e.message : 'No se pudo cargar el historial.' })
      }
    }
  }

  async function pedirSugerencia() {
    if (cargandoSugerencia || bloqueadoPorPadre) return
    // Generación: protege contra una respuesta tardía de un pedido anterior pisando una
    // sugerencia más reciente que el usuario ya podría haber aplicado al borrador de precio.
    const generacion = (generacionSugerenciaRef.current += 1)
    setCargandoSugerencia(true)
    setErrorSugerencia('')
    setSinSugerencia(false)
    try {
      const { precioSugerido } = await clienteDeArticulos.sugerenciaDePrecio(idArticulo, idEmpresaDeSesion)
      if (generacionSugerenciaRef.current !== generacion) return
      setSugerencia(precioSugerido)
      setSinSugerencia(precioSugerido === null)
    } catch (e) {
      if (generacionSugerenciaRef.current === generacion) {
        setSugerencia(null)
        setErrorSugerencia(e instanceof ErrorApi ? e.message : 'No se pudo calcular la sugerencia de precio.')
      }
    } finally {
      if (generacionSugerenciaRef.current === generacion) setCargandoSugerencia(false)
    }
  }

  /** Un clic en "Establecer ahora" o "Programar": valida el borrador y, si el artículo es miembro de una
   * familia, pregunta el alcance en vez de escribir. Quien no es miembro escribe directo. */
  function iniciarGuardado(idLista: number) {
    const estado = estadoDe(idLista)
    const error = errorDelBorrador(estado)
    if (error) {
      actualizarEstado(idLista, { error })
      return
    }

    if (familia === null) {
      void guardarPrecio(idLista, false, undefined)
      return
    }

    actualizarEstado(idLista, { error: '', preguntaDeAlcance: { contexto: contextoDeAlcance(familia) } })
  }

  async function guardarPrecio(idLista: number, confirmarReemplazo: boolean, alcance: AlcanceDeFamilia | undefined) {
    // El espejo sincrónico va primero: dos clics en el mismo tick pasan ambos la guarda de estado de abajo, que
    // recién se actualiza en el próximo render (react-async-state regla 11).
    if (escribiendoRef.current) return
    const estado = estadoDe(idLista)
    if (estado.guardando || estado.refrescando || bloqueadoPorPadre) return
    const monto = Number(estado.monto)

    const errorDeBorrador = errorDelBorrador(estado)
    if (errorDeBorrador) {
      actualizarEstado(idLista, { error: errorDeBorrador })
      return
    }

    escribiendoRef.current = true
    // El reemplazo confirmado deja abierta su confirmación mientras escribe: si el servidor lo rechaza, el motivo se
    // muestra adentro. Cierra con el éxito (el estado de la lista se reinicia) o con las ramas que la reemplazan.
    actualizarEstado(idLista, {
      guardando: true,
      error: '',
      confirmarPendiente: confirmarReemplazo && estado.confirmarPendiente,
      preguntaDeAlcance: null,
    })
    alDeEscribir(true)

    try {
      try {
        if (estado.programado) {
          await clienteDePrecios.programar(idArticulo, {
            idListaPrecio: idLista,
            precio: monto,
            vigenteDesde: new Date(estado.vigenteDesde).toISOString(),
            confirmarReemplazo,
            ...(alcance === undefined ? {} : { alcance }),
          })
        } else {
          await clienteDePrecios.establecer(idArticulo, {
            idListaPrecio: idLista,
            precio: monto,
            confirmarReemplazo,
            ...(alcance === undefined ? {} : { alcance }),
          })
        }
      } catch (e) {
        if (e instanceof ErrorApi && e.codigo === 'precio_pendiente_existe') {
          actualizarEstado(idLista, { guardando: false, confirmarPendiente: true, alcance: alcance ?? null })
          return
        }
        // El artículo es miembro de una familia que el cliente no conocía: la pregunta que no se hizo antes se
        // hace ahora, con el texto del servidor, que nombra la familia y cuántos artículos tiene.
        if (e instanceof ErrorApi && e.codigo === 'alcance_requerido') {
          actualizarEstado(idLista, { guardando: false, confirmarPendiente: false, preguntaDeAlcance: { contexto: e.message } })
          return
        }
        // La pertenencia cambió: lo escrito ya no corresponde al estado real del artículo. El padre recarga el
        // artículo (y con él este editor), así que no queda nada que mostrar acá.
        if (e instanceof ErrorApi && e.codigo === 'familia_cambio') {
          actualizarEstado(idLista, { guardando: false, confirmarPendiente: false })
          alCambiarLaFamilia()
          return
        }
        actualizarEstado(idLista, {
          guardando: false,
          error: e instanceof ErrorApi ? e.message : 'No se pudo guardar el precio.',
        })
        return
      }

      alEscribirPrecio()
      const nombreDeLaFamilia = familia?.nombre ?? null
      if (alcance === 'SoloEste') alSalirDeLaFamilia(nombreDeLaFamilia)
      // La tabla solo muestra los precios de este artículo: lo que pasó con los demás miembros hay que decirlo.
      if (alcance === 'Familia') toast.exito(mensajeDePrecioParaToda(estado.programado, nombreDeLaFamilia))

      // El precio ya quedó confirmado en el servidor: a partir de acá un fallo es solo de refresco
      // de vista, nunca "no se guardó" — evita que el usuario reintente un guardado que ya se aplicó.
      // `refrescando` mantiene el panel inerte hasta que el refresco termine, para no habilitar un
      // segundo guardado que corra en paralelo con este refresco.
      setEstados((prev) => ({ ...prev, [idLista]: { ...estadoDeListaVacio(), refrescando: true } }))

      try {
        await cargarVigentes({ relanzarError: true })
        const generacion = (generacionHistorialRef.current[idLista] = (generacionHistorialRef.current[idLista] ?? 0) + 1)
        const historial = await clienteDePrecios.historial(idArticulo, idLista)
        if (generacionHistorialRef.current[idLista] === generacion) {
          setHistoriales((prev) => ({ ...prev, [idLista]: historial }))
        }
        actualizarEstado(idLista, { refrescando: false })
      } catch {
        actualizarEstado(idLista, {
          refrescando: false,
          error: 'El precio se guardó, pero no se pudo actualizar la vista. Cerrá y volvé a abrir la lista para verlo.',
        })
      }
    } finally {
      // Sin gate: la reentrancia se destraba siempre, también cuando el refresco falló o el artículo cambió.
      escribiendoRef.current = false
      alDeEscribir(false)
    }
  }

  // Solo la carga inicial gatea todo el panel: los refrescos posteriores (p.ej. tras guardar un
  // precio) mantienen el panel montado para no perder el foco ni parpadear en el camino feliz.
  if (cargandoVigentes && !cargaInicialHechaRef.current) return <Cargando texto="Cargando precios…" />

  return (
    <div>
      <div className="d-flex align-items-center justify-content-between">
        <TituloDeSeccion className="mb-0">Precios por lista</TituloDeSeccion>
        <button
          type="button"
          className="btn btn-sm btn-outline-secondary"
          disabled={cargandoSugerencia || bloqueadoPorPadre}
          onClick={pedirSugerencia}
        >
          {cargandoSugerencia ? 'Calculando…' : 'Calcular sugerencia de precio'}
        </button>
      </div>

      {sugerencia !== null && (
        <div className="alert alert-info py-2 px-2 small mt-2">
          Precio sugerido a partir de costo y margen, redondeado al múltiplo configurado: <strong>{formatearImporte(sugerencia, { simbolo: true })}</strong>. Usá "Usar sugerencia"
          en la lista que corresponda — nunca se aplica sola.
        </div>
      )}

      {sinSugerencia && (
        <div className="alert alert-info py-2 px-2 small mt-2">
          No hay costo o margen suficientes para sugerir un precio.
        </div>
      )}

      {errorSugerencia && <div className="alert alert-danger py-2 px-2 small mt-2">{errorSugerencia}</div>}

      {errorVigentes && <div className="alert alert-danger mt-2">{errorVigentes}</div>}

      {familia !== null && (
        <p className="small text-body-secondary mb-0 mt-2">
          Este artículo es parte {descripcionDeFamilia(familia)}: al cambiar un precio se pregunta si se aplica a toda la familia.
        </p>
      )}

      <div className="table-responsive mt-2">
        <table className="table table-sm table-bordered align-middle mb-0">
          <thead>
            <tr>
              <th>Lista</th>
              <th>Precio vigente</th>
              <th className="text-end">Acciones</th>
            </tr>
          </thead>
          <tbody>
            {listasOrdenadas.map((lista) => {
              const vigente = vigentes[lista.id]
              const expandida = listaExpandida === lista.id
              const listaBase = lista.idListaBase !== null ? listasPrecio.find((l) => l.id === lista.idListaBase) : null

              return (
                <Fragment key={lista.id}>
                  <tr>
                    <td>
                      {lista.nombre}
                      {lista.esDefault && <span className="badge text-bg-secondary ms-1">Default</span>}
                      {lista.modo === 'Derivada' && (
                        <div className="text-muted small">
                          Derivada de {listaBase?.nombre ?? lista.idListaBase} (
                          {(lista.porcentaje ?? 0) >= 0 ? '+' : ''}
                          {lista.porcentaje}%)
                        </div>
                      )}
                    </td>
                    <td>{vigente?.precio !== null && vigente?.precio !== undefined ? formatearImporte(vigente.precio, { simbolo: true }) : '—'}</td>
                    <td className="text-end">
                      {lista.modo === 'Fija' && (
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-primary"
                          onClick={() => alternarExpandida(lista)}
                          disabled={bloqueadoPorPadre || estadoDe(lista.id).guardando || estadoDe(lista.id).refrescando}
                        >
                          {expandida ? 'Cerrar' : 'Gestionar'}
                        </button>
                      )}
                    </td>
                  </tr>
                  {expandida && (
                    <tr>
                      <td colSpan={3} className="bg-body-tertiary">
                        <PanelDeLista
                          lista={lista}
                          estado={estadoDe(lista.id)}
                          historial={historiales[lista.id] ?? []}
                          sugerencia={sugerencia}
                          cargandoSugerencia={cargandoSugerencia}
                          bloqueadoPorPadre={bloqueadoPorPadre}
                          onCambio={(parcial) => actualizarEstado(lista.id, parcial)}
                          onGuardar={(confirmarReemplazo) =>
                            confirmarReemplazo
                              ? void guardarPrecio(lista.id, true, estadoDe(lista.id).alcance ?? undefined)
                              : iniciarGuardado(lista.id)
                          }
                          onElegirAlcance={(alcance) => void guardarPrecio(lista.id, false, alcance)}
                        />
                      </td>
                    </tr>
                  )}
                </Fragment>
              )
            })}
            {listasPrecio.length === 0 && (
              <tr>
                <td colSpan={3} className="text-center text-muted py-3">
                  No hay listas de precio activas.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function PanelDeLista({
  lista,
  estado,
  historial,
  sugerencia,
  cargandoSugerencia,
  bloqueadoPorPadre,
  onCambio,
  onGuardar,
  onElegirAlcance,
}: {
  lista: ListaPrecioListado
  estado: EstadoDeLista
  historial: HistorialDePrecio[]
  sugerencia: number | null
  cargandoSugerencia: boolean
  bloqueadoPorPadre: boolean
  onCambio: (parcial: Partial<EstadoDeLista>) => void
  onGuardar: (confirmarReemplazo: boolean) => void
  onElegirAlcance: (alcance: AlcanceDeFamilia) => void
}) {
  const ahora = new Date()
  const filaAbierta = historial.find((h) => h.vigenteHasta === null)
  const pendiente = filaAbierta && new Date(filaAbierta.vigenteDesde) > ahora ? filaAbierta : null
  // Una escritura en vuelo (o un refresco, o el padre ocupado) deja inertes las tres respuestas de la pregunta
  // de alcance; la pregunta abierta, además, deja inerte el resto del panel: el borrador sobre el que se
  // pregunta no puede cambiar mientras se decide.
  const enVuelo = estado.guardando || estado.refrescando || bloqueadoPorPadre
  const preguntaAbierta = estado.preguntaDeAlcance !== null
  const bloqueado = enVuelo || preguntaAbierta
  // Elemento (y no ref) para pasarlo como dato a la confirmación del reemplazo, que se abre tras la respuesta del servidor.
  const [botonDeGuardar, setBotonDeGuardar] = useState<HTMLButtonElement | null>(null)
  const cancelarPreguntaRef = useRef<HTMLButtonElement>(null)
  const devolverElFocoRef = useRef(false)
  const puertaAbierta = preguntaAbierta || estado.confirmarPendiente

  // Al abrirse la pregunta el foco va a "Cancelar", la respuesta que no escribe nada.
  useEffect(() => {
    if (preguntaAbierta) cancelarPreguntaRef.current?.focus()
  }, [preguntaAbierta])

  // Al cerrarse la pregunta (o la confirmación del reemplazo) el foco vuelve al botón que la abrió, recién cuando el
  // panel vuelve a ser operable: tras una respuesta, cuando terminaron la escritura y su refresco (antes, el botón
  // sigue deshabilitado y no recibe el foco).
  useEffect(() => {
    if (puertaAbierta) {
      devolverElFocoRef.current = true
      return
    }
    if (devolverElFocoRef.current && !enVuelo) {
      devolverElFocoRef.current = false
      botonDeGuardar?.focus()
    }
  }, [puertaAbierta, enVuelo, botonDeGuardar])

  return (
    <div className="p-2">
      {pendiente && (
        <div className="alert alert-warning py-1 px-2 small">
          Precio programado: {formatearImporte(pendiente.precio, { simbolo: true })} desde {fechaHoraATexto(pendiente.vigenteDesde)}
        </div>
      )}

      {estado.error && !estado.confirmarPendiente && (
        <div className="alert alert-danger py-1 px-2 small">{estado.error}</div>
      )}

      {estado.confirmarPendiente && (
        <ConfirmacionDeBaja
          titulo={`el precio programado de la lista "${lista.nombre}"`}
          pregunta="Reemplazar"
          nota={
            estado.alcance === 'Familia'
              ? 'Ya existe un precio programado para esta lista en algún artículo de la familia.'
              : 'Ya existe un precio programado para esta lista.'
          }
          etiquetaConfirmar="Reemplazar"
          etiquetaEnCurso="Reemplazando…"
          ocupado={enVuelo}
          disparador={botonDeGuardar}
          error={estado.error || null}
          onConfirmar={() => onGuardar(true)}
          onCancelar={() => onCambio({ confirmarPendiente: false, error: '' })}
        />
      )}

      {estado.preguntaDeAlcance && (
        <Modal titulo="Alcance del precio" ocupado={enVuelo} onCerrar={() => onCambio({ preguntaDeAlcance: null })}>
          <PreguntaDeAlcance
            contexto={estado.preguntaDeAlcance.contexto}
            ocupado={enVuelo}
            refDeCancelar={cancelarPreguntaRef}
            onElegir={onElegirAlcance}
            onCancelar={() => onCambio({ preguntaDeAlcance: null })}
          />
        </Modal>
      )}

      <div className="row g-2 align-items-end">
        <div className="col-auto">
          <label className="form-label mb-0 small" htmlFor={`lp-precio-${lista.id}`}>
            Precio
          </label>
          <CampoImporte
            id={`lp-precio-${lista.id}`}
            className="form-control form-control-sm"
            style={{ width: 140 }}
            valor={estado.monto === '' ? null : Number(estado.monto)}
            disabled={bloqueado}
            onChange={(n) => onCambio({ monto: n === null ? '' : String(n) })}
          />
        </div>

        {sugerencia !== null && (
          <div className="col-auto">
            <button
              type="button"
              className="btn btn-sm btn-outline-info"
              disabled={bloqueado || cargandoSugerencia}
              onClick={() => onCambio({ monto: String(sugerencia) })}
            >
              Usar sugerencia ({formatearImporte(sugerencia, { simbolo: true })})
            </button>
          </div>
        )}

        <div className="col-auto">
          <div className="form-check">
            <input
              id={`lp-programado-${lista.id}`}
              type="checkbox"
              className="form-check-input"
              checked={estado.programado}
              disabled={bloqueado}
              onChange={(e) => onCambio({ programado: e.target.checked })}
            />
            <label className="form-check-label small" htmlFor={`lp-programado-${lista.id}`}>
              Programar a futuro
            </label>
          </div>
        </div>

        {estado.programado && (
          <div className="col-auto">
            <label className="form-label mb-0 small" htmlFor={`lp-vigente-desde-${lista.id}`}>
              Vigente desde
            </label>
            <input
              id={`lp-vigente-desde-${lista.id}`}
              type="datetime-local"
              className="form-control form-control-sm"
              value={estado.vigenteDesde}
              disabled={bloqueado}
              onChange={(e) => onCambio({ vigenteDesde: e.target.value })}
            />
          </div>
        )}

        <div className="col-auto">
          <button
            ref={setBotonDeGuardar}
            type="button"
            className="btn btn-sm btn-success"
            disabled={bloqueado}
            onClick={() => onGuardar(false)}
          >
            {estado.guardando ? 'Guardando…' : estado.refrescando ? 'Actualizando…' : estado.programado ? 'Programar' : 'Establecer ahora'}
          </button>
        </div>
      </div>

      <div className="mt-3">
        <TituloDeSeccion>Historial</TituloDeSeccion>
        <table className="table table-sm mb-0 mt-1">
          <thead>
            <tr>
              <th>Precio</th>
              <th>Vigente desde</th>
              <th>Vigente hasta</th>
            </tr>
          </thead>
          <tbody>
            {historial.map((h) => (
              <tr key={h.id}>
                <td>{formatearImporte(h.precio, { simbolo: true })}</td>
                <td>{fechaHoraATexto(h.vigenteDesde)}</td>
                <td>{h.vigenteHasta ? fechaHoraATexto(h.vigenteHasta) : '—'}</td>
              </tr>
            ))}
            {historial.length === 0 && (
              <tr>
                <td colSpan={3} className="text-center text-muted py-2">
                  Sin historial todavía.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}
