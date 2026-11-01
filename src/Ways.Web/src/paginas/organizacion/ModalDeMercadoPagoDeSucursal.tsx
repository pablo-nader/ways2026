import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../../api/cliente'
import { clienteDeLocalesMercadoPago, copiaDeFalloDeMercadoPago, type EstadoDeLocalMercadoPago } from '../../api/mercadoPago'
import { Cargando } from '../../componentes/Cargando'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import { fechaHoraATexto } from '../../formato/fechas'
import { etiquetasDeCamposFaltantes, presentacionDeLocal } from './estadosDeMercadoPago'
import { useEscrituraUnica } from './useEscrituraUnica'

const CODIGO_DIRECCION_INCOMPLETA = 'direccion_incompleta_para_mercadopago'

type Props = {
  sucursal: { id: number; nombre: string }
  focoDeReserva?: React.RefObject<HTMLElement | null>
  onCerrar: () => void
}

/**
 * Estado de una sucursal como local de Mercado Pago y su registro (solo Admin). Lee el estado al abrirse; registrar devuelve
 * el estado nuevo, así que no hay lectura de refresco. Cada lectura y cada respuesta de escritura llevan el token de
 * generación: si la sucursal cambia o el modal se cierra, una respuesta tardía no pisa lo que está en pantalla.
 */
export function ModalDeMercadoPagoDeSucursal({ sucursal, focoDeReserva, onCerrar }: Props) {
  const [estado, setEstado] = useState<EstadoDeLocalMercadoPago | null>(null)
  const [cargando, setCargando] = useState(true)
  const [errorDeLectura, setErrorDeLectura] = useState('')
  const [errorDeEscritura, setErrorDeEscritura] = useState('')
  const [aviso, setAviso] = useState('')
  const { ocupado, correr } = useEscrituraUnica()
  const generacion = useRef(0)
  const idSucursal = sucursal.id

  /** Una respuesta en vuelo deja de valer cuando la sucursal cambia o el modal se desmonta. */
  const invalidarLecturas = useCallback(() => {
    generacion.current++
  }, [])

  const cargar = useCallback(async () => {
    const token = ++generacion.current
    setCargando(true)
    setErrorDeLectura('')
    try {
      const leido = await clienteDeLocalesMercadoPago.obtener(idSucursal)
      if (generacion.current !== token) return
      setEstado(leido)
    } catch (e) {
      if (generacion.current !== token) return
      setErrorDeLectura(copiaDeFalloDeMercadoPago(e, 'leer_local'))
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [idSucursal])

  useEffect(() => {
    setEstado(null)
    setErrorDeEscritura('')
    setAviso('')
    void cargar()

    return invalidarLecturas
  }, [cargar, invalidarLecturas])

  async function registrar() {
    await correr(async () => {
      const token = generacion.current
      const eraNueva = estado?.estado === 'no_registrada'
      setErrorDeEscritura('')
      setAviso('')
      try {
        const nuevo = await clienteDeLocalesMercadoPago.registrar(idSucursal)
        if (generacion.current !== token) return
        setEstado(nuevo)
        setAviso(eraNueva ? 'La sucursal quedó registrada en Mercado Pago.' : 'Se actualizó la sucursal en Mercado Pago.')
      } catch (e) {
        if (generacion.current !== token) return
        setErrorDeEscritura(copiaDeFalloDeMercadoPago(e, 'registrar_local'))
        if (e instanceof ErrorApi && e.codigo === CODIGO_DIRECCION_INCOMPLETA) void cargar()
      }
    })
  }

  const faltantes = estado?.camposFaltantes ?? []
  const puedeRegistrar =
    estado !== null && (estado.estado === 'no_registrada' || estado.estado === 'desactualizada') && faltantes.length === 0
  const etiquetaDelBoton = estado?.estado === 'desactualizada' ? 'Actualizar en Mercado Pago' : 'Registrar en Mercado Pago'

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCerrar} disabled={ocupado}>
        Cerrar
      </button>
      {puedeRegistrar && (
        <button type="button" className="btn btn-success" onClick={() => void registrar()} disabled={ocupado || cargando}>
          {ocupado ? 'Enviando…' : etiquetaDelBoton}
        </button>
      )}
    </>
  )

  return (
    <Modal
      titulo={`Mercado Pago de ${sucursal.nombre}`}
      tamano="lg"
      ocupado={ocupado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCerrar}
    >
      <ErrorDeFormulario mensaje={errorDeEscritura} />
      {aviso && (
        <div role="status" className="alert alert-success py-2 px-3 small">
          {aviso}
        </div>
      )}

      {cargando && estado === null ? (
        <Cargando />
      ) : errorDeLectura && estado === null ? (
        <div className="alert alert-danger" role="alert">
          {errorDeLectura}{' '}
          <button type="button" className="btn btn-sm btn-outline-danger" disabled={cargando} onClick={() => void cargar()}>
            Reintentar
          </button>
        </div>
      ) : estado ? (
        <div>
          <p className="mb-2">
            <span className={`badge me-2 ${presentacionDeLocal(estado.estado).clase}`}>{presentacionDeLocal(estado.estado).etiqueta}</span>
            {estado.idStoreMercadoPago !== null && (
              <>
                Local de Mercado Pago <strong>{estado.idStoreMercadoPago}</strong>
              </>
            )}
          </p>

          {estado.estado === 'sin_conexion' && (
            <p className="text-muted mb-0">
              La empresa no tiene Mercado Pago conectado. Conectalo desde la pantalla de Empresas para poder registrar la sucursal.
            </p>
          )}
          {estado.estado === 'no_registrada' && (
            <p className="text-muted">La sucursal todavía no está registrada como local en la cuenta de Mercado Pago conectada.</p>
          )}
          {estado.estado === 'desactualizada' && (
            <p className="text-muted">La sucursal se editó después de la última sincronización con Mercado Pago.</p>
          )}
          {estado.fechaRegistro && <p className="mb-1 text-muted">Registrada el {fechaHoraATexto(estado.fechaRegistro)}.</p>}
          {estado.fechaSincronizacion && (
            <p className="mb-1 text-muted">Última sincronización: {fechaHoraATexto(estado.fechaSincronizacion)}.</p>
          )}

          {estado.estado !== 'sin_conexion' && faltantes.length > 0 && (
            <div className="alert alert-warning mt-2 mb-0" role="alert">
              Para registrar la sucursal en Mercado Pago completá: <strong>{etiquetasDeCamposFaltantes(faltantes)}</strong>. Editá la
              sucursal para cargarlos.
            </div>
          )}
        </div>
      ) : null}
    </Modal>
  )
}
