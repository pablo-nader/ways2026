import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'
import { copiaDeFalloDeBaja } from '../api/bajas'
import type { CategoriaAlta, CategoriaListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'

const PROFUNDIDAD_MAXIMA = 3

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_BAJA = 'Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.'

type Formulario = {
  id: number | null
  nombre: string
  orden: string
  idCategoriaPadre: number | null
  activo: boolean
}

const FORMULARIO_VACIO: Formulario = { id: null, nombre: '', orden: '1', idCategoriaPadre: null, activo: true }

type Nodo = CategoriaListado & { hijos: Nodo[]; nivel: number }

/** Arma el árbol client-side a partir del listado plano (`idCategoriaPadre`). El backend ya
 * valida profundidad/ciclos (ADR-12); acá solo se arma la vista — una categoría cuyo padre no
 * está en el listado visible (dado de baja, filtrado) queda como raíz huérfana en vez de
 * desaparecer, para no esconder datos reales. */
function armarArbol(items: CategoriaListado[]): Nodo[] {
  const porId = new Map(items.map((c) => [c.id, c]))
  const hijosDe = new Map<number | null, CategoriaListado[]>()

  for (const item of items) {
    const padre = item.idCategoriaPadre !== null && porId.has(item.idCategoriaPadre) ? item.idCategoriaPadre : null
    const lista = hijosDe.get(padre) ?? []
    lista.push(item)
    hijosDe.set(padre, lista)
  }

  function construir(padre: number | null, nivel: number): Nodo[] {
    return (hijosDe.get(padre) ?? [])
      .sort((a, b) => a.orden - b.orden || a.nombre.localeCompare(b.nombre))
      .map((item) => ({ ...item, nivel, hijos: construir(item.id, nivel + 1) }))
  }

  return construir(null, 1)
}

/** IDs de los descendientes (hijos, nietos, etc.) de un nodo dentro del árbol, para excluirlos
 * de las opciones de "categoría padre" — reasignar una categoría a su propio subárbol formaría
 * un ciclo, y el backend lo rechaza igual, pero no tiene sentido ofrecerlo en el select. */
function idsDeSubarbol(arbol: Nodo[], id: number): Set<number> {
  const ids = new Set<number>()

  function recorrer(nodo: Nodo) {
    for (const hijo of nodo.hijos) {
      ids.add(hijo.id)
      recorrer(hijo)
    }
  }

  function buscar(nodos: Nodo[]): Nodo | undefined {
    for (const nodo of nodos) {
      if (nodo.id === id) return nodo
      const encontrado = buscar(nodo.hijos)
      if (encontrado) return encontrado
    }
    return undefined
  }

  const objetivo = buscar(arbol)
  if (objetivo) recorrer(objetivo)

  return ids
}

function aplanar(arbol: Nodo[]): Nodo[] {
  return arbol.flatMap((nodo) => [nodo, ...aplanar(nodo.hijos)])
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
 */
export function Categorias() {
  const [items, setItems] = useState<CategoriaListado[]>([])
  const [incluirInactivos, setIncluirInactivos] = useState(false)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)
  /** Baja pendiente de confirmación: ver `Empresas.tsx`. */
  const [baja, setBaja] = useState<CategoriaListado | null>(null)
  /** Id de la categoría cuya baja está en vuelo (distinto de `guardando`, que cubre alta/edición). */
  const [ocupadoBaja, setOcupadoBaja] = useState<number | null>(null)
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)

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
    const token = ++generacion.current
    ocupadoRef.current = true
    setGuardando(true)
    setError('')
    setAviso('')
    try {
      try {
        const datosAlta: CategoriaAlta = {
          nombre: datos.nombre,
          idEmpresa: null,
          orden: Number(datos.orden || '1'),
          idCategoriaPadre: datos.idCategoriaPadre,
          activo: datos.activo,
        }

        if (datos.id === null) {
          await api.post('/catalogos/categorias', datosAlta)
        } else {
          await api.put(`/catalogos/categorias/${datos.id}`, datosAlta)
        }
      } catch (e) {
        if (generacion.current === token) setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')

        return
      }

      if (generacion.current !== token) return

      setFormulario(null)
      await refrescarTrasEscribir(
        token,
        datos.id === null ? `Se creó "${datos.nombre}".` : `Se actualizó "${datos.nombre}".`,
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

  const arbol = armarArbol(items)

  function abrirNueva(idCategoriaPadre: number | null) {
    if (ocupadoRef.current) return

    setFormulario({ ...FORMULARIO_VACIO, idCategoriaPadre })
    setAviso('')
    setError('')
  }

  function abrirEdicion(item: CategoriaListado) {
    if (ocupadoRef.current) return

    setFormulario({
      id: item.id,
      nombre: item.nombre,
      orden: String(item.orden),
      idCategoriaPadre: item.idCategoriaPadre,
      activo: item.activo,
    })
    setAviso('')
    setError('')
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
        <label className="form-check-label text-light small" htmlFor="incluir-inactivas">
          Incluir inactivas
        </label>
      </div>
      <button
        type="button"
        className="btn btn-sm btn-success rounded-0 text-nowrap"
        onClick={() => abrirNueva(null)}
        disabled={bloqueado}
      >
        Nueva categoría raíz
      </button>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Categorías" variante="inverse" herramientas={herramientas}>
        <p className="text-muted">
          Taxonomía jerárquica, máximo {PROFUNDIDAD_MAXIMA} niveles (Bebidas → Gaseosas → Cola). El servidor
          rechaza cualquier alta o movimiento que supere ese límite o que forme un ciclo.
        </p>

        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {baja && (
          <ConfirmacionDeBaja
            titulo={`la categoría "${baja.nombre}"`}
            ocupado={ocupadoBaja !== null}
            disparador={disparadorDeLaPuerta}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

        {formulario && (
          <FormularioCategoria
            valor={formulario}
            arbol={arbol}
            guardando={guardando}
            bloqueado={bloqueado}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={() => setFormulario(null)}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : arbol.length === 0 ? (
          <p className="text-muted text-center py-4">No hay categorías cargadas.</p>
        ) : (
          <ul className="list-unstyled mb-0">
            {arbol.map((nodo) => (
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
        <span className="badge rounded-0 text-bg-secondary">Nivel {nodo.nivel}</span>
        <span className={nodo.activo ? '' : 'text-muted text-decoration-line-through'}>{nodo.nombre}</span>
        {!nodo.activo && <span className="badge rounded-0 text-bg-secondary">Inactiva</span>}
        <span className="ms-auto d-flex gap-1">
          {nodo.nivel < PROFUNDIDAD_MAXIMA && (
            <button
              type="button"
              className="btn btn-sm btn-outline-success rounded-0"
              onClick={() => onNueva(nodo.id)}
              disabled={bloqueado}
            >
              + Subcategoría
            </button>
          )}
          <button
            type="button"
            className="btn btn-sm btn-outline-primary rounded-0"
            onClick={() => onEditar(nodo)}
            disabled={bloqueado}
          >
            Editar
          </button>
          {nodo.activo && (
            <button
              type="button"
              className="btn btn-sm btn-outline-danger rounded-0"
              onClick={(evento) => onEliminar(nodo, evento.currentTarget)}
              disabled={bloqueado}
            >
              Baja
            </button>
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

function FormularioCategoria({
  valor,
  arbol,
  guardando,
  bloqueado,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  arbol: Nodo[]
  guardando: boolean
  bloqueado: boolean
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNueva = valor.id === null
  const excluidos = valor.id !== null ? idsDeSubarbol(arbol, valor.id) : new Set<number>()
  const opcionesPadre = aplanar(arbol).filter(
    (nodo) => nodo.id !== valor.id && !excluidos.has(nodo.id) && nodo.nivel < PROFUNDIDAD_MAXIMA,
  )

  return (
    <form
      className="row g-3 border p-3 mb-4 bg-white"
      autoComplete="off"
      onSubmit={(e) => {
        e.preventDefault()
        onGuardar()
      }}
    >
      <div className="col-12">
        <strong>{esNueva ? 'Nueva categoría' : `Editando categoría ${valor.id}`}</strong>
      </div>

      <div className="col-md-4">
        <label className="form-label" htmlFor="fc-nombre">
          Nombre
        </label>
        <input
          id="fc-nombre"
          className="form-control rounded-0"
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
          className="form-select rounded-0"
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
          type="number"
          className="form-control rounded-0"
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
          className="form-select rounded-0"
          value={valor.activo ? 'activo' : 'inactivo'}
          onChange={(e) => onCambio({ ...valor, activo: e.target.value === 'activo' })}
          disabled={bloqueado}
        >
          <option value="activo">Activo</option>
          <option value="inactivo">Inactivo</option>
        </select>
      </div>

      <div className="col-12 d-flex gap-2">
        <button type="submit" className="btn btn-success rounded-0" disabled={bloqueado}>
          {guardando ? 'Guardando…' : 'Guardar'}
        </button>
        <button
          type="button"
          className="btn btn-outline-secondary rounded-0"
          onClick={onCancelar}
          disabled={bloqueado}
        >
          Cancelar
        </button>
      </div>
    </form>
  )
}
