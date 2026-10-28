import { describe, expect, it } from 'vitest'
import { CATALOGO_DE_ACCIONES_AUDITADAS, CATEGORIAS_GASTO } from './tipos'

// El servidor ordena por estas mismas etiquetas (lo que se ve es lo que se ordena). Cada lista literal de abajo
// es idéntica a la de su test espejo del backend: `EtiquetasDeAccionTests` y `EtiquetasDeCategoriaDeGastoTests`
// (tests/Ways.Application.Tests/Listados/EtiquetasDeOrdenTests.cs). Si una cambia, las dos tienen que cambiar.

describe('etiquetas que el servidor replica para ordenar', () => {
  it('CATALOGO_DE_ACCIONES_AUDITADAS es la lista que fija EtiquetasDeAccionTests', () => {
    expect(CATALOGO_DE_ACCIONES_AUDITADAS.map((a) => [a.valor, a.etiqueta])).toEqual([
      ['precio.cambio', 'Cambio de precio'],
      ['venta.anulacion', 'Anulación de venta'],
      ['compra.anulacion', 'Anulación de compra'],
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

  it('CATEGORIAS_GASTO es la lista que fija EtiquetasDeCategoriaDeGastoTests', () => {
    expect(CATEGORIAS_GASTO.map((c) => [c.valor, c.etiqueta])).toEqual([
      ['Proveedor', 'Proveedores'],
      ['Sueldos', 'Sueldos'],
      ['Viaticos', 'Viáticos'],
      ['Impuestos', 'Impuestos'],
      ['Servicios', 'Servicios'],
      ['Otros', 'Otros'],
    ])
  })
})
