import { describe, expect, it } from 'vitest'
import { algunPagoEnEfectivo, reporteZ, ticketDeVenta } from './plantillas'
import type { ComprobanteEmitido, DetalleDeTurno, MedioPagoListado, TurnoConArqueos } from '../api/tipos'

/** Decodificador mínimo para los tests: ASCII pasa directo, LF se vuelve '\n', cualquier otro
 * byte de control (o no-ASCII) se descarta — alcanza para buscar substrings en el ticket, no
 * pretende ser un decoder CP858 real (eso ya lo prueba `escpos.test.ts`). */
function textoPlano(bytes: Uint8Array): string {
  return Array.from(bytes)
    .map((b) => (b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : b === 0x0a ? '\n' : ''))
    .join('')
}

const CONTEXTO = { empresa: 'Almacén Demo', puntoVenta: 'Local Centro', cajero: 'jperez' }

function medioFixture(sobrescribir: Partial<MedioPagoListado> = {}): MedioPagoListado {
  return {
    id: 1,
    nombre: 'Efectivo',
    activo: true,
    idEmpresa: null,
    orden: 1,
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    recargoPorcentaje: null,
    ...sobrescribir,
  }
}

function comprobanteFixture(sobrescribir: Partial<ComprobanteEmitido> = {}): ComprobanteEmitido {
  return {
    id: 1,
    numero: 1,
    numeroVisible: '0001-00000001',
    estado: 'Emitido',
    fecha: '2026-09-16T15:30:00-03:00',
    idPuntoVenta: 1,
    idCliente: 1,
    idComprobanteAsociado: null,
    subtotal: 1000,
    descuentoTotal: 0,
    total: 1000,
    direccionEntrega: null,
    observaciones: null,
    items: [
      {
        orden: 1,
        idArticulo: 1,
        descripcion: 'Coca Cola 1L',
        codigoBarra: null,
        idArea: 1,
        idListaPrecio: 1,
        idOferta: null,
        idAlicuotaIva: 1,
        porcentajeIva: 21,
        cantidad: 2,
        precioUnitario: 500,
        descuento: 0,
        total: 1000,
        idLote: null,
        codigoLote: null,
        loteVencido: false,
      },
    ],
    pagos: [{ idMedioPago: 1, importe: 1000, referencia: null, vuelto: 0 }],
    idPresupuestoOrigen: null,
    ...sobrescribir,
  }
}

describe('ticketDeVenta', () => {
  it('incluye el aviso de que no es un comprobante fiscal', () => {
    const texto = textoPlano(ticketDeVenta(comprobanteFixture(), CONTEXTO, [medioFixture()]))
    expect(texto).toContain('COMPROBANTE NO VALIDO COMO FACTURA')
  })

  it('incluye el número visible, el cajero y el total', () => {
    const texto = textoPlano(ticketDeVenta(comprobanteFixture(), CONTEXTO, [medioFixture()]))
    expect(texto).toContain('0001-00000001')
    expect(texto).toContain('jperez')
    expect(texto).toContain('TOTAL')
    expect(texto).toContain('1.000,00')
  })

  it('incluye cada línea de artículo con cantidad y descripción', () => {
    const texto = textoPlano(ticketDeVenta(comprobanteFixture(), CONTEXTO, [medioFixture()]))
    expect(texto).toContain('2 x Coca Cola 1L')
  })

  it('incluye el nombre del medio de pago resuelto contra la lista, con vuelto si hay', () => {
    const comprobante = comprobanteFixture({
      pagos: [{ idMedioPago: 1, importe: 1500, referencia: null, vuelto: 500 }],
    })
    const texto = textoPlano(ticketDeVenta(comprobante, CONTEXTO, [medioFixture({ id: 1, nombre: 'Efectivo' })]))
    expect(texto).toContain('Efectivo')
    expect(texto).toContain('Vuelto')
  })

  it('termina con el corte parcial (GS V 66 0)', () => {
    const bytes = ticketDeVenta(comprobanteFixture(), CONTEXTO, [medioFixture()])
    expect(Array.from(bytes.slice(-4))).toEqual([0x1d, 0x56, 0x42, 0x00])
  })

  it('pulsa el cajón (ESC p 0 25 250) antes del corte cuando algún pago es en efectivo', () => {
    const comprobante = comprobanteFixture({ pagos: [{ idMedioPago: 1, importe: 1000, referencia: null, vuelto: 0 }] })
    const bytes = ticketDeVenta(comprobante, CONTEXTO, [medioFixture({ id: 1, comportamiento: 'Efectivo' })])
    // Cola exacta: abrirCajon (5) + avanzar(2) (2) + cortar (4) = 11 bytes.
    expect(Array.from(bytes.slice(-11))).toEqual([0x1b, 0x70, 0x00, 25, 250, 0x0a, 0x0a, 0x1d, 0x56, 0x42, 0x00])
  })

  it('nunca pulsa el cajón si todos los pagos son electrónicos/cuenta corriente', () => {
    const comprobante = comprobanteFixture({ pagos: [{ idMedioPago: 2, importe: 1000, referencia: 'abc', vuelto: 0 }] })
    const bytes = ticketDeVenta(comprobante, CONTEXTO, [medioFixture({ id: 2, comportamiento: 'Electronico' })])
    const contieneComandoDeCajon = Array.from(bytes).some(
      (_, i) => bytes[i] === 0x1b && bytes[i + 1] === 0x70 && bytes[i + 2] === 0x00,
    )
    expect(contieneComandoDeCajon).toBe(false)
  })

  it('empieza con ESC @ (inicializar) seguido de ESC t 19 (CP858)', () => {
    const bytes = ticketDeVenta(comprobanteFixture(), CONTEXTO, [medioFixture()])
    expect(Array.from(bytes.slice(0, 5))).toEqual([0x1b, 0x40, 0x1b, 0x74, 19])
  })
})

describe('algunPagoEnEfectivo', () => {
  it('true si algún pago usa un medio con comportamiento Efectivo', () => {
    const comprobante = comprobanteFixture({ pagos: [{ idMedioPago: 1, importe: 1000, referencia: null, vuelto: 0 }] })
    expect(algunPagoEnEfectivo(comprobante, [medioFixture({ id: 1, comportamiento: 'Efectivo' })])).toBe(true)
  })

  it('false si todos los pagos son electrónicos', () => {
    const comprobante = comprobanteFixture({ pagos: [{ idMedioPago: 2, importe: 1000, referencia: 'abc', vuelto: 0 }] })
    expect(algunPagoEnEfectivo(comprobante, [medioFixture({ id: 2, comportamiento: 'Electronico' })])).toBe(false)
  })

  it('false (nunca asumido true) si el medio del pago no está en la lista', () => {
    const comprobante = comprobanteFixture({ pagos: [{ idMedioPago: 99, importe: 1000, referencia: null, vuelto: 0 }] })
    expect(algunPagoEnEfectivo(comprobante, [medioFixture({ id: 1 })])).toBe(false)
  })
})

describe('reporteZ', () => {
  function turnoFixture(sobrescribir: Partial<TurnoConArqueos> = {}): TurnoConArqueos {
    return {
      id: 5,
      idPuntoVenta: 1,
      idEmpleadoApertura: 1,
      idEmpleadoCierre: 1,
      fechaApertura: '2026-09-16T09:00:00-03:00',
      fechaCierre: '2026-09-16T18:00:00-03:00',
      fondoInicial: 5000,
      estado: 'Cerrado',
      observaciones: null,
      arqueos: [{ idMedioPago: 1, importeEsperado: 10000, importeDeclarado: 9900, diferencia: -100 }],
      ...sobrescribir,
    }
  }

  it('con TurnoConArqueos incluye el número de turno y las líneas de arqueo', () => {
    const texto = textoPlano(reporteZ(turnoFixture(), CONTEXTO))
    expect(texto).toContain('REPORTE Z')
    expect(texto).toContain('Turno #5')
    expect(texto).toContain('esperado')
    expect(texto).toContain('declarado')
    expect(texto).toContain('diferencia')
  })

  it('con DetalleDeTurno usa el resumen (solo esperado, sin declarado/diferencia)', () => {
    const detalle: DetalleDeTurno = {
      resumen: {
        idTurnoCaja: 8,
        idMedioAncla: 1,
        medios: [{ idMedioPago: 1, importeEsperado: 3000 }],
        cantidadTickets: 4,
        primerTicket: null,
        ultimoTicket: null,
        ingresosPorArea: [],
        egresos: { porCategoria: [], porArea: [], retiros: 0 },
      },
      tickets: [],
      gastos: [],
    }
    const texto = textoPlano(reporteZ(detalle, CONTEXTO))
    expect(texto).toContain('Turno #8')
    expect(texto).toContain('Tickets: 4')
    expect(texto).not.toContain('declarado')
  })

  it('termina con el corte parcial (GS V 66 0)', () => {
    const bytes = reporteZ(turnoFixture(), CONTEXTO)
    expect(Array.from(bytes.slice(-4))).toEqual([0x1d, 0x56, 0x42, 0x00])
  })
})
