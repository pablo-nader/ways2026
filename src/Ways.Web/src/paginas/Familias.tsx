import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
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
import { ModalDeFamilia, type FormularioDeFamilia } from './familias/ModalDeFamilia'
import { ModalDeNuevaFamilia } from './familias/ModalDeNuevaFamilia'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_DISOLUCION = 'Se disolvió, pero no se pudo actualizar la vista. Recargá la pantalla.'

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
 * Listado de familias de artículos (doc 10 §3): alta, renombrar, activar o desactivar y disolver; el detalle está en
 * `/familias/:id`. El alta (`ModalDeNuevaFamilia`) y el renombrado (`ModalDeFamilia`) son modales. `bloqueado` deja
 * inerte la pantalla entera mientras el guardado, la disolución o la puerta (`ConfirmacionDeBaja`) están abiertos
 * —los enlaces, con `preventDefault`—, así que nada supera a una escritura en vuelo: solo las lecturas llevan
 * generación (la más nueva gana) y las escrituras no, a diferencia de `Categorias.tsx`, donde guardar y dar de baja
 * acuñan el suyo. Un espejo sincrónico (`ocupadoRef`) frena el doble clic en el mismo tick (`react-async-state`
 * reglas 9, 11 y 13). `/familias/nueva` redirige acá con `?nuevo=1`, que abre el modal de alta una sola vez.
 */
export function Familias() {
  const [items, setItems] = useState<FamiliaListado[]>([])
  const [cargando, setCargando] = useState(true)
  // Un slot por fuente (react-async-state regla 14): el fallo de la lectura y el rechazo de una escritura no se pisan.
  const [errorDeCarga, setErrorDeCarga] = useState('')
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [parametros, setParametros] = useSearchParams()
  const [formulario, setFormulario] = useState<FormularioDeFamilia | null>(null)
  /** El rechazo del guardado vive en el modal; `error` queda para lo que le pasó a la pantalla (una familia que ya no existe, la disolución). */
  const [errorDeFormulario, setErrorDeFormulario] = useState('')
  const [guardando, setGuardando] = useState(false)
  const [creando, setCreando] = useState(() => parametros.get('nuevo') === '1')
  const [disolucion, setDisolucion] = useState<FamiliaListado | null>(null)
  /** Id de la familia cuya disolución está en vuelo (distinto de `guardando`, que cubre el renombrado). */
  const [ocupadoDisolucion, setOcupadoDisolucion] = useState<number | null>(null)
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)

  /** Cada lectura (la inicial y el refresco de cada escritura) acuña la suya: la más nueva gana. Las escrituras no
   * usan generación: la pantalla queda inerte mientras una está en vuelo (`bloqueado`), así que nada la supera. */
  const generacion = useRef(0)
  /** Espejo síncrono de "hay una escritura en vuelo": dos clics en el mismo tick pasan la guarda de estado. */
  const ocupadoRef = useRef(false)
  /** Destino de foco al cerrar un modal cuya fila disparadora ya no existe (la tabla se reemplaza por "Cargando…"
   * durante el refresco): el botón de alta, que es estable. */
  const nuevaRef = useRef<HTMLButtonElement>(null)

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

  // El parámetro solo abre el modal al llegar: se consume para que recargar o volver no lo reabra.
  useEffect(() => {
    if (!parametros.has('nuevo')) return

    setParametros(
      (previos) => {
        const siguientes = new URLSearchParams(previos)
        siguientes.delete('nuevo')

        return siguientes
      },
      { replace: true },
    )
  }, [parametros, setParametros])

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
    setErrorDeFormulario('')
    try {
      try {
        await clienteDeFamilias.actualizar(datos.id, { nombre, activo: datos.activo })
      } catch (e) {
        const mensaje = mensajeDeFalloDeEscritura(e, 'guardar la familia')
        if (!(e instanceof ErrorApi && e.estado === 404)) {
          setErrorDeFormulario(mensaje)

          return
        }

        // La familia ya no existe: el modal ofrecería guardar sobre algo que dejó de estar, así que se cierra con el
        // rechazo a la vista y se vuelve a leer el listado. Sigue montado durante la relectura y se cierra al liberarse.
        setError(mensaje)
        await cargar(++generacion.current)
        setFormulario(null)

        return
      }

      // El modal sigue montado durante el refresco y se cierra en el mismo lote que libera `guardando`: así el
      // disparador (o el botón de alta, si la fila se reemplazó) ya está habilitado cuando el modal restaura el foco.
      await refrescarTrasEscribir(`Se actualizó la familia "${nombre}".`, AVISO_REFRESCO_FALLIDO)
      setFormulario(null)
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
        // La familia ya no existe: no hay nada que confirmar, así que la puerta se cierra y se vuelve a leer el listado.
        if (e instanceof ErrorApi && e.estado === 404) {
          setDisolucion(null)
          await cargar(++generacion.current)
        }

        return
      }

      setDisolucion(null)
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
    setErrorDeFormulario('')
  }

  function abrirAlta() {
    setCreando(true)
    setAviso('')
    setError('')
  }

  /** La puerta abierta o cualquier escritura en vuelo bloquean la pantalla entera. Un modal abierto no entra acá: su
   * fondo ya deja inerte el resto, y los disparadores tienen que seguir habilitados para recibir el foco al cerrarlo. */
  const bloqueado = guardando || ocupadoDisolucion !== null || disolucion !== null

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <button ref={nuevaRef} type="button" className="btn btn-sm btn-success text-nowrap" disabled={bloqueado} onClick={abrirAlta}>
        Nueva familia
      </button>
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
        {error && !disolucion && <div className="alert alert-danger">{error}</div>}
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
            error={error}
            onConfirmar={confirmarDisolucion}
            onCancelar={cancelarDisolucion}
          />
        )}

        {formulario && (
          <ModalDeFamilia
            valor={formulario}
            guardando={guardando}
            error={errorDeFormulario}
            focoDeReserva={nuevaRef}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={() => setFormulario(null)}
          />
        )}

        {creando && <ModalDeNuevaFamilia focoDeReserva={nuevaRef} onCancelar={() => setCreando(false)} />}

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
