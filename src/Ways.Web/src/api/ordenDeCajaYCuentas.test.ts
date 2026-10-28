import { describe, expect, it } from 'vitest'
import { COLUMNAS_ORDENABLES_DE_CIERRES, construirQueryDeCierres } from './caja'
import { etiquetaDeClienteDeFila } from './clientes'
import {
  COLUMNAS_ORDENABLES_DE_CUENTA_DE_PROVEEDOR,
  construirQueryEstadoDeCuentaDeProveedor,
  etiquetaDeTipoDeMovimiento,
  filtrosDeEstadoDeCuentaDeProveedorVacios,
} from './cuentaCorrienteDeProveedor'
import {
  COLUMNAS_ORDENABLES_DE_CUENTA_DE_PERSONAL,
  construirQueryDeCuentaDePersonal,
  filtrosDeCuentaDePersonalIniciales,
} from './cuentaDePersonal'
import {
  COLUMNAS_ORDENABLES_DE_HISTORICO_DE_CAJAS,
  construirQueryDeHistoricoDeCajas,
  filtrosDeHistoricoDeCajasVacios,
  rutasDeExportacion,
} from './reportes'
import { etiquetaDeTipo } from '../paginas/cuentaDePersonalFormularios'

// El servidor ordena por estas mismas etiquetas (lo que se ve es lo que se ordena). Cada lista literal es idéntica a la de su
// test espejo del backend, `EtiquetasDeMovimientoDeCuentaTests` (tests/Ways.Application.Tests/Listados). Si una cambia, las dos
// tienen que cambiar.
describe('etiquetas de la columna Tipo que el servidor replica para ordenar', () => {
  it('proveedor es la lista que fija LasEtiquetasDeProveedorSonExactamenteLaListaFijada', () => {
    expect([
      ['Apertura', etiquetaDeTipoDeMovimiento({ tipo: 'Apertura', etiqueta: null })],
      ['Compra', etiquetaDeTipoDeMovimiento({ tipo: 'Compra', etiqueta: null })],
      ['Pago', etiquetaDeTipoDeMovimiento({ tipo: 'Pago', etiqueta: null })],
      ['Ajuste', etiquetaDeTipoDeMovimiento({ tipo: 'Ajuste', etiqueta: 'Manual' })],
      ['AjusteConCompra', etiquetaDeTipoDeMovimiento({ tipo: 'Ajuste', etiqueta: 'AnulacionContramovimiento' })],
    ]).toEqual([
      ['Apertura', 'Apertura'],
      ['Compra', 'Compra'],
      ['Pago', 'Pago'],
      ['Ajuste', 'Ajuste manual'],
      ['AjusteConCompra', 'Contramov. de anulación'],
    ])
  })

  it('personal es la lista que fija LasEtiquetasDePersonalSonExactamenteLaListaFijada', () => {
    expect((['Horas', 'Pago', 'Ajuste'] as const).map((t) => [t, etiquetaDeTipo(t)])).toEqual([
      ['Horas', 'Horas trabajadas'],
      ['Pago', 'Pago'],
      ['Ajuste', 'Ajuste'],
    ])
  })
})

// Las claves son las de `ClavesDeOrden` de cada servicio; los tests `LasClavesAceptadasSonExactamenteLasColumnasDeDatosDeLaPantalla`
// del backend fijan la misma lista.
describe('claves de orden de caja y cuentas', () => {
  it('cada listado acepta exactamente las claves que fija su espejo del backend', () => {
    expect([...COLUMNAS_ORDENABLES_DE_HISTORICO_DE_CAJAS]).toEqual([
      'turno',
      'puntoVenta',
      'apertura',
      'cierre',
      'esperado',
      'declarado',
      'diferencia',
    ])
    expect([...COLUMNAS_ORDENABLES_DE_CIERRES]).toEqual(['turno', 'apertura', 'cierre'])
    expect([...COLUMNAS_ORDENABLES_DE_CUENTA_DE_PROVEEDOR]).toEqual([
      'fecha',
      'tipo',
      'referencia',
      'detalle',
      'importe',
      'saldoResultante',
    ])
    expect([...COLUMNAS_ORDENABLES_DE_CUENTA_DE_PERSONAL]).toEqual([
      'fecha',
      'tipo',
      'horas',
      'valorHora',
      'importe',
      'saldoResultante',
      'detalle',
    ])
  })

  it('sin orden el query no lleva orden ni direccion', () => {
    for (const query of [
      construirQueryDeHistoricoDeCajas(filtrosDeHistoricoDeCajasVacios()),
      construirQueryDeCierres(7, 1, 10),
      construirQueryEstadoDeCuentaDeProveedor(filtrosDeEstadoDeCuentaDeProveedorVacios()),
      construirQueryDeCuentaDePersonal(filtrosDeCuentaDePersonalIniciales()),
    ]) {
      expect(query).not.toContain('orden')
      expect(query).not.toContain('direccion')
    }
  })

  it('con orden el query lo lleva junto con los filtros y la página', () => {
    const orden = { columna: 'importe', direccion: 'descendente' } as const
    const ordenDeCaja = { columna: 'apertura', direccion: 'descendente' } as const

    const historico = new URLSearchParams(
      construirQueryDeHistoricoDeCajas({ ...filtrosDeHistoricoDeCajasVacios(), idPuntoVenta: 4, pagina: 3 }, ordenDeCaja).slice(1),
    )
    expect(historico.get('orden')).toBe('apertura')
    expect(historico.get('direccion')).toBe('descendente')
    expect(historico.get('idPuntoVenta')).toBe('4')
    expect(historico.get('pagina')).toBe('3')

    const cierres = new URLSearchParams(construirQueryDeCierres(7, 3, 10, ordenDeCaja).slice(1))
    expect([cierres.get('orden'), cierres.get('direccion'), cierres.get('idPuntoVenta'), cierres.get('estado'), cierres.get('pagina')]).toEqual([
      'apertura',
      'descendente',
      '7',
      'Cerrado',
      '3',
    ])

    const proveedor = new URLSearchParams(
      construirQueryEstadoDeCuentaDeProveedor({ ...filtrosDeEstadoDeCuentaDeProveedorVacios(), historico: true, pagina: 3 }, orden).slice(1),
    )
    expect([proveedor.get('orden'), proveedor.get('direccion'), proveedor.get('historico'), proveedor.get('pagina')]).toEqual([
      'importe',
      'descendente',
      'true',
      '3',
    ])

    const personal = new URLSearchParams(
      construirQueryDeCuentaDePersonal({ ...filtrosDeCuentaDePersonalIniciales(), historico: true, pagina: 3, orden }).slice(1),
    )
    expect([personal.get('orden'), personal.get('direccion'), personal.get('historico'), personal.get('pagina')]).toEqual([
      'importe',
      'descendente',
      'true',
      '3',
    ])
  })

  it('la exportación del histórico de cajas nunca lleva el orden', () => {
    const ruta = rutasDeExportacion.historicoDeCajas({ idPuntoVenta: 4, desde: '2026-05-01', hasta: '2026-05-31' })

    expect(ruta).not.toContain('orden')
    expect(ruta).not.toContain('direccion')
  })
})

describe('etiquetaDeClienteDeFila', () => {
  it('es el número y el nombre que resolvió el servidor, y por su id cuando el cliente fue dado de baja', () => {
    expect(etiquetaDeClienteDeFila({ idCliente: 9, numeroCliente: 12, nombreCliente: 'Ferretería Sur' })).toBe('#12 — Ferretería Sur')
    expect(etiquetaDeClienteDeFila({ idCliente: 9, numeroCliente: null, nombreCliente: null })).toBe('Cliente #9')
    expect(etiquetaDeClienteDeFila({ idCliente: 9 })).toBe('Cliente #9')
  })
})
