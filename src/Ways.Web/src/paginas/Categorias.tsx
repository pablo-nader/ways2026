import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'
import { copiaDeFalloDeBaja } from '../api/bajas'
import type { CategoriaAlta, CategoriaListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { BotonIcono } from '../componentes/BotonIcono'
import { ErrorDeFormulario } from '../componentes/ErrorDeFormulario'
import type { EstadoDeOrden } from '../componentes/grilla'
import { normalizarTexto } from '../componentes/grilla/filtros'
import { siguienteOrden } from '../componentes/grilla/ordenamiento'
import { Modal } from '../componentes/Modal'
import { atributosDeCampoEntero, parsearNumeroDecimal } from '../formato/numeros'
import { aplanar, armarArbol, filtrarArbol, idsDeSubarbol, ordenarArbol, type Nodo } from './categorias/arbolDeCategorias'

const PROFUNDIDAD_MAXIMA = 3
const ID_FORMULARIO_CATEGORIA = 'formulario-categoria'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_BAJA = 'Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.'

const DESCRIPCION_DE_ORDEN = {
  original: 'original',
  ascendente: 'ascendente',
  descendente: 'descendente',
} as const
/** Una categoría recién creada no tiene hijos: si su nombre no contiene el filtro activo, la vista no la muestra. */
function quedaOculta(nombre: string, filtro: string): boolean {
  return normalizarTexto(filtro) !== '' && !normalizarTexto(nombre).includes(normalizarTexto(filtro))
}

const INDICADOR_DE_ORDEN = { ascendente: '▲', descendente: '▼' } as const

type Formulario = {
  id: number | null
  nombre: string
  orden: string
  idCategoriaPadre: number | null
  activo: boolean
  /** Empresa de la fila que se edita; null en un alta y en una categoría de todo el tenant. */
  idEmpresa: number | null
}

const FORMULARIO_VACIO: Formulario = {
  id: null,
  nombre: '',
  orden: '1',
  idCategoriaPadre: null,
  activo: true,
  idEmpresa: null,
}

/**
 * `categorias` es el escape hatch de ADR-11 (árbol + regla de profundidad) — no pasa por
 * `PaginaCatalogo`, pero la baja (fix/web-bajas-catalogos) sigue el MISMO patrón que
 * `Empresas.tsx`/`PaginaCatalogo.tsx` (`react-async-state` regla 10): puerta modal
 * (`ConfirmacionDeBaja`), token de generación + `ocupadoRef` de re-entrancia compartidos entre
 * guardado y baja, `bloqueado` deja inerte toda la pantalla mientras cualquiera está en vuelo.
 * El backend ahora bloquea la baja de una categoría con subcategorías (`categoria_en_uso`), así
 * que la puerta ya no puede prometer que "las subcategorías quedan sin este padre visible" — esa
 * advertencia del `confirm()` nativo se retira.
 *
 * El alta y la edición viven en un `Modal` (mismo criterio que `PaginaCatalogo.tsx`): un guardado
 * exitoso lo cierra y el aviso queda en la pantalla; el rechazo de un guardado se muestra adentro
 * (`errorFormulario`), porque un alert de la pantalla quedaría detrás del fondo. Los errores de la
 * carga del listado y de la baja siguen en la pantalla.
 */
export function Categorias() {
  const [items, setItems] = useState<CategoriaListado[]>([])
  const [incluirInactivos, setIncluirInactivos] = useState(false)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [errorFormulario, setErrorFormulario] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)
  const [filtroNombre, setFiltroNombre] = useState('')
  /** Orden de las hermanas por nombre: `null` es el orden del servidor (`orden` y luego nombre). */
  const [orden, setOrden] = useState<EstadoDeOrden>(null)
  /** Baja pendiente de confirmación: ver `Empresas.tsx`. */
  const [baja, setBaja] = useState<CategoriaListado | null>(null)
  /** Id de la categoría cuya baja está en vuelo (distinto de `guardando`, que cubre alta/edición). */
  const [ocupadoBaja, setOcupadoBaja] = useState<number | null>(null)
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)
  /** Destino de foco cuando la modal se cierra con la pantalla inerte (guardado en vuelo): ningún
   * botón es alcanzable en ese momento. */
  const contenedorRef = useRef<HTMLDivElement>(null)

  /** Contrato de invalidación compartido por guardar y por la baja: ver `Empresas.tsx`. */
  const generacion = useRef(0)
  /** Espejo síncrono de "hay una escritura en vuelo" — la ÚNICA guarda de re-entrancia válida. */
  const ocupadoRef = useRef(false)

  const cargar = useCallback(async (token: number, conInactivos: boolean, propagar = false) => {
    setCargando(true)
    try {
      const parametros = conInactivos ? '?incluirInactivos=true' : ''
      const filas = await api.get<CategoriaListado[]>(`/catalogos/categorias${parametros}`)
      if (generacion.current !== token) return
      setItems(filas)
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las categorías.')
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar(++generacion.current, incluirInactivos)
  }, [cargar, incluirInactivos])

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya
   * commiteó nunca se reporta como fallida (`react-async-state` regla 6). */
  async function refrescarTrasEscribir(token: number, mensajeOk: string, avisoDeFallo: string) {
    if (generacion.current !== token) return

    setAviso(mensajeOk)
    try {
      await cargar(token, incluirInactivos, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${avisoDeFallo}`)
    }
  }

  async function guardar() {
    if (!formulario || ocupadoRef.current) return

    const datos = formulario
    // Una lectura en vuelo queda invalidada por el token de esta escritura: no va a dejar datos ni a
    // bajar `cargando`. Si la escritura falla no hay refresco que lo repare, así que se vuelve a pedir.
    const habiaLecturaEnVuelo = cargando
    const token = ++generacion.current
    ocupadoRef.current = true
    setGuardando(true)
    setError('')
    setErrorFormulario('')
    setAviso('')
    try {
      try {
        const datosAlta: CategoriaAlta = {
          nombre: datos.nombre,
          idEmpresa: datos.idEmpresa,
          orden: parsearNumeroDecimal(datos.orden, { permitirNegativo: true }) ?? 1,
          idCategoriaPadre: datos.idCategoriaPadre,
          activo: datos.activo,
        }

        if (datos.id === null) {
          await api.post('/catalogos/categorias', datosAlta)
        } else {
          await api.put(`/catalogos/categorias/${datos.id}`, datosAlta)
        }
      } catch (e) {
        if (generacion.current === token) {
          setErrorFormulario(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
          if (habiaLecturaEnVuelo) void cargar(++generacion.current, incluirInactivos)
        }

        return
      }

      if (generacion.current !== token) return

      setFormulario(null)
      await refrescarTrasEscribir(
        token,
        datos.id === null
          ? `Se creó "${datos.nombre}"${quedaOculta(datos.nombre, filtroNombre) ? ' (oculta por el filtro)' : ''}.`
          : `Se actualizó "${datos.nombre}".`,
        AVISO_REFRESCO_FALLIDO,
      )
    } finally {
      ocupadoRef.current = false
      setGuardando(false)
    }
  }

  /** Ver `Empresas.tsx`: mismo patrón de puerta, mismo contrato de invalidación, misma
   * re-entrancia. Abrir NO acuña generación: no hay escritura todavía. */
  function pedirBaja(item: CategoriaListado, disparador: HTMLElement | null) {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(disparador)
    setBaja(item)
    setError('')
    setAviso('')
  }

  function cancelarBaja() {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(null)
    setBaja(null)
    setError('')
    setAviso('')
  }

  async function confirmarBaja() {
    if (!baja || ocupadoRef.current) return

    const item = baja
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupadoBaja(item.id)
    setError('')
    setAviso('')
    try {
      try {
        await api.delete(`/catalogos/categorias/${item.id}`)
      } catch (e) {
        setError(copiaDeFalloDeBaja(e, 'la categoría'))

        return
      }

      setBaja(null)
      // La baja de la categoría que se está editando se lleva también su formulario: dejarlo
      // abierto ofrecía guardar sobre una entidad que ya no existe, y el PUT moría en 404.
      setFormulario((prev) => (prev?.id === item.id ? null : prev))
      await refrescarTrasEscribir(token, `Se dio de baja "${item.nombre}".`, AVISO_REFRESCO_FALLIDO_BAJA)
    } finally {
      ocupadoRef.current = false
      setOcupadoBaja(null)
    }
  }

  /** La puerta abierta o cualquier escritura en vuelo bloquean la pantalla entera: ver
   * `Empresas.tsx`. */
  const bloqueado = guardando || ocupadoBaja !== null || baja !== null

  /** El árbol completo alimenta el selector de padre del formulario; la vista aplica filtro y orden aparte. */
  const arbol = useMemo(() => armarArbol(items), [items])
  const hayFiltro = filtroNombre !== ''
  const arbolVisible = useMemo(
    () => ordenarArbol(filtrarArbol(arbol, filtroNombre), orden),
    [arbol, filtroNombre, orden],
  )

  function abrirNueva(idCategoriaPadre: number | null) {
    if (ocupadoRef.current) return

    setFormulario({ ...FORMULARIO_VACIO, idCategoriaPadre })
    setAviso('')
    setError('')
    setErrorFormulario('')
  }

  function abrirEdicion(item: CategoriaListado) {
    if (ocupadoRef.current) return

    setFormulario({
      id: item.id,
      nombre: item.nombre,
      orden: String(item.orden),
      idCategoriaPadre: item.idCategoriaPadre,
      activo: item.activo,
      idEmpresa: item.idEmpresa,
    })
    setAviso('')
    setError('')
    setErrorFormulario('')
  }

  /** La modal no ofrece cancelar con una escritura en vuelo (`ocupado`), así que cerrar no supersede
   * ninguna escritura y no acuña generación. */
  function cancelarFormulario() {
    setFormulario(null)
  }

  const herramientas = (
    <nav className="p-2 d-flex align-items-center gap-3">
      <div className="form-check form-switch mb-0">
        <input
          id="incluir-inactivas"
          className="form-check-input"
          type="checkbox"
          checked={incluirInactivos}
          onChange={(e) => setIncluirInactivos(e.target.checked)}
          disabled={bloqueado}
        />
        <label className="form-check-label text-body-secondary small" htmlFor="incluir-inactivas">
          Incluir inactivas
        </label>
      </div>
      <button
        type="button"
        className="btn btn-sm btn-success text-nowrap"
        onClick={() => abrirNueva(null)}
        disabled={bloqueado}
      >
        Nueva categoría raíz
      </button>
    </nav>
  )

  return (
    <div ref={contenedorRef} tabIndex={-1} style={{ outline: 'none' }} className="container-fluid py-4">
      <Box titulo="Categorías" variante="inverse" herramientas={herramientas}>
        <p className="text-muted">
          Taxonomía jerárquica, máximo {PROFUNDIDAD_MAXIMA} niveles (Bebidas → Gaseosas → Cola). El servidor
          rechaza cualquier alta o movimiento que supere ese límite o que forme un ciclo.
        </p>

        {error && !baja && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}

        {baja && (
          <ConfirmacionDeBaja
            titulo={`la categoría "${baja.nombre}"`}
            ocupado={ocupadoBaja !== null}
            disparador={disparadorDeLaPuerta}
            error={error}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

        {formulario && (
          <ModalDeCategoria
            valor={formulario}
            arbol={arbol}
            guardando={guardando}
            bloqueado={bloqueado}
            error={errorFormulario}
            focoDeReserva={contenedorRef}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={cancelarFormulario}
          />
        )}

        <div className="d-flex align-items-center gap-2 mb-3">
          <input
            type="search"
            className="form-control form-control-sm"
            style={{ maxWidth: '20rem' }}
            aria-label="Filtrar por nombre"
            placeholder="Filtrar por nombre…"
            value={filtroNombre}
            onChange={(e) => setFiltroNombre(e.target.value)}
            disabled={bloqueado}
          />
          <button
            type="button"
            className="btn btn-sm btn-outline-secondary text-nowrap"
            onClick={() => setOrden((actual) => siguienteOrden(actual, 'nombre'))}
          >
            Nombre{' '}
            <span className="visually-hidden">(orden {DESCRIPCION_DE_ORDEN[orden?.direccion ?? 'original']})</span>{' '}
            <span aria-hidden="true" className={orden ? '' : 'opacity-25'}>
              {orden ? INDICADOR_DE_ORDEN[orden.direccion] : '↕'}
            </span>
          </button>
          <BotonIcono icono="limpiar" onClick={() => setFiltroNombre('')} disabled={bloqueado || !hayFiltro} />
        </div>

        {cargando ? (
          <Cargando />
        ) : arbol.length === 0 ? (
          <p className="text-muted text-center py-4">No hay categorías cargadas.</p>
        ) : arbolVisible.length === 0 ? (
          <p className="text-muted text-center py-4">Ningún registro coincide con los filtros.</p>
        ) : (
          <ul className="list-unstyled mb-0">
            {arbolVisible.map((nodo) => (
              <NodoCategoria
                key={nodo.id}
                nodo={nodo}
                bloqueado={bloqueado}
                onNueva={abrirNueva}
                onEditar={abrirEdicion}
                onEliminar={pedirBaja}
              />
            ))}
          </ul>
        )}
      </Box>
    </div>
  )
}

function NodoCategoria({
  nodo,
  bloqueado,
  onNueva,
  onEditar,
  onEliminar,
}: {
  nodo: Nodo
  bloqueado: boolean
  onNueva: (idCategoriaPadre: number | null) => void
  onEditar: (item: CategoriaListado) => void
  onEliminar: (item: CategoriaListado, disparador: HTMLElement | null) => void
}) {
  return (
    <li className="mb-1">
      <div
        className="d-flex align-items-center gap-2 border-bottom py-2"
        style={{ paddingLeft: `${(nodo.nivel - 1) * 1.5}rem` }}
      >
        <span className="badge text-bg-secondary">Nivel {nodo.nivel}</span>
        <span className={nodo.activo ? '' : 'text-muted text-decoration-line-through'}>{nodo.nombre}</span>
        {!nodo.activo && <span className="badge text-bg-secondary">Inactiva</span>}
        <span className="ms-auto d-flex gap-1">
          {nodo.nivel < PROFUNDIDAD_MAXIMA && (
            <button
              type="button"
              className="btn btn-sm btn-outline-success"
              onClick={() => onNueva(nodo.id)}
              disabled={bloqueado}
            >
              + Subcategoría
            </button>
          )}
          <BotonIcono
            icono="editar"
            onClick={() => onEditar(nodo)}
            disabled={bloqueado}
          />
          {nodo.activo && (
            <BotonIcono
              icono="eliminar"
              onClick={(evento) => onEliminar(nodo, evento.currentTarget)}
              disabled={bloqueado}
            />
          )}
        </span>
      </div>

      {nodo.hijos.length > 0 && (
        <ul className="list-unstyled mb-0">
          {nodo.hijos.map((hijo) => (
            <NodoCategoria
              key={hijo.id}
              nodo={hijo}
              bloqueado={bloqueado}
              onNueva={onNueva}
              onEditar={onEditar}
              onEliminar={onEliminar}
            />
          ))}
        </ul>
      )}
    </li>
  )
}

function ModalDeCategoria({
  valor,
  arbol,
  guardando,
  bloqueado,
  error,
  focoDeReserva,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  arbol: Nodo[]
  guardando: boolean
  bloqueado: boolean
  error: string
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNueva = valor.id === null
  const excluidos = valor.id !== null ? idsDeSubarbol(arbol, valor.id) : new Set<number>()
  const opcionesPadre = aplanar(arbol).filter(
    (nodo) => nodo.id !== valor.id && !excluidos.has(nodo.id) && nodo.nivel < PROFUNDIDAD_MAXIMA,
  )

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_CATEGORIA} className="btn btn-success" disabled={bloqueado}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={esNueva ? 'Nueva categoría' : 'Editar categoría'}
      tamano="lg"
      ocupado={bloqueado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} />
      <form
        id={ID_FORMULARIO_CATEGORIA}
        className="row g-3"
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <div className="col-md-4">
          <label className="form-label" htmlFor="fc-nombre">
            Nombre
          </label>
          <input
            id="fc-nombre"
            autoFocus
            className="form-control"
            maxLength={150}
            value={valor.nombre}
            onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
            disabled={bloqueado}
            required
          />
        </div>

        <div className="col-md-3">
          <label className="form-label" htmlFor="fc-padre">
            Categoría padre
          </label>
          <select
            id="fc-padre"
            className="form-select"
            value={valor.idCategoriaPadre ?? ''}
            onChange={(e) =>
              onCambio({ ...valor, idCategoriaPadre: e.target.value === '' ? null : Number(e.target.value) })
            }
            disabled={bloqueado}
          >
            <option value="">— Ninguna (raíz) —</option>
            {opcionesPadre.map((nodo) => (
              <option key={nodo.id} value={nodo.id}>
                {'—'.repeat(nodo.nivel - 1)} {nodo.nombre}
              </option>
            ))}
          </select>
        </div>

        <div className="col-md-2">
          <label className="form-label" htmlFor="fc-orden">
            Orden
          </label>
          <input
            id="fc-orden"
            {...atributosDeCampoEntero({ permitirNegativo: true })}
            className="form-control"
            value={valor.orden}
            onChange={(e) => onCambio({ ...valor, orden: e.target.value })}
            disabled={bloqueado}
            required
          />
        </div>

        <div className="col-md-2">
          <label className="form-label" htmlFor="fc-activo">
            Estado
          </label>
          <select
            id="fc-activo"
            className="form-select"
            value={valor.activo ? 'activo' : 'inactivo'}
            onChange={(e) => onCambio({ ...valor, activo: e.target.value === 'activo' })}
            disabled={bloqueado}
          >
            <option value="activo">Activo</option>
            <option value="inactivo">Inactivo</option>
          </select>
        </div>
      </form>
    </Modal>
  )
}
