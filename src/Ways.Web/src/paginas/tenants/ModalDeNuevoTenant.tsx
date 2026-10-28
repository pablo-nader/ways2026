import { useRef, useState } from 'react'
import { api, ErrorApi } from '../../api/cliente'
import type { ResultadoAprovisionamiento, SolicitudDeAprovisionamiento } from '../../api/tipos'
import { MODOS_PUNTO_VENTA } from '../../api/tipos'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'

const ID_FORMULARIO_NUEVO_TENANT = 'formulario-nuevo-tenant'

const FORMULARIO_VACIO: SolicitudDeAprovisionamiento = {
  nombreTenant: '',
  razonSocialEmpresa: '',
  nombrePuntoVenta: '',
  mailAdmin: '',
  modo: 'Web',
}

type Props = {
  focoDeReserva: React.RefObject<HTMLElement | null>
  /** Se llama cuando el servidor confirmó el alta: el listado que está detrás tiene que volver a leerse. */
  onCreado: () => void
  onCancelar: () => void
}

/**
 * Aprovisiona un tenant nuevo de punta a punta (ADR-16): tenant + empresa + punto de venta + la plantilla (área
 * "General", medios de pago Efectivo/Transferencia) + el admin del tenant, todo en una transacción atómica del lado del
 * servidor. Solo lo ve `root` (`Politicas.SoloPlataforma`) — root administra tenants, no opera ninguno.
 *
 * Tras un alta que salió bien el modal NO se cierra solo: la contraseña temporal del admin se muestra una sola vez y
 * tiene que seguir a la vista hasta que el operador la anota. El formulario queda vacío para el próximo alta; cerrarlo
 * (Cerrar, Escape o la ×) descarta el resultado. Mientras guarda queda inerte entero (campos, pie y cierre), y como el
 * modal no se puede reemplazar ni reabrir durante la escritura, la respuesta no necesita gate propio.
 */
export function ModalDeNuevoTenant({ focoDeReserva, onCreado, onCancelar }: Props) {
  const [formulario, setFormulario] = useState<SolicitudDeAprovisionamiento>({ ...FORMULARIO_VACIO })
  const [enviando, setEnviando] = useState(false)
  const [error, setError] = useState('')
  const [resultado, setResultado] = useState<ResultadoAprovisionamiento | null>(null)
  const [copiado, setCopiado] = useState(false)
  /** Espejo síncrono de "hay un alta en vuelo": un doble envío en el mismo tick no aprovisiona dos tenants. */
  const enviandoRef = useRef(false)

  async function enviar() {
    if (enviandoRef.current) return

    enviandoRef.current = true
    setEnviando(true)
    setError('')
    setResultado(null)
    setCopiado(false)

    try {
      const creado = await api.post<ResultadoAprovisionamiento>('/plataforma/tenants', formulario)
      setResultado(creado)
      setFormulario({ ...FORMULARIO_VACIO })
      onCreado()
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo aprovisionar el tenant.')
    } finally {
      enviandoRef.current = false
      setEnviando(false)
    }
  }

  async function copiarPassword() {
    if (!resultado) return
    try {
      await navigator.clipboard.writeText(resultado.passwordTemporal)
      setCopiado(true)
    } catch {
      // Sin permiso de portapapeles: la contraseña sigue visible en pantalla para copiar a mano.
    }
  }

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={enviando}>
        {resultado ? 'Cerrar' : 'Cancelar'}
      </button>
      <button type="submit" form={ID_FORMULARIO_NUEVO_TENANT} className="btn btn-success" disabled={enviando}>
        {enviando ? 'Aprovisionando…' : 'Aprovisionar tenant'}
      </button>
    </>
  )

  return (
    <Modal
      titulo="Nuevo tenant"
      tamano="lg"
      desplazable
      ocupado={enviando}
      etiquetaCerrar="Cerrar ventana"
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <p className="text-muted">
        Crea el tenant, su empresa, un punto de venta y el usuario admin en un solo paso. La contraseña temporal del admin se muestra{' '}
        <strong>una sola vez</strong>: no queda guardada en texto plano en ningún lado.
      </p>

      <ErrorDeFormulario mensaje={error} />

      {resultado && (
        <div className="alert alert-success">
          <p className="mb-2">
            Tenant #{resultado.idTenant} creado: empresa #{resultado.idEmpresa}, punto de venta #{resultado.idPuntoVenta}, usuario admin #
            {resultado.idUsuarioAdmin}.
          </p>
          <p className="mb-2 fw-bold">Anotá esta contraseña temporal ahora — no se vuelve a mostrar:</p>
          <div className="d-flex align-items-center gap-2">
            <code className="fs-5 bg-body px-2 py-1 border">{resultado.passwordTemporal}</code>
            <button type="button" className="btn btn-sm btn-outline-success" onClick={copiarPassword}>
              {copiado ? 'Copiada' : 'Copiar'}
            </button>
          </div>
        </div>
      )}

      <form
        id={ID_FORMULARIO_NUEVO_TENANT}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          void enviar()
        }}
      >
        <fieldset disabled={enviando} className="row g-3 border-0 p-0 m-0">
          <div className="col-md-6">
            <label className="form-label" htmlFor="nt-tenant">
              Nombre del tenant
            </label>
            <input
              id="nt-tenant"
              className="form-control"
              maxLength={150}
              value={formulario.nombreTenant}
              onChange={(e) => setFormulario({ ...formulario, nombreTenant: e.target.value })}
              required
            />
          </div>

          <div className="col-md-6">
            <label className="form-label" htmlFor="nt-empresa">
              Razón social de la empresa
            </label>
            <input
              id="nt-empresa"
              className="form-control"
              maxLength={150}
              value={formulario.razonSocialEmpresa}
              onChange={(e) => setFormulario({ ...formulario, razonSocialEmpresa: e.target.value })}
              required
            />
          </div>

          <div className="col-md-6">
            <label className="form-label" htmlFor="nt-puntoventa">
              Nombre del punto de venta
            </label>
            <input
              id="nt-puntoventa"
              className="form-control"
              maxLength={150}
              value={formulario.nombrePuntoVenta}
              onChange={(e) => setFormulario({ ...formulario, nombrePuntoVenta: e.target.value })}
              required
            />
          </div>

          <div className="col-md-6">
            <label className="form-label" htmlFor="nt-mail">
              Mail del admin del tenant
            </label>
            <input
              id="nt-mail"
              type="email"
              className="form-control"
              maxLength={255}
              value={formulario.mailAdmin}
              onChange={(e) => setFormulario({ ...formulario, mailAdmin: e.target.value })}
              required
            />
          </div>

          <div className="col-md-6">
            <label className="form-label" htmlFor="nt-modo">
              Modo del punto de venta
            </label>
            <select
              id="nt-modo"
              className="form-select"
              value={formulario.modo}
              onChange={(e) =>
                setFormulario({ ...formulario, modo: e.target.value as SolicitudDeAprovisionamiento['modo'] })
              }
            >
              {MODOS_PUNTO_VENTA.map((modo) => (
                <option key={modo} value={modo}>
                  {modo}
                </option>
              ))}
            </select>
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
