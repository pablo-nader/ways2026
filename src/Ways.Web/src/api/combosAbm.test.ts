import { describe, expect, it } from 'vitest'
import { COPIA_RESULTADO_INCIERTO } from './bajas'
import { ErrorApi } from './cliente'
import {
  aSolicitudDeCombo,
  aValoresCombo,
  CANTIDAD_MAXIMA,
  comboCoincideConAlta,
  COMPONENTES_MAXIMOS,
  errorDeServidorDeCombo,
  esArticuloElegibleParaCombo,
  esComboInexistente,
  esResultadoIncierto,
  etiquetaDeCodigoDeCombo,
  filaDeComponenteVacia,
  formularioComboVacio,
  hayErrores,
  MENSAJE_COMBO_INEXISTENTE,
  MENSAJE_COMPONENTE_NO_DISPONIBLE,
  mensajeDeFalloDeBajaDeCombo,
  PRECIO_MAXIMO,
  resumenDeComponentes,
  resumenDeFormulario,
  tieneComponentesNoDisponibles,
  validarCombo,
  type FilaDeComponente,
  type FormularioCombo,
} from './combosAbm'
import type { AltaCombo, ComboListado,ComponenteDeComboListado } from './tipos'

function fila(sobrescribir: Partial<FilaDeComponente> = {}): FilaDeComponente {
  return { ...filaDeComponenteVacia(), idArticulo: 1, nombre: 'Pancho', cantidad: '1', ...sobrescribir }
}

function articulo(id: number, cantidad = '1'): FilaDeComponente {
  return fila({ tipo: 'Articulo', idArticulo: id, idFamilia: '', nombre: `Art ${id}`, cantidad })
}

function familia(id: number, cantidad = '1'): FilaDeComponente {
  return fila({ tipo: 'Familia', idArticulo: '', idFamilia: id, nombre: `Fam ${id}`, cantidad })
}

function formulario(sobrescribir: Partial<FormularioCombo> = {}): FormularioCombo {
  return {
    id: null,
    codigo: '',
    nombre: 'Pancho y Coca',
    precio: 5000,
    idEmpresa: '',
    activo: true,
    componentes: [articulo(1), familia(7)],
    ...sobrescribir,
  }
}

function componenteListado(sobrescribir: Partial<ComponenteDeComboListado> = {}): ComponenteDeComboListado {
  return { idArticulo: 1, idFamilia: null, nombre: 'Pancho', cantidad: 1, disponible: true, ...sobrescribir }
}

function comboListado(sobrescribir: Partial<ComboListado> = {}): ComboListado {
  return {
    id: 4,
    codigo: 12,
    nombre: 'Pancho y Coca',
    precio: 5000,
    idEmpresa: null,
    activo: true,
    componentes: [componenteListado(), componenteListado({ idArticulo: null, idFamilia: 7, nombre: 'Línea Coca 500', cantidad: 2 })],
    ...sobrescribir,
  }
}

function rechazo(estado: number, codigo: string, mensaje = 'mensaje del servidor') {
  return new ErrorApi(estado, codigo, mensaje)
}

describe('formularioComboVacio', () => {
  it('arranca activo, sin código ni precio y con dos filas de artículo vacías de cantidad 1 y clave distinta', () => {
    const f = formularioComboVacio()

    expect(f).toMatchObject({ id: null, codigo: '', nombre: '', precio: null, idEmpresa: '', activo: true })
    expect(f.componentes).toHaveLength(2)
    expect(f.componentes.map((c) => [c.tipo, c.idArticulo, c.idFamilia, c.cantidad, c.disponible])).toEqual([
      ['Articulo', '', '', '1', true],
      ['Articulo', '', '', '1', true],
    ])
    expect(new Set(f.componentes.map((c) => c.clave)).size).toBe(2)
  })
})

describe('aValoresCombo', () => {
  it('lleva el combo al formulario: código y cantidad como texto, empresa nula como vacío, tipo por la referencia', () => {
    const f = aValoresCombo(comboListado())

    expect(f).toMatchObject({ id: 4, codigo: '12', nombre: 'Pancho y Coca', precio: 5000, idEmpresa: '', activo: true })
    expect(f.componentes.map((c) => [c.tipo, c.idArticulo, c.idFamilia, c.nombre, c.cantidad, c.disponible])).toEqual([
      ['Articulo', 1, '', 'Pancho', '1', true],
      ['Familia', '', 7, 'Línea Coca 500', '2', true],
    ])
  })

  it('conserva la empresa, el estado inactivo y el nombre vacío con disponible falso de un componente dado de baja', () => {
    const f = aValoresCombo(
      comboListado({
        idEmpresa: 3,
        activo: false,
        componentes: [componenteListado({ nombre: null, disponible: false }), componenteListado({ idArticulo: 2 })],
      }),
    )

    expect(f.idEmpresa).toBe(3)
    expect(f.activo).toBe(false)
    expect(f.componentes[0]).toMatchObject({ idArticulo: 1, nombre: '', disponible: false })
    expect(f.componentes[1].disponible).toBe(true)
  })
})

describe('aSolicitudDeCombo', () => {
  it('sin código no manda la clave, y cada componente manda solo su referencia', () => {
    const cuerpo = aSolicitudDeCombo(formulario({ componentes: [articulo(1, '1'), familia(7, '2')] }))

    expect(cuerpo).toStrictEqual({
      nombre: 'Pancho y Coca',
      precio: 5000,
      idEmpresa: null,
      activo: true,
      componentes: [
        { idArticulo: 1, cantidad: 1 },
        { idFamilia: 7, cantidad: 2 },
      ],
    })
    expect('codigo' in cuerpo).toBe(false)
  })

  it('con código lo manda como número; la empresa elegida y el estado inactivo viajan', () => {
    const cuerpo = aSolicitudDeCombo(formulario({ codigo: ' 12 ', idEmpresa: 3, activo: false }))

    expect(cuerpo.codigo).toBe(12)
    expect(cuerpo.idEmpresa).toBe(3)
    expect(cuerpo.activo).toBe(false)
  })

  it('recorta el nombre y convierte la cantidad escrita con espacios', () => {
    const cuerpo = aSolicitudDeCombo(formulario({ nombre: '  Combo  ', componentes: [articulo(1, ' 3 '), articulo(2)] }))

    expect(cuerpo.nombre).toBe('Combo')
    expect(cuerpo.componentes[0]).toStrictEqual({ idArticulo: 1, cantidad: 3 })
  })

  it('un código vacío no viaja ni siquiera en la edición: el servidor conserva el actual', () => {
    expect('codigo' in aSolicitudDeCombo(formulario({ id: 4, codigo: '   ' }))).toBe(false)
  })
})

describe('comboCoincideConAlta', () => {
  const cuerpo: AltaCombo = {
    nombre: 'Pancho y Coca',
    precio: 5000,
    idEmpresa: null,
    activo: true,
    componentes: [
      { idArticulo: 1, cantidad: 1 },
      { idFamilia: 7, cantidad: 2 },
    ],
  }

  it('coincide cuando todo es igual, con el nombre sin espacios ni mayúsculas y sin código tipeado', () => {
    expect(comboCoincideConAlta(cuerpo, comboListado())).toBe(true)
    expect(comboCoincideConAlta(cuerpo, comboListado({ nombre: '  pancho Y COCA ', codigo: 99 }))).toBe(true)
  })

  it('los componentes reordenados siguen coincidiendo', () => {
    const invertidos = comboListado().componentes.slice().reverse()
    expect(comboCoincideConAlta(cuerpo, comboListado({ componentes: invertidos }))).toBe(true)
  })

  it('el orden del cuerpo enviado tampoco importa', () => {
    const alReves: AltaCombo = { ...cuerpo, componentes: cuerpo.componentes.slice().reverse() }
    expect(comboCoincideConAlta(alReves, comboListado())).toBe(true)
  })

  it('un componente de más en el listado no coincide', () => {
    const conExtra = [...comboListado().componentes, componenteListado({ idArticulo: null, idFamilia: 9 })]
    expect(comboCoincideConAlta(cuerpo, comboListado({ componentes: conExtra }))).toBe(false)
  })

  it.each<[string, Partial<ComboListado>]>([
    ['nombre', { nombre: 'Otro' }],
    ['precio', { precio: 5001 }],
    ['empresa', { idEmpresa: 3 }],
    ['activo', { activo: false }],
    ['cantidad de un componente', { componentes: [componenteListado(), componenteListado({ idArticulo: null, idFamilia: 7, cantidad: 3 })] }],
    ['artículo de un componente', { componentes: [componenteListado({ idArticulo: 2 }), componenteListado({ idArticulo: null, idFamilia: 7, cantidad: 2 })] }],
    ['familia de un componente', { componentes: [componenteListado(), componenteListado({ idArticulo: null, idFamilia: 8, cantidad: 2 })] }],
    ['tipo de un componente', { componentes: [componenteListado(), componenteListado({ idArticulo: 7, idFamilia: null, cantidad: 2 })] }],
    ['cantidad de componentes', { componentes: [componenteListado()] }],
  ])('no coincide si difiere %s', (_campo, cambio) => {
    expect(comboCoincideConAlta(cuerpo, comboListado(cambio))).toBe(false)
  })

  it('con código tipeado exige el mismo código; sin código tipeado lo ignora', () => {
    expect(comboCoincideConAlta({ ...cuerpo, codigo: 12 }, comboListado({ codigo: 12 }))).toBe(true)
    expect(comboCoincideConAlta({ ...cuerpo, codigo: 12 }, comboListado({ codigo: 13 }))).toBe(false)
    expect(comboCoincideConAlta(cuerpo, comboListado({ codigo: 13 }))).toBe(true)
  })
})

describe('etiquetaDeCodigoDeCombo', () => {
  it('antepone la C que el cajero tipea', () => {
    expect(etiquetaDeCodigoDeCombo(12)).toBe('C12')
  })
})

describe('resumenDeComponentes', () => {
  it('une los componentes con cantidad y marca las familias', () => {
    expect(resumenDeComponentes(comboListado().componentes)).toBe('1 × Pancho + 2 × Línea Coca 500 (familia)')
  })

  it('un componente dado de baja se nombra por su número y se marca como no disponible', () => {
    expect(
      resumenDeComponentes([
        componenteListado({ idArticulo: 5, nombre: null, disponible: false }),
        componenteListado({ idArticulo: null, idFamilia: 9, nombre: null, disponible: false }),
      ]),
    ).toBe('1 × Artículo #5 (no disponible) + 1 × Familia #9 (familia) (no disponible)')
  })
})

describe('tieneComponentesNoDisponibles', () => {
  it('es verdadero si algún componente no está disponible y falso si todos lo están', () => {
    expect(tieneComponentesNoDisponibles(comboListado())).toBe(false)
    expect(
      tieneComponentesNoDisponibles(comboListado({ componentes: [componenteListado(), componenteListado({ disponible: false })] })),
    ).toBe(true)
  })
})

describe('resumenDeFormulario', () => {
  it('describe lo armado y agrega el precio con el formato de importes', () => {
    expect(resumenDeFormulario(formulario({ precio: 5000 }))).toBe('1 × Art 1 + 1 × Fam 7 (familia) por $ 5.000,00')
  })

  it('omite el precio mientras no es un importe positivo', () => {
    expect(resumenDeFormulario(formulario({ precio: null }))).toBe('1 × Art 1 + 1 × Fam 7 (familia)')
    expect(resumenDeFormulario(formulario({ precio: 0 }))).toBe('1 × Art 1 + 1 × Fam 7 (familia)')
  })

  it('omite las filas sin referencia o con cantidad inválida, y es vacío si no queda ninguna', () => {
    const sinElegir = fila({ idArticulo: '', nombre: '' })
    expect(resumenDeFormulario(formulario({ componentes: [articulo(1, '2'), sinElegir, articulo(3, 'x'), articulo(4, '0')] }))).toBe(
      '2 × Art 1 por $ 5.000,00',
    )
    expect(resumenDeFormulario(formulario({ componentes: [sinElegir] }))).toBe('')
    expect(resumenDeFormulario(formulario({ componentes: [] }))).toBe('')
  })

  it('una fila elegida sin nombre se nombra por su número', () => {
    expect(resumenDeFormulario(formulario({ componentes: [fila({ idArticulo: 9, nombre: '' })], precio: null }))).toBe('1 × Artículo #9')
  })
})

describe('validarCombo — nombre', () => {
  it('rechaza el nombre vacío o de solo espacios', () => {
    expect(validarCombo(formulario({ nombre: '' })).nombre).toBe('Ingresá un nombre para el combo.')
    expect(validarCombo(formulario({ nombre: '   ' })).nombre).toBe('Ingresá un nombre para el combo.')
  })

  it('admite 150 caracteres y rechaza 151', () => {
    expect(validarCombo(formulario({ nombre: 'a'.repeat(150) })).nombre).toBeUndefined()
    expect(validarCombo(formulario({ nombre: 'a'.repeat(151) })).nombre).toBe('El nombre no puede superar los 150 caracteres.')
  })
})

describe('validarCombo — precio', () => {
  it.each([null, 0, -1])('rechaza %s como precio', (precio) => {
    expect(validarCombo(formulario({ precio })).precio).toBe('Ingresá un precio mayor a cero.')
  })

  it('rechaza más de dos decimales y admite dos', () => {
    expect(validarCombo(formulario({ precio: 10.005 })).precio).toBe('El precio admite hasta dos decimales.')
    expect(validarCombo(formulario({ precio: 12.34 })).precio).toBeUndefined()
  })

  // Cláusula que prueba: la comparación de decimales es exacta (la de punto flotante rechazaba importes grandes válidos).
  it.each([1234567890.12, 600000000.07, 999999999999.99, 0.01])('admite %s: tiene dos decimales exactos', (precio) => {
    expect(validarCombo(formulario({ precio })).precio).toBeUndefined()
  })

  it.each([10.005, 0.001, 1234567890.123])('rechaza %s: tiene más de dos decimales', (precio) => {
    expect(validarCombo(formulario({ precio })).precio).toBe('El precio admite hasta dos decimales.')
  })

  it('admite el máximo y rechaza lo que lo supera', () => {
    expect(validarCombo(formulario({ precio: PRECIO_MAXIMO })).precio).toBeUndefined()
    expect(validarCombo(formulario({ precio: PRECIO_MAXIMO + 1 })).precio).toBe('El precio no puede superar 999.999.999.999,99.')
  })
})

describe('validarCombo — código', () => {
  it.each(['', '   ', '1', '999999', ' 12 '])('admite %j', (codigo) => {
    expect(validarCombo(formulario({ codigo })).codigo).toBeUndefined()
  })

  it.each(['0', '1000000', 'C1', '1.5', '-3', 'abc'])('rechaza %j', (codigo) => {
    expect(validarCombo(formulario({ codigo })).codigo).toBe('El código tiene que ser un entero entre 1 y 999999.')
  })
})

describe('validarCombo — componentes', () => {
  it('una fila sin referencia pide elegir según su tipo', () => {
    const sinArticulo = fila({ tipo: 'Articulo', idArticulo: '' })
    const sinFamilia = fila({ tipo: 'Familia', idArticulo: '', idFamilia: '' })

    const errores = validarCombo(formulario({ componentes: [sinArticulo, sinFamilia] }))

    expect(errores.filas[sinArticulo.clave]).toEqual({ referencia: 'Elegí un artículo.' })
    expect(errores.filas[sinFamilia.clave]).toEqual({ referencia: 'Elegí una familia.' })
  })

  it('marca el segundo artículo repetido y la segunda familia repetida, no la primera', () => {
    const [a1, a2, f1, f2] = [articulo(1), articulo(1), familia(7), familia(7)]

    const errores = validarCombo(formulario({ componentes: [a1, a2, f1, f2] }))

    expect(errores.filas[a1.clave]).toBeUndefined()
    expect(errores.filas[a2.clave]).toEqual({ referencia: 'Este artículo ya está en el combo.' })
    expect(errores.filas[f1.clave]).toBeUndefined()
    expect(errores.filas[f2.clave]).toEqual({ referencia: 'Esta familia ya está en el combo.' })
  })

  it('un artículo y una familia con el mismo id no son duplicados', () => {
    expect(hayErrores(validarCombo(formulario({ componentes: [articulo(7), familia(7)] })))).toBe(false)
  })

  it('un componente no disponible debe quitarse o reemplazarse', () => {
    const baja = articulo(5)
    baja.disponible = false

    const errores = validarCombo(formulario({ componentes: [baja, articulo(1)] }))

    expect(errores.filas[baja.clave]).toEqual({ referencia: MENSAJE_COMPONENTE_NO_DISPONIBLE })
  })

  it.each([
    ['', 'Ingresá la cantidad.'],
    ['   ', 'Ingresá la cantidad.'],
    ['0', 'La cantidad tiene que ser un entero entre 1 y 999999.'],
    ['1.5', 'La cantidad tiene que ser un entero entre 1 y 999999.'],
    ['-1', 'La cantidad tiene que ser un entero entre 1 y 999999.'],
    ['abc', 'La cantidad tiene que ser un entero entre 1 y 999999.'],
    [String(CANTIDAD_MAXIMA + 1), 'La cantidad tiene que ser un entero entre 1 y 999999.'],
  ])('rechaza la cantidad %j', (cantidad, mensaje) => {
    const mala = articulo(1, cantidad)

    expect(validarCombo(formulario({ componentes: [mala, articulo(2, '5')] })).filas[mala.clave]).toEqual({ cantidad: mensaje })
  })

  it('admite el máximo de cantidad', () => {
    expect(hayErrores(validarCombo(formulario({ componentes: [articulo(1, String(CANTIDAD_MAXIMA)), articulo(2)] })))).toBe(false)
  })

  it('exige al menos dos unidades en total: una fila de 1 no alcanza, una de 2 o dos de 1 sí', () => {
    const MENSAJE = 'El combo tiene que sumar al menos 2 unidades entre todos sus componentes.'

    expect(validarCombo(formulario({ componentes: [articulo(1, '1')] })).componentes).toBe(MENSAJE)
    expect(validarCombo(formulario({ componentes: [] })).componentes).toBe(MENSAJE)
    expect(validarCombo(formulario({ componentes: [articulo(1, '2')] })).componentes).toBeUndefined()
    expect(validarCombo(formulario({ componentes: [articulo(1, '1'), articulo(2, '1')] })).componentes).toBeUndefined()
  })

  it('con una cantidad inválida no suma unidades: el error queda en la fila y no en el conjunto', () => {
    const errores = validarCombo(formulario({ componentes: [articulo(1, 'x')] }))

    expect(errores.componentes).toBeUndefined()
    expect(Object.keys(errores.filas)).toHaveLength(1)
  })

  it('admite 50 componentes y rechaza 51', () => {
    const cincuenta = Array.from({ length: COMPONENTES_MAXIMOS }, (_, i) => articulo(i + 1))
    const cincuentaYUno = [...cincuenta, articulo(999)]

    expect(validarCombo(formulario({ componentes: cincuenta })).componentes).toBeUndefined()
    expect(validarCombo(formulario({ componentes: cincuentaYUno })).componentes).toBe('Un combo admite hasta 50 componentes.')
  })

  it('un formulario correcto no tiene errores', () => {
    expect(hayErrores(validarCombo(formulario()))).toBe(false)
  })
})

describe('hayErrores', () => {
  it('detecta cada tipo de error por separado', () => {
    expect(hayErrores({ filas: {} })).toBe(false)
    expect(hayErrores({ filas: {}, nombre: 'x' })).toBe(true)
    expect(hayErrores({ filas: {}, precio: 'x' })).toBe(true)
    expect(hayErrores({ filas: {}, codigo: 'x' })).toBe(true)
    expect(hayErrores({ filas: {}, componentes: 'x' })).toBe(true)
    expect(hayErrores({ filas: { 1: { cantidad: 'x' } } })).toBe(true)
  })
})

describe('errorDeServidorDeCombo — cada código del contrato cae en su lugar', () => {
  it.each([
    [400, 'combo_precio_invalido', 'precio'],
    [400, 'combo_componente_invalido', 'componentes'],
    [400, 'combo_cantidad_invalida', 'componentes'],
    [400, 'combo_componente_duplicado', 'componentes'],
    [409, 'combo_componente_duplicado', 'componentes'],
    [400, 'combo_unidades_insuficientes', 'componentes'],
    [400, 'combo_componentes_excedidos', 'componentes'],
    [400, 'combo_componente_no_unitario', 'componentes'],
    [400, 'combo_componente_por_importe', 'componentes'],
    [400, 'combo_codigo_invalido', 'codigo'],
    [409, 'combo_codigo_duplicado', 'codigo'],
    [409, 'combo_codigo_agotado', 'codigo'],
    [400, 'nombre_requerido', 'nombre'],
    [400, 'nombre_muy_largo', 'nombre'],
    [400, 'activo_requerido', 'formulario'],
  ])('%i %s → %s, con un mensaje propio y no el del servidor', (estado, codigo, campo) => {
    const resultado = errorDeServidorDeCombo(rechazo(estado, codigo))

    expect(resultado.campo).toBe(campo)
    expect(resultado.mensaje).not.toBe('')
    expect(resultado.mensaje).not.toBe('mensaje del servidor')
  })

  it('el mensaje de cada código dice lo suyo', () => {
    expect(errorDeServidorDeCombo(rechazo(400, 'combo_componente_no_unitario')).mensaje).toContain('por peso')
    expect(errorDeServidorDeCombo(rechazo(400, 'combo_componente_por_importe')).mensaje).toContain('por importe')
    expect(errorDeServidorDeCombo(rechazo(409, 'combo_codigo_duplicado')).mensaje).toBe('Ya hay un combo con ese código. Elegí otro.')
    expect(errorDeServidorDeCombo(rechazo(400, 'combo_componentes_excedidos')).mensaje).toContain('50')
  })

  it('referencia_invalida va al formulario con el mensaje del servidor primero y la guía detrás', () => {
    const resultado = errorDeServidorDeCombo(rechazo(400, 'referencia_invalida', 'No existe el artículo 5.'))

    expect(resultado.campo).toBe('formulario')
    expect(resultado.mensaje.startsWith('No existe el artículo 5. ')).toBe(true)
    expect(resultado.mensaje).toContain('Quitá o reemplazá')
  })

  it('referencia_invalida sin mensaje queda solo con la guía', () => {
    expect(errorDeServidorDeCombo(rechazo(400, 'referencia_invalida', '  ')).mensaje.startsWith('Quitá o reemplazá')).toBe(true)
  })

  it('un código desconocido cae en el formulario con el mensaje del servidor', () => {
    expect(errorDeServidorDeCombo(rechazo(400, 'algo_nuevo', 'Dijo algo raro.'))).toEqual({ campo: 'formulario', mensaje: 'Dijo algo raro.' })
  })

  it('un código que existe en el prototipo de un objeto no se resuelve como conocido', () => {
    expect(errorDeServidorDeCombo(rechazo(400, 'constructor', 'Dijo algo raro.'))).toEqual({ campo: 'formulario', mensaje: 'Dijo algo raro.' })
  })

  it('un código desconocido sin mensaje cae en un texto genérico', () => {
    expect(errorDeServidorDeCombo(rechazo(400, 'algo_nuevo', ''))).toEqual({ campo: 'formulario', mensaje: 'No se pudo guardar el combo.' })
  })

  it('un 404 dice que el combo ya no existe', () => {
    expect(errorDeServidorDeCombo(rechazo(404, 'no_encontrado'))).toEqual({ campo: 'formulario', mensaje: MENSAJE_COMBO_INEXISTENTE })
  })

  it('un 5xx o un fallo sin respuesta avisa que no se sabe si se guardó', () => {
    expect(errorDeServidorDeCombo(rechazo(500, 'error_interno', 'boom')).mensaje).toBe(`No se pudo guardar el combo. ${COPIA_RESULTADO_INCIERTO}`)
    expect(errorDeServidorDeCombo(new Error('sin red')).mensaje).toBe(`No se pudo guardar el combo. ${COPIA_RESULTADO_INCIERTO}`)
  })

  it('un resultado_incierto del servidor conserva su propio mensaje', () => {
    expect(errorDeServidorDeCombo(rechazo(503, 'resultado_incierto', 'Verificá el listado.')).mensaje).toBe(
      'No se pudo guardar el combo. Verificá el listado.',
    )
  })
})

describe('MENSAJE_COMBO_INEXISTENTE', () => {
  it('es neutro: no atribuye la baja a otra persona (una baja reintentada también lo recibe)', () => {
    expect(MENSAJE_COMBO_INEXISTENTE).toBe('El combo ya no existe. Se actualizó el listado.')
  })
})

describe('esResultadoIncierto', () => {
  it('es verdadero para un 5xx y para un fallo sin respuesta, no para un rechazo 4xx', () => {
    expect(esResultadoIncierto(rechazo(500, 'error_interno'))).toBe(true)
    expect(esResultadoIncierto(rechazo(503, 'resultado_incierto'))).toBe(true)
    expect(esResultadoIncierto(new Error('sin red'))).toBe(true)
    expect(esResultadoIncierto(rechazo(409, 'combo_codigo_duplicado'))).toBe(false)
    expect(esResultadoIncierto(rechazo(404, 'no_encontrado'))).toBe(false)
  })
})

describe('esComboInexistente', () => {
  it('es verdadero solo para un 404 de la API', () => {
    expect(esComboInexistente(rechazo(404, 'no_encontrado'))).toBe(true)
    expect(esComboInexistente(rechazo(409, 'x'))).toBe(false)
    expect(esComboInexistente(new Error('404'))).toBe(false)
  })
})

describe('mensajeDeFalloDeBajaDeCombo', () => {
  it('404, resultado incierto y mensaje del servidor', () => {
    expect(mensajeDeFalloDeBajaDeCombo(rechazo(404, 'no_encontrado'))).toBe(MENSAJE_COMBO_INEXISTENTE)
    expect(mensajeDeFalloDeBajaDeCombo(rechazo(500, 'error_interno'))).toBe(`No se pudo dar de baja el combo. ${COPIA_RESULTADO_INCIERTO}`)
    expect(mensajeDeFalloDeBajaDeCombo(new Error('sin red'))).toBe(`No se pudo dar de baja el combo. ${COPIA_RESULTADO_INCIERTO}`)
    expect(mensajeDeFalloDeBajaDeCombo(rechazo(409, 'otra', 'Está en uso.'))).toBe('Está en uso.')
    expect(mensajeDeFalloDeBajaDeCombo(rechazo(409, 'otra', ' '))).toBe('No se pudo dar de baja el combo.')
  })
})

describe('esArticuloElegibleParaCombo', () => {
  it('solo los artículos por unidad y activos pueden ser componentes', () => {
    expect(esArticuloElegibleParaCombo({ unidadVenta: 'Unidad', activo: true })).toBe(true)
    expect(esArticuloElegibleParaCombo({ unidadVenta: 'Peso', activo: true })).toBe(false)
    expect(esArticuloElegibleParaCombo({ unidadVenta: 'Unidad', activo: false })).toBe(false)
  })
})
