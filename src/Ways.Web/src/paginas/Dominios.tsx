import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { ErrorApi } from '../api/cliente'
import { copiaDeFalloDeBaja } from '../api/bajas'
import {
  AYUDA_DE_VERIFICACION,
  copiaDeFalloDeDominio,
  debeReleerTrasFallo,
  textoDeVerificacion,
} from '../api/dominios'
import { clienteDeOrganizacion, opcionesDeTenantAsignable } from '../api/organizacion'
import type { DominioListado, TenantListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { BotonIcono } from '../componentes/BotonIcono'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'

/**
 * Dominios propios de un tenant — solo plataforma (`Politicas.SoloPlataforma`). El dominio decide a
 * qué tenant llega una request anónima, así que cargarlo y verificarlo es una tarea de la
 * plataforma y no de un admin de tenant. Se elige el tenant, se lista lo suyo y se agrega, se marca
 * como verificado o se elimina.
 *
 * Contrato de invalidación (`react-async-state` reglas 2-4): hay dos lecturas con su generación.
 * `generacionTenants` la acuña el montaje. `generacionDominios` la acuña toda acción que cambia lo
 * que la lista muestra —elegir otro tenant y cada refresco posterior a una escritura— ANTES de tocar
 * el estado, y cada aplicación posterior a un `await` la vuelve a chequear.
 *
 * Las escrituras no se gatean por generación y no hace falta: mientras una está en vuelo, su
 * refresco incluido, la pantalla queda inerte (`bloqueado`: nada puede cambiar de tenant, reabrir la
 * puerta ni lanzar otra escritura) y la respuesta no navega. Lo que sí se gatea es el refresco, que
 * es una lectura.
 */
export function Dominios() {
  const [tenants, setTenants] = useState<TenantListado[] | null>(null)
  const [errorDeTenants, setErrorDeTenants] = useState('')
  const [idTenant, setIdTenant] = useState('')
  const [dominios, setDominios] = useState<DominioListado[] | null>(null)
  const [cargandoDominios, setCargandoDominios] = useState(false)

  const [host, setHost] = useState('')
  const [esPrincipal, setEsPrincipal] = useState(false)

  // `limpiarMensajes` borra el rechazo del alta, el de la acción (verificar y eliminar comparten ese
  // slot) y el aviso. El error de lectura solo lo escriben `elegirTenant` y `cargarDominios`.
  const [errorDeLectura, setErrorDeLectura] = useState('')
  const [errorDeAlta, setErrorDeAlta] = useState('')
  const [errorDeAccion, setErrorDeAccion] = useState('')
  const [aviso, setAviso] = useState('')

  const [ocupado, setOcupado] = useState(false)
  /** Solo para el texto del botón de alta: la inercia la gobierna `ocupado`. */
  const [agregando, setAgregando] = useState(false)
  /** Baja pendiente de confirmación. La puerta es modal: ver `bloqueado`. */
  const [confirmacion, setConfirmacion] = useState<DominioListado | null>(null)
  /** Control que abrió la puerta, capturado en su `onClick` (ver `ConfirmacionDeBaja.tsx`). */
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)

  const generacionTenants = useRef(0)
  const generacionDominios = useRef(0)
  /** Espejo SÍNCRONO de `ocupado` y única guarda de re-entrancia: dos clics del mismo tick leen el
   * mismo render. Se apaga en el `finally` de cada escritura, sin gate. */
  const ocupadoRef = useRef(false)

  useEffect(() => {
    const token = ++generacionTenants.current

    clienteDeOrganizacion
      .listarTenants()
      .then((filas) => {
        if (generacionTenants.current === token) setTenants(filas)
      })
      .catch((e) => {
        if (generacionTenants.current === token) {
          setErrorDeTenants(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los tenants.')
          setTenants([])
        }
      })
  }, [])

  const cargarDominios = useCallback(async (id: number, token: number, propagar = false) => {
    setCargandoDominios(true)
    try {
      const filas = await clienteDeOrganizacion.listarDominios(id)
      if (generacionDominios.current !== token) return
      setDominios(filas)
      setErrorDeLectura('')
    } catch (e) {
      if (generacionDominios.current !== token) return
      if (propagar) throw e
      setErrorDeLectura(copiaDeFalloDeDominio(e, 'No se pudieron cargar los dominios.'))
    } finally {
      if (generacionDominios.current === token) setCargandoDominios(false)
    }
  }, [])

  function limpiarMensajes() {
    setErrorDeAlta('')
    setErrorDeAccion('')
    setAviso('')
  }

  function elegirTenant(valor: string) {
    const token = ++generacionDominios.current
    limpiarMensajes()
    setErrorDeLectura('')
    setIdTenant(valor)
    setHost('')
    setEsPrincipal(false)
    setDominios(null)

    if (valor !== '') void cargarDominios(Number(valor), token)
  }

  /** El refresco va fuera del try/catch de la escritura: una escritura que ya commiteó nunca se
   * reporta como fallida (regla 6). */
  async function refrescarTrasEscribir(id: number, mensajeOk: string) {
    setAviso(mensajeOk)
    try {
      await cargarDominios(id, ++generacionDominios.current, true)
    } catch {
      setAviso(`${mensajeOk} ${AVISO_REFRESCO_FALLIDO}`)
    }
  }

  /** Tras un fallo que no dice qué quedó escrito se relee la lista para mostrar el estado real. No
   * toca los mensajes: el del fallo sigue visible. Un fallo de la relectura queda en `errorDeLectura`. */
  async function releerTrasFallo(id: number) {
    await cargarDominios(id, ++generacionDominios.current)
  }

  async function agregar(evento: FormEvent) {
    evento.preventDefault()
    if (ocupadoRef.current) return

    const id = Number(idTenant)
    ocupadoRef.current = true
    setOcupado(true)
    setAgregando(true)
    limpiarMensajes()
    try {
      let creado: DominioListado
      try {
        creado = await clienteDeOrganizacion.agregarDominio(id, { host: host.trim(), esPrincipal })
      } catch (e) {
        setErrorDeAlta(copiaDeFalloDeDominio(e, 'No se pudo agregar el dominio.'))
        if (debeReleerTrasFallo(e)) await releerTrasFallo(id)

        return
      }

      setHost('')
      setEsPrincipal(false)
      await refrescarTrasEscribir(
        id,
        `Se agregó el dominio "${creado.host}". Cuando su DNS apunte al servidor, marcalo como verificado.`,
      )
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
      setAgregando(false)
    }
  }

  async function verificar(dominio: DominioListado) {
    if (ocupadoRef.current) return

    ocupadoRef.current = true
    setOcupado(true)
    limpiarMensajes()
    try {
      try {
        await clienteDeOrganizacion.verificarDominio(dominio.idTenant, dominio.id)
      } catch (e) {
        setErrorDeAccion(copiaDeFalloDeDominio(e, `No se pudo marcar "${dominio.host}" como verificado.`))
        if (debeReleerTrasFallo(e)) await releerTrasFallo(dominio.idTenant)

        return
      }

      await refrescarTrasEscribir(dominio.idTenant, `Se marcó como verificado "${dominio.host}".`)
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
    }
  }

  async function eliminar(dominio: DominioListado) {
    if (ocupadoRef.current) return

    ocupadoRef.current = true
    setOcupado(true)
    limpiarMensajes()
    try {
      try {
        await clienteDeOrganizacion.eliminarDominio(dominio.idTenant, dominio.id)
      } catch (e) {
        // La puerta queda abierta con el motivo al lado: el operador ve por qué no se pudo sin
        // perder de vista qué estaba por eliminar.
        setErrorDeAccion(copiaDeFalloDeBaja(e, 'el dominio'))
        if (debeReleerTrasFallo(e)) await releerTrasFallo(dominio.idTenant)

        return
      }

      setConfirmacion(null)
      await refrescarTrasEscribir(dominio.idTenant, `Se eliminó el dominio "${dominio.host}".`)
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
    }
  }

  function pedirEliminar(dominio: DominioListado, disparador: HTMLElement | null) {
    limpiarMensajes()
    setDisparadorDeLaPuerta(disparador)
    setConfirmacion(dominio)
  }

  function cancelarEliminar() {
    limpiarMensajes()
    setDisparadorDeLaPuerta(null)
    setConfirmacion(null)
  }

  /** La puerta abierta bloquea la pantalla entera, no solo la escritura en vuelo. */
  const bloqueado = ocupado || confirmacion !== null
  /** Mientras la lista se lee no se escribe sobre ella; elegir otro tenant sí está permitido. */
  const inerte = bloqueado || cargandoDominios

  const opciones = opcionesDeTenantAsignable(tenants ?? [])

  return (
    <div className="container-fluid py-4">
      <Box titulo="Dominios" variante="inverse">
        {errorDeTenants && <div className="alert alert-danger">{errorDeTenants}</div>}
        {errorDeLectura && (
          <div className="alert alert-danger d-flex align-items-center justify-content-between gap-2">
            <span>{errorDeLectura}</span>
            {dominios === null && idTenant !== '' && (
              <button
                type="button"
                className="btn btn-sm btn-outline-danger"
                onClick={() => elegirTenant(idTenant)}
                disabled={bloqueado}
              >
                Reintentar
              </button>
            )}
          </div>
        )}
        {errorDeAccion && <div className="alert alert-danger">{errorDeAccion}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}

        {confirmacion && (
          <ConfirmacionDeBaja
            titulo={`el dominio "${confirmacion.host}"`}
            pregunta="Eliminar"
            nota="El dominio deja de resolver y su host queda libre para cargarse de nuevo."
            etiquetaConfirmar="Confirmar eliminación"
            etiquetaEnCurso="Eliminando…"
            ocupado={ocupado}
            disparador={disparadorDeLaPuerta}
            onConfirmar={() => void eliminar(confirmacion)}
            onCancelar={cancelarEliminar}
          />
        )}

        {tenants === null ? (
          <Cargando />
        ) : (
          <>
            <div className="col-md-4 mb-3">
              <label className="form-label" htmlFor="dom-tenant">
                Tenant
              </label>
              <select
                id="dom-tenant"
                className="form-select"
                value={idTenant}
                onChange={(e) => elegirTenant(e.target.value)}
                disabled={bloqueado}
              >
                <option value="">Elegir un tenant…</option>
                {opciones.map((o) => (
                  <option key={o.valor} value={o.valor}>
                    {o.etiqueta}
                  </option>
                ))}
              </select>
            </div>

            {idTenant !== '' && (
              <>
                <p className="text-muted small">{AYUDA_DE_VERIFICACION}</p>

                <form className="row g-3 border p-3 mb-4 bg-body" onSubmit={agregar}>
                  <div className="col-md-5">
                    <label className="form-label" htmlFor="dom-host">
                      Host
                    </label>
                    <input
                      id="dom-host"
                      className="form-control"
                      placeholder="tienda.ejemplo.com"
                      maxLength={253}
                      value={host}
                      onChange={(e) => setHost(e.target.value)}
                      disabled={inerte}
                      aria-invalid={errorDeAlta !== ''}
                      aria-describedby={errorDeAlta ? 'dom-host-error' : undefined}
                      required
                    />
                    {errorDeAlta && (
                      <div id="dom-host-error" className="text-danger small mt-1" role="alert">
                        {errorDeAlta}
                      </div>
                    )}
                  </div>
                  <div className="col-md-3 d-flex align-items-end">
                    <div className="form-check mb-2">
                      <input
                        id="dom-principal"
                        type="checkbox"
                        className="form-check-input"
                        checked={esPrincipal}
                        onChange={(e) => setEsPrincipal(e.target.checked)}
                        disabled={inerte}
                      />
                      <label className="form-check-label" htmlFor="dom-principal">
                        Principal
                      </label>
                    </div>
                  </div>
                  <div className="col-md-4 d-flex align-items-end">
                    <button type="submit" className="btn btn-success" disabled={inerte}>
                      {agregando ? 'Guardando…' : 'Agregar dominio'}
                    </button>
                  </div>
                </form>

                {dominios === null ? (
                  cargandoDominios && <Cargando />
                ) : (
                  <div className="table-responsive">
                    <table className="table table-striped table-hover table-bordered align-middle">
                      <thead>
                        <tr>
                          <th>Host</th>
                          <th>Principal</th>
                          <th>Verificación</th>
                          <th className="text-end">Acciones</th>
                        </tr>
                      </thead>
                      <tbody>
                        {dominios.map((d) => (
                          <tr key={d.id}>
                            <td>{d.host}</td>
                            <td>{d.esPrincipal && <span className="badge text-bg-primary">Principal</span>}</td>
                            <td>{textoDeVerificacion(d.verificadoEn)}</td>
                            <td className="text-end text-nowrap">
                              {d.verificadoEn === null && (
                                <button
                                  type="button"
                                  className="btn btn-sm btn-outline-success me-1"
                                  aria-label={`Marcar verificado: ${d.host}`}
                                  onClick={() => void verificar(d)}
                                  disabled={inerte}
                                >
                                  Marcar verificado
                                </button>
                              )}
                              <BotonIcono
                                icono="eliminar"
                                etiqueta={`Eliminar ${d.host}`}
                                onClick={(evento) => pedirEliminar(d, evento.currentTarget)}
                                disabled={inerte}
                              />
                            </td>
                          </tr>
                        ))}
                        {dominios.length === 0 && (
                          <tr>
                            <td colSpan={4} className="text-center text-muted py-4">
                              Este tenant todavía no tiene dominios.
                            </td>
                          </tr>
                        )}
                      </tbody>
                    </table>
                  </div>
                )}
              </>
            )}
          </>
        )}
      </Box>
    </div>
  )
}
