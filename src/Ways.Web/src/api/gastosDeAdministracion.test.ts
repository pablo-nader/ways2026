import { describe, expect, it } from 'vitest'
import {
  aSolicitudDeGastoDeAdministracion,
  categoriaAlElegirProveedorAdministracion,
  construirQueryDeGastosDeAdministracion,
  filtrosDeGastosDeAdministracionVacios,
  formularioDeGastoDeAdministracionCompleto,
  formularioDeGastoDeAdministracionVacio,
  type FormularioDeGastoDeAdministracion,
} from './gastos'

function formularioFixture(sobrescribir: Partial<FormularioDeGastoDeAdministracion> = {}): FormularioDeGastoDeAdministracion {
  return {
    fecha: '2026-09-01',
    idEmpresa: 3,
    idPuntoVenta: '',
    categoria: 'Otros',
    idProveedor: '',
    idArea: '',
    concepto: 'Alquiler de septiembre',
    detalle: '',
    idMedioPago: 7,
    numeroFactura: '',
    importe: 1000,
    ...sobrescribir,
  }
}

// Cláusula bajo prueba: las dos ramas de `categoriaAlElegirProveedorAdministracion` — mismo
// contrato que `categoriaAlElegirProveedor` (POS), verificado por separado porque vive en un
// módulo propio de esta pantalla.
describe('categoriaAlElegirProveedorAdministracion', () => {
  it('elegir un proveedor cambia la categoría a Proveedor, sea cual sea la categoría actual', () => {
    expect(categoriaAlElegirProveedorAdministracion(5, 'Otros')).toBe('Proveedor')
    expect(categoriaAlElegirProveedorAdministracion(5, 'Servicios')).toBe('Proveedor')
  })

  it('limpiar el proveedor mientras la categoría sigue en Proveedor la vuelve a Otros', () => {
    expect(categoriaAlElegirProveedorAdministracion(null, 'Proveedor')).toBe('Otros')
  })

  it('limpiar el proveedor con una categoría ya distinta de Proveedor no la toca', () => {
    expect(categoriaAlElegirProveedorAdministracion(null, 'Servicios')).toBe('Servicios')
  })
})

describe('formularioDeGastoDeAdministracionVacio', () => {
  it('arranca con la fecha dada, categoría Otros y el resto de los campos vacíos', () => {
    const formulario = formularioDeGastoDeAdministracionVacio('2026-09-28')
    expect(formulario.fecha).toBe('2026-09-28')
    expect(formulario.categoria).toBe('Otros')
    expect(formulario.idEmpresa).toBe('')
    expect(formulario.importe).toBeNull()
  })

  it('preselecciona la empresa cuando se la pasa (empresa única del tenant)', () => {
    expect(formularioDeGastoDeAdministracionVacio('2026-09-28', 9).idEmpresa).toBe(9)
  })
})

// Cláusula bajo prueba: dto-contract-honesty — cada campo aceptado tiene un destino: `''` en los
// selects opcionales se traduce a `null`, nunca se filtra silenciosamente.
describe('aSolicitudDeGastoDeAdministracion', () => {
  it('mapea los campos obligatorios tal cual', () => {
    const solicitud = aSolicitudDeGastoDeAdministracion(formularioFixture())
    expect(solicitud.fecha).toBe('2026-09-01')
    expect(solicitud.idEmpresa).toBe(3)
    expect(solicitud.categoria).toBe('Otros')
    expect(solicitud.concepto).toBe('Alquiler de septiembre')
    expect(solicitud.idMedioPago).toBe(7)
    expect(solicitud.importe).toBe(1000)
    expect(solicitud.idComprobanteCompra).toBeNull()
  })

  it('los selects opcionales en blanco viajan null, nunca 0 ni string vacío', () => {
    const solicitud = aSolicitudDeGastoDeAdministracion(formularioFixture({ idPuntoVenta: '', idProveedor: '', idArea: '' }))
    expect(solicitud.idPuntoVenta).toBeNull()
    expect(solicitud.idProveedor).toBeNull()
    expect(solicitud.idArea).toBeNull()
  })

  it('un valor elegido en los selects opcionales viaja tal cual', () => {
    const solicitud = aSolicitudDeGastoDeAdministracion(
      formularioFixture({ idPuntoVenta: 4, idProveedor: 11, idArea: 2, categoria: 'Proveedor' }),
    )
    expect(solicitud.idPuntoVenta).toBe(4)
    expect(solicitud.idProveedor).toBe(11)
    expect(solicitud.idArea).toBe(2)
  })

  it('detalle y numeroFactura en blanco viajan null; con texto, recortado', () => {
    expect(aSolicitudDeGastoDeAdministracion(formularioFixture({ detalle: '', numeroFactura: '' })).detalle).toBeNull()
    expect(aSolicitudDeGastoDeAdministracion(formularioFixture({ detalle: '  Flete  ' })).detalle).toBe('Flete')
    expect(aSolicitudDeGastoDeAdministracion(formularioFixture({ numeroFactura: '  A-1  ' })).numeroFactura).toBe('A-1')
  })
})

describe('formularioDeGastoDeAdministracionCompleto', () => {
  it('true cuando fecha, empresa, medio de pago, concepto e importe positivo están', () => {
    expect(formularioDeGastoDeAdministracionCompleto(formularioFixture())).toBe(true)
  })

  it.each([
    ['fecha', { fecha: '' }],
    ['idEmpresa', { idEmpresa: '' as const }],
    ['idMedioPago', { idMedioPago: '' as const }],
    ['concepto en blanco', { concepto: '   ' }],
    ['importe null', { importe: null }],
    ['importe cero', { importe: 0 }],
    ['importe negativo', { importe: -5 }],
  ])('false cuando falta %s', (_nombre, cambios) => {
    expect(formularioDeGastoDeAdministracionCompleto(formularioFixture(cambios))).toBe(false)
  })
})

describe('filtrosDeGastosDeAdministracionVacios / construirQueryDeGastosDeAdministracion', () => {
  it('sin ningún filtro solo manda pagina y tamanio', () => {
    const query = construirQueryDeGastosDeAdministracion(filtrosDeGastosDeAdministracionVacios())
    expect(query).toBe('?pagina=1&tamanio=25')
  })

  it('incluye cada filtro presente, sin desde/hasta cuando están vacíos', () => {
    const query = construirQueryDeGastosDeAdministracion({
      ...filtrosDeGastosDeAdministracionVacios(),
      idEmpresa: 3,
      idPuntoVenta: 7,
      categoria: 'Proveedor',
      origenFondos: 'Tesoreria',
      idProveedor: 11,
    })
    const parametros = new URLSearchParams(query.slice(1))
    expect(parametros.get('idEmpresa')).toBe('3')
    expect(parametros.get('idPuntoVenta')).toBe('7')
    expect(parametros.get('categoria')).toBe('Proveedor')
    expect(parametros.get('origenFondos')).toBe('Tesoreria')
    expect(parametros.get('idProveedor')).toBe('11')
    expect(parametros.has('desde')).toBe(false)
    expect(parametros.has('hasta')).toBe(false)
  })

  it('desde/hasta viajan con offset horario explícito (mismo criterio que compras.ts)', () => {
    const query = construirQueryDeGastosDeAdministracion({
      ...filtrosDeGastosDeAdministracionVacios(),
      desde: '2026-09-01',
      hasta: '2026-09-28',
    })
    const parametros = new URLSearchParams(query.slice(1))
    expect(parametros.get('desde')).toMatch(/^2026-09-01T00:00:00[+-]\d{2}:\d{2}$/)
    expect(parametros.get('hasta')).toMatch(/^2026-09-28T23:59:59\.999[+-]\d{2}:\d{2}$/)
  })
})
