import { describe, expect, it } from 'vitest'
import { ErrorApi, ErrorDeRed } from '../api/cliente'
import type { LineaDevolvible, TicketDevolvible } from '../api/devoluciones'
import type { VentaDeTurnoListado } from '../api/tipos'
import {
  copiaDelMotivo,
  devolucionDisponible,
  MOTIVO_DEVOLUCION_SIN_CONEXION,
  MOTIVO_DEVOLUCION_SIN_TURNO,
  errorDeEmisionDeDevolucion,
  errorDeLecturaDeTicket,
  evaluarCantidad,
  firmaDeLaSeleccion,
  lineaPorCodigoDeBarra,
  lineasDeLaDevolucion,
  numeroDeTicketTipeado,
  solicitudDeDevolucion,
  sumarPorEscaneo,
  ticketsRecientes,
  unidadDeLinea,
} from './devolucion'

function linea(sobrescribir: Partial<LineaDevolvible> = {}): LineaDevolvible {
  return {
    idArticulo: 1,
    descripcion: 'Yerba 1 kg',
    codigoBarra: '7790001',
    importe: null,
    cantidadVendida: 5,
    cantidadDevuelta: 1,
    cantidadDevolvible: 4,
    importeUnitario: 100,
    importeDeLoRestante: 400,
    ...sobrescribir,
  }
}

function ticket(sobrescribir: Partial<TicketDevolvible> = {}): TicketDevolvible {
  return {
    id: 77,
    numero: 12,
    numeroVisible: '0001-00000012',
    fecha: '2026-11-15T10:00:00Z',
    idPuntoVenta: 3,
    idCliente: 9,
    nombreCliente: 'Ana',
    total: 500,
    devolvible: true,
    motivo: null,
    lineas: [linea()],
    ...sobrescribir,
  }
}

describe('copiaDelMotivo', () => {
  it('cada motivo del servidor tiene su copia en español', () => {
    expect(copiaDelMotivo('anulado')).toBe('El ticket está anulado: no se puede devolver.')
    expect(copiaDelMotivo('con_factura_fiscal')).toMatch(/factura fiscal/)
    expect(copiaDelMotivo('sin_unidades_por_devolver')).toMatch(/ya se devolvió todo/i)
    expect(copiaDelMotivo('no_es_un_ticket_de_venta')).toMatch(/no es un ticket de venta/i)
  })

  it('un motivo desconocido o ausente no se inventa: usa una copia genérica', () => {
    expect(copiaDelMotivo('otro')).toBe('Este ticket no admite devoluciones.')
    expect(copiaDelMotivo(null)).toBe('Este ticket no admite devoluciones.')
  })
})

describe('unidadDeLinea', () => {
  it('una línea con importe es de un artículo por importe y el resto no declara unidad', () => {
    expect(unidadDeLinea(linea({ importe: 250 }))).toBe('Importe')
    expect(unidadDeLinea(linea())).toBeUndefined()
  })
})

describe('evaluarCantidad', () => {
  it('vacío o cero es "no se devuelve", sin error', () => {
    expect(evaluarCantidad(linea(), '')).toEqual({ cantidad: 0, error: null })
    expect(evaluarCantidad(linea(), '0')).toEqual({ cantidad: 0, error: null })
  })

  it('acepta coma o punto y hasta tres decimales en un peso', () => {
    expect(evaluarCantidad(linea(), '1,25')).toEqual({ cantidad: 1.25, error: null })
    expect(evaluarCantidad(linea(), '2.125')).toEqual({ cantidad: 2.125, error: null })
  })

  it('rechaza más de tres decimales sin redondear lo tipeado', () => {
    const resultado = evaluarCantidad(linea(), '1,2345')
    expect(resultado.cantidad).toBe(0)
    expect(resultado.error).toMatch(/3 decimales/)
  })

  it('una línea por importe solo admite enteros', () => {
    const porImporte = linea({ importe: 250, cantidadDevolvible: 3 })
    expect(evaluarCantidad(porImporte, '2')).toEqual({ cantidad: 2, error: null })
    expect(evaluarCantidad(porImporte, '1,5').error).toMatch(/entera/)
  })

  it('el tope es lo que se puede devolver: igual al tope pasa y por encima se rechaza', () => {
    expect(evaluarCantidad(linea({ cantidadDevolvible: 4 }), '4')).toEqual({ cantidad: 4, error: null })
    const pasado = evaluarCantidad(linea({ cantidadDevolvible: 4 }), '5')
    expect(pasado.cantidad).toBe(0)
    expect(pasado.error).toBe('Se puede devolver hasta 4.')
  })

  it('el tope de un peso se muestra con coma decimal', () => {
    expect(evaluarCantidad(linea({ cantidadDevolvible: 1.5 }), '2').error).toBe('Se puede devolver hasta 1,5.')
  })

  it('un texto que no es un número o es negativo se rechaza', () => {
    expect(evaluarCantidad(linea(), 'abc').error).toMatch(/número/)
    expect(evaluarCantidad(linea(), '-1').error).toMatch(/número/)
  })
})

describe('sumarPorEscaneo', () => {
  it('suma uno a la cantidad tipeada', () => {
    expect(sumarPorEscaneo(linea(), '')).toEqual({ texto: '1', aviso: null })
    expect(sumarPorEscaneo(linea(), '2')).toEqual({ texto: '3', aviso: null })
  })

  it('no pasa del tope: deja el texto y avisa', () => {
    const resultado = sumarPorEscaneo(linea({ cantidadDevolvible: 4 }), '4')
    expect(resultado.texto).toBe('4')
    expect(resultado.aviso).toBe('Ya se agregó todo lo que se puede devolver de Yerba 1 kg.')
  })

  it('suma sin arrastrar el error de coma flotante de un peso', () => {
    expect(sumarPorEscaneo(linea({ cantidadDevolvible: 10 }), '0,2').texto).toBe('1,2')
  })

  it('una cantidad tipeada inválida se toma como cero', () => {
    expect(sumarPorEscaneo(linea(), 'abc').texto).toBe('1')
  })
})

describe('numeroDeTicketTipeado', () => {
  it('acepta el correlativo solo o el número visible', () => {
    expect(numeroDeTicketTipeado('12')).toBe(12)
    expect(numeroDeTicketTipeado(' 0001-00000012 ')).toBe(12)
  })

  it('rechaza vacío, cero, texto y números negativos o decimales', () => {
    for (const texto of ['', '0', 'abc', '-3', '1,5', '0001-0', '-']) expect(numeroDeTicketTipeado(texto)).toBeNull()
  })
})

describe('lineaPorCodigoDeBarra', () => {
  it('devuelve la posición de la línea con ese código, ignorando espacios', () => {
    const lineas = [linea({ codigoBarra: 'A' }), linea({ idArticulo: 2, codigoBarra: 'B' })]
    expect(lineaPorCodigoDeBarra(lineas, ' B ')).toBe(1)
  })

  it('sin coincidencia, o con código vacío, devuelve -1', () => {
    const lineas = [linea({ codigoBarra: 'A' }), linea({ idArticulo: 2, codigoBarra: null })]
    expect(lineaPorCodigoDeBarra(lineas, 'Z')).toBe(-1)
    expect(lineaPorCodigoDeBarra(lineas, '')).toBe(-1)
  })
})

describe('lineasDeLaDevolucion', () => {
  const lineas = [linea({ idArticulo: 1 }), linea({ idArticulo: 2, importe: 250, cantidadDevolvible: 3 }), linea({ idArticulo: 3 })]

  it('arma solo las líneas con cantidad, con cantidad positiva e importe solo en la de un artículo por importe', () => {
    const resultado = lineasDeLaDevolucion(lineas, { 0: '2', 1: '1', 2: '' })
    expect(resultado).toEqual({
      posiciones: [0, 1],
      lineas: [
        { idArticulo: 1, cantidad: 2, codigoBarra: '7790001', idLote: null },
        { idArticulo: 2, cantidad: 1, codigoBarra: '7790001', idLote: null, importe: 250 },
      ],
    })
  })

  it('sin ninguna cantidad no hay devolución', () => {
    expect(lineasDeLaDevolucion(lineas, { 0: '', 1: '0' })).toBeNull()
  })

  it('una cantidad inválida o por encima del tope anula toda la selección', () => {
    expect(lineasDeLaDevolucion(lineas, { 0: '2', 1: '9' })).toBeNull()
    expect(lineasDeLaDevolucion(lineas, { 0: 'abc' })).toBeNull()
  })
})

describe('firmaDeLaSeleccion', () => {
  it('cambia con la cantidad y con el ticket de origen, y es igual para la misma selección', () => {
    const a = firmaDeLaSeleccion(1, [{ idArticulo: 1, cantidad: 1, codigoBarra: null, idLote: null }])
    const b = firmaDeLaSeleccion(1, [{ idArticulo: 1, cantidad: 1, codigoBarra: null, idLote: null }])
    const c = firmaDeLaSeleccion(1, [{ idArticulo: 1, cantidad: 2, codigoBarra: null, idLote: null }])
    const d = firmaDeLaSeleccion(2, [{ idArticulo: 1, cantidad: 1, codigoBarra: null, idLote: null }])
    expect(a).toBe(b)
    expect(a).not.toBe(c)
    expect(a).not.toBe(d)
  })
})

describe('solicitudDeDevolucion', () => {
  const lineasDeVenta = [{ idArticulo: 1, cantidad: 2, codigoBarra: '7790001', idLote: null }]

  it('es una nota de crédito asociada al ticket, con un único pago en efectivo igual al reintegro y sin vuelto', () => {
    expect(solicitudDeDevolucion({ ticket: ticket(), lineas: lineasDeVenta, reintegro: 200, idMedioEfectivo: 5 })).toEqual({
      idPuntoVenta: 3,
      idCliente: 9,
      codigoTipoComprobante: 'NCX',
      idComprobanteAsociado: 77,
      lineas: lineasDeVenta,
      pagos: [{ idMedioPago: 5, importe: 200, referencia: null, vuelto: 0 }],
      direccionEntrega: null,
      observaciones: null,
    })
  })

  it('el punto de venta es el del ticket de origen, no el de otro', () => {
    const solicitud = solicitudDeDevolucion({ ticket: ticket({ idPuntoVenta: 8 }), lineas: lineasDeVenta, reintegro: 1, idMedioEfectivo: 5 })
    expect(solicitud.idPuntoVenta).toBe(8)
  })
})

describe('errorDeEmisionDeDevolucion', () => {
  it('desactualizada y excede lo vendido piden releer el ticket y revisar', () => {
    for (const codigo of ['devolucion_desactualizada', 'devolucion_excede_lo_vendido', 'devolucion_reintegro_invalido']) {
      const resultado = errorDeEmisionDeDevolucion(new ErrorApi(409, codigo, 'texto del servidor'))
      expect(resultado.releer).toBe(true)
      expect(resultado.incierto).toBe(false)
    }
  })

  it('cada código tiene su copia en español', () => {
    const casos: Record<string, RegExp> = {
      comprobante_asociado_requerido: /ticket de origen/,
      comprobante_asociado_invalido: /no admite devoluciones/,
      devolucion_requiere_nota_de_credito_fiscal: /nota de crédito fiscal/,
      devolucion_sin_conexion: /conexión/,
      devolucion_solo_en_efectivo: /solo en efectivo/,
      devolucion_con_vuelto: /vuelto/,
      turno_no_abierto: /turno abierto/,
    }
    for (const [codigo, patron] of Object.entries(casos)) {
      const resultado = errorDeEmisionDeDevolucion(new ErrorApi(400, codigo, 'x'))
      expect(resultado.mensaje).toMatch(patron)
      expect(resultado.releer).toBe(false)
    }
  })

  it('un código desconocido muestra el mensaje del servidor', () => {
    expect(errorDeEmisionDeDevolucion(new ErrorApi(400, 'otro', 'Algo pasó.')).mensaje).toBe('Algo pasó.')
  })

  it('una falla de red y un 503 resultado_incierto son resultado incierto y piden releer', () => {
    const red = errorDeEmisionDeDevolucion(new ErrorDeRed(new TypeError('Failed to fetch')))
    expect(red).toMatchObject({ incierto: true, releer: true })
    expect(red.mensaje).toMatch(/Ventas del turno/)
    const incierto = errorDeEmisionDeDevolucion(new ErrorApi(503, 'resultado_incierto', 'x'))
    expect(incierto).toMatchObject({ incierto: true, releer: true })
  })

  it('un error que no es de la API no se queda sin mensaje', () => {
    expect(errorDeEmisionDeDevolucion(new Error('boom')).mensaje).toBe('No se pudo registrar la devolución.')
  })
})

describe('errorDeLecturaDeTicket', () => {
  it('un 404 dice que no se encontró el ticket y otro error conserva el mensaje del servidor', () => {
    expect(errorDeLecturaDeTicket(new ErrorApi(404, 'no_encontrado', 'x'))).toBe('No se encontró ese ticket en este punto de venta.')
    expect(errorDeLecturaDeTicket(new ErrorApi(500, 'error', 'Falló.'))).toBe('Falló.')
    expect(errorDeLecturaDeTicket(new ErrorDeRed(null))).toMatch(/conexión/i)
  })
})

describe('ticketsRecientes', () => {
  function venta(sobrescribir: Partial<VentaDeTurnoListado>): VentaDeTurnoListado {
    return {
      id: 1,
      numero: 1,
      numeroVisible: '0001-00000001',
      estado: 'Emitido',
      fecha: '2026-11-15T10:00:00Z',
      idCliente: 1,
      nombreCliente: 'Ana',
      total: 100,
      descuentoManualTotal: 0,
      recargoManualTotal: 0,
      mediosDePago: [],
      ...sobrescribir,
    }
  }

  it('deja solo ventas emitidas de total positivo (ni anuladas ni notas de crédito), las más nuevas primero', () => {
    const resultado = ticketsRecientes([
      venta({ id: 1, fecha: '2026-11-15T09:00:00Z' }),
      venta({ id: 2, estado: 'Anulado', fecha: '2026-11-15T12:00:00Z' }),
      venta({ id: 3, total: -50, fecha: '2026-11-15T12:30:00Z' }),
      venta({ id: 4, fecha: '2026-11-15T11:00:00Z' }),
    ])
    expect(resultado.map((v) => v.id)).toEqual([4, 1])
  })

  it('se queda con diez', () => {
    const muchas = Array.from({ length: 14 }, (_, i) => venta({ id: i + 1, fecha: `2026-11-15T${String(i + 1).padStart(2, '0')}:00:00Z` }))
    expect(ticketsRecientes(muchas)).toHaveLength(10)
  })
})

describe('devolucionDisponible', () => {
  it('sin conexión queda deshabilitada y dice por qué, aunque falten otros requisitos', () => {
    expect(devolucionDisponible({ enLinea: false, turnoAbierto: false, inerte: true })).toEqual({ habilitado: false, motivo: MOTIVO_DEVOLUCION_SIN_CONEXION })
  })

  it('sin turno abierto queda deshabilitada y dice por qué', () => {
    expect(devolucionDisponible({ enLinea: true, turnoAbierto: false, inerte: false })).toEqual({ habilitado: false, motivo: MOTIVO_DEVOLUCION_SIN_TURNO })
  })

  it('con conexión y turno abierto, la habilita salvo que la pantalla esté inerte', () => {
    expect(devolucionDisponible({ enLinea: true, turnoAbierto: true, inerte: false }).habilitado).toBe(true)
    expect(devolucionDisponible({ enLinea: true, turnoAbierto: true, inerte: true }).habilitado).toBe(false)
  })
})
