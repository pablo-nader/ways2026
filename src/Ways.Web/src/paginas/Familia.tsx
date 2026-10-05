import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { Link, useLocation, useParams } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { clienteDeFamilias } from '../api/familias'
import type { FamiliaDetalle, MiembroDeFamilia, ResultadoDeAgrupacion } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { CAMPOS_COMPARTIDOS, cantidadDeArticulos, describirEstadoDePrecios } from './articulos/familia'
import { avisoDeAgregado, LIMITE_DE_ARTICULOS } from './familias/agrupacion'
import { ListaDeElegidos } from './familias/ListaDeElegidos'
import { mensajeDeError } from './familias/mensajes'
import { SelectorDeArticulos, type ArticuloElegido } from './familias/SelectorDeArticulos'
import { useCatalogosDeFamilias, type CatalogosDeFamilias } from './familias/useCatalogosDeFamilias'
import { usePrevisualizacion } from './familias/usePrevisualizacion'
import { formatearValorCompartido } from './familias/valoresCompartidos'
import { VistaDePrevisualizacion } from './familias/VistaDePrevisualizacion'

const AVISO_REFRESCO_FALLIDO = 'No se pudo actualizar la vista. Recargá la pantalla.'

/** Detalle de una familia (doc 10 §3). Se monta con `key={id}` (react-async-state regla 8): pasar de una familia a
 * otra remonta la pantalla y con ella su estado, en vez de arrastrar la selección de una a la otra. */
export function Familia() {
  const { id } = useParams()

  return <DetalleDeFamilia key={id} id={id} />
}

function DetalleDeFamilia({ id }: { id: string | undefined }) {
  const idFamilia = id !== undefined && /^\d+$/.test(id) ? Number(id) : null
  const ubicacion = useLocation()
  // La pantalla que creó la familia deja su aviso en el estado de la navegación.
  const avisoDeEntrada = (ubicacion.state as { aviso?: string } | null)?.aviso ?? ''
  const catalogos = useCatalogosDeFamilias()

  const [detalle, setDetalle] = useState<FamiliaDetalle | null>(null)
  const [cargando, setCargando] = useState(idFamilia !== null)
  // Un slot por fuente (react-async-state regla 14): la carga, las escrituras y el aviso de éxito no se pisan.
  const [error, setError] = useState(idFamilia === null ? 'No se especificó una familia válida.' : '')
  const [errorDeEscritura, setErrorDeEscritura] = useState('')
  const [aviso, setAviso] = useState(avisoDeEntrada)
  const [salida, setSalida] = useState<MiembroDeFamilia | null>(null)
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)
  const [saliendo, setSaliendo] = useState<number | null>(null)
  const [agregando, setAgregando] = useState(false)

  /** Cada lectura (la inicial y el refresco de cada escritura) acuña la suya: la más nueva gana. Las escrituras no
   * usan generación: la pantalla queda inerte mientras una está en vuelo (`bloqueado`), así que nada la supera. */
  const generacion = useRef(0)
  /** Espejo síncrono de "hay una escritura en vuelo": dos clics en el mismo tick pasan la guarda de estado. */
  const ocupadoRef = useRef(false)

  const cargar = useCallback(
    async (token: number, propagar = false) => {
      if (idFamilia === null) return

      setCargando(true)
      try {
        const lectura = await clienteDeFamilias.obtener(idFamilia)
        if (generacion.current !== token) return
        setDetalle(lectura)
        setError('')
      } catch (e) {
        if (generacion.current !== token) return
        if (propagar) throw e
        // Una familia que ya no existe no se sigue mostrando con los datos de la última lectura.
        if (e instanceof ErrorApi && e.estado === 404) setDetalle(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar la familia.')
      } finally {
        if (generacion.current === token) setCargando(false)
      }
    },
    [idFamilia],
  )

  useEffect(() => {
    void cargar(++generacion.current)
  }, [cargar])

  function pedirSalida(miembro: MiembroDeFamilia, disparador: HTMLElement | null) {
    setDisparadorDeLaPuerta(disparador)
    setSalida(miembro)
    setErrorDeEscritura('')
    setAviso('')
  }

  function cancelarSalida() {
    setDisparadorDeLaPuerta(null)
    setSalida(null)
    setErrorDeEscritura('')
  }

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya commiteó nunca se
   * reporta como fallida (`react-async-state` regla 6). */
  async function refrescarTrasEscribir(mensajeOk: string) {
    setAviso(mensajeOk)
    try {
      await cargar(++generacion.current, true)
    } catch {
      setAviso(`${mensajeOk} ${AVISO_REFRESCO_FALLIDO}`)
    }
  }

  async function confirmarSalida() {
    if (!salida || idFamilia === null || ocupadoRef.current) return

    const miembro = salida
    ocupadoRef.current = true
    setSaliendo(miembro.id)
    setErrorDeEscritura('')
    try {
      try {
        await clienteDeFamilias.sacarArticulo(idFamilia, miembro.id)
      } catch (e) {
        setErrorDeEscritura(mensajeDeError(e, 'sacar el artículo de la familia'))
        // La pertenencia ya no es la que la pantalla muestra (`familia_cambio`) o la familia ya no existe: se la
        // vuelve a leer para no seguir mostrando lo que dejó de ser cierto.
        if (e instanceof ErrorApi && (e.codigo === 'familia_cambio' || e.estado === 404)) {
          setSalida(null)
          await cargar(++generacion.current)
        }

        return
      }

      setSalida(null)
      await refrescarTrasEscribir(`Se sacó el artículo "${miembro.nombre}" de la familia: conserva todos sus valores y sus precios.`)
    } finally {
      ocupadoRef.current = false
      setSaliendo(null)
    }
  }

  /** Lo que el panel de agregado confirmó: se avisa y se vuelve a leer la familia (con sus miembros nuevos). */
  async function alAgregar(resultado: ResultadoDeAgrupacion) {
    setErrorDeEscritura('')
    await refrescarTrasEscribir(avisoDeAgregado(resultado))
  }

  /** La puerta abierta o cualquier escritura en vuelo (la salida o el agregado) bloquean la pantalla entera. */
  const bloqueado = salida !== null || saliendo !== null || agregando

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <Link to="/familias" className="btn btn-sm btn-outline-secondary text-nowrap">
        Volver al listado
      </Link>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo={detalle ? `Familia "${detalle.nombre}"` : 'Familia'} variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger">{error}</div>}
        {errorDeEscritura && <div className="alert alert-danger">{errorDeEscritura}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}
        {catalogos.aviso && <div className="alert alert-warning">{catalogos.aviso}</div>}

        {salida && (
          <ConfirmacionDeBaja
            titulo={`el artículo "${salida.nombre}" de la familia`}
            pregunta="Sacar"
            nota="El artículo queda sin familia y conserva todos sus valores y sus precios; los demás artículos de la familia no cambian."
            etiquetaConfirmar="Confirmar salida"
            etiquetaEnCurso="Sacando…"
            ocupado={saliendo !== null}
            disparador={disparadorDeLaPuerta}
            onConfirmar={confirmarSalida}
            onCancelar={cancelarSalida}
          />
        )}

        {cargando && detalle === null ? (
          <Cargando />
        ) : (
          detalle && (
            <>
              <div className="mb-3 d-flex flex-wrap align-items-center gap-2">
                <span className={`badge ${detalle.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                  {detalle.activo ? 'Activa' : 'Inactiva'}
                </span>
                <span className="text-body-secondary">{cantidadDeArticulos(detalle.articulos.length)}</span>
              </div>

              <MiembrosDeLaFamilia detalle={detalle} catalogos={catalogos} bloqueado={bloqueado} onSacar={pedirSalida} />
              <ValoresDeLaReferencia detalle={detalle} catalogos={catalogos} />
              <AgregarArticulos
                detalle={detalle}
                catalogos={catalogos}
                bloqueadoPorLaPantalla={salida !== null || saliendo !== null}
                alOcuparse={setAgregando}
                alAgregar={alAgregar}
              />
            </>
          )
        )}
      </Box>
    </div>
  )
}

function MiembrosDeLaFamilia({
  detalle,
  catalogos,
  bloqueado,
  onSacar,
}: {
  detalle: FamiliaDetalle
  catalogos: CatalogosDeFamilias
  bloqueado: boolean
  onSacar: (miembro: MiembroDeFamilia, disparador: HTMLElement | null) => void
}) {
  return (
    <section className="mb-4">
      <h6>Artículos de la familia</h6>
      <div className="table-responsive">
        <table className="table table-striped table-hover table-bordered align-middle">
          <thead>
            <tr>
              <th>Código</th>
              <th>Nombre</th>
              <th>Marca</th>
              <th>Estado</th>
              <th className="text-end">Acciones</th>
            </tr>
          </thead>
          <tbody>
            {detalle.articulos.map((miembro, indice) => (
              <tr key={miembro.id}>
                <td>{miembro.codigoInterno}</td>
                <td>
                  {miembro.nombre}
                  {indice === 0 && <span className="badge text-bg-info ms-2">Referencia</span>}
                </td>
                <td>{miembro.idMarca === null ? '—' : (catalogos.marcas.get(miembro.idMarca) ?? `#${miembro.idMarca}`)}</td>
                <td>{miembro.activo ? 'Activo' : 'Inactivo'}</td>
                <td className="text-end text-nowrap">
                  <button
                    type="button"
                    className="btn btn-sm btn-outline-danger"
                    aria-label={`Sacar ${miembro.nombre} de la familia`}
                    disabled={bloqueado}
                    onClick={(evento) => onSacar(miembro, evento.currentTarget)}
                  >
                    Sacar
                  </button>
                </td>
              </tr>
            ))}
            {detalle.articulos.length === 0 && (
              <tr>
                <td colSpan={5} className="text-center text-muted py-4">
                  La familia no tiene artículos vivos.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function ValoresDeLaReferencia({ detalle, catalogos }: { detalle: FamiliaDetalle; catalogos: CatalogosDeFamilias }) {
  const referencia = detalle.articulos[0]
  const valores = detalle.valores
  if (valores === null || referencia === undefined) return null

  return (
    <section className="mb-4">
      <h6>Valores compartidos</h6>
      <p className="small text-body-secondary">
        Son los del artículo de referencia ({referencia.codigoInterno} — {referencia.nombre}), el de menor id: todos los artículos de
        la familia los tienen iguales. Se cambian editando cualquiera de ellos.
      </p>
      <dl className="row mb-3">
        {CAMPOS_COMPARTIDOS.map((campo) => (
          <Fragment key={campo.clave}>
            <dt className="col-sm-4">{campo.etiqueta}</dt>
            <dd className="col-sm-8">{formatearValorCompartido(campo.clave, valores, catalogos.nombres)}</dd>
          </Fragment>
        ))}
      </dl>

      <h6>Precios por lista</h6>
      <div className="table-responsive">
        <table className="table table-sm table-bordered align-middle">
          <thead>
            <tr>
              <th>Lista de precios</th>
              <th>Precio</th>
            </tr>
          </thead>
          <tbody>
            {detalle.precios.map((precio) => (
              <tr key={precio.idListaPrecio}>
                <td>{catalogos.listas.get(precio.idListaPrecio) ?? `Lista ${precio.idListaPrecio}`}</td>
                <td>{describirEstadoDePrecios(precio.estado)}</td>
              </tr>
            ))}
            {detalle.precios.length === 0 && (
              <tr>
                <td colSpan={2} className="text-center text-muted">
                  No hay listas de precio fijas.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </section>
  )
}

/**
 * Sumar artículos que ya existen a la familia (`POST /api/familias/{id}/articulos`): se eligen, se previsualiza qué
 * cambiaría en cada uno al alinearlo con la referencia y recién entonces se confirma. Lo previsualizado es de lo
 * elegido: cualquier cambio de la selección o de la familia lo descarta, y confirmar exige una previsualización
 * sin problemas (la API rechaza lo mismo con 409/422/400 si la foto quedó vieja).
 */
function AgregarArticulos({
  detalle,
  catalogos,
  bloqueadoPorLaPantalla,
  alOcuparse,
  alAgregar,
}: {
  detalle: FamiliaDetalle
  catalogos: CatalogosDeFamilias
  /** La pantalla tiene una puerta abierta o una salida en vuelo. */
  bloqueadoPorLaPantalla: boolean
  /** Avisa a la pantalla que este panel tiene una escritura en vuelo, para que deje inerte el resto. */
  alOcuparse: (enCurso: boolean) => void
  alAgregar: (resultado: ResultadoDeAgrupacion) => void | Promise<void>
}) {
  const [elegidos, setElegidos] = useState<ArticuloElegido[]>([])
  const [selectorAbierto, setSelectorAbierto] = useState(false)
  const [confirmando, setConfirmando] = useState(false)
  const [errorDeConfirmacion, setErrorDeConfirmacion] = useState('')
  const { previsualizacion, previsualizando, error: errorDePrevisualizacion, previsualizar, invalidar } = usePrevisualizacion()
  const confirmandoRef = useRef(false)

  // Una familia nueva (otro miembro, otra referencia, otros precios) deja sin valor lo previsualizado.
  useEffect(() => {
    invalidar()
  }, [detalle, invalidar])

  const referencia = detalle.articulos[0]

  if (!detalle.activo) {
    return (
      <section>
        <h6>Agregar artículos</h6>
        <div className="alert alert-warning mb-0">
          La familia está inactiva: no admite artículos nuevos. Activala desde el listado de familias para poder sumarle artículos.
        </div>
      </section>
    )
  }

  if (referencia === undefined) {
    return (
      <section>
        <h6>Agregar artículos</h6>
        <div className="alert alert-warning mb-0">
          La familia no tiene artículos vivos: no hay un artículo de referencia al que alinear. Disolvela desde el listado o creá una
          familia nueva.
        </div>
      </section>
    )
  }

  const bloqueado = bloqueadoPorLaPantalla || previsualizando || confirmando
  const excedido = elegidos.length > LIMITE_DE_ARTICULOS
  const sinProblemas = previsualizacion !== null && previsualizacion.problemas.length === 0
  const nombresDeArticulos = new Map<number, ArticuloElegido>(elegidos.map((a) => [a.id, a]))

  function elegir(nuevos: ArticuloElegido[]) {
    setElegidos(nuevos)
    invalidar()
    setErrorDeConfirmacion('')
    setSelectorAbierto(false)
  }

  function quitar(idArticulo: number) {
    setElegidos((previos) => previos.filter((a) => a.id !== idArticulo))
    invalidar()
    setErrorDeConfirmacion('')
  }

  function pedirPrevisualizacion() {
    setErrorDeConfirmacion('')
    void previsualizar({ idArticuloReferencia: referencia.id, idsArticulos: elegidos.map((a) => a.id) })
  }

  async function confirmar() {
    // El botón ya está deshabilitado con problemas o con la pantalla bloqueada: acá solo hace falta el espejo
    // sincrónico, porque dos clics en el mismo tick pasan la guarda de estado.
    if (confirmandoRef.current) return

    confirmandoRef.current = true
    setConfirmando(true)
    alOcuparse(true)
    try {
      let resultado: ResultadoDeAgrupacion
      try {
        resultado = await clienteDeFamilias.agregarArticulos(detalle.id, { idsArticulos: elegidos.map((a) => a.id) })
      } catch (e) {
        // La foto quedó vieja (otro escritor cambió algo): se muestra lo que la API rechazó y hay que volver a
        // previsualizar antes de confirmar.
        setErrorDeConfirmacion(mensajeDeError(e, 'agregar los artículos a la familia'))
        invalidar()
        return
      }

      setElegidos([])
      invalidar()
      await alAgregar(resultado)
    } finally {
      confirmandoRef.current = false
      setConfirmando(false)
      alOcuparse(false)
    }
  }

  return (
    <section>
      <h6>Agregar artículos</h6>
      <p className="small text-body-secondary">
        Los artículos que sumes toman de la referencia ({referencia.codigoInterno} — {referencia.nombre}) sus campos compartidos y
        el precio de cada lista. Previsualizá primero qué cambia en cada uno.
      </p>

      {errorDeConfirmacion && <div className="alert alert-danger">{errorDeConfirmacion}</div>}

      <ListaDeElegidos elegidos={elegidos} bloqueado={bloqueado} vacio="Todavía no elegiste ningún artículo." onQuitar={quitar} />
      {excedido && (
        <div className="alert alert-warning py-1 px-2 small">
          Se pueden agregar hasta {LIMITE_DE_ARTICULOS} artículos por vez: quitá {elegidos.length - LIMITE_DE_ARTICULOS} para poder
          previsualizar.
        </div>
      )}

      <div className="d-flex flex-wrap gap-2 mb-3">
        <button type="button" className="btn btn-outline-primary" disabled={bloqueado} onClick={() => setSelectorAbierto(true)}>
          Elegir artículos
        </button>
        <button
          type="button"
          className="btn btn-outline-secondary"
          disabled={bloqueado || elegidos.length === 0 || excedido}
          onClick={pedirPrevisualizacion}
        >
          {previsualizando ? 'Previsualizando…' : 'Previsualizar'}
        </button>
      </div>

      {errorDePrevisualizacion && <div className="alert alert-danger">{errorDePrevisualizacion}</div>}

      {previsualizacion && (
        <>
          <VistaDePrevisualizacion
            previsualizacion={previsualizacion}
            articulos={nombresDeArticulos}
            nombres={catalogos.nombres}
            listas={catalogos.listas}
          />
          <button type="button" className="btn btn-success" disabled={bloqueado || !sinProblemas} onClick={() => void confirmar()}>
            {confirmando ? 'Agregando…' : 'Confirmar y agregar'}
          </button>
        </>
      )}

      {selectorAbierto && (
        <SelectorDeArticulos
          titulo="Elegir artículos para agregar"
          multiple
          elegidos={elegidos}
          idFamiliaDeDestino={detalle.id}
          onListo={elegir}
          onCerrar={() => setSelectorAbierto(false)}
        />
      )}
    </section>
  )
}
