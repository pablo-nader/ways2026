import { ROL } from '../../api/tipos'
import type { EstadoUsuario } from '../../api/tipos'

export type Formulario = {
  id: number | null
  usuario: string
  mail: string
  rolId: number
  estado: EstadoUsuario
  password: string
  /** Solo se manda en el alta: `ActualizarUsuario` no acepta tenant. `null` es a la vez el valor
   * del rol root y el que manda un admin de tenant, a quien el servidor le impone el suyo. */
  idTenant: number | null
}

export const FORMULARIO_VACIO: Formulario = {
  id: null,
  usuario: '',
  mail: '',
  rolId: ROL.Vendedor,
  estado: 'Activo',
  password: '',
  idTenant: null,
}
