import { describe, expect, it } from 'vitest'
import { lineaDeCompraVacia, type EncabezadoDeCompraFormulario, type LineaDeCompraFormulario } from '../../api/compras'
import {
  hayCambiosSinGuardar,
  huellaDeFormulario,
  lineasIncompletas,
  mensajeDeLoQueImpideGuardar,
  MENSAJE_NUMERO_INCOMPLETO,
} from './validacionDeCompra'

function encabezado(sobrescribir: Partial<EncabezadoDeCompraFormulario> = {}): EncabezadoDeCompraFormulario {
  return {
    idProveedor: 1,
    idTipoComprobante: 5,
    idPuntoVenta: 2,
    numeroExterno: '0003-00012345',
    fechaComprobante: '2026-08-01',
    observaciones: '',
    idOrdenCompra: null,
    discriminaIva: null,
    ivaImpreso: {},
    preciosIncluyenIva: false,
    percepciones: [],
    percepcionesDescartadas: [],
    ...sobrescribir,
  }
}

function completa(clave = 1): LineaDeCompraFormulario {
  return { ...lineaDeCompraVacia(clave), descripcion: 'Flete', cantidad: '1', importe: 500, idAlicuotaIva: 3 }
}

function incompleta(clave = 2): LineaDeCompraFormulario {
  return { ...lineaDeCompraVacia(clave), descripcion: 'Flete', cantidad: '1' }
}

describe('lineasIncompletas', () => {
  it('devuelve las filas con algún dato al que le falta algo, y no las vacías ni las completas', () => {
    expect(lineasIncompletas([completa(1), incompleta(2), lineaDeCompraVacia(3)]).map((l) => l.clave)).toEqual([2])
  })
})

describe('mensajeDeLoQueImpideGuardar', () => {
  it('un borrador con líneas completas y número entero se puede guardar', () => {
    expect(mensajeDeLoQueImpideGuardar(encabezado(), [completa(), lineaDeCompraVacia(2)], false)).toBeNull()
  })

  it('un borrador sin número ni fecha ni líneas también se puede guardar: eso solo lo pide confirmar', () => {
    expect(mensajeDeLoQueImpideGuardar(encabezado({ numeroExterno: '', fechaComprobante: '' }), [lineaDeCompraVacia(1)], false)).toBeNull()
  })

  it('una fila incompleta bloquea el borrador y dice cuántas', () => {
    expect(mensajeDeLoQueImpideGuardar(encabezado(), [completa(), incompleta(2), incompleta(3)], false)).toBe(
      'Hay 2 líneas incompletas: complételas o quítelas antes de guardar.',
    )
  })

  it('un número de comprobante a medias bloquea el borrador y la confirmación', () => {
    const medio = encabezado({ numeroExterno: '0003-' })
    expect(mensajeDeLoQueImpideGuardar(medio, [completa()], false)).toBe(MENSAJE_NUMERO_INCOMPLETO)
    expect(mensajeDeLoQueImpideGuardar(medio, [completa()], true)).toBe(MENSAJE_NUMERO_INCOMPLETO)
  })

  it('confirmar pide número, fecha y al menos una línea completa, y dice cuáles faltan', () => {
    expect(mensajeDeLoQueImpideGuardar(encabezado({ numeroExterno: '', fechaComprobante: '' }), [], true)).toBe(
      'Para guardar y confirmar falta: el número del comprobante, la fecha del comprobante y al menos una línea completa.',
    )
    expect(mensajeDeLoQueImpideGuardar(encabezado({ fechaComprobante: '' }), [completa()], true)).toBe(
      'Para guardar y confirmar falta: la fecha del comprobante.',
    )
    expect(mensajeDeLoQueImpideGuardar(encabezado({ numeroExterno: '' }), [completa()], true)).toBe(
      'Para guardar y confirmar falta: el número del comprobante.',
    )
    expect(mensajeDeLoQueImpideGuardar(encabezado(), [lineaDeCompraVacia(1)], true)).toBe(
      'Para guardar y confirmar falta: al menos una línea completa.',
    )
  })

  it('confirmar con todo completo y una fila incompleta sobrante igual se bloquea por la fila', () => {
    expect(mensajeDeLoQueImpideGuardar(encabezado(), [completa(), incompleta()], true)).toBe(
      'Hay 1 línea incompleta: complétela o quítela antes de guardar.',
    )
  })

  it('confirmar suma las dos frases cuando falta algo del encabezado y además hay una fila incompleta', () => {
    expect(mensajeDeLoQueImpideGuardar(encabezado({ fechaComprobante: '' }), [completa(), incompleta()], true)).toBe(
      'Para guardar y confirmar falta: la fecha del comprobante. Hay 1 línea incompleta: complétela o quítela antes de guardar.',
    )
  })
})

describe('huellaDeFormulario / hayCambiosSinGuardar', () => {
  it('la huella ignora las claves de React, las filas vacías y los datos que la pantalla completa sola', () => {
    const base = huellaDeFormulario(encabezado(), [completa(1), lineaDeCompraVacia(2)])
    const otras = huellaDeFormulario(encabezado(), [{ ...completa(9), unidadVenta: 'Unidad', unidadesPorBulto: 12 }, lineaDeCompraVacia(10)])
    expect(otras).toBe(base)
  })

  it('la huella cambia con cualquier dato que el operador pueda editar', () => {
    const base = huellaDeFormulario(encabezado(), [completa()])
    expect(huellaDeFormulario(encabezado({ observaciones: 'x' }), [completa()])).not.toBe(base)
    for (const cambio of [
      { cantidad: '2' },
      { importe: 501 },
      { descuentoPorcentaje: 5 },
      { idAlicuotaIva: 4 },
      { descripcion: 'Otro' },
      { codigoProveedor: 'AB' },
      { unidadDeMedida: 'Bulto' as const },
      { codigoLote: 'L' },
      { fechaVencimiento: '2026-12-01' },
      { idArticulo: 7 },
    ]) {
      expect(huellaDeFormulario(encabezado(), [{ ...completa(), ...cambio }])).not.toBe(base)
    }
  })

  it('una compra existente tiene cambios solo si la huella difiere de la guardada', () => {
    const guardada = huellaDeFormulario(encabezado(), [completa()])
    const parametros = { esNuevo: false, encabezado: encabezado(), lineas: [completa(), lineaDeCompraVacia(2)], huellaGuardada: guardada }
    expect(hayCambiosSinGuardar(parametros)).toBe(false)
    expect(hayCambiosSinGuardar({ ...parametros, lineas: [{ ...completa(), cantidad: '9' }] })).toBe(true)
    expect(hayCambiosSinGuardar({ ...parametros, encabezado: encabezado({ observaciones: 'x' }) })).toBe(true)
  })

  it('una compra existente todavía sin huella guardada no cuenta como modificada', () => {
    expect(hayCambiosSinGuardar({ esNuevo: false, encabezado: encabezado(), lineas: [completa()], huellaGuardada: null })).toBe(false)
  })

  it('una compra nueva cuenta lo que el operador cargó, no lo que la pantalla preselecciona', () => {
    const vacio = encabezado({ idProveedor: '', idTipoComprobante: '', numeroExterno: '', fechaComprobante: '' })
    const nueva = { esNuevo: true, huellaGuardada: null, encabezado: vacio, lineas: [lineaDeCompraVacia(1)] }
    expect(hayCambiosSinGuardar(nueva)).toBe(false)
    // el punto de venta único y las percepciones sugeridas no son cambios del operador
    expect(hayCambiosSinGuardar({ ...nueva, encabezado: { ...vacio, idPuntoVenta: 2 } })).toBe(false)

    expect(hayCambiosSinGuardar({ ...nueva, lineas: [{ ...lineaDeCompraVacia(1), codigoProveedor: 'A' }] })).toBe(true)
    expect(hayCambiosSinGuardar({ ...nueva, encabezado: { ...vacio, numeroExterno: '0001-' } })).toBe(true)
    expect(hayCambiosSinGuardar({ ...nueva, encabezado: { ...vacio, fechaComprobante: '2026-08-01' } })).toBe(true)
    expect(hayCambiosSinGuardar({ ...nueva, encabezado: { ...vacio, observaciones: 'x' } })).toBe(true)
    expect(hayCambiosSinGuardar({ ...nueva, encabezado: { ...vacio, idProveedor: 1 } })).toBe(true)
    expect(hayCambiosSinGuardar({ ...nueva, encabezado: { ...vacio, idTipoComprobante: 5 } })).toBe(true)
  })
})
