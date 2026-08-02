import { useCallback, useEffect, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'
import type { CategoriaAlta, CategoriaListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

const PROFUNDIDAD_MAXIMA = 3

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

export function Categorias() {
  const [items, setItems] = useState<CategoriaListado[]>([])
  const [incluirInactivos, setIncluirInactivos] = useState(false)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)

  const cargar = useCallback(async (conInactivos: boolean) => {
    setCargando(true)
    setError('')
    try {
      const parametros = conInactivos ? '?incluirInactivos=true' : ''
      setItems(await api.get<CategoriaListado[]>(`/catalogos/categorias${parametros}`))
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las categorías.')
    } finally {
      setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar(incluirInactivos)
  }, [cargar, incluirInactivos])

  async function guardar() {
    if (!formulario) return

    setGuardando(true)
    setError('')
    setAviso('')

    try {
      const datos: CategoriaAlta = {
        nombre: formulario.nombre,
        idEmpresa: null,
        orden: Number(formulario.orden || '1'),
        idCategoriaPadre: formulario.idCategoriaPadre,
        activo: formulario.activo,
      }

      if (formulario.id === null) {
        await api.post('/catalogos/categorias', datos)
        setAviso(`Se creó "${formulario.nombre}".`)
      } else {
        await api.put(`/catalogos/categorias/${formulario.id}`, datos)
        setAviso(`Se actualizó "${formulario.nombre}".`)
      }

      setFormulario(null)
      await cargar(incluirInactivos)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
    } finally {
      setGuardando(false)
    }
  }

  async function eliminar(item: CategoriaListado) {
    if (!confirm(`¿Dar de baja "${item.nombre}"? Sus subcategorías quedan sin este padre visible.`)) return

    setError('')
    setAviso('')
    try {
      await api.delete(`/catalogos/categorias/${item.id}`)
      setAviso(`"${item.nombre}" se dio de baja.`)
      await cargar(incluirInactivos)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo completar la acción.')
    }
  }

  const arbol = armarArbol(items)

  function abrirNueva(idCategoriaPadre: number | null) {
    setFormulario({ ...FORMULARIO_VACIO, idCategoriaPadre })
    setAviso('')
    setError('')
  }

  function abrirEdicion(item: CategoriaListado) {
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
        />
        <label className="form-check-label text-light small" htmlFor="incluir-inactivas">
          Incluir inactivas
        </label>
      </div>
      <button type="button" className="btn btn-sm btn-success rounded-0 text-nowrap" onClick={() => abrirNueva(null)}>
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

        {formulario && (
          <FormularioCategoria
            valor={formulario}
            arbol={arbol}
            guardando={guardando}
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
                onNueva={abrirNueva}
                onEditar={abrirEdicion}
                onEliminar={eliminar}
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
  onNueva,
  onEditar,
  onEliminar,
}: {
  nodo: Nodo
  onNueva: (idCategoriaPadre: number | null) => void
  onEditar: (item: CategoriaListado) => void
  onEliminar: (item: CategoriaListado) => void
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
            >
              + Subcategoría
            </button>
          )}
          <button type="button" className="btn btn-sm btn-outline-primary rounded-0" onClick={() => onEditar(nodo)}>
            Editar
          </button>
          {nodo.activo && (
            <button
              type="button"
              className="btn btn-sm btn-outline-danger rounded-0"
              onClick={() => onEliminar(nodo)}
            >
              Baja
            </button>
          )}
        </span>
      </div>

      {nodo.hijos.length > 0 && (
        <ul className="list-unstyled mb-0">
          {nodo.hijos.map((hijo) => (
            <NodoCategoria key={hijo.id} nodo={hijo} onNueva={onNueva} onEditar={onEditar} onEliminar={onEliminar} />
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
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  arbol: Nodo[]
  guardando: boolean
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
        >
          <option value="activo">Activo</option>
          <option value="inactivo">Inactivo</option>
        </select>
      </div>

      <div className="col-12 d-flex gap-2">
        <button type="submit" className="btn btn-success rounded-0" disabled={guardando}>
          {guardando ? 'Guardando…' : 'Guardar'}
        </button>
        <button
          type="button"
          className="btn btn-outline-secondary rounded-0"
          onClick={onCancelar}
          disabled={guardando}
        >
          Cancelar
        </button>
      </div>
    </form>
  )
}
