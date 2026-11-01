import { useCallback, useEffect, useRef, useState } from 'react'
import {
  clienteDeCajasMercadoPago,
  copiaDeFalloDeMercadoPago,
  esUrlDeAutorizacionNavegable,
  type EstadoDeCajaMercadoPago,
  type ListadoDeTerminales,
} from '../../api/mercadoPago'
import { Cargando } from '../../componentes/Cargando'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import { fechaHoraATexto } from '../../formato/fechas'
import { etiquetaDeModoDeTerminal, presentacionDeCaja, tieneAsignacionVieja } from './estadosDeMercadoPago'
import { useEscrituraUnica } from './useEscrituraUnica'

const AVISO_LISTA_NO_ACTUALIZADA = 'No se pudo actualizar la lista de terminales.'

type Props = {
  puntoVenta: { id: number; nombre: string }
  focoDeReserva?: React.RefObject<HTMLElement | null>
  onCerrar: () => void
}

const tieneCaja = (estado: EstadoDeCajaMercadoPago | null) => estado?.estado === 'registrada' || estado?.estado === 'desactualizada'

/**
 * Estado de un punto de venta como caja de Mercado Pago (solo Admin): registro, QR estático y terminal Point. Dos lecturas
 * con generación propia (la caja y las terminales); toda respuesta de lectura o de escritura se compara con la generación
 * vigente antes de tocar el estado. Una sola escritura a la vez, con el modal inerte hasta que termina.
 */
export function ModalDeMercadoPagoDePuntoVenta({ puntoVenta, focoDeReserva, onCerrar }: Props) {
  const [estado, setEstado] = useState<EstadoDeCajaMercadoPago | null>(null)
  const [cargando, setCargando] = useState(true)
  const [errorDeLectura, setErrorDeLectura] = useState('')
  const [terminales, setTerminales] = useState<ListadoDeTerminales | null>(null)
  const [cargandoTerminales, setCargandoTerminales] = useState(false)
  const [errorDeTerminales, setErrorDeTerminales] = useState('')
  const [errorDeEscritura, setErrorDeEscritura] = useState('')
  const [aviso, setAviso] = useState('')
  const { ocupado, correr } = useEscrituraUnica()
  const generacion = useRef(0)
  const generacionTerminales = useRef(0)
  const idPuntoVenta = puntoVenta.id

  /** Devuelve `false` si la lectura falló; una lectura superada por otra no cuenta como fallo. */
  const cargarTerminales = useCallback(async (): Promise<boolean> => {
    const token = ++generacionTerminales.current
    setCargandoTerminales(true)
    setErrorDeTerminales('')
    try {
      const leidas = await clienteDeCajasMercadoPago.listarTerminales(idPuntoVenta)
      if (generacionTerminales.current !== token) return true
      setTerminales(leidas)

      return true
    } catch (e) {
      if (generacionTerminales.current !== token) return true
      setErrorDeTerminales(copiaDeFalloDeMercadoPago(e, 'leer_terminales'))

      return false
    } finally {
      if (generacionTerminales.current === token) setCargandoTerminales(false)
    }
  }, [idPuntoVenta])

  /** Una respuesta en vuelo deja de valer cuando el punto de venta cambia o el modal se desmonta. */
  const invalidarLecturas = useCallback(() => {
    generacion.current++
    generacionTerminales.current++
  }, [])

  const cargar = useCallback(async () => {
    const token = ++generacion.current
    setCargando(true)
    setErrorDeLectura('')
    try {
      const leido = await clienteDeCajasMercadoPago.obtener(idPuntoVenta)
      if (generacion.current !== token) return
      setEstado(leido)
      if (tieneCaja(leido)) await cargarTerminales()
    } catch (e) {
      if (generacion.current !== token) return
      setErrorDeLectura(copiaDeFalloDeMercadoPago(e, 'leer_caja'))
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [idPuntoVenta, cargarTerminales])

  useEffect(() => {
    setEstado(null)
    setTerminales(null)
    setErrorDeTerminales('')
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
        const nuevo = await clienteDeCajasMercadoPago.registrar(idPuntoVenta)
        if (generacion.current !== token) return
        setEstado(nuevo)
        setAviso(eraNueva ? 'El punto de venta quedó registrado en Mercado Pago.' : 'Se actualizó el punto de venta en Mercado Pago.')
        if (tieneCaja(nuevo)) await cargarTerminales()
      } catch (e) {
        if (generacion.current !== token) return
        setErrorDeEscritura(copiaDeFalloDeMercadoPago(e, 'registrar_caja'))
      }
    })
  }

  /** La escritura de terminal y la relectura de la lista van separadas: lo que ya se guardó no se informa como fallo. */
  async function cambiarTerminal(
    accion: 'asignar_terminal' | 'quitar_terminal',
    escribir: () => Promise<{ modoCambiado: boolean }>,
    mensajeOk: (r: { modoCambiado: boolean }) => string,
  ) {
    await correr(async () => {
      const token = generacion.current
      setErrorDeEscritura('')
      setAviso('')
      let resultado: { modoCambiado: boolean }
      try {
        resultado = await escribir()
      } catch (e) {
        if (generacion.current !== token) return
        setErrorDeEscritura(copiaDeFalloDeMercadoPago(e, accion))

        return
      }

      if (generacion.current !== token) return
      const refrescada = await cargarTerminales()
      if (generacion.current !== token) return
      setAviso(refrescada ? mensajeOk(resultado) : `${mensajeOk(resultado)} ${AVISO_LISTA_NO_ACTUALIZADA}`)
    })
  }

  const asignar = (idTerminal: string) =>
    cambiarTerminal(
      'asignar_terminal',
      () => clienteDeCajasMercadoPago.asignarTerminal(idPuntoVenta, idTerminal),
      (r) =>
        r.modoCambiado
          ? `Se asignó la terminal ${idTerminal} y se pasó a modo PDV. Reiniciala para que tome el cambio.`
          : `Se asignó la terminal ${idTerminal}.`,
    )

  const quitar = () =>
    cambiarTerminal(
      'quitar_terminal',
      () => clienteDeCajasMercadoPago.quitarTerminal(idPuntoVenta),
      () => 'Se quitó la terminal de este punto de venta en Ways. La terminal sigue vinculada en Mercado Pago.',
    )

  const puedeRegistrar = estado !== null && (estado.estado === 'no_registrada' || estado.estado === 'desactualizada')
  const etiquetaDelBoton = estado?.estado === 'desactualizada' ? 'Actualizar en Mercado Pago' : 'Registrar en Mercado Pago'
  const urlQr = estado?.urlQr && esUrlDeAutorizacionNavegable(estado.urlQr) ? estado.urlQr : null
  const asignacionVieja = terminales !== null && tieneAsignacionVieja(terminales)

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
      titulo={`Mercado Pago de ${puntoVenta.nombre}`}
      tamano="lg"
      desplazable
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
            <span className={`badge me-2 ${presentacionDeCaja(estado.estado).clase}`}>{presentacionDeCaja(estado.estado).etiqueta}</span>
            {estado.idPosMercadoPago !== null && (
              <>
                Caja de Mercado Pago <strong>{estado.idPosMercadoPago}</strong>
              </>
            )}
          </p>

          {estado.estado === 'sin_conexion' && (
            <p className="text-muted mb-0">
              La empresa no tiene Mercado Pago conectado. Conectalo desde la pantalla de Empresas para poder registrar el punto de venta.
            </p>
          )}
          {estado.estado === 'local_no_registrado' && (
            <p className="text-muted mb-0">
              La sucursal de este punto de venta todavía no está registrada en Mercado Pago. Registrala primero desde la pantalla de
              Sucursales.
            </p>
          )}
          {estado.estado === 'no_registrada' && (
            <p className="text-muted mb-0">El punto de venta todavía no está registrado como caja en Mercado Pago.</p>
          )}
          {estado.estado === 'desactualizada' && (
            <p className="text-muted">El punto de venta se editó o cambió de sucursal después de la última sincronización con Mercado Pago.</p>
          )}
          {estado.fechaRegistro && <p className="mb-1 text-muted">Registrado el {fechaHoraATexto(estado.fechaRegistro)}.</p>}
          {estado.fechaSincronizacion && (
            <p className="mb-1 text-muted">Última sincronización: {fechaHoraATexto(estado.fechaSincronizacion)}.</p>
          )}

          {tieneCaja(estado) && (
            <section className="mt-3" aria-label="QR del punto de venta">
              <h2 className="h6">QR del punto de venta</h2>
              {urlQr ? (
                <div className="d-flex align-items-center gap-3">
                  <img src={urlQr} alt={`QR de ${puntoVenta.nombre}`} width={160} height={160} />
                  <a href={urlQr} target="_blank" rel="noopener noreferrer">
                    Abrir para imprimir
                  </a>
                </div>
              ) : (
                <p className="text-muted mb-0">Mercado Pago todavía no devolvió el QR de esta caja.</p>
              )}
            </section>
          )}

          {tieneCaja(estado) && (
            <section className="mt-4" aria-label="Terminal Point">
              <h2 className="h6">Terminal Point</h2>
              {cargandoTerminales && terminales === null ? (
                <Cargando texto="Cargando terminales…" />
              ) : errorDeTerminales && terminales === null ? (
                <div className="alert alert-danger" role="alert">
                  {errorDeTerminales}{' '}
                  <button
                    type="button"
                    className="btn btn-sm btn-outline-danger"
                    disabled={ocupado || cargandoTerminales}
                    onClick={() => void cargarTerminales()}
                  >
                    Reintentar
                  </button>
                </div>
              ) : terminales ? (
                <>
                  {errorDeTerminales && (
                    <div className="alert alert-danger py-2 px-3 small" role="alert">
                      {errorDeTerminales}
                    </div>
                  )}
                  {asignacionVieja && (
                    <div className="alert alert-warning" role="alert">
                      La terminal <strong>{terminales.idTerminalAsignada}</strong> figura asignada en Ways, pero ya no está vinculada a
                      esta caja en Mercado Pago (se desvinculó desde la app). Quitá la asignación o asigná otra terminal.
                    </div>
                  )}
                  {terminales.idTerminalAsignada !== null && (
                    <p className="mb-2">
                      Terminal asignada en Ways: <strong>{terminales.idTerminalAsignada}</strong>{' '}
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger ms-2"
                        disabled={ocupado}
                        onClick={() => void quitar()}
                      >
                        Quitar asignación
                      </button>
                    </p>
                  )}
                  {terminales.terminales.length === 0 ? (
                    <div>
                      <p className="text-muted mb-2">
                        {terminales.instrucciones ?? 'No hay terminales vinculadas a esta caja. Vinculala desde la app de Mercado Pago.'}
                      </p>
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-secondary"
                        disabled={ocupado || cargandoTerminales}
                        onClick={() => void cargarTerminales()}
                      >
                        Actualizar lista
                      </button>
                    </div>
                  ) : (
                    <>
                      <p className="text-muted small">
                        Al asignar una terminal, Ways la pasa a modo PDV si no lo estaba; Mercado Pago pide reiniciarla para que lo tome.
                      </p>
                      <table className="table table-sm align-middle">
                        <thead>
                          <tr>
                            <th scope="col">Terminal</th>
                            <th scope="col">Modo</th>
                            <th scope="col" className="text-end">
                              Acción
                            </th>
                          </tr>
                        </thead>
                        <tbody>
                          {terminales.terminales.map((t) => (
                            <tr key={t.id}>
                              <td>
                                {t.id}
                                {t.asignada && <span className="badge bg-success ms-2">Asignada</span>}
                              </td>
                              <td>{etiquetaDeModoDeTerminal(t.modoOperativo)}</td>
                              <td className="text-end">
                                {!t.asignada && (
                                  <button
                                    type="button"
                                    className="btn btn-sm btn-outline-primary"
                                    disabled={ocupado}
                                    onClick={() => void asignar(t.id)}
                                  >
                                    Asignar
                                  </button>
                                )}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </>
                  )}
                </>
              ) : null}
            </section>
          )}
        </div>
      ) : null}
    </Modal>
  )
}
