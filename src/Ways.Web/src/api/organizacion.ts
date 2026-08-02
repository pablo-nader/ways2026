/**
 * Cliente de organización (etapa 4B): tenants (plataforma-only), empresas y puntos de venta
 * (plataforma ve/edita cualquiera, un admin de tenant ve/edita solo los propios — lo
 * garantiza `OrganizacionEndpoints`/`ServicioDeOrganizacion` del lado del servidor, acá no
 * hay lógica de alcance). Alta y baja siguen siendo plataforma-only vía aprovisionamiento
 * (`NuevoTenant.tsx`) — este cliente solo lista/edita datos descriptivos y
 * suspende/reactiva un tenant.
 */
import { api } from './cliente'
import type {
  EmpresaEdicion,
  EmpresaListado,
  PuntoVentaEdicion,
  PuntoVentaListado,
  TenantEdicion,
  TenantListado,
} from './tipos'

export const clienteDeOrganizacion = {
  listarTenants: () => api.get<TenantListado[]>('/plataforma/tenants'),
  editarTenant: (id: number, datos: TenantEdicion) =>
    api.put<TenantListado>(`/plataforma/tenants/${id}`, datos),
  suspenderTenant: (id: number) => api.post<TenantListado>(`/plataforma/tenants/${id}/suspender`),
  reactivarTenant: (id: number) => api.post<TenantListado>(`/plataforma/tenants/${id}/reactivar`),

  listarEmpresas: () => api.get<EmpresaListado[]>('/empresas'),
  editarEmpresa: (id: number, datos: EmpresaEdicion) => api.put<EmpresaListado>(`/empresas/${id}`, datos),

  listarPuntosVenta: () => api.get<PuntoVentaListado[]>('/puntos-venta'),
  editarPuntoVenta: (id: number, datos: PuntoVentaEdicion) =>
    api.put<PuntoVentaListado>(`/puntos-venta/${id}`, datos),
}
