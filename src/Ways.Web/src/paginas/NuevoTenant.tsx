import { useState } from 'react'
import type { FormEvent } from 'react'
import { api, ErrorApi } from '../api/cliente'
import type { ResultadoAprovisionamiento, SolicitudDeAprovisionamiento } from '../api/tipos'
import { Box } from '../componentes/Box'

const FORMULARIO_VACIO: SolicitudDeAprovisionamiento = {
  nombreTenant: '',
  razonSocialEmpresa: '',
  nombrePuntoVenta: '',
  mailAdmin: '',
}

/**
 * Aprovisiona un tenant nuevo de punta a punta (ADR-16): tenant + empresa + punto de venta +
 * la plantilla (área "General", medios de pago Efectivo/Transferencia) + el admin del tenant,
 * todo en una transacción atómica del lado del servidor. Solo lo ve `root`
 * (`Politicas.SoloPlataforma`) — root administra tenants, no opera ninguno.
 *
 * Esta pantalla también cubre la parte de "alta" del ABM de tenants: no hay todavía un
 * endpoint para listar o suspender tenants existentes (ninguna tarea de los slices 1-3 lo
 * construyó), así que esa parte queda fuera de esta etapa — ver el reporte de la etapa 4.
 */
export function NuevoTenant() {
  const [formulario, setFormulario] = useState<SolicitudDeAprovisionamiento>({ ...FORMULARIO_VACIO })
  const [enviando, setEnviando] = useState(false)
  const [error, setError] = useState('')
  const [resultado, setResultado] = useState<ResultadoAprovisionamiento | null>(null)
  const [copiado, setCopiado] = useState(false)

  async function enviar(evento: FormEvent) {
    evento.preventDefault()
    setEnviando(true)
    setError('')
    setResultado(null)
    setCopiado(false)

    try {
      const creado = await api.post<ResultadoAprovisionamiento>('/plataforma/tenants', formulario)
      setResultado(creado)
      setFormulario({ ...FORMULARIO_VACIO })
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo aprovisionar el tenant.')
    } finally {
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

  return (
    <div className="container-fluid py-4">
      <Box titulo="Nuevo tenant" variante="inverse">
        <p className="text-muted">
          Crea el tenant, su empresa, un punto de venta y el usuario admin en un solo paso. La contraseña
          temporal del admin se muestra <strong>una sola vez</strong>: no queda guardada en texto plano en
          ningún lado.
        </p>

        {error && <div className="alert alert-danger rounded-0">{error}</div>}

        {resultado && (
          <div className="alert alert-success rounded-0">
            <p className="mb-2">
              Tenant #{resultado.idTenant} creado: empresa #{resultado.idEmpresa}, punto de venta #
              {resultado.idPuntoVenta}, usuario admin #{resultado.idUsuarioAdmin}.
            </p>
            <p className="mb-2 fw-bold">Anotá esta contraseña temporal ahora — no se vuelve a mostrar:</p>
            <div className="d-flex align-items-center gap-2">
              <code className="fs-5 bg-white px-2 py-1 border rounded-0">{resultado.passwordTemporal}</code>
              <button type="button" className="btn btn-sm btn-outline-success rounded-0" onClick={copiarPassword}>
                {copiado ? 'Copiada' : 'Copiar'}
              </button>
            </div>
          </div>
        )}

        <form className="row g-3" autoComplete="off" onSubmit={enviar}>
          <div className="col-md-3">
            <label className="form-label" htmlFor="nt-tenant">
              Nombre del tenant
            </label>
            <input
              id="nt-tenant"
              className="form-control rounded-0"
              maxLength={150}
              value={formulario.nombreTenant}
              onChange={(e) => setFormulario({ ...formulario, nombreTenant: e.target.value })}
              required
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="nt-empresa">
              Razón social de la empresa
            </label>
            <input
              id="nt-empresa"
              className="form-control rounded-0"
              maxLength={150}
              value={formulario.razonSocialEmpresa}
              onChange={(e) => setFormulario({ ...formulario, razonSocialEmpresa: e.target.value })}
              required
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="nt-puntoventa">
              Nombre del punto de venta
            </label>
            <input
              id="nt-puntoventa"
              className="form-control rounded-0"
              maxLength={150}
              value={formulario.nombrePuntoVenta}
              onChange={(e) => setFormulario({ ...formulario, nombrePuntoVenta: e.target.value })}
              required
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="nt-mail">
              Mail del admin del tenant
            </label>
            <input
              id="nt-mail"
              type="email"
              className="form-control rounded-0"
              maxLength={255}
              value={formulario.mailAdmin}
              onChange={(e) => setFormulario({ ...formulario, mailAdmin: e.target.value })}
              required
            />
          </div>

          <div className="col-12">
            <button type="submit" className="btn btn-success rounded-0" disabled={enviando}>
              {enviando ? 'Aprovisionando…' : 'Aprovisionar tenant'}
            </button>
          </div>
        </form>
      </Box>
    </div>
  )
}
