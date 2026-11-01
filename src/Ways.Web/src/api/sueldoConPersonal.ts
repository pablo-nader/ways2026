/**
 * Pago de sueldos desde "Pagos" (administración): con la categoría Sueldos se puede elegir un empleado y el
 * guardado pasa a ser un pago de personal (baja su saldo del libro de horas) en vez de un pago común.
 */
import type { LineaDePagoEditable } from './lineasDePago'
import type { FormularioDePagoDeAdministracion } from './registroDePagos'
import type {
  CategoriaPago,
  LineaDeSolicitudDePagoDePersonal,
  PersonalListado,
  SolicitudDePagoDePersonal,
} from './tipos'

/** Solo Sueldos ofrece elegir un empleado; el resto de las categorías sigue ofreciendo el proveedor. */
export function ofreceElegirPersonal(categoria: CategoriaPago): boolean {
  return categoria === 'Sueldos'
}

/** El formulario es un pago de personal solo con Sueldos y un empleado elegido. */
export function esPagoAPersonal(f: Pick<FormularioDePagoDeAdministracion, 'categoria' | 'idPersonal'>): boolean {
  return ofreceElegirPersonal(f.categoria) && f.idPersonal !== ''
}

/** Lo que cambia al elegir una categoría: entrar a Sueldos descarta el proveedor y salir de Sueldos descarta el empleado. */
export function cambiosAlElegirCategoria(
  previo: Pick<FormularioDePagoDeAdministracion, 'idProveedor' | 'idPersonal'>,
  categoria: CategoriaPago,
): Pick<FormularioDePagoDeAdministracion, 'categoria' | 'idProveedor' | 'idPersonal'> {
  return ofreceElegirPersonal(categoria)
    ? { categoria, idProveedor: '', idPersonal: previo.idPersonal }
    : { categoria, idProveedor: previo.idProveedor, idPersonal: '' }
}

/** El personal por nombre, sin distinguir tildes ni mayúsculas. */
export function elegiblesComoPersonal(personal: readonly PersonalListado[]): PersonalListado[] {
  return [...personal].sort((a, b) => a.nombre.localeCompare(b.nombre, 'es'))
}

/**
 * El pago de personal equivalente al pago de administración: sale de la tesorería de la empresa elegida, igual que
 * el pago común. El concepto, el área y el número de factura no existen en un pago de personal (el servidor arma el
 * concepto con el nombre del empleado), por eso no viajan; el punto de venta, si se eligió, fija la zona horaria.
 */
export function aSolicitudDePagoDePersonalDesdeAdministracion(
  f: FormularioDePagoDeAdministracion,
): SolicitudDePagoDePersonal {
  if (f.idEmpresa === '') throw new Error('Falta la empresa que paga.')

  const lineas: LineaDeSolicitudDePagoDePersonal[] = f.lineas.map((l) => {
    const referencia = l.referencia.trim()
    return {
      idMedioPago: l.idMedioPago === '' ? 0 : l.idMedioPago,
      importe: l.importe ?? 0,
      ...(referencia !== '' && { referencia }),
    }
  })
  const detalle = f.detalle.trim()

  return {
    fecha: f.fecha,
    idEmpresa: f.idEmpresa,
    ...(f.idPuntoVenta !== '' && { idPuntoVenta: f.idPuntoVenta }),
    origenFondos: 'Tesoreria',
    lineas,
    ...(detalle !== '' && { detalle }),
  }
}

/**
 * El pago de personal del POS: el cajero paga solo del cajón del turno (`CajaTurno`) y no descuenta la cuenta corriente
 * del empleado; el servidor responde 403 con cualquier otro origen o con descuento, así que esos campos ni viajan. El
 * concepto, el área y el número de factura tampoco existen en un pago de personal. Las observaciones quedan como detalle.
 */
export function aSolicitudDePagoDePersonalDesdeElPos(f: {
  idPuntoVenta: number
  fecha: string
  lineas: readonly LineaDePagoEditable[]
  observaciones: string
}): SolicitudDePagoDePersonal {
  const lineas: LineaDeSolicitudDePagoDePersonal[] = f.lineas.map((l) => {
    const referencia = l.referencia.trim()
    return {
      idMedioPago: l.idMedioPago === '' ? 0 : l.idMedioPago,
      importe: l.importe ?? 0,
      ...(referencia !== '' && { referencia }),
    }
  })
  const detalle = f.observaciones.trim()

  return {
    fecha: f.fecha,
    idPuntoVenta: f.idPuntoVenta,
    origenFondos: 'CajaTurno',
    lineas,
    ...(detalle !== '' && { detalle }),
  }
}

export const MOTIVO_PAGO_A_PERSONAL_SIN_CONEXION = 'Sin conexión: pagar a un empleado necesita conexión con el servidor.'

/** El pago de personal solo existe con conexión: no hay cola offline de pagos de personal. */
export function pagoAPersonalDisponible(estado: { enLinea: boolean }): { habilitado: boolean; motivo: string } {
  if (!estado.enLinea) return { habilitado: false, motivo: MOTIVO_PAGO_A_PERSONAL_SIN_CONEXION }
  return { habilitado: true, motivo: '' }
}
