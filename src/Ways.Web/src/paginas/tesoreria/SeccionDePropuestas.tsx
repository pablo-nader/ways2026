import { useCallback, useEffect, useState } from 'react'
import {
  avisosDeDetalle,
  claseDeEstadoDePropuesta,
  clienteDeConciliacionMercadoPago,
  cuentasDeDestino,
  etiquetaDeEstadoDePropuesta,
  etiquetaDeTipoDePropuesta,
  mensajeDeErrorDeConciliacion,
  pideElegirOrigen,
  sePuedeAplicar,
  seleccionVigente,
  textoDeEfecto,
  textoDeFecha,
  textoDeImporte,
  type AccionDePropuesta,
  type PropuestaDeTesoreria,
} from '../../api/conciliacionMercadoPago'
import { clienteDeCuentasTesoreria } from '../../api/cuentasTesoreria'
import { Cargando } from '../../componentes/Cargando'
import { useLectura } from './useLectura'

type Props = {
  idConciliacion: number
  idEmpresa: number
  /** Cambia cuando una escritura ya aplicada pide volver a leer las propuestas. */
  version: number
  /** Solo el Admin aplica o descarta; el Supervisor las ve. */
  puedeResolver: boolean
  /** Hay una escritura en vuelo o un diálogo abierto en la pantalla: todo queda inerte. */
  bloqueado: boolean
  onResolver: ResolverPropuesta
}

/** Pide resolver una propuesta; en un retiro lleva el destino elegido y, si no tenía origen fijo, el origen elegido. */
export type ResolverPropuesta = (
  idConciliacion: number,
  propuesta: PropuestaDeTesoreria,
  accion: AccionDePropuesta,
  destino: DestinoElegido | null,
  origen: DestinoElegido | null,
) => void

/** La cuenta que recibe un retiro (o de la que sale, si se eligió el origen), tal como se eligió en la lista. */
export type DestinoElegido = { id: number; nombre: string }

const seleccionarId = (valor: string): number | null => (valor === '' ? null : Number(valor))

const mismasClaves = (a: Record<number, string>, b: Record<number, string>) =>
  Object.keys(a).length === Object.keys(b).length && Object.entries(a).every(([k, v]) => b[Number(k)] === v)

const mensajeDeLectura = (e: unknown) => mensajeDeErrorDeConciliacion(e, 'lectura')

/**
 * "Movimientos a tesorería": lo que la conciliación propone escribir en la tesorería (la diferencia de comisión y los retiros del reporte).
 * Nada se escribe hasta que el Admin aplica; aplicar un retiro pide la cuenta que lo recibe. Las lecturas pasan por `useLectura`; las
 * escrituras son del padre, que las confirma en un diálogo y deja inerte la pantalla mientras están en vuelo.
 */
export function SeccionDePropuestas({ idConciliacion, idEmpresa, version, puedeResolver, bloqueado, onResolver }: Props) {
  const [destinos, setDestinos] = useState<Record<number, string>>({})
  const [origenes, setOrigenes] = useState<Record<number, string>>({})

  const leerPropuestas = useCallback(() => {
    void version
    return clienteDeConciliacionMercadoPago.propuestas(idConciliacion)
  }, [idConciliacion, version])
  const propuestas = useLectura(leerPropuestas, mensajeDeLectura)

  const leerCuentas = useCallback(() => clienteDeCuentasTesoreria.listar(), [])
  const cuentas = useLectura(puedeResolver ? leerCuentas : null, mensajeDeLectura)

  const lista = propuestas.datos

  // Una cuenta elegida que la lectura siguiente ya no ofrece (baja, inactiva, ya no es de un medio de Mercado Pago) se descarta del estado.
  useEffect(() => {
    if (lista === null) return
    const origenVigente = (p: PropuestaDeTesoreria) => p.idCuentaOrigen ?? seleccionarId(seleccionVigente(p.origenesPosibles, origenes[p.id]))
    const origenesNuevos: Record<number, string> = {}
    const destinosNuevos: Record<number, string> = {}
    for (const p of lista) {
      const origen = seleccionVigente(p.origenesPosibles, origenes[p.id])
      if (origen !== '') origenesNuevos[p.id] = origen
      const destino = seleccionVigente(cuentasDeDestino(cuentas.datos ?? [], idEmpresa, origenVigente(p)), destinos[p.id])
      if (destino !== '') destinosNuevos[p.id] = destino
    }
    setOrigenes((previos) => (mismasClaves(previos, origenesNuevos) ? previos : origenesNuevos))
    setDestinos((previos) => (mismasClaves(previos, destinosNuevos) ? previos : destinosNuevos))
  }, [lista, cuentas.datos, idEmpresa, origenes, destinos])

  return (
    <section aria-label="Movimientos a tesorería" className="mb-4">
      <h6>Movimientos a tesorería</h6>
      <p className="small text-muted mb-2">
        Lo que esta conciliación propone escribir en la tesorería. Nada se escribe hasta que un Admin lo aplica.
      </p>

      {propuestas.error && (
        <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2" role="alert">
          <span>{propuestas.error}</span>
          <button type="button" className="btn btn-sm btn-outline-danger" onClick={propuestas.recargar}>
            Reintentar
          </button>
        </div>
      )}
      {puedeResolver && cuentas.error && (
        <div className="alert alert-warning d-flex justify-content-between align-items-center gap-2" role="alert">
          <span>No se pudieron cargar las cuentas de tesorería: {cuentas.error}</span>
          <button type="button" className="btn btn-sm btn-outline-secondary" onClick={cuentas.recargar}>
            Reintentar
          </button>
        </div>
      )}
      {propuestas.cargando && lista === null && <Cargando />}

      {lista &&
        (lista.length === 0 ? (
          <p className="text-muted py-2">Esta conciliación no propone ningún movimiento.</p>
        ) : (
          <div className="table-responsive">
            <table className="table table-sm align-middle">
              <thead>
                <tr>
                  <th>Tipo</th>
                  <th>Qué hace</th>
                  <th>Estado</th>
                  {puedeResolver && <th>Cuenta de origen</th>}
                  {puedeResolver && <th>Cuenta de destino</th>}
                  {puedeResolver && <th />}
                </tr>
              </thead>
              <tbody>
                {lista.map((p) => {
                  const pendiente = p.estado === 'pendiente'
                  const eligeOrigen = pideElegirOrigen(p) && p.origenesPosibles.length > 0
                  const origenElegido = seleccionVigente(p.origenesPosibles, origenes[p.id])
                  const idOrigen = p.idCuentaOrigen ?? seleccionarId(origenElegido)
                  const opciones = cuentasDeDestino(cuentas.datos ?? [], idEmpresa, idOrigen)
                  const elegida = seleccionVigente(opciones, destinos[p.id])
                  const aplicable = sePuedeAplicar(p)
                  const etiqueta = p.tipo === 'retiro' ? `retiro de la línea ${p.numeroLinea ?? ''}`.trim() : 'diferencia de comisión'
                  return (
                    <tr key={p.id}>
                      <td className="text-nowrap">{etiquetaDeTipoDePropuesta(p.tipo)}</td>
                      <td>
                        <div>{textoDeEfecto(p)}</div>
                        {p.tipo === 'retiro' && (
                          <div className="small text-muted">
                            Línea {p.numeroLinea} · {p.sourceId} · {textoDeFecha(p.fechaTransaccion)}
                          </div>
                        )}
                        {p.detalle && (
                          <div className="small text-muted">
                            {p.detalle.cantidadDeCobros} cobro(s): comisión real {textoDeImporte(p.detalle.comisionRealTotal)} contra{' '}
                            {textoDeImporte(p.detalle.comisionEstimadaTotal)} que descontó el cierre.
                            {avisosDeDetalle(p.detalle).map((aviso) => (
                              <div key={aviso}>{aviso}</div>
                            ))}
                          </div>
                        )}
                        {pideElegirOrigen(p) && p.origenesPosibles.length === 0 && (
                          <div className="small text-danger">
                            Ningún medio de pago de Mercado Pago de la empresa acredita en una cuenta de tesorería activa: no hay cuenta de la que retirar.
                            Se puede descartar.
                          </div>
                        )}
                        {!pendiente && p.resueltaEl && (
                          <div className="small text-muted">
                            {etiquetaDeEstadoDePropuesta(p.estado)} el {textoDeFecha(p.resueltaEl)}
                            {p.resueltaPor ? ` por ${p.resueltaPor}` : ''}
                          </div>
                        )}
                      </td>
                      <td>
                        <span className={`badge ${claseDeEstadoDePropuesta(p.estado)}`}>{etiquetaDeEstadoDePropuesta(p.estado)}</span>
                      </td>
                      {puedeResolver && (
                        <td>
                          {eligeOrigen && (
                            <select
                              className="form-select form-select-sm"
                              aria-label={`Cuenta de origen del ${etiqueta}`}
                              value={origenElegido}
                              disabled={bloqueado}
                              onChange={(e) => setOrigenes((previos) => ({ ...previos, [p.id]: e.target.value }))}
                            >
                              <option value="">Elegir…</option>
                              {p.origenesPosibles.map((c) => (
                                <option key={c.id} value={c.id}>
                                  {c.nombre}
                                </option>
                              ))}
                            </select>
                          )}
                          {!eligeOrigen && p.tipo === 'retiro' && pendiente && p.nombreCuentaOrigen}
                        </td>
                      )}
                      {puedeResolver && (
                        <td>
                          {p.tipo === 'retiro' && pendiente && (
                            <select
                              className="form-select form-select-sm"
                              aria-label={`Cuenta de destino del ${etiqueta}`}
                              value={elegida}
                              disabled={bloqueado || !aplicable}
                              onChange={(e) => setDestinos((previos) => ({ ...previos, [p.id]: e.target.value }))}
                            >
                              <option value="">Elegir…</option>
                              {opciones.map((c) => (
                                <option key={c.id} value={c.id}>
                                  {c.nombre}
                                </option>
                              ))}
                            </select>
                          )}
                        </td>
                      )}
                      {puedeResolver && (
                        <td className="text-end text-nowrap">
                          {pendiente && (
                            <>
                              <button
                                type="button"
                                className="btn btn-sm btn-primary me-1"
                                disabled={bloqueado || !aplicable || (p.tipo === 'retiro' && (elegida === '' || (eligeOrigen && origenElegido === '')))}
                                aria-label={`Aplicar ${etiqueta}`}
                                onClick={() =>
                                  onResolver(
                                    idConciliacion,
                                    p,
                                    'aplicar',
                                    p.tipo === 'retiro' ? (opciones.find((c) => c.id === Number(elegida)) ?? null) : null,
                                    eligeOrigen ? (p.origenesPosibles.find((c) => c.id === Number(origenElegido)) ?? null) : null,
                                  )
                                }
                              >
                                Aplicar
                              </button>
                              <button
                                type="button"
                                className="btn btn-sm btn-outline-secondary"
                                disabled={bloqueado}
                                aria-label={`Descartar ${etiqueta}`}
                                onClick={() => onResolver(idConciliacion, p, 'descartar', null, null)}
                              >
                                Descartar
                              </button>
                            </>
                          )}
                        </td>
                      )}
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        ))}
    </section>
  )
}
