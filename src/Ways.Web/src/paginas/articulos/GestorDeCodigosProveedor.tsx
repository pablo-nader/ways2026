import { useCallback, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import type { CodigoProveedorListado, ProveedorListado } from '../../api/tipos'
import { BotonIcono } from '../../componentes/BotonIcono'
import { Cargando } from '../../componentes/Cargando'
import {
  LARGO_MAXIMO_CODIGO_PROVEEDOR,
  mensajeDeErrorDeCodigoProveedor,
  nombreDeProveedorDeCodigo,
} from './codigosProveedor'
import { etiquetaDeProveedor } from './helpers'

/**
 * Códigos de proveedor del artículo: tabla con edición en línea, baja con confirmación y alta.
 * Cada escritura recarga la lista; la sección queda inerte hasta que esa recarga aterriza.
 */
export function GestorDeCodigosProveedor({
  idArticulo,
  idProveedorHabitual,
  proveedores,
  bloqueadoPorPadre,
  alDeEscribir,
}: {
  idArticulo: number
  idProveedorHabitual: number | ''
  proveedores: ProveedorListado[]
  bloqueadoPorPadre: boolean
  alDeEscribir: (enCurso: boolean) => void
}) {
  const [codigos, setCodigos] = useState<CodigoProveedorListado[]>([])
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [ocupado, setOcupado] = useState(false)
  const [editando, setEditando] = useState<{ id: number; texto: string } | null>(null)
  const [confirmandoBaja, setConfirmandoBaja] = useState<number | null>(null)
  // null = sigue al proveedor habitual del artículo hasta que la persona elija otro.
  const [proveedorElegido, setProveedorElegido] = useState<number | '' | null>(null)
  const [nuevoCodigo, setNuevoCodigo] = useState('')
  // Espejo sincrónico de `ocupado`: dos clics en el mismo tick pasan ambos la guarda de estado.
  const ocupadoRef = useRef(false)
  // Generación de lecturas: la última iniciada gana; cambiar de artículo o desmontar la invalida.
  const generacion = useRef(0)

  const invalidarLecturas = useCallback(() => {
    generacion.current++
  }, [])

  const cargar = useCallback(
    async (mensajeDeFallo: string): Promise<void> => {
      const mia = ++generacion.current
      try {
        const lista = await clienteDeArticulos.codigosProveedor(idArticulo)
        if (generacion.current === mia) setCodigos(lista)
      } catch (e) {
        if (generacion.current === mia) setError(mensajeDeErrorDeCodigoProveedor(e, mensajeDeFallo))
      } finally {
        if (generacion.current === mia) setCargando(false)
      }
    },
    [idArticulo],
  )

  useEffect(() => {
    setCargando(true)
    setError('')
    void cargar('No se pudieron cargar los códigos de proveedor.')
    return invalidarLecturas
  }, [cargar, invalidarLecturas])

  const bloqueado = ocupado || bloqueadoPorPadre
  const proveedorDelAlta = proveedorElegido ?? idProveedorHabitual

  async function escribir(operacion: () => Promise<unknown>, mensajeDeFallo: string, alExito: () => void) {
    if (ocupadoRef.current || bloqueadoPorPadre) return
    ocupadoRef.current = true
    setOcupado(true)
    setError('')
    alDeEscribir(true)
    try {
      try {
        await operacion()
      } catch (e) {
        setError(mensajeDeErrorDeCodigoProveedor(e, mensajeDeFallo))
        return
      }
      alExito()
      await cargar('Se guardó, pero no se pudo actualizar la lista.')
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
      alDeEscribir(false)
    }
  }

  function cancelarEdicion() {
    setEditando(null)
    setError('')
  }

  function guardarEdicion() {
    if (!editando) return
    const codigo = editando.texto.trim()
    const actual = codigos.find((c) => c.idCodigoProveedor === editando.id)
    if (!codigo) {
      setError('El código es obligatorio.')
      return
    }
    if (actual?.codigo === codigo) {
      cancelarEdicion()
      return
    }
    void escribir(
      () => clienteDeArticulos.editarCodigoProveedor(idArticulo, editando.id, codigo),
      'No se pudo guardar el código de proveedor.',
      () => setEditando(null),
    )
  }

  function eliminar(idCodigoProveedor: number) {
    void escribir(
      () => clienteDeArticulos.eliminarCodigoProveedor(idArticulo, idCodigoProveedor),
      'No se pudo eliminar el código de proveedor.',
      () => setConfirmandoBaja(null),
    )
  }

  function agregar() {
    const codigo = nuevoCodigo.trim()
    if (proveedorDelAlta === '' || !codigo) return
    void escribir(
      () => clienteDeArticulos.asociarCodigoProveedor(idArticulo, { idProveedor: proveedorDelAlta, codigo }),
      'No se pudo agregar el código de proveedor.',
      () => setNuevoCodigo(''),
    )
  }

  return (
    <div>
      <strong className="text-muted small text-uppercase">Códigos de proveedor</strong>
      {error && <div className="alert alert-danger py-1 px-2 small mt-2">{error}</div>}

      {cargando ? (
        <Cargando texto="Cargando códigos de proveedor…" />
      ) : codigos.length === 0 ? (
        <p className="text-muted small mt-2 mb-2">Sin códigos de proveedor cargados.</p>
      ) : (
        <div className="table-responsive mt-2">
          <table className="table table-sm align-middle mb-2">
            <thead>
              <tr>
                <th>Proveedor</th>
                <th>Código</th>
                <th className="text-end">Acciones</th>
              </tr>
            </thead>
            <tbody>
              {codigos.map((c) => {
                const enEdicion = editando?.id === c.idCodigoProveedor
                const enBaja = confirmandoBaja === c.idCodigoProveedor
                return (
                  <tr key={c.idCodigoProveedor}>
                    <td>{nombreDeProveedorDeCodigo(c.idProveedor, proveedores)}</td>
                    <td>
                      {enEdicion ? (
                        <input
                          type="text"
                          className="form-control form-control-sm"
                          aria-label="Código de proveedor"
                          maxLength={LARGO_MAXIMO_CODIGO_PROVEEDOR}
                          value={editando.texto}
                          disabled={bloqueado}
                          autoFocus
                          onChange={(e) => setEditando({ id: c.idCodigoProveedor, texto: e.target.value })}
                          onKeyDown={(e) => {
                            if (e.key === 'Enter') {
                              e.preventDefault()
                              guardarEdicion()
                            } else if (e.key === 'Escape') {
                              e.preventDefault()
                              e.stopPropagation()
                              cancelarEdicion()
                            }
                          }}
                        />
                      ) : (
                        c.codigo
                      )}
                    </td>
                    <td className="text-end text-nowrap">
                      {enEdicion ? (
                        <>
                          <button type="button" className="btn btn-sm btn-primary me-1" disabled={bloqueado} onClick={guardarEdicion}>
                            Guardar
                          </button>
                          <button type="button" className="btn btn-sm btn-outline-secondary" disabled={bloqueado} onClick={cancelarEdicion}>
                            Cancelar
                          </button>
                        </>
                      ) : enBaja ? (
                        <>
                          <span className="small me-2">¿Eliminar este código?</span>
                          <button
                            type="button"
                            className="btn btn-sm btn-danger me-1"
                            disabled={bloqueado}
                            onClick={() => eliminar(c.idCodigoProveedor)}
                          >
                            Sí, eliminar
                          </button>
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-secondary"
                            disabled={bloqueado}
                            onClick={() => setConfirmandoBaja(null)}
                          >
                            No
                          </button>
                        </>
                      ) : (
                        <>
                          <BotonIcono
                            icono="editar"
                            etiqueta={`Editar código ${c.codigo}`}
                            className="me-1"
                            disabled={bloqueado}
                            onClick={() => {
                              setConfirmandoBaja(null)
                              setError('')
                              setEditando({ id: c.idCodigoProveedor, texto: c.codigo })
                            }}
                          />
                          <BotonIcono
                            icono="eliminar"
                            etiqueta={`Eliminar código ${c.codigo}`}
                            disabled={bloqueado}
                            onClick={() => {
                              setEditando(null)
                              setError('')
                              setConfirmandoBaja(c.idCodigoProveedor)
                            }}
                          />
                        </>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      <div className="d-flex flex-wrap gap-2 mt-2">
        <select
          className="form-select"
          style={{ maxWidth: 240 }}
          aria-label="Proveedor del código nuevo"
          value={proveedorDelAlta}
          disabled={bloqueado}
          onChange={(e) => setProveedorElegido(e.target.value === '' ? '' : Number(e.target.value))}
        >
          <option value="">Elegí un proveedor</option>
          {proveedores.map((p) => (
            <option key={p.id} value={p.id}>
              {etiquetaDeProveedor(p)}
            </option>
          ))}
        </select>
        <input
          type="text"
          className="form-control"
          style={{ maxWidth: 240 }}
          maxLength={LARGO_MAXIMO_CODIGO_PROVEEDOR}
          placeholder="Código de proveedor"
          aria-label="Código de proveedor nuevo"
          value={nuevoCodigo}
          disabled={bloqueado}
          onChange={(e) => setNuevoCodigo(e.target.value)}
          onKeyDown={(e) => {
            if (e.key !== 'Enter') return
            e.preventDefault()
            agregar()
          }}
        />
        <button
          type="button"
          className="btn btn-outline-primary"
          disabled={bloqueado || proveedorDelAlta === '' || !nuevoCodigo.trim()}
          onClick={agregar}
        >
          Agregar código
        </button>
      </div>
    </div>
  )
}
