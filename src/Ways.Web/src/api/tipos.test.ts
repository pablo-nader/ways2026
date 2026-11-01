import { describe, expect, it } from 'vitest'
import { CATALOGO_DE_ACCIONES_AUDITADAS, CATEGORIAS_PAGO } from './tipos'

// El servidor ordena por estas mismas etiquetas (lo que se ve es lo que se ordena). Cada lista literal de abajo
// es idéntica a la de su test espejo del backend: `EtiquetasDeAccionTests` y `EtiquetasDeCategoriaDePagoTests`
// (tests/Ways.Application.Tests/Listados/EtiquetasDeOrdenTests.cs). Si una cambia, las dos tienen que cambiar.

describe('etiquetas que el servidor replica para ordenar', () => {
  it('CATALOGO_DE_ACCIONES_AUDITADAS es la lista que fija EtiquetasDeAccionTests', () => {
    expect(CATALOGO_DE_ACCIONES_AUDITADAS.map((a) => [a.valor, a.etiqueta])).toEqual([
      ['precio.cambio', 'Cambio de precio'],
      ['venta.anulacion', 'Anulación de venta'],
      ['compra.anulacion', 'Anulación de compra'],
      ['compra.edicion', 'Edición de compra'],
      ['compra.redondeo', 'Ajuste de redondeo de compra'],
      ['pago.aplicacion', 'Aplicación de pago a compras'],
      ['pago.desaplicacion', 'Desaplicación de pago de una compra'],
      ['pago.redondeo', 'Ajuste de redondeo de pago'],
      ['pago.reasignacion', 'Reasignación de pago de sueldo'],
      ['cobro.aplicacion', 'Aplicación de cobro a ventas'],
      ['cobro.desaplicacion', 'Desaplicación de cobro de una venta'],
      ['stock.ajuste', 'Ajuste de stock'],
      ['stock.decomiso', 'Decomiso de stock'],
      ['stock.conteo', 'Conteo de inventario'],
      ['cc.reliquidacion', 'Reliquidación de cuenta corriente'],
      ['usuario.alta', 'Alta de usuario'],
      ['usuario.actualizacion', 'Actualización de usuario'],
      ['usuario.baja', 'Baja de usuario'],
      ['usuario.desbloqueo', 'Desbloqueo de usuario'],
      ['usuario.password', 'Cambio de contraseña'],
      ['tenant.baja', 'Baja de tenant'],
      ['empresa.baja', 'Baja de empresa'],
      ['pv.baja', 'Baja de punto de venta'],
    ])
  })

  it('CATEGORIAS_PAGO es la lista que fija EtiquetasDeCategoriaDePagoTests', () => {
    expect(CATEGORIAS_PAGO.map((c) => [c.valor, c.etiqueta])).toEqual([
      ['Proveedor', 'Proveedores'],
      ['Sueldos', 'Sueldos'],
      ['Viaticos', 'Viáticos'],
      ['Impuestos', 'Impuestos'],
      ['Servicios', 'Servicios'],
      ['Otros', 'Otros'],
    ])
  })
})
