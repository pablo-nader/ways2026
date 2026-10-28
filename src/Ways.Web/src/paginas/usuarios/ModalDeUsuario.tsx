import type { OpcionDeFiltro } from '../../api/organizacion'
import { ESTADOS_USUARIO, ROL } from '../../api/tipos'
import type { EstadoUsuario, RolListado } from '../../api/tipos'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import type { Formulario } from './usuarioFormulario'

const ID_FORMULARIO_USUARIO = 'formulario-usuario'

type Props = {
  valor: Formulario
  roles: RolListado[]
  tenants: OpcionDeFiltro[]
  ofreceTenant: boolean
  tenantsCargando: boolean
  tenantIndisponible: boolean
  /** Solo gobierna la ETIQUETA del botón: "Guardando…" es cierto de una escritura en vuelo, no de
   * una puerta de confirmación abierta. */
  guardando: boolean
  /** Escritura en vuelo O puerta de confirmación abierta: gobierna todo `disabled` y el cierre. */
  bloqueado: boolean
  /** Rechazo del guardado o de la precondición del alta: vive dentro del modal. */
  error: string
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
}

/**
 * Alta y edición de un usuario en un modal.
 *
 * `tenants` es el conjunto de tenants ASIGNABLES, y solo se ofrece a un actor de plataforma
 * (`ofreceTenant`): un admin de tenant no enumera tenants — crea siempre dentro del suyo y el
 * servidor se lo impone (spec S5).
 *
 * El selector espeja `PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance`, que sigue siendo la
 * autoridad: root SIEMPRE es de plataforma (tenant nulo) y cualquier otro rol SIEMPRE necesita
 * uno. La rama de rol root es DEFENSA EN PROFUNDIDAD, no un camino vivo: `GET /roles`
 * (`PoliticaDeRoles.RolesAsignablesPor`) no devuelve Root para NINGÚN actor, ni siquiera para un
 * root, así que la opción no llega al `<select>` de rol y la combinación rol-root-con-tenant no es
 * alcanzable desde esta pantalla. La rama existe por si el catálogo de roles cambiara. Lo que sí
 * es camino vivo es el `required` del tenant para cualquier otro rol: sin él el servidor contesta
 * 400 `tenant_requerido`, y ese error se rinde por el camino de siempre.
 */
export function ModalDeUsuario({
  valor,
  roles,
  tenants,
  ofreceTenant,
  tenantsCargando,
  tenantIndisponible,
  guardando,
  bloqueado,
  error,
  focoDeReserva,
  onCambio,
  onGuardar,
  onCancelar,
}: Props) {
  const esNuevo = valor.id === null
  const esRolDePlataforma = valor.rolId === ROL.Root
  const sinTenantAsignable = esNuevo && ofreceTenant && tenantIndisponible

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
        Cancelar
      </button>
      <button
        type="submit"
        form={ID_FORMULARIO_USUARIO}
        className="btn btn-success"
        disabled={bloqueado || sinTenantAsignable}
      >
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={esNuevo ? 'Nuevo usuario' : `Editando usuario ${valor.id}`}
      tamano="lg"
      desplazable
      ocupado={bloqueado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} />
      <form
        id={ID_FORMULARIO_USUARIO}
        className="row g-3"
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <div className="col-md-6">
          <label className="form-label" htmlFor="f-usuario">
            Usuario
          </label>
          <input
            id="f-usuario"
            className="form-control"
            maxLength={40}
            value={valor.usuario}
            onChange={(e) => onCambio({ ...valor, usuario: e.target.value })}
            disabled={bloqueado}
            required
          />
        </div>

        <div className="col-md-6">
          <label className="form-label" htmlFor="f-mail">
            Mail
          </label>
          <input
            id="f-mail"
            type="email"
            className="form-control"
            maxLength={255}
            value={valor.mail}
            onChange={(e) => onCambio({ ...valor, mail: e.target.value })}
            disabled={bloqueado}
            required
          />
        </div>

        <div className="col-md-4">
          <label className="form-label" htmlFor="f-rol">
            Rol
          </label>
          <select
            id="f-rol"
            className="form-select"
            value={valor.rolId}
            onChange={(e) => {
              // El tenant se limpia en el ESTADO, no solo al pintarlo: el alta manda `idTenant` tal
              // cual. Defensa en profundidad — ver el doc-comment: `GET /roles` no ofrece Root.
              const rolId = Number(e.target.value)
              onCambio({ ...valor, rolId, idTenant: rolId === ROL.Root ? null : valor.idTenant })
            }}
            disabled={bloqueado}
          >
            {roles.map((r) => (
              <option key={r.id} value={r.id}>
                {r.nombre}
              </option>
            ))}
          </select>
        </div>

        {esNuevo && ofreceTenant && (
          <div className="col-md-4">
            <label className="form-label" htmlFor="f-tenant">
              Tenant de la cuenta
            </label>
            <select
              id="f-tenant"
              className="form-select"
              value={valor.idTenant === null ? '' : String(valor.idTenant)}
              onChange={(e) =>
                onCambio({ ...valor, idTenant: e.target.value === '' ? null : Number(e.target.value) })
              }
              disabled={bloqueado || tenantsCargando || esRolDePlataforma || sinTenantAsignable}
              required
            >
              <option value="">
                {esRolDePlataforma
                  ? 'Sin tenant (plataforma)'
                  : tenantsCargando
                    ? 'Cargando…'
                    : sinTenantAsignable
                      ? 'No se pudo cargar la lista'
                      : 'Elegí un tenant'}
              </option>
              {tenants.map((t) => (
                <option key={t.valor} value={t.valor}>
                  {t.etiqueta}
                </option>
              ))}
            </select>
          </div>
        )}

        <div className="col-md-4">
          <label className="form-label" htmlFor="f-estado">
            Estado
          </label>
          <select
            id="f-estado"
            className="form-select"
            value={valor.estado}
            onChange={(e) => onCambio({ ...valor, estado: e.target.value as EstadoUsuario })}
            disabled={bloqueado}
          >
            {ESTADOS_USUARIO.map((e) => (
              <option key={e} value={e}>
                {e}
              </option>
            ))}
          </select>
        </div>

        <div className="col-md-6">
          <label className="form-label" htmlFor="f-password">
            Contraseña
          </label>
          <input
            id="f-password"
            type="password"
            className="form-control"
            placeholder={esNuevo ? 'Mínimo 8 caracteres' : 'Dejar vacío para no cambiar'}
            value={valor.password}
            onChange={(e) => onCambio({ ...valor, password: e.target.value })}
            disabled={bloqueado}
            required={esNuevo}
          />
        </div>
      </form>
    </Modal>
  )
}
