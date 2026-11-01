import type { EstadoDeCaja, EstadoDeLocal, ListadoDeTerminales } from '../../api/mercadoPago'

type Presentacion = { etiqueta: string; clase: string }

const PRESENTACION_DE_LOCAL: Readonly<Record<EstadoDeLocal, Presentacion>> = {
  sin_conexion: { etiqueta: 'Sin conexión', clase: 'bg-secondary' },
  no_registrada: { etiqueta: 'No registrada', clase: 'bg-secondary' },
  registrada: { etiqueta: 'Registrada', clase: 'bg-success' },
  desactualizada: { etiqueta: 'Desactualizada', clase: 'bg-warning text-dark' },
}

const PRESENTACION_DE_CAJA: Readonly<Record<EstadoDeCaja, Presentacion>> = {
  sin_conexion: { etiqueta: 'Sin conexión', clase: 'bg-secondary' },
  local_no_registrado: { etiqueta: 'Sucursal sin registrar', clase: 'bg-secondary' },
  no_registrada: { etiqueta: 'No registrada', clase: 'bg-secondary' },
  registrada: { etiqueta: 'Registrada', clase: 'bg-success' },
  desactualizada: { etiqueta: 'Desactualizada', clase: 'bg-warning text-dark' },
}

const DESCONOCIDO: Presentacion = { etiqueta: 'Estado desconocido', clase: 'bg-secondary' }

/** Etiqueta y clase de la insignia de una sucursal; un estado que el servidor agregue después cae en "desconocido" y no rompe la pantalla. */
export function presentacionDeLocal(estado: string): Presentacion {
  return (PRESENTACION_DE_LOCAL as Readonly<Record<string, Presentacion>>)[estado] ?? DESCONOCIDO
}

export function presentacionDeCaja(estado: string): Presentacion {
  return (PRESENTACION_DE_CAJA as Readonly<Record<string, Presentacion>>)[estado] ?? DESCONOCIDO
}

const ETIQUETA_DE_CAMPO: Readonly<Record<string, string>> = {
  nombre: 'Nombre',
  calle: 'Calle',
  altura: 'Altura (número)',
  ciudad: 'Ciudad',
  provincia: 'Provincia',
  latitud: 'Latitud',
  longitud: 'Longitud',
}

/** Nombre de columna de Ways → rótulo del formulario de la sucursal; un campo desconocido se muestra tal cual. */
export function etiquetaDeCampoFaltante(campo: string): string {
  return ETIQUETA_DE_CAMPO[campo] ?? campo
}

export function etiquetasDeCamposFaltantes(campos: readonly string[]): string {
  return campos.map(etiquetaDeCampoFaltante).join(', ')
}

const ETIQUETA_DE_MODO: Readonly<Record<string, string>> = {
  PDV: 'Punto de venta (PDV)',
  STANDALONE: 'Independiente (STANDALONE)',
}

export function etiquetaDeModoDeTerminal(modo: string | null): string {
  if (!modo) return 'Sin dato'

  return ETIQUETA_DE_MODO[modo] ?? modo
}

/**
 * Ways guarda una terminal que la caja ya no tiene vinculada (se desvinculó en la app de Mercado Pago): hay que avisarlo para
 * que la operadora la quite. Sin terminal guardada no hay nada que avisar, aunque `asignadaVigente` sea falso.
 */
export function tieneAsignacionVieja(listado: Pick<ListadoDeTerminales, 'idTerminalAsignada' | 'asignadaVigente'>): boolean {
  return listado.idTerminalAsignada !== null && !listado.asignadaVigente
}
