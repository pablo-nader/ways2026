import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  ENCABEZADO_DE_ORIGEN,
  RESULTADOS_EN_ORDEN,
  TAMANO_MAXIMO_DE_ARCHIVO,
  avisosDeDetalle,
  claseDeEstadoDePropuesta,
  claseDeResultado,
  clienteDeConciliacionMercadoPago,
  cuentasDeDestino,
  esFilaConfirmable,
  etiquetaDeEstadoDePropuesta,
  etiquetaDeTipoDePropuesta,
  etiquetaDeResultado,
  importesDifieren,
  lineasDeConfirmacion,
  mensajeDeErrorDeConciliacion,
  motivoDeRangoInvalido,
  rangoDeFilas,
  rangoUltimosDias,
  sePuedeAplicar,
  resumenOrdenado,
  textoDeEfecto,
  textoDeResumen,
  totalDePaginas,
  totalesDelResumen,
  ultimaConciliacion,
  validarArchivo,
  type CandidatoDeConfirmacion,
  type ConciliacionListado,
  type FilaDeConciliacion,
  type ResumenDeResultado,
} from './conciliacionMercadoPago'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPostFormularioMock = vi.fn()

vi.mock('./cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...args),
    post: (...args: unknown[]) => apiPostMock(...args),
    postFormulario: (...args: unknown[]) => apiPostFormularioMock(...args),
  },
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.estado = estado
      this.codigo = codigo
    }
  },
  ErrorDeRed: class ErrorDeRedMock extends Error {},
}))

const { ErrorApi, ErrorDeRed } = await import('./cliente')

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPostFormularioMock.mockReset()
})

function resumen(resultado: string, cantidad: number, importe = 0, comision = 0, neto = 0): ResumenDeResultado {
  return { resultado, cantidad, importe, comision, neto }
}

function fila(sobrescribir: Partial<FilaDeConciliacion> = {}): FilaDeConciliacion {
  return {
    id: 5,
    numeroLinea: 12,
    resultado: 'sin_cobro_en_ways',
    sourceId: 'S-1',
    externalReference: null,
    tipoTransaccion: 'SETTLEMENT',
    importe: 1500,
    comision: -50,
    neto: 1450,
    fechaTransaccion: '2026-10-30T15:00:00Z',
    fechaLiberacion: null,
    externalPosId: null,
    motivo: null,
    idCobroMercadoPago: null,
    idCobro: null,
    candidatos: [candidato()],
    aplicadaAlCobro: false,
    comisionGuardada: null,
    netoGuardado: null,
    ...sobrescribir,
  }
}

function candidato(sobrescribir: Partial<CandidatoDeConfirmacion> = {}): CandidatoDeConfirmacion {
  return { idCobro: 90, idComprobanteVenta: 4, numeroVisible: '0001-00000042', fecha: '2026-10-30T14:00:00Z', importe: 1500, referencia: null, ...sobrescribir }
}

describe('clienteDeConciliacionMercadoPago', () => {
  it('sube el archivo en multipart con el encabezado de origen web', async () => {
    apiPostFormularioMock.mockResolvedValue({})
    const archivo = new File(['a,b'], 'reporte.csv', { type: 'text/csv' })

    await clienteDeConciliacionMercadoPago.subir(7, archivo)

    const [ruta, formulario, encabezados] = apiPostFormularioMock.mock.calls[0] as [string, FormData, Record<string, string>]
    expect(ruta).toBe('/mercadopago/conciliaciones?idEmpresa=7')
    expect((formulario.get('archivo') as File).name).toBe('reporte.csv')
    expect(encabezados).toEqual({ [ENCABEZADO_DE_ORIGEN]: 'web' })
    expect(ENCABEZADO_DE_ORIGEN).toBe('X-Ways-Origen')
  })

  it('arma las rutas de lectura con sus parámetros y omite el filtro vacío', async () => {
    apiGetMock.mockResolvedValue({})

    await clienteDeConciliacionMercadoPago.listar(7)
    await clienteDeConciliacionMercadoPago.detalle(3, { resultado: null, pagina: 2, tamano: 100 })
    await clienteDeConciliacionMercadoPago.detalle(3, { resultado: 'retiro', pagina: 1, tamano: 100 })
    await clienteDeConciliacionMercadoPago.cobrosSinVenta(7)
    await clienteDeConciliacionMercadoPago.pagosSinVerificar(7, '2026-10-01', '2026-10-31')

    expect(apiGetMock.mock.calls.map((c) => c[0])).toEqual([
      '/mercadopago/conciliaciones?idEmpresa=7',
      '/mercadopago/conciliaciones/3?pagina=2&tamano=100',
      '/mercadopago/conciliaciones/3?resultado=retiro&pagina=1&tamano=100',
      '/mercadopago/cobros-sin-venta?idEmpresa=7',
      '/mercadopago/pagos-sin-verificar?idEmpresa=7&desde=2026-10-01&hasta=2026-10-31',
    ])
  })

  it('confirma con el idCobro del pago en el cuerpo', async () => {
    apiPostMock.mockResolvedValue(undefined)

    await clienteDeConciliacionMercadoPago.confirmar(3, 5, 90)

    expect(apiPostMock).toHaveBeenCalledWith('/mercadopago/conciliaciones/3/filas/5/confirmar', { idCobro: 90 })
  })
})

describe('postFormulario del cliente real', () => {
  const fetchOriginal = globalThis.fetch

  afterEach(() => {
    globalThis.fetch = fetchOriginal
  })

  it('manda el FormData sin fijar Content-Type (lo arma el navegador con su límite) y conserva el encabezado propio', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }))
    globalThis.fetch = fetchMock as unknown as typeof fetch
    const real = await vi.importActual<typeof import('./cliente')>('./cliente')
    const formulario = new FormData()
    formulario.append('archivo', new File(['x'], 'a.csv'))

    await real.api.postFormulario('/x', formulario, { 'X-Ways-Origen': 'web' })

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const encabezados = init.headers as Record<string, string>
    expect(init.method).toBe('POST')
    expect(init.body).toBe(formulario)
    expect(encabezados['X-Ways-Origen']).toBe('web')
    expect(Object.keys(encabezados).map((k) => k.toLowerCase())).not.toContain('content-type')
  })
})

describe('resultados', () => {
  it('etiqueta cada resultado conocido y deja pasar uno desconocido', () => {
    for (const r of RESULTADOS_EN_ORDEN) expect(etiquetaDeResultado(r)).not.toBe(r)
    expect(etiquetaDeResultado('sin_cobro_en_ways')).toBe('Sin cobro en Ways')
    expect(etiquetaDeResultado('nuevo_resultado')).toBe('nuevo_resultado')
  })

  it('pinta verde lo conciliado, ámbar lo que pide mirar, rojo lo inválido y gris el resto', () => {
    expect(claseDeResultado('conciliado')).toBe('text-bg-success')
    expect(claseDeResultado('importe_distinto')).toBe('text-bg-warning')
    expect(claseDeResultado('invalida')).toBe('text-bg-danger')
    expect(claseDeResultado('duplicada')).toBe('text-bg-secondary')
  })

  it('suma los totales del resumen', () => {
    const totales = totalesDelResumen([resumen('conciliado', 3, 3000, -150, 2850), resumen('sin_venta', 1, 500, -25, 475), resumen('duplicada', 2)])

    expect(totales).toEqual({ cantidad: 6, importe: 3500, comision: -175, neto: 3325 })
    expect(totalesDelResumen([])).toEqual({ cantidad: 0, importe: 0, comision: 0, neto: 0 })
  })

  it('ordena el resumen por el orden de resultados y deja los desconocidos al final', () => {
    const ordenado = resumenOrdenado([resumen('invalida', 1), resumen('raro', 1), resumen('conciliado', 1), resumen('retiro', 1)])

    expect(ordenado.map((r) => r.resultado)).toEqual(['conciliado', 'retiro', 'invalida', 'raro'])
  })

  it('describe el conteo en una línea sin los resultados vacíos', () => {
    expect(textoDeResumen([resumen('sin_venta', 1), resumen('conciliado', 12), resumen('retiro', 0)])).toBe('Conciliado 12 · Cobro sin venta 1')
    expect(textoDeResumen([])).toBe('—')
  })
})

describe('validarArchivo', () => {
  it('rechaza sin archivo, vacío y por encima de 10 MB; acepta el borde', () => {
    expect(validarArchivo(null).valido).toBe(false)
    expect(validarArchivo({ name: 'a.csv', size: 0 })).toEqual({ valido: false, mensaje: 'El archivo está vacío.' })
    expect(validarArchivo({ name: 'a.csv', size: TAMANO_MAXIMO_DE_ARCHIVO + 1 }).valido).toBe(false)
    expect(validarArchivo({ name: 'a.csv', size: TAMANO_MAXIMO_DE_ARCHIVO })).toEqual({ valido: true })
  })
})

describe('paginado', () => {
  it('calcula páginas y el rango de filas mostrado', () => {
    expect(totalDePaginas(0, 100)).toBe(1)
    expect(totalDePaginas(100, 100)).toBe(1)
    expect(totalDePaginas(101, 100)).toBe(2)
    expect(rangoDeFilas(0, 1, 100)).toEqual({ desde: 0, hasta: 0 })
    expect(rangoDeFilas(250, 3, 100)).toEqual({ desde: 201, hasta: 250 })
  })
})

describe('confirmación', () => {
  it('solo es confirmable una fila sin cobro en Ways, sin confirmar y con candidatos', () => {
    expect(esFilaConfirmable(fila())).toBe(true)
    expect(esFilaConfirmable(fila({ resultado: 'conciliado' }))).toBe(false)
    expect(esFilaConfirmable(fila({ idCobro: 90 }))).toBe(false)
    expect(esFilaConfirmable(fila({ candidatos: [] }))).toBe(false)
  })

  it('arma las líneas del diálogo con importes y fechas de la fila y del pago', () => {
    const lineas = lineasDeConfirmacion(fila(), candidato({ importe: 1499 }))

    expect(lineas.map((l) => l.etiqueta)).toEqual([
      'Fila del reporte',
      'Importe del reporte',
      'Fecha del reporte',
      'Venta',
      'Importe del pago',
      'Fecha del pago',
    ])
    expect(lineas[0].valor).toBe('Línea 12 · S-1')
    expect(lineas[1].valor).toContain('1.500')
    expect(lineas[3].valor).toBe('0001-00000042')
    expect(lineas[4].valor).toContain('1.499')
  })

  it('detecta importes distintos al centavo y no avisa sin importe en la fila', () => {
    expect(importesDifieren(fila({ importe: 1500 }), candidato({ importe: 1500 }))).toBe(false)
    expect(importesDifieren(fila({ importe: 1500 }), candidato({ importe: 1500.01 }))).toBe(true)
    expect(importesDifieren(fila({ importe: null }), candidato())).toBe(false)
  })
})

describe('rangos', () => {
  it('calcula los últimos N días inclusive con la fecha inyectada', () => {
    expect(rangoUltimosDias(30, new Date(2026, 10, 2))).toEqual({ desde: '2026-10-04', hasta: '2026-11-02' })
  })

  it('rechaza fechas vacías, invertidas y rangos de más de 366 días', () => {
    expect(motivoDeRangoInvalido('', '2026-01-01')).toContain('Elegí')
    expect(motivoDeRangoInvalido('2026-02-01', '2026-01-01')).toContain('posterior')
    expect(motivoDeRangoInvalido('2025-01-01', '2026-01-03')).toContain('366')
    expect(motivoDeRangoInvalido('2026-01-01', '2026-01-31')).toBeNull()
  })
})

describe('ultimaConciliacion', () => {
  it('elige la subida más reciente sin depender del orden de la lista', () => {
    const c = (id: number, subidaEl: string) => ({ id, subidaEl }) as ConciliacionListado

    expect(ultimaConciliacion([c(1, '2026-10-01T10:00:00Z'), c(2, '2026-10-03T10:00:00Z'), c(3, '2026-10-02T10:00:00Z')])?.id).toBe(2)
    expect(ultimaConciliacion([])).toBeNull()
  })
})

describe('mensajeDeErrorDeConciliacion', () => {
  const api = (estado: number, codigo: string, mensaje = 'del servidor') => new ErrorApi(estado, codigo, mensaje)

  it.each([
    [403, 'solicitud_no_permitida', 'solo se acepta desde la web'],
    [400, 'conciliacion_archivo_requerido', 'Elegí el archivo'],
    [413, 'conciliacion_archivo_demasiado_grande', '10 MB'],
    [400, 'conciliacion_reporte_invalido', 'reporte de transacciones'],
    [409, 'conciliacion_ya_importada', 'ya se importó'],
    [400, 'conciliacion_resultado_invalido', 'no es válido'],
    [400, 'conciliacion_paginacion_invalida', 'página'],
    [400, 'conciliacion_pago_invalido', 'pago elegido'],
    [409, 'conciliacion_fila_no_confirmable', 'sin cobro en Ways'],
    [409, 'conciliacion_fila_ya_confirmada', 'ya fue confirmada'],
    [409, 'conciliacion_pago_no_esta_sin_verificar', 'ya no está sin verificar'],
    [409, 'conciliacion_pago_ya_verificado', 'ya fue verificado'],
    [409, 'venta_anulada', 'anulada'],
    [409, 'importe_distinto', 'no coincide'],
  ])('%i %s tiene copia propia', (estado, codigo, fragmento) => {
    const mensaje = mensajeDeErrorDeConciliacion(api(estado, codigo), 'subida')

    expect(mensaje).toContain(fragmento)
    expect(mensaje).not.toBe('del servidor')
  })

  it('un código desconocido usa el mensaje del servidor, y 404/403 tienen su copia', () => {
    expect(mensajeDeErrorDeConciliacion(api(500, 'raro'), 'lectura')).toBe('del servidor')
    expect(mensajeDeErrorDeConciliacion(api(404, 'no_encontrado'), 'confirmacion')).toContain('ya no existen')
    expect(mensajeDeErrorDeConciliacion(api(403, 'prohibido'), 'lectura')).toContain('permiso')
  })

  it('la caída de red en una escritura es incierta y en una lectura es falta de conexión', () => {
    expect(mensajeDeErrorDeConciliacion(new ErrorDeRed('x'), 'subida')).toContain('Revisá la solapa Conciliaciones')
    expect(mensajeDeErrorDeConciliacion(new ErrorDeRed('x'), 'confirmacion')).toContain('Recargá')
    expect(mensajeDeErrorDeConciliacion(new ErrorDeRed('x'), 'lectura')).toContain('Sin conexión')
  })

  it('un error que no es de la API cae al texto genérico del contexto', () => {
    expect(mensajeDeErrorDeConciliacion(new Error('x'), 'subida')).toBe('No se pudo importar el archivo.')
    expect(mensajeDeErrorDeConciliacion(null, 'confirmacion')).toBe('No se pudo confirmar la fila.')
  })
})

describe('propuestas de tesorería', () => {
  const api = (estado: number, codigo: string, mensaje = 'del servidor') => new ErrorApi(estado, codigo, mensaje)

  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
  })

  it('el cliente lee, aplica con el destino y descarta con cuerpo vacío', async () => {
    apiGetMock.mockResolvedValue([])
    apiPostMock.mockResolvedValue({})

    await clienteDeConciliacionMercadoPago.propuestas(3)
    await clienteDeConciliacionMercadoPago.aplicarPropuesta(3, 11, 71)
    await clienteDeConciliacionMercadoPago.aplicarPropuesta(3, 12, null)
    await clienteDeConciliacionMercadoPago.descartarPropuesta(3, 11)

    expect(apiGetMock).toHaveBeenCalledWith('/mercadopago/conciliaciones/3/propuestas')
    expect(apiPostMock).toHaveBeenNthCalledWith(1, '/mercadopago/conciliaciones/3/propuestas/11/aplicar', { idCuentaDestino: 71 })
    expect(apiPostMock).toHaveBeenNthCalledWith(2, '/mercadopago/conciliaciones/3/propuestas/12/aplicar', { idCuentaDestino: null })
    expect(apiPostMock).toHaveBeenNthCalledWith(3, '/mercadopago/conciliaciones/3/propuestas/11/descartar', {})
  })

  it('el efecto distingue débito, crédito y retiro', () => {
    const base = { nombreCuentaOrigen: 'Mercado Pago', nombreCuentaDestino: null }
    expect(textoDeEfecto({ ...base, tipo: 'diferencia_comision', importe: -1.5 })).toMatch(/^Debita .*1,50 de Mercado Pago$/)
    expect(textoDeEfecto({ ...base, tipo: 'diferencia_comision', importe: 2 })).toMatch(/^Acredita .*2,00 en Mercado Pago$/)
    expect(textoDeEfecto({ ...base, tipo: 'retiro', importe: 40 })).toMatch(/^Pasa .*40,00 de Mercado Pago a la cuenta que elijas$/)
    expect(textoDeEfecto({ ...base, nombreCuentaDestino: 'Banco', tipo: 'retiro', importe: 40 })).toMatch(/a Banco$/)
    expect(textoDeEfecto({ tipo: 'diferencia_comision', importe: -1, nombreCuentaOrigen: null, nombreCuentaDestino: null })).toContain(
      'la cuenta de Mercado Pago',
    )
  })

  it('solo se aplica una pendiente y, siendo retiro, con cuenta de origen', () => {
    expect(sePuedeAplicar({ estado: 'pendiente', tipo: 'diferencia_comision', idCuentaOrigen: 1 })).toBe(true)
    expect(sePuedeAplicar({ estado: 'pendiente', tipo: 'retiro', idCuentaOrigen: 1 })).toBe(true)
    expect(sePuedeAplicar({ estado: 'pendiente', tipo: 'retiro', idCuentaOrigen: null })).toBe(false)
    expect(sePuedeAplicar({ estado: 'aplicada', tipo: 'diferencia_comision', idCuentaOrigen: 1 })).toBe(false)
    expect(sePuedeAplicar({ estado: 'descartada', tipo: 'retiro', idCuentaOrigen: 1 })).toBe(false)
  })

  it('las cuentas de destino son las activas de la empresa, sin el origen, por nombre', () => {
    const cuentas = [
      { id: 1, idEmpresa: 1, nombre: 'Zeta', activo: true },
      { id: 2, idEmpresa: 1, nombre: 'Alfa', activo: true },
      { id: 3, idEmpresa: 1, nombre: 'Baja', activo: false },
      { id: 4, idEmpresa: 2, nombre: 'Ajena', activo: true },
      { id: 5, idEmpresa: 1, nombre: 'Origen', activo: true },
    ]
    expect(cuentasDeDestino(cuentas, 1, 5).map((c) => c.id)).toEqual([2, 1])
    expect(cuentasDeDestino(cuentas, 1, null).map((c) => c.id)).toEqual([2, 5, 1])
  })

  it('los avisos dicen solo lo que quedó fuera del cálculo', () => {
    expect(avisosDeDetalle({ cobrosSinCierre: 0, cobrosSinComisionReal: 0 })).toEqual([])
    expect(avisosDeDetalle({ cobrosSinCierre: 2, cobrosSinComisionReal: 0 })).toHaveLength(1)
    expect(avisosDeDetalle({ cobrosSinCierre: 2, cobrosSinComisionReal: 1 })).toHaveLength(2)
  })

  it('las etiquetas y clases conocen los estados y el tipo, y caen al texto crudo', () => {
    expect(etiquetaDeTipoDePropuesta('retiro')).toBe('Retiro')
    expect(etiquetaDeTipoDePropuesta('raro')).toBe('raro')
    expect(etiquetaDeEstadoDePropuesta('descartada')).toBe('Descartada')
    expect(claseDeEstadoDePropuesta('aplicada')).toBe('bg-success')
    expect(claseDeEstadoDePropuesta('descartada')).toBe('bg-secondary')
    expect(claseDeEstadoDePropuesta('pendiente')).toContain('bg-warning')
  })

  it('los códigos de la resolución tienen su copia y la red caída es incierta', () => {
    expect(mensajeDeErrorDeConciliacion(api(409, 'propuesta_ya_aplicada'), 'resolucion')).toBe('La propuesta ya se aplicó.')
    expect(mensajeDeErrorDeConciliacion(api(409, 'periodo_conciliado'), 'resolucion')).toContain('ya conciliado')
    expect(mensajeDeErrorDeConciliacion(api(404, 'no_encontrado'), 'resolucion')).toContain('ya no existen')
    expect(mensajeDeErrorDeConciliacion(new ErrorDeRed('x'), 'resolucion')).toContain('No se pudo confirmar si la propuesta')
    expect(mensajeDeErrorDeConciliacion(null, 'resolucion')).toBe('No se pudo resolver la propuesta.')
  })
})
