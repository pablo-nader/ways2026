import { useCallback, useEffect, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'
import type { CampoDescriptor, DescriptorDeCatalogo, ValorDeCampo } from '../api/catalogos'
import type { CatalogoListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

type Formulario = {
  id: number | null
  nombre: string
  activo: boolean
  valores: Record<string, ValorDeCampo>
}

/**
 * ABM genérico de un catálogo de tenant (ADR-11): el descriptor define qué campos propios
 * tiene además de `nombre`/`activo` (comunes a los 5) — esta pantalla no sabe nada de un
 * catálogo en particular. `categorias` no pasa por acá: es el escape hatch (árbol + regla de
 * profundidad, `Categorias.tsx`).
 */
export function PaginaCatalogo<TListado extends CatalogoListado, TAlta>({
  definicion,
}: {
  definicion: DescriptorDeCatalogo<TListado, TAlta>
}) {
  const [items, setItems] = useState<TListado[]>([])
  const [incluirInactivos, setIncluirInactivos] = useState(false)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)

  const { recurso, titulo, tituloSingular, campos, valoresPorDefecto, aValores, aAlta } = definicion

  const cargar = useCallback(
    async (conInactivos: boolean) => {
      setCargando(true)
      setError('')
      try {
        const parametros = conInactivos ? '?incluirInactivos=true' : ''
        setItems(await api.get<TListado[]>(`/catalogos/${recurso}${parametros}`))
      } catch (e) {
        setError(e instanceof ErrorApi ? e.message : `No se pudo cargar ${titulo.toLowerCase()}.`)
      } finally {
        setCargando(false)
      }
    },
    [recurso, titulo],
  )

  useEffect(() => {
    void cargar(incluirInactivos)
  }, [cargar, incluirInactivos])

  function abrirNuevo() {
    setFormulario({ id: null, nombre: '', activo: true, valores: { ...valoresPorDefecto } })
    setAviso('')
    setError('')
  }

  function abrirEdicion(item: TListado) {
    setFormulario({ id: item.id, nombre: item.nombre, activo: item.activo, valores: aValores(item) })
    setAviso('')
    setError('')
  }

  async function guardar() {
    if (!formulario) return

    setGuardando(true)
    setError('')
    setAviso('')

    try {
      const datos = aAlta(formulario.nombre, formulario.activo, formulario.valores)

      if (formulario.id === null) {
        await api.post(`/catalogos/${recurso}`, datos)
        setAviso(`Se creó "${formulario.nombre}".`)
      } else {
        await api.put(`/catalogos/${recurso}/${formulario.id}`, datos)
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

  async function eliminar(item: TListado) {
    if (!confirm(`¿Dar de baja "${item.nombre}"?`)) return

    setError('')
    setAviso('')
    try {
      await api.delete(`/catalogos/${recurso}/${item.id}`)
      setAviso(`"${item.nombre}" se dio de baja.`)
      await cargar(incluirInactivos)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo completar la acción.')
    }
  }

  const columnasExtra = campos.filter((c) => c.columnaEnListado)

  const herramientas = (
    <nav className="p-2 d-flex align-items-center gap-3">
      <div className="form-check form-switch mb-0">
        <input
          id="incluir-inactivos"
          className="form-check-input"
          type="checkbox"
          checked={incluirInactivos}
          onChange={(e) => setIncluirInactivos(e.target.checked)}
        />
        <label className="form-check-label text-light small" htmlFor="incluir-inactivos">
          Incluir inactivos
        </label>
      </div>
      <button type="button" className="btn btn-sm btn-success rounded-0 text-nowrap" onClick={abrirNuevo}>
        Nuevo
      </button>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo={titulo} variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {formulario && (
          <FormularioCatalogo
            valor={formulario}
            campos={campos}
            tituloSingular={tituloSingular}
            guardando={guardando}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={() => setFormulario(null)}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>ID</th>
                  <th>Nombre</th>
                  {columnasExtra.map((c) => (
                    <th key={c.clave}>{c.etiqueta}</th>
                  ))}
                  <th>Estado</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => {
                  const valores = aValores(item)
                  return (
                    <tr key={item.id}>
                      <td>{String(item.id).padStart(4, '0')}</td>
                      <td>{item.nombre}</td>
                      {columnasExtra.map((c) => (
                        <td key={c.clave}>{formatearValorDeColumna(c, valores[c.clave])}</td>
                      ))}
                      <td>
                        <span className={`badge rounded-0 ${item.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                          {item.activo ? 'Activo' : 'Inactivo'}
                        </span>
                      </td>
                      <td className="text-end text-nowrap">
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-primary rounded-0 me-1"
                          onClick={() => abrirEdicion(item)}
                        >
                          Editar
                        </button>
                        {item.activo && (
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-danger rounded-0"
                            onClick={() => eliminar(item)}
                          >
                            Baja
                          </button>
                        )}
                      </td>
                    </tr>
                  )
                })}
                {items.length === 0 && (
                  <tr>
                    <td colSpan={4 + columnasExtra.length} className="text-center text-muted py-4">
                      No hay {titulo.toLowerCase()} cargados.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </Box>
    </div>
  )
}

function formatearValorDeColumna(campo: CampoDescriptor, valor: ValorDeCampo | undefined) {
  if (campo.tipo === 'booleano') return valor ? 'Sí' : 'No'
  if (campo.tipo === 'select') {
    return campo.opciones?.find((o) => o.valor === valor)?.etiqueta ?? String(valor ?? '—')
  }
  return valor === '' || valor === undefined || valor === null ? '—' : String(valor)
}

function FormularioCatalogo({
  valor,
  campos,
  tituloSingular,
  guardando,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  campos: CampoDescriptor[]
  tituloSingular: string
  guardando: boolean
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNuevo = valor.id === null

  function cambiarValorPropio(clave: string, nuevo: ValorDeCampo) {
    onCambio({ ...valor, valores: { ...valor.valores, [clave]: nuevo } })
  }

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
        <strong>{esNuevo ? `Nueva ${tituloSingular}` : `Editando ${tituloSingular} ${valor.id}`}</strong>
      </div>

      <div className="col-md-4">
        <label className="form-label" htmlFor="f-nombre">
          Nombre
        </label>
        <input
          id="f-nombre"
          className="form-control rounded-0"
          maxLength={150}
          value={valor.nombre}
          onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
          required
        />
      </div>

      {campos.map((campo) => (
        <div className="col-md-3" key={campo.clave}>
          <label className="form-label" htmlFor={`f-${campo.clave}`}>
            {campo.etiqueta}
          </label>
          {campo.tipo === 'booleano' ? (
            <div className="form-check pt-2">
              <input
                id={`f-${campo.clave}`}
                type="checkbox"
                className="form-check-input"
                checked={Boolean(valor.valores[campo.clave])}
                onChange={(e) => cambiarValorPropio(campo.clave, e.target.checked)}
              />
            </div>
          ) : campo.tipo === 'select' ? (
            <select
              id={`f-${campo.clave}`}
              className="form-select rounded-0"
              value={String(valor.valores[campo.clave] ?? '')}
              onChange={(e) => cambiarValorPropio(campo.clave, e.target.value)}
              required={campo.requerido}
            >
              {(campo.opciones ?? []).map((o) => (
                <option key={o.valor} value={o.valor}>
                  {o.etiqueta}
                </option>
              ))}
            </select>
          ) : (
            <input
              id={`f-${campo.clave}`}
              type="number"
              step={campo.tipo === 'numeroDecimal' ? '0.01' : '1'}
              className="form-control rounded-0"
              value={String(valor.valores[campo.clave] ?? '')}
              onChange={(e) => cambiarValorPropio(campo.clave, e.target.value)}
              required={campo.requerido}
            />
          )}
        </div>
      ))}

      <div className="col-md-2">
        <label className="form-label" htmlFor="f-activo">
          Estado
        </label>
        <select
          id="f-activo"
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
