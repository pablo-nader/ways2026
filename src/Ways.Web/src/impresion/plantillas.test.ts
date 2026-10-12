import { describe, expect, it } from 'vitest'
import { algunPagoEnEfectivo, cierreDeTurno, lineasDeCierreDeTurno, pulsoDeCajon, reporteZ, ticketDePagoACuenta, ticketDeVenta, ticketRetiroDeEfectivo } from './plantillas'
import type { ComprobanteEmitido, DetalleDeTurno, MedioPagoListado, ResumenDeCierrePorRetiro, TurnoConArqueos } from '../api/tipos'
import { COLUMNAS_FUENTE_A, dosColumnas } from './escpos'
import { fechaHoraATexto } from '../formato/fechas'

/** Decodificador mínimo para los tests: ASCII pasa directo, LF se vuelve '\n', cualquier otro
 * byte de control (o no-ASCII) se descarta — alcanza para buscar substrings en el ticket, no
 * pretende ser un decoder CP858 real (eso ya lo prueba `escpos.test.ts`). */
function textoPlano(bytes: Uint8Array): string {
  return Array.from(bytes)
    .map((b) => (b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : b === 0x0a ? '\n' : ''))
    .join('')
}

const CONTEXTO = { empresa: 'Almacén Demo', puntoVenta: 'Local Centro', cajero: 'jperez' }

/** `true` si los bytes contienen el comando de pulso de cajón (`ESC p 0`, `0x1b 0x70 0x00`) en
 * cualquier posición — mismo criterio que las variantes inline ya usadas en `ticketDeVenta`. */
function contieneComandoDeCajon(bytes: Uint8Array): boolean {
  return Array.from(bytes).some((_, i) => bytes[i] === 0x1b && bytes[i + 1] === 0x70 && bytes[i + 2] === 0x00)
}

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
    porcentajeFacturacion: 0,
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
    descuentoManualTotal: 0,
    recargoManualTotal: 0,
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
        ajusteManualPorcentaje: null,
        ajusteManual: 0,
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

  it('ticket ORIGINAL (sin opciones): pulsa el cajón (ESC p 0 25 250) antes del corte cuando algún pago es en efectivo', () => {
    const comprobante = comprobanteFixture({ pagos: [{ idMedioPago: 1, importe: 1000, referencia: null, vuelto: 0 }] })
    const bytes = ticketDeVenta(comprobante, CONTEXTO, [medioFixture({ id: 1, comportamiento: 'Efectivo' })])
    // Cola exacta: abrirCajon (5) + avanzar(2) (2) + cortar (4) = 11 bytes.
    expect(Array.from(bytes.slice(-11))).toEqual([0x1b, 0x70, 0x00, 25, 250, 0x0a, 0x0a, 0x1d, 0x56, 0x42, 0x00])
  })

  it('REIMPRESION: nunca pulsa el cajón aunque algún pago sea en efectivo (decisión del dueño: reimprimir no es una venta nueva)', () => {
    const comprobante = comprobanteFixture({ pagos: [{ idMedioPago: 1, importe: 1000, referencia: null, vuelto: 0 }] })
    const bytes = ticketDeVenta(comprobante, CONTEXTO, [medioFixture({ id: 1, comportamiento: 'Efectivo' })], { reimpresion: true })

    const contieneComandoDeCajon = Array.from(bytes).some(
      (_, i) => bytes[i] === 0x1b && bytes[i + 1] === 0x70 && bytes[i + 2] === 0x00,
    )
    expect(contieneComandoDeCajon).toBe(false)
    // Termina en avanzar(2) + cortar (6 bytes) directo, sin el pulso de cajón antes.
    expect(Array.from(bytes.slice(-6))).toEqual([0x0a, 0x0a, 0x1d, 0x56, 0x42, 0x00])
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

  it('sin opciones (default), nunca incluye la línea de REIMPRESION', () => {
    const texto = textoPlano(ticketDeVenta(comprobanteFixture(), CONTEXTO, [medioFixture()]))
    expect(texto).not.toContain('REIMPRESION')
  })

  it('con { reimpresion: true } incluye una línea "REIMPRESION" bien visible, sin perder el resto del contenido', () => {
    const reimpreso = textoPlano(ticketDeVenta(comprobanteFixture(), CONTEXTO, [medioFixture()], { reimpresion: true }))

    expect(reimpreso).toContain('REIMPRESION')
    // El resto del ticket sigue igual: el número, el ítem y el total no cambian.
    expect(reimpreso).toContain('0001-00000001')
    expect(reimpreso).toContain('2 x Coca Cola 1L')
    expect(reimpreso).toContain('TOTAL')
    expect(reimpreso).toContain('1.000,00')
  })

  it('la línea de REIMPRESION aparece ANTES del aviso de "no válido como factura"', () => {
    const reimpreso = textoPlano(ticketDeVenta(comprobanteFixture(), CONTEXTO, [medioFixture()], { reimpresion: true }))
    expect(reimpreso.indexOf('REIMPRESION')).toBeLessThan(reimpreso.indexOf('COMPROBANTE NO VALIDO COMO FACTURA'))
  })
})

/** Decodifica las líneas imprimibles saltando los comandos ESC/GS completos (con sus parámetros), a
 * diferencia de `textoPlano`, que deja sus parámetros imprimibles pegados al texto. Solo ASCII. */
function lineasDe(bytes: Uint8Array): string[] {
  const largoDelComando = (b: number, siguiente: number): number => {
    if (b === 0x1b) return siguiente === 0x40 ? 2 : siguiente === 0x70 ? 5 : 3
    return siguiente === 0x56 ? 4 : 3
  }
  const lineas: string[] = []
  let actual = ''
  for (let i = 0; i < bytes.length; ) {
    const b = bytes[i]
    if (b === 0x1b || b === 0x1d) {
      i += largoDelComando(b, bytes[i + 1])
    } else if (b === 0x0a) {
      lineas.push(actual)
      actual = ''
      i += 1
    } else {
      if (b >= 0x20 && b < 0x7f) actual += String.fromCharCode(b)
      i += 1
    }
  }
  return lineas
}

describe('ticketDeVenta — cliente identificado con su saldo', () => {
  const CLIENTE = { numero: 2, nombre: 'Juan Perez', saldoAntes: 500, saldoDespues: 1200 }
  const MEDIOS = [medioFixture({ id: 1, comportamiento: 'Efectivo' })]

  it('imprime el cliente y el saldo anterior y actual de la cuenta', () => {
    const lineas = lineasDe(ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS, { cliente: CLIENTE }))
    expect(lineas).toContain('Cliente: #2 - Juan Perez')
    expect(lineas).toContain(dosColumnas('Saldo anterior', '$ 500,00'))
    expect(lineas).toContain(dosColumnas('Saldo actual', '$ 1.200,00'))
  })

  it('el cliente va debajo del cajero y los saldos debajo de los pagos', () => {
    const lineas = lineasDe(ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS, { cliente: CLIENTE }))
    expect(lineas.indexOf('Cliente: #2 - Juan Perez')).toBe(lineas.indexOf('Cajero: jperez') + 1)
    expect(lineas.indexOf(dosColumnas('Saldo anterior', '$ 500,00'))).toBeGreaterThan(lineas.indexOf('Pagos:'))
  })

  it('sin cliente (Consumidor Final) no imprime ni cliente ni saldos', () => {
    const texto = textoPlano(ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS))
    expect(texto).not.toContain('Cliente:')
    expect(texto).not.toContain('Saldo')
  })

  it('una reimpresión nunca imprime saldos aunque se le pase el cliente: los históricos no se conocen', () => {
    const texto = textoPlano(ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS, { reimpresion: true, cliente: CLIENTE }))
    expect(texto).not.toContain('Saldo')
    expect(texto).not.toContain('Cliente:')
  })

  it('un saldo no confirmado se rotula "(estimado)" en las dos líneas', () => {
    const lineas = lineasDe(ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS, { cliente: { ...CLIENTE, estimado: true } }))
    expect(lineas).toContain(dosColumnas('Saldo anterior (estimado)', '$ 500,00'))
    expect(lineas).toContain(dosColumnas('Saldo actual (estimado)', '$ 1.200,00'))
  })

  it('un saldo confirmado no lleva la marca de estimado', () => {
    const texto = textoPlano(ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS, { cliente: { ...CLIENTE, estimado: false } }))
    expect(texto).not.toContain('estimado')
  })

  it('un saldo negativo se imprime "a favor" con su valor absoluto, nunca con signo', () => {
    const lineas = lineasDe(ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS, { cliente: { ...CLIENTE, saldoAntes: -80, saldoDespues: -30 } }))
    expect(lineas).toContain(dosColumnas('Saldo anterior a favor', '$ 80,00'))
    expect(lineas).toContain(dosColumnas('Saldo actual a favor', '$ 30,00'))
    expect(lineas.filter((l) => l.includes('-$'))).toEqual([])
  })

  it('un saldo que pasa de a favor a deuda rotula cada línea por su signo', () => {
    const lineas = lineasDe(ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS, { cliente: { ...CLIENTE, saldoAntes: -30, saldoDespues: 70 } }))
    expect(lineas).toContain(dosColumnas('Saldo anterior a favor', '$ 30,00'))
    expect(lineas).toContain(dosColumnas('Saldo actual', '$ 70,00'))
  })

  it('con un importe de 9 dígitos la marca "(estimado)" sobrevive: se abrevia la base del rótulo', () => {
    const cliente = { ...CLIENTE, saldoAntes: -123456789, saldoDespues: -123456739, estimado: true }
    const lineas = lineasDe(ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS, { cliente }))
    expect(lineas).toContain(dosColumnas('Saldo ant. a fav. (estimado)', '$ 123.456.789,00'))
    expect(lineas).toContain(dosColumnas('Saldo actual a favor (estimado)', '$ 123.456.739,00'))
    expect(lineas.filter((l) => l.startsWith('Saldo') && !l.includes('(estimado)'))).toEqual([])
  })

  it('con importes que entran, los rótulos no se abrevian', () => {
    const lineas = lineasDe(ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS, { cliente: { ...CLIENTE, saldoAntes: -1234, saldoDespues: -1000, estimado: true } }))
    expect(lineas).toContain(dosColumnas('Saldo anterior a favor (estimado)', '$ 1.234,00'))
  })

  it('un nombre largo se recorta al ancho del ticket en vez de partir la línea', () => {
    const nombre = 'Distribuidora Mayorista de Productos Regionales Sociedad Anonima'
    const lineas = lineasDe(ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS, { cliente: { ...CLIENTE, nombre } }))
    const linea = lineas.find((l) => l.startsWith('Cliente:'))!
    expect(linea.length).toBeLessThanOrEqual(48)
    expect(linea).toBe(`Cliente: #2 - ${nombre}`.slice(0, 47))
  })

  it('conserva el pulso del cajón y el corte al final', () => {
    const bytes = ticketDeVenta(comprobanteFixture(), CONTEXTO, MEDIOS, { cliente: CLIENTE })
    expect(Array.from(bytes.slice(-11))).toEqual([0x1b, 0x70, 0x00, 25, 250, 0x0a, 0x0a, 0x1d, 0x56, 0x42, 0x00])
  })
})

describe('ticketDePagoACuenta', () => {
  const MEDIOS = [
    medioFixture({ id: 1, nombre: 'Efectivo', comportamiento: 'Efectivo' }),
    medioFixture({ id: 2, nombre: 'Tarjeta', comportamiento: 'Electronico', admiteVuelto: false }),
  ]
  const RECIBO = comprobanteFixture({
    numeroVisible: '0007-00000900',
    fecha: '2026-09-16T15:30:00-03:00',
    items: [],
    total: 0,
    subtotal: 0,
    pagos: [
      { idMedioPago: 1, importe: 500, referencia: null, vuelto: 50 },
      { idMedioPago: 2, importe: 200, referencia: 'AUT1', vuelto: 0 },
    ],
  })
  const DATOS = { comprobante: RECIBO, cliente: { numero: 2, nombre: 'Juan Perez' }, saldoAnterior: 1000, saldoNuevo: 350 }

  it('arma el encabezado, el tipo y número del comprobante, la fecha, el cajero y el cliente', () => {
    const lineas = lineasDe(ticketDePagoACuenta(DATOS, CONTEXTO, MEDIOS))
    expect(lineas.some((l) => l.includes('Demo'))).toBe(true)
    expect(lineas).toContain('Local Centro')
    expect(lineas).toContain('RECIBO DE PAGO A CUENTA')
    expect(lineas).toContain('Comprobante: RC 0007-00000900')
    expect(lineas).toContain(`Fecha: ${fechaHoraATexto('2026-09-16T15:30:00-03:00')}`)
    expect(lineas).toContain('Cajero: jperez')
    expect(lineas).toContain('Cliente: #2 - Juan Perez')
  })

  it('lista cada medio con su importe, el vuelto entregado y el total pagado (importes menos vueltos)', () => {
    const lineas = lineasDe(ticketDePagoACuenta(DATOS, CONTEXTO, MEDIOS))
    const desde = lineas.indexOf('Pagos:')
    expect(lineas.slice(desde, desde + 5)).toEqual([
      'Pagos:',
      dosColumnas('Efectivo', '$ 500,00'),
      dosColumnas('  Vuelto', '$ 50,00'),
      dosColumnas('Tarjeta', '$ 200,00'),
      dosColumnas('Total pagado', '$ 650,00'),
    ])
  })

  it('termina con saldo anterior, total pagado y saldo nuevo, en ese orden', () => {
    const lineas = lineasDe(ticketDePagoACuenta(DATOS, CONTEXTO, MEDIOS))
    const anterior = lineas.indexOf(dosColumnas('Saldo anterior', '$ 1.000,00'))
    const nuevo = lineas.indexOf(dosColumnas('Saldo nuevo', '$ 350,00'))
    expect(anterior).toBeGreaterThan(-1)
    expect(nuevo).toBeGreaterThan(anterior)
  })

  it('un saldo estimado (consulta posterior fallida o ventas en cola) se rotula "(estimado)"', () => {
    const lineas = lineasDe(ticketDePagoACuenta({ ...DATOS, estimado: true }, CONTEXTO, MEDIOS))
    expect(lineas).toContain(dosColumnas('Saldo anterior (estimado)', '$ 1.000,00'))
    expect(lineas).toContain(dosColumnas('Saldo nuevo (estimado)', '$ 350,00'))
  })

  it('un saldo confirmado no lleva la marca de estimado', () => {
    expect(textoPlano(ticketDePagoACuenta({ ...DATOS, estimado: false }, CONTEXTO, MEDIOS))).not.toContain('estimado')
  })

  it('un saldo nuevo negativo se imprime "a favor" con el valor absoluto', () => {
    const lineas = lineasDe(ticketDePagoACuenta({ ...DATOS, saldoAnterior: 100, saldoNuevo: -550 }, CONTEXTO, MEDIOS))
    expect(lineas).toContain(dosColumnas('Saldo anterior', '$ 100,00'))
    expect(lineas).toContain(dosColumnas('Saldo nuevo a favor', '$ 550,00'))
    expect(lineas.filter((l) => l.includes('-$'))).toEqual([])
  })

  it('con un importe de 9 dígitos la marca "(estimado)" del recibo sobrevive', () => {
    const lineas = lineasDe(ticketDePagoACuenta({ ...DATOS, saldoAnterior: 123456789, saldoNuevo: -1234567890, estimado: true }, CONTEXTO, MEDIOS))
    expect(lineas).toContain(dosColumnas('Saldo anterior (estimado)', '$ 123.456.789,00'))
    expect(lineas).toContain(dosColumnas('Saldo nvo. a fav. (estimado)', '$ 1.234.567.890,00'))
  })

  it('un nombre largo se recorta al ancho del ticket', () => {
    const nombre = 'Distribuidora Mayorista de Productos Regionales Sociedad Anonima'
    const lineas = lineasDe(ticketDePagoACuenta({ ...DATOS, cliente: { numero: 2, nombre } }, CONTEXTO, MEDIOS))
    expect(lineas.find((l) => l.startsWith('Cliente:'))).toBe(`Cliente: #2 - ${nombre}`.slice(0, 47))
  })

  it('un medio que no está en la lista se rotula por su número', () => {
    const datos = { ...DATOS, comprobante: { ...RECIBO, pagos: [{ idMedioPago: 99, importe: 100, referencia: null, vuelto: 0 }] } }
    expect(lineasDe(ticketDePagoACuenta(datos, CONTEXTO, MEDIOS))).toContain(dosColumnas('Medio #99', '$ 100,00'))
  })

  it('con algún pago en efectivo pulsa el cajón en el mismo trabajo, antes del corte', () => {
    const bytes = ticketDePagoACuenta(DATOS, CONTEXTO, MEDIOS)
    expect(Array.from(bytes.slice(-11))).toEqual([0x1b, 0x70, 0x00, 25, 250, 0x0a, 0x0a, 0x1d, 0x56, 0x42, 0x00])
  })

  it('sin pagos en efectivo nunca pulsa el cajón', () => {
    const datos = { ...DATOS, comprobante: { ...RECIBO, pagos: [{ idMedioPago: 2, importe: 200, referencia: 'AUT1', vuelto: 0 }] } }
    const bytes = ticketDePagoACuenta(datos, CONTEXTO, MEDIOS)
    expect(contieneComandoDeCajon(bytes)).toBe(false)
    expect(Array.from(bytes.slice(-6))).toEqual([0x0a, 0x0a, 0x1d, 0x56, 0x42, 0x00])
  })

  it('empieza con ESC @ y la página CP858, como el resto de los tickets', () => {
    expect(Array.from(ticketDePagoACuenta(DATOS, CONTEXTO, MEDIOS).slice(0, 5))).toEqual([0x1b, 0x40, 0x1b, 0x74, 19])
  })
})

describe('ticketDeVenta — ajuste manual de precio', () => {
  const MEDIOS = [medioFixture()]

  /** Una línea de 2 × $ 500 (bruto $ 1.000) con el porcentaje dado; `ajusteManual` y los totales
   * del encabezado salen de la misma cuenta que el servidor. */
  function comprobanteAjustado(porcentaje: number | null, sobrescribir: Partial<ComprobanteEmitido> = {}): ComprobanteEmitido {
    const base = comprobanteFixture()
    const ajuste = porcentaje === null ? 0 : Math.round(1000 * porcentaje) / 100
    return comprobanteFixture({
      descuentoManualTotal: porcentaje !== null && porcentaje < 0 ? -ajuste : 0,
      recargoManualTotal: porcentaje !== null && porcentaje > 0 ? ajuste : 0,
      total: 1000 + ajuste,
      items: [{ ...base.items[0], ajusteManualPorcentaje: porcentaje, ajusteManual: ajuste, total: 1000 + ajuste }],
      ...sobrescribir,
    })
  }

  /** Decodifica los bytes a líneas de texto SACANDO los comandos ESC/POS con su argumento (a
   * diferencia de `textoPlano`, que deja filtrar el byte imprimible del comando: "ESC a 1" deja una
   * "a" pegada al texto), para poder comparar una línea completa. Cubre los comandos que emite
   * `ConstructorDeTicket`; un byte no ASCII (acentos en CP858) se vuelve "?". */
  function lineasDelTicket(comprobante: ComprobanteEmitido, opciones: { reimpresion?: boolean } = {}): string[] {
    const bytes = ticketDeVenta(comprobante, CONTEXTO, MEDIOS, opciones)
    let texto = ''
    for (let i = 0; i < bytes.length; i++) {
      const byte = bytes[i]
      if (byte === 0x1b) {
        const comando = bytes[i + 1]
        i += comando === 0x40 ? 1 : comando === 0x70 ? 4 : 2
      } else if (byte === 0x1d) {
        i += bytes[i + 1] === 0x56 ? 3 : 2
      } else if (byte === 0x0a) {
        texto += '\n'
      } else {
        texto += byte >= 0x20 && byte < 0x7f ? String.fromCharCode(byte) : '?'
      }
    }
    return texto.split('\n')
  }

  it('sin ajustes no imprime ningún aviso ni fila de ajuste', () => {
    const texto = lineasDelTicket(comprobanteAjustado(null)).join('\n')

    expect(texto).not.toContain('MANUAL')
    expect(texto).not.toContain('Desc. manual')
    expect(texto).not.toContain('Recargo')
  })

  it('sin ajustes imprime byte por byte lo mismo que un comprobante de un servidor que todavía no manda los campos', () => {
    const conCampos = comprobanteFixture()
    const sinCampos = {
      ...conCampos,
      items: conCampos.items.map(({ ajusteManualPorcentaje: _porcentaje, ajusteManual: _monto, ...resto }) => resto),
      descuentoManualTotal: undefined,
      recargoManualTotal: undefined,
    } as unknown as ComprobanteEmitido

    expect(ticketDeVenta(sinCampos, CONTEXTO, MEDIOS)).toEqual(ticketDeVenta(conCampos, CONTEXTO, MEDIOS))
  })

  it('con descuento manual: marca el encabezado, la línea con su porcentaje y monto con signo, y el bloque de totales', () => {
    const lineas = lineasDelTicket(comprobanteAjustado(-10))

    expect(lineas).toContain('*** CON DESCUENTO MANUAL ***')
    expect(lineas.join('\n')).not.toContain('CON RECARGO MANUAL')

    const filaDeLinea = lineas.find((l) => l.includes('Desc. manual 10%')) ?? ''
    expect(filaDeLinea).toMatch(/^ {2}Desc\. manual 10% +-\$ 100,00$/)
    expect(filaDeLinea).toHaveLength(48)

    const filaDeTotales = lineas.find((l) => l.startsWith('Desc. manual')) ?? ''
    expect(filaDeTotales).toMatch(/^Desc\. manual +-\$ 100,00$/)
    expect(filaDeTotales).toHaveLength(48)
    expect(lineas.some((l) => l.startsWith('Recargo'))).toBe(false)

    // El total ya viene ajustado del servidor y es lo que se imprime.
    expect(lineas.find((l) => l.startsWith('TOTAL'))).toContain('900,00')
  })

  it('con recargo manual: marca el encabezado y la línea y el total llevan "+"', () => {
    const lineas = lineasDelTicket(comprobanteAjustado(15))

    expect(lineas).toContain('*** CON RECARGO MANUAL ***')
    expect(lineas.join('\n')).not.toContain('CON DESCUENTO MANUAL')
    expect(lineas.find((l) => l.includes('Recargo 15%'))).toMatch(/^ {2}Recargo 15% +\+\$ 150,00$/)
    expect(lineas.find((l) => l.startsWith('Recargo'))).toMatch(/^Recargo +\+\$ 150,00$/)
    expect(lineas.some((l) => l.startsWith('Desc. manual'))).toBe(false)
    expect(lineas.find((l) => l.startsWith('TOTAL'))).toContain('1.150,00')
  })

  it('con porcentaje decimal lo imprime con coma y sin ceros de relleno', () => {
    const lineas = lineasDelTicket(comprobanteAjustado(-12.5))

    expect(lineas.find((l) => l.includes('Desc. manual 12,5%'))).toMatch(/-\$ 125,00$/)
  })

  it('con descuento y recargo a la vez imprime los dos avisos (descuento primero) y las dos filas de totales', () => {
    const base = comprobanteFixture()
    const comprobante = comprobanteFixture({
      subtotal: 1500,
      descuentoManualTotal: 100,
      recargoManualTotal: 75,
      total: 1475,
      items: [
        { ...base.items[0], ajusteManualPorcentaje: -10, ajusteManual: -100, total: 900 },
        { ...base.items[0], orden: 2, descripcion: 'Fanta 1.5L', cantidad: 1, precioUnitario: 500, ajusteManualPorcentaje: 15, ajusteManual: 75, total: 575 },
      ],
    })
    const lineas = lineasDelTicket(comprobante)

    const iDescuento = lineas.indexOf('*** CON DESCUENTO MANUAL ***')
    const iRecargo = lineas.indexOf('*** CON RECARGO MANUAL ***')
    expect(iDescuento).toBeGreaterThan(-1)
    expect(iRecargo).toBe(iDescuento + 1)
    expect(lineas.find((l) => l.startsWith('Desc. manual'))).toMatch(/-\$ 100,00$/)
    expect(lineas.find((l) => l.startsWith('Recargo'))).toMatch(/\+\$ 75,00$/)
  })

  it('los avisos van debajo de "no válido como factura" y arriba de los datos del comprobante', () => {
    const lineas = lineasDelTicket(comprobanteAjustado(-10))

    const iFactura = lineas.indexOf('COMPROBANTE NO VALIDO COMO FACTURA')
    const iAviso = lineas.indexOf('*** CON DESCUENTO MANUAL ***')
    const iNumero = lineas.findIndex((l) => l.startsWith('Comprobante:'))
    expect(iFactura).toBeGreaterThan(-1)
    expect(iAviso).toBe(iFactura + 1)
    expect(iNumero).toBeGreaterThan(iAviso)
  })

  it('la fila del ajuste va justo debajo de la línea y después del descuento por oferta, si lo hay', () => {
    const base = comprobanteFixture()
    const comprobante = comprobanteAjustado(-10, {
      descuentoTotal: 40,
      items: [{ ...base.items[0], descuento: 40, ajusteManualPorcentaje: -10, ajusteManual: -96, total: 864 }],
      descuentoManualTotal: 96,
      total: 864,
    })
    const lineas = lineasDelTicket(comprobante)

    const iLinea = lineas.findIndex((l) => l.startsWith('2 x Coca Cola 1L'))
    expect(lineas[iLinea + 1]).toMatch(/^ {2}Descuento +-\$ 40,00$/)
    expect(lineas[iLinea + 2]).toMatch(/^ {2}Desc\. manual 10% +-\$ 96,00$/)
  })

  it('una reimpresión con ajuste conserva los avisos y sigue empezando por REIMPRESION', () => {
    const lineas = lineasDelTicket(comprobanteAjustado(15), { reimpresion: true })

    expect(lineas).toContain('*** REIMPRESION ***')
    expect(lineas).toContain('*** CON RECARGO MANUAL ***')
    expect(lineas.indexOf('*** REIMPRESION ***')).toBeLessThan(lineas.indexOf('*** CON RECARGO MANUAL ***'))
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
      arqueos: [{ idMedioPago: 1, importeEsperado: 10000, importeDeclarado: 9900, diferencia: -100, importeEsperadoOriginal: null }],
      fechaRecalculo: null,
      idEmpleadoRecalculo: null,
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
      fechaRecalculo: null,
      idEmpleadoRecalculo: null,
    }
    const texto = textoPlano(reporteZ(detalle, CONTEXTO))
    expect(texto).toContain('Turno #8')
    expect(texto).toContain('Tickets: 4')
    expect(texto).not.toContain('declarado')
  })

  describe('marca de recálculo', () => {
    function detalleConRecalculo(fechaRecalculo: string | null): DetalleDeTurno {
      return {
        resumen: {
          idTurnoCaja: 8,
          idMedioAncla: 1,
          medios: [],
          cantidadTickets: 0,
          primerTicket: null,
          ultimoTicket: null,
          ingresosPorArea: [],
          egresos: { porCategoria: [], porArea: [], retiros: 0 },
        },
        tickets: [],
        gastos: [],
        fechaRecalculo,
        idEmpleadoRecalculo: fechaRecalculo ? 9 : null,
      }
    }

    it('imprime la línea cuando el detalle trae fechaRecalculo', () => {
      const iso = '2026-09-20T15:30:00Z'
      const texto = textoPlano(reporteZ(detalleConRecalculo(iso), CONTEXTO))
      expect(texto).toContain(`Arqueo recalculado el ${fechaHoraATexto(iso)}`)
    })

    it('no imprime la línea cuando fechaRecalculo es null', () => {
      expect(textoPlano(reporteZ(detalleConRecalculo(null), CONTEXTO))).not.toContain('Arqueo recalculado')
    })
  })

  it('termina con el corte parcial (GS V 66 0)', () => {
    const bytes = reporteZ(turnoFixture(), CONTEXTO)
    expect(Array.from(bytes.slice(-4))).toEqual([0x1d, 0x56, 0x42, 0x00])
  })
})

describe('pulsoDeCajon (stage-pos-retiros-y-cierre-por-retiro, etapa 5)', () => {
  it('es EXACTAMENTE inicializar + abrirCajon — nada de texto, avance de papel ni corte', () => {
    // ESC @ (inicializar, 2 bytes) + ESC p 0 25 250 (abrirCajon, 5 bytes) = 7 bytes, ni uno más.
    expect(Array.from(pulsoDeCajon())).toEqual([0x1b, 0x40, 0x1b, 0x70, 0x00, 25, 250])
  })
})

function retiroFixture(sobrescribir: Partial<{ fecha: string; importe: number }> = {}) {
  return { fecha: '2026-09-19T15:30:00-03:00', importe: 5000, ...sobrescribir }
}

describe('ticketRetiroDeEfectivo (stage-pos-retiros-y-cierre-por-retiro, etapa 5)', () => {
  it('incluye el título, la fecha, el importe y el vendedor (contexto.cajero)', () => {
    const texto = textoPlano(ticketRetiroDeEfectivo(retiroFixture(), CONTEXTO))
    expect(texto).toContain('RETIRO DE EFECTIVO')
    expect(texto).toContain('5.000,00')
    expect(texto).toContain('jperez')
  })

  it('incluye una línea de firma', () => {
    const texto = textoPlano(ticketRetiroDeEfectivo(retiroFixture(), CONTEXTO))
    expect(texto).toContain('Firma')
  })

  it('termina con el corte parcial (GS V 66 0)', () => {
    const bytes = ticketRetiroDeEfectivo(retiroFixture(), CONTEXTO)
    expect(Array.from(bytes.slice(-4))).toEqual([0x1d, 0x56, 0x42, 0x00])
  })

  /**
   * Cláusula bajo prueba: `ticketRetiroDeEfectivo` NUNCA llama `abrirCajon()` — el cajón para este
   * retiro ya se pulsó en su propio trabajo previo (`pulsoDeCajon`), un segundo pulso acá sería un
   * agujero de control de caja (mismo criterio que `REIMPRESION` en `ticketDeVenta`).
   *
   * Evidencia de mutación (mutation-proof-tests regla 2): agregando temporalmente un
   * `.abrirCajon()` antes de `.avanzar(3)` en `ticketRetiroDeEfectivo`, este test pasó a FALLAR
   * (`contieneComandoDeCajon` dio `true`); revertido el agregado, vuelve a pasar.
   */
  it('NUNCA pulsa el cajón (el pulso ya ocurrió en su propio trabajo previo)', () => {
    expect(contieneComandoDeCajon(ticketRetiroDeEfectivo(retiroFixture(), CONTEXTO))).toBe(false)
  })
})

function resumenCierreFixture(sobrescribir: Partial<ResumenDeCierrePorRetiro> = {}): ResumenDeCierrePorRetiro {
  return {
    idTurnoCaja: 501,
    puntoVenta: { id: 7, numero: 7, nombre: 'Local Centro' },
    fechaApertura: '2026-09-19T09:00:00-03:00',
    fechaCierre: '2026-09-19T18:00:00-03:00',
    vendedor: 'jperez',
    empleadoCierre: 'jperez',
    fondoInicial: 500,
    ventasPorMedio: [{ idMedioPago: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', importe: 8000 }],
    totalVentas: 8000,
    totalDigitales: 0,
    totalEfectivo: 8000,
    ventasACuentaCorriente: 0,
    retiros: [{ fecha: '2026-09-19T15:00:00-03:00', importe: 3000, motivo: 'Retiro de efectivo', empleado: 'jperez' }],
    totalRetiros: 3000,
    ventasEnEfectivoNetas: 8000,
    gastosEnEfectivo: 0,
    refuerzos: 0,
    diferencia: 0,
    ...sobrescribir,
  }
}

/** Saca los comandos ESC/GS (con sus parámetros) y deja solo el texto, con LF como separador. */
function lineasDeTexto(bytes: Uint8Array): string[] {
  let texto = ''
  for (let i = 0; i < bytes.length; i++) {
    const b = bytes[i]
    if (b === 0x1b) i += b === 0x1b && bytes[i + 1] === 0x40 ? 1 : 2
    else if (b === 0x1d) i += bytes[i + 1] === 0x56 ? 3 : 2
    else if (b === 0x0a) texto += '\n'
    else if (b >= 0x20 && b < 0x7f) texto += String.fromCharCode(b)
  }
  return texto.split('\n')
}

describe('lineasDeCierreDeTurno', () => {
  it('es el mismo contenido que cierreDeTurno manda a la térmica, línea por línea', () => {
    const resumen = resumenCierreFixture({ gastosEnEfectivo: 250, diferencia: -100 })

    const lineasDelNavegador = lineasDeCierreDeTurno(resumen, CONTEXTO).map((l) => l.texto.replace(/[^\x20-\x7e]/g, ''))
    const lineasDeLaTermica = lineasDeTexto(cierreDeTurno(resumen, CONTEXTO))

    expect(lineasDeLaTermica.slice(0, lineasDelNavegador.length)).toEqual(lineasDelNavegador)
    expect(lineasDelNavegador).toContain('CIERRE DE TURNO')
    expect(lineasDelNavegador).toContain('Turno #501')
    expect(lineasDelNavegador.some((l) => l.startsWith('Gastos en efectivo'))).toBe(true)
    expect(lineasDelNavegador.some((l) => l.startsWith('Faltante'))).toBe(true)
  })
})

describe('cierreDeTurno (stage-pos-retiros-y-cierre-por-retiro, etapa 5)', () => {
  it('incluye el turno, las fechas de apertura/cierre y el vendedor', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture(), CONTEXTO))
    expect(texto).toContain('CIERRE DE TURNO')
    expect(texto).toContain('Turno #501')
    expect(texto).toContain('Vendedor: jperez')
  })

  it('cierre por la MISMA persona que abrió: no repite el nombre en una segunda línea', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture({ vendedor: 'jperez', empleadoCierre: 'jperez' }), CONTEXTO))
    expect(texto).not.toContain('Cerrado por')
  })

  it('cierre por una persona DISTINTA de quien abrió: agrega la línea "Cerrado por"', () => {
    const texto = textoPlano(
      cierreDeTurno(resumenCierreFixture({ vendedor: 'jperez', empleadoCierre: 'mgomez' }), CONTEXTO),
    )
    expect(texto).toContain('Vendedor: jperez')
    expect(texto).toContain('Cerrado por: mgomez')
  })

  it('incluye cada medio de "ventas por medio de pago" y el total general', () => {
    const resumen = resumenCierreFixture({
      ventasPorMedio: [
        { idMedioPago: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', importe: 5000 },
        { idMedioPago: 2, nombre: 'Tarjeta', comportamiento: 'Electronico', importe: 3000 },
      ],
      totalVentas: 8000,
      totalDigitales: 3000,
      totalEfectivo: 5000,
    })
    const texto = textoPlano(cierreDeTurno(resumen, CONTEXTO))
    expect(texto).toContain('Efectivo')
    expect(texto).toContain('5.000,00')
    expect(texto).toContain('Tarjeta')
    expect(texto).toContain('3.000,00')
    expect(texto).toContain('Total general')
    expect(texto).toContain('8.000,00')
  })

  describe('subtotales por clasificación', () => {
    const resumenMixto = () =>
      resumenCierreFixture({
        ventasPorMedio: [
          { idMedioPago: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', importe: 5000 },
          { idMedioPago: 2, nombre: 'Tarjeta', comportamiento: 'Electronico', importe: 3000 },
        ],
        totalVentas: 8000,
        totalDigitales: 3000,
        totalEfectivo: 5000,
        ventasACuentaCorriente: 700,
      })

    it('imprime separador, digitales, efectivo y total general alineados, y el fiado aparte después del total', () => {
      const lineas = lineasDeCierreDeTurno(resumenMixto(), CONTEXTO).map((l) => l.texto)
      const iTarjeta = lineas.findIndex((l) => l.startsWith('Tarjeta'))
      const iFiado = lineas.findIndex((l) => l.startsWith('Fiado en cuenta corriente'))
      const bloque = lineas.slice(iTarjeta + 1, iFiado + 1).map((l) => l.replace(/\s+/g, ' '))

      expect(bloque[0]).toMatch(/^-+$/)
      expect(bloque.slice(1)).toEqual([
        'Total digitales $ 3.000,00',
        'Total efectivo $ 5.000,00',
        'Total general $ 8.000,00',
        '',
        'Fiado en cuenta corriente $ 700,00',
      ])
      const anchos = lineas.filter((l) => /^(Total (digitales|efectivo|general)|Fiado)/.test(l)).map((l) => l.length)
      expect(new Set(anchos).size).toBe(1)
      expect(lineas.filter((l) => l.includes('Total general'))).toHaveLength(1)
      expect(lineas.some((l) => l.startsWith('Total ventas'))).toBe(false)
    })

    it('omite el fiado en cuenta corriente cuando es cero', () => {
      const texto = textoPlano(cierreDeTurno(resumenCierreFixture(), CONTEXTO))
      expect(texto).toContain('Total digitales')
      expect(texto).toContain('Total efectivo')
      expect(texto).not.toContain('Fiado')
    })

    it.each([
      ['payload anterior al desglose', {}],
      ['payload parcial (falta un importe)', { totalEfectivo: undefined }],
      ['payload con un importe que no es número', { totalDigitales: Number.NaN }],
    ])('%s: no imprime subtotales ni fiado y conserva el total general', (_nombre, defecto) => {
      const base: Partial<ResumenDeCierrePorRetiro> = resumenMixto()
      if (Object.keys(defecto).length === 0) {
        delete base.totalDigitales
        delete base.totalEfectivo
        delete base.ventasACuentaCorriente
      } else {
        Object.assign(base, defecto)
      }
      const texto = textoPlano(cierreDeTurno(base as ResumenDeCierrePorRetiro, CONTEXTO))
      expect(texto).not.toContain('Total digitales')
      expect(texto).not.toContain('Total efectivo')
      expect(texto).not.toContain('Fiado')
      expect(texto).not.toContain('undefined')
      expect(texto).not.toContain('NaN')
      expect(texto).toContain('Total general')
    })
  })

  it('imprime cada retiro en UNA línea: hora, rótulo corto y importe, sin motivo largo ni empleado', () => {
    const local = (h: number, m: number) => new Date(2026, 8, 19, h, m).toISOString()
    const resumen = resumenCierreFixture({
      retiros: [
        { fecha: local(16, 5), importe: 1000, motivo: 'Retiro de efectivo', empleado: 'jperez' },
        { fecha: local(18, 0), importe: 5000, motivo: 'Retiro de cierre de turno', empleado: 'jperez' },
        { fecha: local(19, 30), importe: 250, motivo: 'Pago de flete', empleado: 'jperez' },
      ],
      totalRetiros: 6250,
    })
    const lineas = lineasDeCierreDeTurno(resumen, CONTEXTO).map((l) => l.texto)
    const desde = lineas.indexOf('Retiros:')
    const hasta = lineas.findIndex((l) => l.startsWith('Total retiros'))

    expect(lineas.slice(desde + 1, hasta)).toEqual([
      dosColumnas('16:05 Efectivo', '$ 1.000,00'),
      dosColumnas('18:00 Cierre', '$ 5.000,00'),
      dosColumnas('19:30 Pago de flete', '$ 250,00'),
    ])
    expect(lineas.join('|')).not.toContain('jperez (')
    expect(lineas.join('|')).not.toContain('Retiro de')
    expect(lineas[hasta]).toBe(dosColumnas('Total retiros', '$ 6.250,00'))
  })

  it('un motivo libre largo se trunca y el importe queda siempre en la misma línea', () => {
    const motivo = 'Pago al proveedor de limpieza por el servicio extraordinario del mes'
    const resumen = resumenCierreFixture({
      retiros: [{ fecha: new Date(2026, 8, 19, 9, 0).toISOString(), importe: 123456.78, motivo, empleado: 'jperez' }],
      totalRetiros: 123456.78,
    })
    const lineas = lineasDeCierreDeTurno(resumen, CONTEXTO).map((l) => l.texto)
    const linea = lineas[lineas.indexOf('Retiros:') + 1]

    expect(linea).toHaveLength(COLUMNAS_FUENTE_A)
    expect(linea.startsWith('09:00 Pago al proveedor')).toBe(true)
    expect(linea.endsWith('$ 123.456,78')).toBe(true)
  })

  it('sin retiros en el turno, lo dice explícitamente en vez de una sección vacía', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture({ retiros: [], totalRetiros: 0 }), CONTEXTO))
    expect(texto).toContain('Sin retiros en el turno.')
  })

  it('incluye "Ventas en efectivo"', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture({ ventasEnEfectivoNetas: 4500 }), CONTEXTO))
    expect(texto).toContain('Ventas en efectivo')
    expect(texto).toContain('4.500,00')
  })

  it('"Gastos en efectivo" == 0: la línea NO aparece', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture({ gastosEnEfectivo: 0 }), CONTEXTO))
    expect(texto).not.toContain('Gastos en efectivo')
  })

  it('"Gastos en efectivo" != 0: la línea aparece con el importe', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture({ gastosEnEfectivo: 750 }), CONTEXTO))
    expect(texto).toContain('Gastos en efectivo')
    expect(texto).toContain('750,00')
  })

  it('"Refuerzos" == 0: la línea NO aparece', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture({ refuerzos: 0 }), CONTEXTO))
    expect(texto).not.toContain('Refuerzos')
  })

  it('"Refuerzos" != 0: la línea aparece con el importe', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture({ refuerzos: 1200 }), CONTEXTO))
    expect(texto).toContain('Refuerzos')
    expect(texto).toContain('1.200,00')
  })

  it('incluye el fondo inicial, aclarando que queda en caja', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture({ fondoInicial: 500 }), CONTEXTO))
    expect(texto).toContain('Fondo inicial')
    expect(texto).toContain('queda en caja')
    expect(texto).toContain('500,00')
  })

  /**
   * Cláusula bajo prueba: el rótulo de `diferencia` — "Sobrante" si > 0, "Faltante" si < 0, "Sin
   * diferencia" si == 0 — y que el importe mostrado es el VALOR ABSOLUTO (nunca "-$100,00" al
   * lado de "Faltante").
   *
   * Evidencia de mutación (mutation-proof-tests regla 2): invirtiendo temporalmente la condición a
   * `resumen.diferencia < 0 ? 'Sobrante' : resumen.diferencia > 0 ? 'Faltante' : 'Sin diferencia'`,
   * los tres tests de abajo pasaron a FALLAR (sobrante/faltante intercambiados); revertida la
   * inversión, los tres vuelven a pasar.
   */
  it('diferencia > 0: rotula "Sobrante" con el valor absoluto', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture({ diferencia: 150 }), CONTEXTO))
    expect(texto).toContain('Sobrante')
    expect(texto).not.toContain('Faltante')
    expect(texto).toContain('150,00')
  })

  it('diferencia < 0: rotula "Faltante" con el valor absoluto (nunca el signo "-")', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture({ diferencia: -150 }), CONTEXTO))
    expect(texto).toContain('Faltante')
    expect(texto).not.toContain('Sobrante')
    expect(texto).toContain('150,00')
    expect(texto).not.toContain('-150,00')
    expect(texto).not.toContain('-$ 150,00')
  })

  it('diferencia == 0: rotula "Sin diferencia" (ni Sobrante ni Faltante)', () => {
    const texto = textoPlano(cierreDeTurno(resumenCierreFixture({ diferencia: 0 }), CONTEXTO))
    expect(texto).toContain('Sin diferencia')
    expect(texto).not.toContain('Sobrante')
    expect(texto).not.toContain('Faltante')
  })

  it('termina con el corte parcial (GS V 66 0)', () => {
    const bytes = cierreDeTurno(resumenCierreFixture(), CONTEXTO)
    expect(Array.from(bytes.slice(-4))).toEqual([0x1d, 0x56, 0x42, 0x00])
  })

  /**
   * Cláusula bajo prueba: `cierreDeTurno` NUNCA llama `abrirCajon()` — el cajón para este cierre
   * ya se pulsó en su propio trabajo previo (`pulsoDeCajon`), antes de que el cajero cuente el
   * efectivo a retirar.
   *
   * Evidencia de mutación (mutation-proof-tests regla 2): agregando temporalmente un
   * `.abrirCajon()` antes de `.avanzar(2)` en `cierreDeTurno`, este test pasó a FALLAR; revertido
   * el agregado, vuelve a pasar.
   */
  it('NUNCA pulsa el cajón (el pulso ya ocurrió en su propio trabajo previo)', () => {
    expect(contieneComandoDeCajon(cierreDeTurno(resumenCierreFixture(), CONTEXTO))).toBe(false)
  })
})
