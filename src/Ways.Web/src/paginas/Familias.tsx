import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { copiaDeFalloDeBaja } from '../api/bajas'
import { ErrorApi } from '../api/cliente'
import { clienteDeFamilias } from '../api/familias'
import type { FamiliaListado } from '../api/tipos'
import { BotonIcono } from '../componentes/BotonIcono'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { CAMPOS_PROPIOS, cantidadDeArticulos } from './articulos/familia'
import { mensajeDeFalloDeEscritura } from './familias/mensajes'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_DISOLUCION = 'Se disolvió, pero no se pudo actualizar la vista. Recargá la pantalla.'

type Formulario = { id: number; nombre: string; activo: boolean }

/** Lo que la puerta de la disolución dice que pasa con los artículos: sus valores y sus precios no se tocan. */
function notaDeDisolucion(familia: FamiliaListado): string {
  const cantidad = familia.cantidadArticulos
  const articulos =
    cantidad === 0
      ? 'La familia no tiene artículos vivos.'
      : cantidad === 1
        ? 'Su único artículo queda sin familia y conserva todos sus valores y sus precios.'
        : `Sus ${cantidadDeArticulos(cantidad)} quedan sin familia y conservan todos sus valores y sus precios.`

  return `${articulos} La familia se da de baja y su nombre se puede volver a usar.`
}

/**
 * Listado de familias de artículos (doc 10 §3): renombrar, activar o desactivar y disolver; el detalle y las
 * altas están en `/familias/:id` y `/familias/nueva`. `bloqueado` deja inerte la pantalla entera mientras el
 * guardado, la disolución o la puerta (`ConfirmacionDeBaja`) están abiertos —los enlaces, con `preventDefault`—, así
 * que nada supera a una escritura en vuelo: solo las lecturas llevan generación (la más nueva gana) y las escrituras
 * no, a diferencia de `Categorias.tsx`, donde guardar y dar de baja acuñan el suyo. Un espejo sincrónico
 * (`ocupadoRef`) frena el doble clic en el mismo tick (`react-async-state` reglas 9, 11 y 13).
 */
export function Familias() {
  const [items, setItems] = useState<FamiliaListado[]>([])
  const [cargando, setCargando] = useState(true)
  // Un slot por fuente (react-async-state regla 14): el fallo de la lectura y el rechazo de una escritura no se pisan.
  const [errorDeCarga, setErrorDeCarga] = useState('')
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)
  const [disolucion, setDisolucion] = useState<FamiliaListado | null>(null)
  /** Id de la familia cuya disolución está en vuelo (distinto de `guardando`, que cubre el renombrado). */
  const [ocupadoDisolucion, setOcupadoDisolucion] = useState<number | null>(null)
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)

  /** Cada lectura (la inicial y el refresco de cada escritura) acuña la suya: la más nueva gana. Las escrituras no
   * usan generación: la pantalla queda inerte mientras una está en vuelo (`bloqueado`), así que nada la supera. */
  const generacion = useRef(0)
  /** Espejo síncrono de "hay una escritura en vuelo": dos clics en el mismo tick pasan la guarda de estado. */
  const ocupadoRef = useRef(false)

  const cargar = useCallback(async (token: number, propagar = false) => {
    setCargando(true)
    try {
      const filas = await clienteDeFamilias.listar()
      if (generacion.current !== token) return
      setItems(filas)
      setErrorDeCarga('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setErrorDeCarga(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las familias.')
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar(++generacion.current)
  }, [cargar])

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya commiteó nunca se
   * reporta como fallida (`react-async-state` regla 6). */
  async function refrescarTrasEscribir(mensajeOk: string, avisoDeFallo: string) {
    setAviso(mensajeOk)
    try {
      await cargar(++generacion.current, true)
    } catch {
      setAviso(`${mensajeOk} ${avisoDeFallo}`)
    }
  }

  async function guardar() {
    if (!formulario || ocupadoRef.current) return

    const datos = formulario
    const nombre = datos.nombre.trim()
    ocupadoRef.current = true
    setGuardando(true)
    setError('')
    setAviso('')
    try {
      try {
        await clienteDeFamilias.actualizar(datos.id, { nombre, activo: datos.activo })
      } catch (e) {
        setError(mensajeDeFalloDeEscritura(e, 'guardar la familia'))
        // La familia ya no existe: el formulario ofrecería guardar sobre algo que dejó de estar, así que se cierra y se
        // vuelve a leer el listado.
        if (e instanceof ErrorApi && e.estado === 404) {
          setFormulario(null)
          await cargar(++generacion.current)
        }

        return
      }

      setFormulario(null)
      await refrescarTrasEscribir(`Se actualizó la familia "${nombre}".`, AVISO_REFRESCO_FALLIDO)
    } finally {
      ocupadoRef.current = false
      setGuardando(false)
    }
  }

  function pedirDisolucion(familia: FamiliaListado, disparador: HTMLElement | null) {
    setDisparadorDeLaPuerta(disparador)
    setDisolucion(familia)
    setError('')
    setAviso('')
  }

  function cancelarDisolucion() {
    setDisparadorDeLaPuerta(null)
    setDisolucion(null)
    setError('')
  }

  async function confirmarDisolucion() {
    if (!disolucion || ocupadoRef.current) return

    const familia = disolucion
    ocupadoRef.current = true
    setOcupadoDisolucion(familia.id)
    setError('')
    try {
      try {
        await clienteDeFamilias.disolver(familia.id)
      } catch (e) {
        setError(copiaDeFalloDeBaja(e, 'la familia', 'disolver'))
        // La familia ya no existe: no hay nada que confirmar, así que la puerta se cierra, se cierra el formulario de
        // esa familia si estaba abierto y se vuelve a leer el listado.
        if (e instanceof ErrorApi && e.estado === 404) {
          setDisolucion(null)
          setFormulario((prev) => (prev?.id === familia.id ? null : prev))
          await cargar(++generacion.current)
        }

        return
      }

      setDisolucion(null)
      // Disolver la familia que se está editando se lleva también su formulario: dejarlo abierto ofrecía guardar
      // sobre una familia que ya no existe, y el PUT moría en 404.
      setFormulario((prev) => (prev?.id === familia.id ? null : prev))
      await refrescarTrasEscribir(
        `Se disolvió la familia "${familia.nombre}": sus artículos quedaron sin familia.`,
        AVISO_REFRESCO_FALLIDO_DISOLUCION,
      )
    } finally {
      ocupadoRef.current = false
      setOcupadoDisolucion(null)
    }
  }

  function abrirEdicion(familia: FamiliaListado) {
    setFormulario({ id: familia.id, nombre: familia.nombre, activo: familia.activo })
    setAviso('')
    setError('')
  }

  function cancelarEdicion() {
    setFormulario(null)
    setError('')
  }

  /** La puerta abierta o cualquier escritura en vuelo bloquean la pantalla entera. */
  const bloqueado = guardando || ocupadoDisolucion !== null || disolucion !== null

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <Link
        to="/familias/nueva"
        className="btn btn-sm btn-success text-nowrap"
        aria-disabled={bloqueado}
        onClick={(evento) => {
          if (bloqueado) evento.preventDefault()
        }}
      >
        Nueva familia
      </Link>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Familias" variante="inverse" herramientas={herramientas}>
        <p className="text-muted">
          Una familia agrupa artículos idénticos en sus campos compartidos y en el precio de cada lista de precios fija: lo que se cambia en uno
          se aplica a todos. Son propios de cada artículo: {CAMPOS_PROPIOS}.
        </p>

        {errorDeCarga && <div className="alert alert-danger">{errorDeCarga}</div>}
        {error && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}

        {disolucion && (
          <ConfirmacionDeBaja
            titulo={`la familia "${disolucion.nombre}"`}
            pregunta="Disolver"
            nota={notaDeDisolucion(disolucion)}
            etiquetaConfirmar="Confirmar disolución"
            etiquetaEnCurso="Disolviendo…"
            ocupado={ocupadoDisolucion !== null}
            disparador={disparadorDeLaPuerta}
            onConfirmar={confirmarDisolucion}
            onCancelar={cancelarDisolucion}
          />
        )}

        {formulario && (
          <FormularioDeFamilia
            valor={formulario}
            guardando={guardando}
            bloqueado={bloqueado}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={cancelarEdicion}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>Nombre</th>
                  <th className="text-end">Artículos</th>
                  <th>Estado</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {items.map((familia) => (
                  <tr key={familia.id}>
                    <td>{familia.nombre}</td>
                    <td className="text-end">{familia.cantidadArticulos}</td>
                    <td>
                      <span className={`badge ${familia.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                        {familia.activo ? 'Activa' : 'Inactiva'}
                      </span>
                    </td>
                    <td className="text-end text-nowrap">
                      <div className="d-inline-flex gap-1 align-items-center">
                        <Link
                          to={`/familias/${familia.id}`}
                          className="btn btn-sm btn-outline-primary"
                          aria-label={`Ver detalle de ${familia.nombre}`}
                          aria-disabled={bloqueado}
                          onClick={(evento) => {
                            if (bloqueado) evento.preventDefault()
                          }}
                        >
                          Ver detalle
                        </Link>
                        <BotonIcono
                          icono="editar"
                          etiqueta={`Editar ${familia.nombre}`}
                          onClick={() => abrirEdicion(familia)}
                          disabled={bloqueado}
                        />
                        <BotonIcono
                          icono="eliminar"
                          etiqueta={`Disolver ${familia.nombre}`}
                          title="Disolver la familia"
                          onClick={(evento) => pedirDisolucion(familia, evento.currentTarget)}
                          disabled={bloqueado}
                        />
                      </div>
                    </td>
                  </tr>
                ))}
                {items.length === 0 && errorDeCarga === '' && (
                  <tr>
                    <td colSpan={4} className="text-center text-muted py-4">
                      Todavía no hay familias.
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

function FormularioDeFamilia({
  valor,
  guardando,
  bloqueado,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  guardando: boolean
  bloqueado: boolean
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  return (
    <form
      className="row g-3 border p-3 mb-4 bg-body"
      autoComplete="off"
      onSubmit={(e) => {
        e.preventDefault()
        onGuardar()
      }}
    >
      <div className="col-12">
        <strong>Editando familia {valor.id}</strong>
      </div>

      <div className="col-md-6">
        <label className="form-label" htmlFor="ff-nombre">
          Nombre
        </label>
        <input
          id="ff-nombre"
          className="form-control"
          maxLength={150}
          value={valor.nombre}
          onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
          disabled={bloqueado}
          required
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="ff-estado">
          Estado
        </label>
        <select
          id="ff-estado"
          className="form-select"
          value={valor.activo ? 'activa' : 'inactiva'}
          aria-describedby="ff-estado-ayuda"
          onChange={(e) => onCambio({ ...valor, activo: e.target.value === 'activa' })}
          disabled={bloqueado}
        >
          <option value="activa">Activa</option>
          <option value="inactiva">Inactiva</option>
        </select>
        <div id="ff-estado-ayuda" className="form-text">
          Una familia inactiva no admite artículos nuevos.
        </div>
      </div>

      <div className="col-12 d-flex gap-2">
        <button type="submit" className="btn btn-success" disabled={bloqueado}>
          {guardando ? 'Guardando…' : 'Guardar'}
        </button>
        <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
          Cancelar
        </button>
      </div>
    </form>
  )
}
