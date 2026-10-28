import { describe, expect, it } from 'vitest'
import {
  descriptorAreas,
  descriptorGrupos,
  descriptorListasPrecio,
  descriptorMarcas,
  avisoDeLasCuentas,
  descriptorMediosPago,
  columnasDeCatalogo,
  textoDeColumna,
} from './catalogos'
import type { DescriptorDeCatalogo } from './catalogos'
import { filtrarFilas } from '../componentes/grilla/filtros'
import { ordenarFilas } from '../componentes/grilla/ordenamiento'
import type { CatalogoListado, ListaPrecioListado, MedioPagoAdministrado } from './tipos'

function listaFixture(sobrescribir: Partial<ListaPrecioListado> = {}): ListaPrecioListado {
  return {
    id: 1,
    nombre: 'Lista mayorista',
    activo: true,
    idEmpresa: null,
    esDefault: false,
    modo: 'Fija',
    idListaBase: null,
    porcentaje: null,
    ...sobrescribir,
  }
}

// fix/web-bajas-catalogos: cada descriptor tiene que nombrar su propio sujeto de baja, con el
// MISMO texto que el `SujetoDeBaja` del servicio del catálogo del lado del servidor
// (`ServicioDeAreas`, `ServicioDeMarcas`, etc.) — si no coinciden, la puerta de confirmación y la
// copia del 409 nombran la entidad distinto.
describe('sujetoDeBaja de cada descriptor de catálogo', () => {
  it('nombra cada catálogo con el artículo correcto, igual que el servicio del servidor', () => {
    expect(descriptorAreas.sujetoDeBaja).toBe('el área')
    expect(descriptorMarcas.sujetoDeBaja).toBe('la marca')
    expect(descriptorGrupos.sujetoDeBaja).toBe('el grupo')
    expect(descriptorMediosPago.sujetoDeBaja).toBe('el medio de pago')
    expect(descriptorListasPrecio.sujetoDeBaja).toBe('la lista de precios')
  })
})

describe('descriptorGrupos.aAlta — separador decimal', () => {
  it('el margen con coma se lee igual que con punto, y vacío es null', () => {
    expect(descriptorGrupos.aAlta('G', true, { margen: '12,5' }, null).margen).toBe(12.5)
    expect(descriptorGrupos.aAlta('G', true, { margen: '12.5' }, null).margen).toBe(12.5)
    expect(descriptorGrupos.aAlta('G', true, { margen: '' }, null).margen).toBeNull()
  })
})

describe('descriptorListasPrecio.aAlta', () => {
  it('fuerza idListaBase y porcentaje a null en modo Fija aunque el formulario tenga valores residuales', () => {
    const alta = descriptorListasPrecio.aAlta('Lista A', true, {
      esDefault: false,
      modo: 'Fija',
      idListaBase: '3',
      porcentaje: '10',
    }, null)

    expect(alta.idListaBase).toBeNull()
    expect(alta.porcentaje).toBeNull()
  })

  it('convierte idListaBase y porcentaje a número en modo Derivada', () => {
    const alta = descriptorListasPrecio.aAlta('Lista B', true, {
      esDefault: false,
      modo: 'Derivada',
      idListaBase: '3',
      porcentaje: '10.5',
    }, null)

    expect(alta.idListaBase).toBe(3)
    expect(alta.porcentaje).toBe(10.5)
  })

  it('convierte los strings vacíos a null en modo Derivada', () => {
    const alta = descriptorListasPrecio.aAlta('Lista C', true, {
      esDefault: false,
      modo: 'Derivada',
      idListaBase: '',
      porcentaje: '',
    }, null)

    expect(alta.idListaBase).toBeNull()
    expect(alta.porcentaje).toBeNull()
  })

  it('booleaniza esDefault', () => {
    const altaTrue = descriptorListasPrecio.aAlta('Lista D', true, {
      esDefault: 'algo' as unknown as boolean,
      modo: 'Fija',
      idListaBase: '',
      porcentaje: '',
    }, null)
    const altaFalse = descriptorListasPrecio.aAlta('Lista E', true, {
      esDefault: false,
      modo: 'Fija',
      idListaBase: '',
      porcentaje: '',
    }, null)

    expect(altaTrue.esDefault).toBe(true)
    expect(altaFalse.esDefault).toBe(false)
  })

  it('pasa nombre y activo sin transformar y la empresa que recibe', () => {
    const alta = descriptorListasPrecio.aAlta('Lista F', false, {
      esDefault: false,
      modo: 'Fija',
      idListaBase: '',
      porcentaje: '',
    }, 9)

    expect(alta.nombre).toBe('Lista F')
    expect(alta.activo).toBe(false)
    expect(alta.idEmpresa).toBe(9)
  })
})

describe('descriptorListasPrecio.aValores', () => {
  it('mapea idListaBase y porcentaje null a string vacío', () => {
    const valores = descriptorListasPrecio.aValores(listaFixture({ idListaBase: null, porcentaje: null }))

    expect(valores.idListaBase).toBe('')
    expect(valores.porcentaje).toBe('')
  })

  it('mapea idListaBase y porcentaje no nulos a String(...)', () => {
    const valores = descriptorListasPrecio.aValores(listaFixture({ idListaBase: 7, porcentaje: 12.5 }))

    expect(valores.idListaBase).toBe('7')
    expect(valores.porcentaje).toBe('12.5')
  })

  it('pasa esDefault y modo sin transformar', () => {
    const valores = descriptorListasPrecio.aValores(listaFixture({ esDefault: true, modo: 'Derivada' }))

    expect(valores.esDefault).toBe(true)
    expect(valores.modo).toBe('Derivada')
  })
})

describe('descriptorListasPrecio campo idListaBase — opcionesDesdeListado', () => {
  const campoIdListaBase = descriptorListasPrecio.campos.find((c) => c.clave === 'idListaBase')
  if (!campoIdListaBase?.opcionesDesdeListado) {
    throw new Error('El campo idListaBase debe declarar opcionesDesdeListado')
  }
  const opcionesDesdeListado = campoIdListaBase.opcionesDesdeListado

  const listas: ListaPrecioListado[] = [
    listaFixture({ id: 1, nombre: 'Fija activa', activo: true, modo: 'Fija' }),
    listaFixture({ id: 2, nombre: 'Fija inactiva', activo: false, modo: 'Fija' }),
    listaFixture({ id: 3, nombre: 'Derivada activa', activo: true, modo: 'Derivada' }),
    listaFixture({ id: 4, nombre: 'Otra fija activa', activo: true, modo: 'Fija' }),
  ]

  it('filtra las listas inactivas', () => {
    const opciones = opcionesDesdeListado(listas, null)

    expect(opciones.some((o) => o.valor === '2')).toBe(false)
  })

  it('filtra las listas en modo Derivada', () => {
    const opciones = opcionesDesdeListado(listas, null)

    expect(opciones.some((o) => o.valor === '3')).toBe(false)
  })

  it('excluye la lista actual cuando idActual está definido (autoexclusión al editar)', () => {
    const opciones = opcionesDesdeListado(listas, 1)

    expect(opciones.map((o) => o.valor)).toEqual(['4'])
  })

  it('incluye todas las Fija activas cuando idActual es null (alta nueva)', () => {
    const opciones = opcionesDesdeListado(listas, null)

    expect(opciones.map((o) => o.valor)).toEqual(['1', '4'])
  })

  it('mapea cada opción a { valor: String(id), etiqueta: nombre }', () => {
    const opciones = opcionesDesdeListado(listas, null)

    expect(opciones).toEqual([
      { valor: '1', etiqueta: 'Fija activa' },
      { valor: '4', etiqueta: 'Otra fija activa' },
    ])
  })
})

function medioFixture(sobrescribir: Partial<MedioPagoAdministrado> = {}): MedioPagoAdministrado {
  return {
    id: 1,
    nombre: 'Transferencia',
    activo: true,
    idEmpresa: null,
    orden: 2,
    comportamiento: 'Electronico',
    admiteVuelto: false,
    requiereReferencia: false,
    recargoPorcentaje: null,
    porcentajeFacturacion: 0,
    idCuentaTesoreria: null,
    nombreCuentaTesoreria: null,
    cuentaTesoreriaInactiva: false,
    comisionPorcentaje: 0,
    retencionIibbPorcentaje: 0,
    retencionIvaPorcentaje: 0,
    retencionGananciasPorcentaje: 0,
    ...sobrescribir,
  }
}

describe('descriptorMediosPago.porcentajeFacturacion', () => {
  it('arranca en 0 en el alta', () => {
    expect(descriptorMediosPago.valoresPorDefecto.porcentajeFacturacion).toBe('0')
  })

  it('declara el campo numérico decimal con su etiqueta', () => {
    const campo = descriptorMediosPago.campos.find((c) => c.clave === 'porcentajeFacturacion')
    expect(campo).toMatchObject({ etiqueta: 'Porcentaje a facturar (%)', tipo: 'numeroDecimal' })
  })

  it('aValores lleva el número del servidor a texto, incluido el cero', () => {
    expect(descriptorMediosPago.aValores(medioFixture({ porcentajeFacturacion: 37.5 })).porcentajeFacturacion).toBe('37.5')
    expect(descriptorMediosPago.aValores(medioFixture({ porcentajeFacturacion: 0 })).porcentajeFacturacion).toBe('0')
  })

  it('aAlta convierte el texto a número', () => {
    const alta = descriptorMediosPago.aAlta('QR', true, { ...descriptorMediosPago.valoresPorDefecto, porcentajeFacturacion: '12.5' }, null)
    expect(alta.porcentajeFacturacion).toBe(12.5)
  })

  it('aAlta manda 0 cuando el campo queda vacío, nunca null', () => {
    const alta = descriptorMediosPago.aAlta('QR', true, { ...descriptorMediosPago.valoresPorDefecto, porcentajeFacturacion: '' }, null)
    expect(alta.porcentajeFacturacion).toBe(0)
  })

  it('ida y vuelta: aValores y aAlta conservan el porcentaje', () => {
    const valores = descriptorMediosPago.aValores(medioFixture({ porcentajeFacturacion: 100 }))
    expect(descriptorMediosPago.aAlta('Transferencia', true, valores, null).porcentajeFacturacion).toBe(100)
  })
})

describe('descriptorMediosPago — cuenta, comisión y retenciones', () => {
  const claves = [
    'idCuentaTesoreria',
    'comisionPorcentaje',
    'retencionIibbPorcentaje',
    'retencionIvaPorcentaje',
    'retencionGananciasPorcentaje',
  ]

  function valoresElectronicos(sobrescribir: Record<string, string | boolean> = {}) {
    return { ...descriptorMediosPago.valoresPorDefecto, comportamiento: 'Electronico', ...sobrescribir }
  }

  it('los cinco campos solo se ven con comportamiento Electronico', () => {
    for (const clave of claves) {
      const campo = descriptorMediosPago.campos.find((c) => c.clave === clave)
      expect(campo?.visibleSi, clave).toBeDefined()
      expect(campo?.visibleSi?.(valoresElectronicos()), clave).toBe(true)
      expect(campo?.visibleSi?.({ ...valoresElectronicos(), comportamiento: 'Efectivo' }), clave).toBe(false)
      expect(campo?.visibleSi?.({ ...valoresElectronicos(), comportamiento: 'CuentaCorriente' }), clave).toBe(false)
    }
  })

  it('los porcentajes aceptan tres decimales y la cuenta explica cómo entra lo cobrado', () => {
    for (const clave of claves.slice(1)) {
      expect(descriptorMediosPago.campos.find((c) => c.clave === clave)).toMatchObject({ tipo: 'numeroDecimal', paso: '0.001' })
    }

    const cuenta = descriptorMediosPago.campos.find((c) => c.clave === 'idCuentaTesoreria')
    expect(cuenta?.ayuda).toContain('neto de la comisión y las retenciones')
    expect(cuenta?.opcionVacia).toBe('— Sin cuenta —')
  })

  it('el alta arranca sin cuenta y con todo en 0', () => {
    const alta = descriptorMediosPago.aAlta('QR', true, valoresElectronicos(), null)

    expect(alta).toMatchObject({
      idCuentaTesoreria: null,
      comisionPorcentaje: 0,
      retencionIibbPorcentaje: 0,
      retencionIvaPorcentaje: 0,
      retencionGananciasPorcentaje: 0,
    })
  })

  it('aValores lleva la cuenta y los porcentajes a texto y aAlta los devuelve como números', () => {
    const item = medioFixture({
      idCuentaTesoreria: 7,
      nombreCuentaTesoreria: 'Mercado Pago',
      cuentaTesoreriaInactiva: true,
      comisionPorcentaje: 4.5,
      retencionIibbPorcentaje: 1.25,
      retencionIvaPorcentaje: 0.001,
      retencionGananciasPorcentaje: 0,
    })

    const valores = descriptorMediosPago.aValores(item)
    expect(valores).toMatchObject({
      idCuentaTesoreria: '7',
      nombreCuentaTesoreria: 'Mercado Pago',
      cuentaTesoreriaInactiva: true,
      comisionPorcentaje: '4.5',
      retencionIibbPorcentaje: '1.25',
      retencionIvaPorcentaje: '0.001',
      retencionGananciasPorcentaje: '0',
    })

    expect(descriptorMediosPago.aAlta('QR', true, valores, null)).toMatchObject({
      idCuentaTesoreria: 7,
      comisionPorcentaje: 4.5,
      retencionIibbPorcentaje: 1.25,
      retencionIvaPorcentaje: 0.001,
      retencionGananciasPorcentaje: 0,
    })
  })

  it('un medio sin cuenta vuelve a mandarse sin cuenta, sin convertir el vacío en 0', () => {
    const valores = descriptorMediosPago.aValores(medioFixture())

    expect(valores.idCuentaTesoreria).toBe('')
    expect(descriptorMediosPago.aAlta('QR', true, valores, null).idCuentaTesoreria).toBeNull()
  })

  it('un porcentaje vacío se manda como 0 y nunca como null', () => {
    const alta = descriptorMediosPago.aAlta('QR', true, valoresElectronicos({ comisionPorcentaje: '', retencionIvaPorcentaje: '' }), null)

    expect(alta.comisionPorcentaje).toBe(0)
    expect(alta.retencionIvaPorcentaje).toBe(0)
  })

  it.each(['Efectivo', 'CuentaCorriente'])(
    'con comportamiento %s los campos ocultos se mandan limpios aunque hayan quedado cargados',
    (comportamiento) => {
      const alta = descriptorMediosPago.aAlta(
        'Caja',
        true,
        valoresElectronicos({
          comportamiento,
          idCuentaTesoreria: '7',
          comisionPorcentaje: '4.5',
          retencionIibbPorcentaje: '1',
          retencionIvaPorcentaje: '2',
          retencionGananciasPorcentaje: '3',
        }),
        null,
      )

      expect(alta).toMatchObject({
        idCuentaTesoreria: null,
        comisionPorcentaje: 0,
        retencionIibbPorcentaje: 0,
        retencionIvaPorcentaje: 0,
        retencionGananciasPorcentaje: 0,
      })
    },
  )

  it('las opciones remotas arman el selector con las cuentas leídas y conservan la actual si la lectura falló', () => {
    const remotas = descriptorMediosPago.campos.find((c) => c.clave === 'idCuentaTesoreria')?.opcionesRemotas
    const valores = { idCuentaTesoreria: '9', nombreCuentaTesoreria: 'Banco viejo', cuentaTesoreriaInactiva: true }

    expect(remotas?.armar(null, valores)).toEqual([{ valor: '9', etiqueta: 'Banco viejo (inactiva)' }])
    expect(remotas?.armar([], { idCuentaTesoreria: '', nombreCuentaTesoreria: '', cuentaTesoreriaInactiva: false })).toEqual([])
  })
})

describe('avisoDeLasCuentas', () => {
  const cuentaDeEjemplo = {
    id: 1,
    idEmpresa: 7,
    nombreEmpresa: 'Comercio',
    nombre: 'MP',
    saldoInicial: 0,
    saldoActual: 0,
    activo: true,
    saldoInicialEditable: true,
  }
  const opcion = [{ valor: '1', etiqueta: 'MP' }]

  it('si fallaron las cuentas lo dice, y si la lectura entera rechazó también', () => {
    const sinCuentas = avisoDeLasCuentas({ cuentas: null, empresas: [{ id: 7 }] }, [])
    const rechazada = avisoDeLasCuentas(null, [])

    for (const aviso of [sinCuentas, rechazada]) {
      expect(aviso?.error).toBe(true)
      expect(aviso?.texto).toContain('las cuentas de tesorería')
    }
  })

  it('si solo fallaron las empresas no dice que fallaron las cuentas', () => {
    const aviso = avisoDeLasCuentas({ cuentas: [cuentaDeEjemplo], empresas: null }, opcion)

    expect(aviso?.error).toBe(true)
    expect(aviso?.texto).toContain('lista de empresas')
    expect(aviso?.texto).not.toContain('cargar las cuentas')
  })

  it('sin cuentas creadas en el tenant dice que hay que crear una', () => {
    const aviso = avisoDeLasCuentas({ cuentas: [], empresas: [{ id: 7 }] }, [])

    expect(aviso?.error).toBe(false)
    expect(aviso?.texto).toContain('creá una en Cuentas de tesorería')
  })

  it('con cuentas que el medio no puede tomar explica la regla de la empresa', () => {
    const aviso = avisoDeLasCuentas({ cuentas: [cuentaDeEjemplo], empresas: [{ id: 7 }, { id: 8 }] }, [])

    expect(aviso?.error).toBe(false)
    expect(aviso?.texto).toContain('una sola empresa')
  })

  it('con opciones para elegir no avisa nada', () => {
    expect(avisoDeLasCuentas({ cuentas: [cuentaDeEjemplo], empresas: [{ id: 7 }] }, opcion)).toBeNull()
  })
})

describe('descriptorMediosPago — empresa de la fila', () => {
  it('aAlta devuelve la empresa que recibe, para que editar no convierta el medio en uno de todo el tenant', () => {
    const valores = descriptorMediosPago.aValores(medioFixture({ idEmpresa: 4 }))

    expect(valores.idEmpresa).toBe('4')
    expect(descriptorMediosPago.aAlta('QR', true, valores, 4).idEmpresa).toBe(4)
  })

  it('un medio de todo el tenant y un alta nueva mandan idEmpresa null', () => {
    expect(descriptorMediosPago.aValores(medioFixture({ idEmpresa: null })).idEmpresa).toBe('')
    expect(descriptorMediosPago.aAlta('QR', true, descriptorMediosPago.valoresPorDefecto, null).idEmpresa).toBeNull()
  })
})

describe('aAlta de los catálogos genéricos — empresa de la fila', () => {
  const catalogos = [
    { nombre: 'áreas', descriptor: descriptorAreas },
    { nombre: 'marcas', descriptor: descriptorMarcas },
    { nombre: 'grupos', descriptor: descriptorGrupos },
    { nombre: 'listas de precio', descriptor: descriptorListasPrecio },
    { nombre: 'medios de pago', descriptor: descriptorMediosPago },
  ] as const

  it.each(catalogos)('%s: una edición devuelve la empresa de la fila', ({ descriptor }) => {
    const alta = (descriptor.aAlta as (n: string, a: boolean, v: Record<string, unknown>, e: number | null) => { idEmpresa: number | null })(
      'X',
      true,
      descriptor.valoresPorDefecto,
      6,
    )

    expect(alta.idEmpresa).toBe(6)
  })

  it.each(catalogos)('%s: una fila de todo el tenant y un alta mandan null', ({ descriptor }) => {
    const alta = (descriptor.aAlta as (n: string, a: boolean, v: Record<string, unknown>, e: number | null) => { idEmpresa: number | null })(
      'X',
      true,
      descriptor.valoresPorDefecto,
      null,
    )

    expect(alta.idEmpresa).toBeNull()
  })
})

describe('columnasDeCatalogo', () => {
  it('arma las celdas del encabezado: ID, Nombre, columnas propias, Estado y Acciones sin filtro', () => {
    expect(columnasDeCatalogo(descriptorListasPrecio).celdas).toEqual(['id', 'nombre', 'esDefault', 'modo', 'estado', null])
    expect(columnasDeCatalogo(descriptorMarcas).celdas).toEqual(['id', 'nombre', 'estado', null])
    expect(columnasDeCatalogo(descriptorMediosPago).celdas).toEqual(['id', 'nombre', 'comportamiento', 'estado', null])
  })

  it('ordena Orden como número y no como texto', () => {
    const { ordenables } = columnasDeCatalogo(descriptorAreas)
    const nueve = { id: 1, nombre: 'a', activo: true, idEmpresa: null, orden: 9 }
    const diez = { id: 2, nombre: 'b', activo: true, idEmpresa: null, orden: 10 }

    expect(ordenarFilas([diez, nueve], ordenables, { columna: 'orden', direccion: 'ascendente' })).toEqual([nueve, diez])
  })

  it('un margen vacío es un vacío: va al final aunque se ordene de mayor a menor', () => {
    const { ordenables } = columnasDeCatalogo(descriptorGrupos)
    const sin = { id: 1, nombre: 'a', activo: true, idEmpresa: null, margen: null }
    const con = { id: 2, nombre: 'b', activo: true, idEmpresa: null, margen: 5 }
    const mas = { id: 3, nombre: 'c', activo: true, idEmpresa: null, margen: 12 }

    expect(ordenarFilas([sin, con, mas], ordenables, { columna: 'margen', direccion: 'descendente' })).toEqual([mas, con, sin])
  })

  it('la columna booleana se filtra con Sí / No y la de modo con las etiquetas que muestra', () => {
    const { filtrables } = columnasDeCatalogo(descriptorListasPrecio)
    const base = listaFixture({ id: 1, esDefault: true })
    const derivada = listaFixture({ id: 2, modo: 'Derivada', idListaBase: 1, porcentaje: 10 })

    expect(filtrables.esDefault.opciones?.map((o) => o.valor)).toEqual(['Sí', 'No'])
    expect(filtrarFilas([base, derivada], filtrables, { esDefault: 'Sí' })).toEqual([base])
    expect(filtrarFilas([base, derivada], filtrables, { modo: 'Derivada (calculada sobre otra lista)' })).toEqual([derivada])
  })

  it('Estado ofrece Activo e Inactivo', () => {
    const { filtrables } = columnasDeCatalogo(descriptorMarcas)
    const activa = { id: 1, nombre: 'Nike', activo: true, idEmpresa: null }
    const inactiva = { id: 2, nombre: 'Puma', activo: false, idEmpresa: null }

    expect(filtrables.estado.opciones?.map((o) => o.valor)).toEqual(['Activo', 'Inactivo'])
    expect(filtrarFilas([activa, inactiva], filtrables, { estado: 'Inactivo' })).toEqual([inactiva])
  })

  it('el ID se filtra por el número que se muestra con ceros', () => {
    const { filtrables } = columnasDeCatalogo(descriptorMarcas)

    expect(filtrables.id.valor({ id: 7, nombre: 'x', activo: true, idEmpresa: null })).toBe('0007')
  })

  it('una columna con formatearListado ordena y filtra por la etiqueta, y una referencia colgante cuenta como vacía', () => {
    const etiquetas: Record<number, string> = { 1: 'Mayorista' }
    type Fila = CatalogoListado & { idLista: number }
    const descriptor: DescriptorDeCatalogo<Fila, unknown> = {
      recurso: 'x',
      titulo: 'X',
      tituloSingular: 'x',
      campos: [
        {
          clave: 'idLista',
          etiqueta: 'Lista',
          tipo: 'select',
          columnaEnListado: true,
          formatearListado: (item) => etiquetas[(item as Fila).idLista] ?? '',
        },
      ],
      valoresPorDefecto: {},
      aValores: (item) => ({ idLista: String(item.idLista) }),
      aAlta: () => ({}),
      sujetoDeBaja: 'el área',
    }
    const conLista = { id: 1, nombre: 'a', activo: true, idEmpresa: null, idLista: 1 }
    const colgante = { id: 2, nombre: 'b', activo: true, idEmpresa: null, idLista: 99 }
    const { ordenables, filtrables } = columnasDeCatalogo(descriptor)

    expect(filtrables.idLista.opciones).toBeUndefined()
    expect(filtrarFilas([conLista, colgante], filtrables, { idLista: 'mayor' })).toEqual([conLista])
    expect(ordenarFilas([colgante, conLista], ordenables, { columna: 'idLista', direccion: 'descendente' })).toEqual([
      conLista,
      colgante,
    ])
    expect(textoDeColumna(descriptor.campos[0], '99', colgante)).toBe('')
  })
})

describe('textoDeColumna', () => {
  const select = descriptorListasPrecio.campos[1]

  it('un booleano es Sí o No', () => {
    const booleano = descriptorListasPrecio.campos[0]

    expect(textoDeColumna(booleano, true, null)).toBe('Sí')
    expect(textoDeColumna(booleano, false, null)).toBe('No')
  })

  it('un select muestra la etiqueta de su opción y cae al valor si no la encuentra', () => {
    expect(textoDeColumna(select, 'Derivada', null)).toBe('Derivada (calculada sobre otra lista)')
    expect(textoDeColumna(select, 'Otra', null)).toBe('Otra')
  })

  it('sin valor devuelve vacío', () => {
    expect(textoDeColumna(select, '', null)).toBe('')
    expect(textoDeColumna(select, undefined, null)).toBe('')
    expect(textoDeColumna(descriptorAreas.campos[0], '', null)).toBe('')
  })
})
