import { useId, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import type { ClienteListado } from '../api/tipos'
import { Modal } from '../componentes/Modal'
import { useMontado } from '../paginas/familias/useMontado'
import { armarAltaRapida } from './altaRapidaDeCliente'

type Props = {
  /** El cliente ya existe en el servidor: quien llama lo selecciona, lo suma a la copia local y cierra el modal. */
  alCreado: (cliente: ClienteListado) => void
  alCerrar: () => void
}

/**
 * Alta rápida de un cliente desde la venta: nombre, teléfono y domicilio. El servidor lo crea sin cuenta
 * corriente, así que no hay campos de crédito. Mientras guarda, el modal y sus campos quedan inertes.
 */
export function ModalAltaRapidaDeCliente({ alCreado, alCerrar }: Props) {
  const idFormulario = useId()
  const [nombre, setNombre] = useState('')
  const [telefono, setTelefono] = useState('')
  const [domicilio, setDomicilio] = useState('')
  const [error, setError] = useState('')
  const [guardando, setGuardando] = useState(false)
  const guardandoRef = useRef(false)
  const montado = useMontado()

  const alta = armarAltaRapida(nombre, telefono, domicilio)

  async function guardar() {
    if (guardandoRef.current) return
    const datos = armarAltaRapida(nombre, telefono, domicilio)
    if (datos === null) return
    guardandoRef.current = true
    setGuardando(true)
    setError('')
    try {
      const creado = await clienteDeClientes.crearRapido(datos)
      if (!montado.current) return
      alCreado(creado)
    } catch (e) {
      if (!montado.current) return
      setError(e instanceof ErrorApi ? e.message : 'No se pudo crear el cliente. Revisá la conexión e intentá de nuevo.')
    } finally {
      guardandoRef.current = false
      if (montado.current) setGuardando(false)
    }
  }

  return (
    <Modal
      titulo="Nuevo cliente"
      tamano="sm"
      ocupado={guardando}
      restaurarFoco={false}
      onCerrar={alCerrar}
      pie={
        <>
          <button type="button" className="btn btn-outline-secondary" disabled={guardando} onClick={alCerrar}>
            Cancelar
          </button>
          <button type="submit" form={idFormulario} className="btn btn-primary" disabled={guardando || alta === null}>
            Crear cliente
          </button>
        </>
      }
    >
      <form
        id={idFormulario}
        onSubmit={(evento) => {
          evento.preventDefault()
          void guardar()
        }}
      >
        <div className="mb-3">
          <label htmlFor={`${idFormulario}-nombre`} className="form-label">
            Nombre
          </label>
          <input
            id={`${idFormulario}-nombre`}
            className="form-control"
            autoFocus
            required
            maxLength={150}
            value={nombre}
            disabled={guardando}
            onChange={(e) => setNombre(e.target.value)}
          />
        </div>
        <div className="mb-3">
          <label htmlFor={`${idFormulario}-telefono`} className="form-label">
            Teléfono
          </label>
          <input
            id={`${idFormulario}-telefono`}
            className="form-control"
            type="tel"
            maxLength={50}
            value={telefono}
            disabled={guardando}
            onChange={(e) => setTelefono(e.target.value)}
          />
        </div>
        <div className="mb-3">
          <label htmlFor={`${idFormulario}-domicilio`} className="form-label">
            Domicilio
          </label>
          <input
            id={`${idFormulario}-domicilio`}
            className="form-control"
            maxLength={255}
            value={domicilio}
            disabled={guardando}
            onChange={(e) => setDomicilio(e.target.value)}
          />
        </div>
        <p className="form-text mb-0">El cliente se crea sin cuenta corriente. El crédito se configura desde la administración.</p>
        {error !== '' && (
          <div className="alert alert-danger mt-3 mb-0" role="alert">
            {error}
          </div>
        )}
      </form>
    </Modal>
  )
}
