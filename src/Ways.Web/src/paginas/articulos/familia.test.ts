import { describe, expect, it } from 'vitest'
import type { FamiliaDetalle, FamiliaListado, ValoresCompartidosDeLaFamilia } from '../../api/tipos'
import {
  altaConFamiliaLista,
  avisoDeValoresDistintos,
  camposCompartidosModificados,
  camposNombradosEnMensaje,
  CAMPOS_COMPARTIDOS,
  CODIGOS_DE_CONFLICTO_DE_ALTA,
  cantidadDeArticulos,
  contextoDeAlcance,
  describirEstadoDePrecios,
  descripcionDeFamilia,
  etiquetaDeFamilia,
  familiaDelArticulo,
  formatearFechaHora,
  mensajeDeAlta,
  mensajeDeEdicion,
  opcionesDeFamilia,
  valoresDeFamiliaAFormulario,
  type CampoCompartido,
} from './familia'
import { formularioVacio, type Formulario } from './FormularioArticulo'

function valoresFixture(sobrescribir: Partial<ValoresCompartidosDeLaFamilia> = {}): ValoresCompartidosDeLaFamilia {
  return {
    idArea: 1,
    idCategoria: 2,
    idGrupo: 3,
    idProveedorHabitual: 4,
    idAlicuotaIva: 5,
    unidadVenta: 'Unidad',
    unidadesPorBulto: 6,
    esProducto: true,
    controlaLote: false,
    acumulaEnVenta: true,
    costoLista: 100,
    descuentoProveedor: 10,
    costoNominal: 90,
    ...sobrescribir,
  }
}

function formulario(sobrescribir: Partial<Formulario> = {}): Formulario {
  return { ...formularioVacio(), ...valoresDeFamiliaAFormulario(valoresFixture()), ...sobrescribir }
}

function detalleFixture(sobrescribir: Partial<FamiliaDetalle> = {}): FamiliaDetalle {
  return {
    id: 7,
    nombre: 'Sabores',
    activo: true,
    articulos: [
      { id: 1, codigoInterno: 'A0001', nombre: 'Vainilla', idMarca: null, activo: true },
      { id: 2, codigoInterno: 'A0002', nombre: 'Frutilla', idMarca: null, activo: true },
      { id: 3, codigoInterno: 'A0003', nombre: 'Chocolate', idMarca: null, activo: true },
    ],
    valores: valoresFixture(),
    precios: [],
    ...sobrescribir,
  }
}

function familiaListado(sobrescribir: Partial<FamiliaListado> = {}): FamiliaListado {
  return { id: 1, nombre: 'Sabores', activo: true, cantidadArticulos: 3, ...sobrescribir }
}

describe('CAMPOS_COMPARTIDOS', () => {
  it('son los trece campos compartidos, con las columnas del servidor y en su orden de declaración', () => {
    expect(CAMPOS_COMPARTIDOS.map((c) => c.columna)).toEqual([
      'id_area',
      'id_categoria',
      'id_grupo',
      'id_proveedor_habitual',
      'id_alicuota_iva',
      'unidad_venta',
      'unidades_por_bulto',
      'es_producto',
      'controla_lote',
      'acumula_en_venta',
      'costo_lista',
      'descuento_proveedor',
      'costo_nominal',
    ])
  })

  it('las claves son exactamente las de ValoresCompartidosDeLaFamilia: ni una de más ni una de menos', () => {
    expect(CAMPOS_COMPARTIDOS.map((c) => c.clave).sort()).toEqual(Object.keys(valoresFixture()).sort())
  })

  it('cada campo tiene una etiqueta legible, distinta de las demás', () => {
    const etiquetas = CAMPOS_COMPARTIDOS.map((c) => c.etiqueta)
    expect(new Set(etiquetas).size).toBe(13)
    expect(etiquetas.every((e) => e.trim() !== '')).toBe(true)
  })
})

describe('valoresDeFamiliaAFormulario', () => {
  it('copia los trece valores con la forma del formulario: números como están y decimales como texto', () => {
    expect(valoresDeFamiliaAFormulario(valoresFixture())).toEqual({
      idArea: 1,
      idCategoria: 2,
      idGrupo: 3,
      idProveedorHabitual: 4,
      idAlicuotaIva: 5,
      unidadVenta: 'Unidad',
      unidadesPorBulto: '6',
      esProducto: true,
      controlaLote: false,
      acumulaEnVenta: true,
      costoLista: '100',
      descuentoProveedor: '10',
      costoNominal: '90',
    })
  })

  it('un id de catálogo dado de baja (null en el servidor) y un decimal sin valor quedan vacíos', () => {
    const resultado = valoresDeFamiliaAFormulario(
      valoresFixture({
        idArea: null,
        idCategoria: null,
        idGrupo: null,
        idProveedorHabitual: null,
        unidadesPorBulto: null,
        costoLista: null,
        descuentoProveedor: null,
        costoNominal: null,
      }),
    )

    expect(resultado).toMatchObject({
      idArea: '',
      idCategoria: '',
      idGrupo: '',
      idProveedorHabitual: '',
      unidadesPorBulto: '',
      costoLista: '',
      descuentoProveedor: '',
      costoNominal: '',
    })
  })

  it('un cero NO es "sin valor": 0 viaja como "0"', () => {
    expect(valoresDeFamiliaAFormulario(valoresFixture({ costoLista: 0, descuentoProveedor: 0 }))).toMatchObject({
      costoLista: '0',
      descuentoProveedor: '0',
    })
  })

  it('los booleanos y la unidad de venta pasan tal cual', () => {
    expect(
      valoresDeFamiliaAFormulario(valoresFixture({ esProducto: false, controlaLote: true, acumulaEnVenta: false, unidadVenta: 'Peso' })),
    ).toMatchObject({ esProducto: false, controlaLote: true, acumulaEnVenta: false, unidadVenta: 'Peso' })
  })
})

describe('camposCompartidosModificados', () => {
  it('dos formularios idénticos no modifican nada', () => {
    expect(camposCompartidosModificados(formulario(), formulario())).toEqual([])
  })

  const cambios: [CampoCompartido['clave'], Partial<Formulario>][] = [
    ['idArea', { idArea: 99 }],
    ['idCategoria', { idCategoria: 99 }],
    ['idGrupo', { idGrupo: 99 }],
    ['idProveedorHabitual', { idProveedorHabitual: 99 }],
    ['idAlicuotaIva', { idAlicuotaIva: 99 }],
    ['unidadVenta', { unidadVenta: 'Peso' }],
    ['unidadesPorBulto', { unidadesPorBulto: '12' }],
    ['esProducto', { esProducto: false }],
    ['controlaLote', { controlaLote: true }],
    ['acumulaEnVenta', { acumulaEnVenta: false }],
    ['costoLista', { costoLista: '120' }],
    ['descuentoProveedor', { descuentoProveedor: '15' }],
    ['costoNominal', { costoNominal: '95' }],
  ]

  it.each(cambios)('detecta el cambio de %s y solo ese', (clave, cambio) => {
    expect(camposCompartidosModificados(formulario(), formulario(cambio)).map((c) => c.clave)).toEqual([clave])
  })

  it('los trece casos de arriba cubren los trece campos compartidos', () => {
    expect(cambios.map(([clave]) => clave).sort()).toEqual(CAMPOS_COMPARTIDOS.map((c) => c.clave).sort())
  })

  it('un campo propio nunca cuenta, aunque cambie todo lo que es del artículo', () => {
    const original = formulario()
    const editado = formulario({
      nombre: 'Otro nombre',
      descripcion: 'Otra descripción',
      codigoInterno: 'Z9999',
      idMarca: 8,
      activo: false,
      disponibleParaTodas: false,
      idsEmpresas: [1, 2],
    })

    expect(camposCompartidosModificados(original, editado)).toEqual([])
  })

  it('la pertenencia a la familia tampoco es un campo compartido', () => {
    expect(camposCompartidosModificados(formulario({ idFamilia: 7 }), formulario({ idFamilia: '' }))).toEqual([])
  })

  it('un id vacío y un id vacío son el mismo valor; vaciar un id que tenía valor es un cambio', () => {
    expect(camposCompartidosModificados(formulario({ idCategoria: '' }), formulario({ idCategoria: '' }))).toEqual([])
    expect(camposCompartidosModificados(formulario(), formulario({ idCategoria: '' })).map((c) => c.clave)).toEqual(['idCategoria'])
  })

  it('un decimal se compara como número: "100" y "100.0" son lo mismo, y "" no es lo mismo que "0"', () => {
    expect(camposCompartidosModificados(formulario({ costoLista: '100' }), formulario({ costoLista: '100.0' }))).toEqual([])
    expect(camposCompartidosModificados(formulario({ costoLista: '100' }), formulario({ costoLista: ' 100 ' }))).toEqual([])
    expect(camposCompartidosModificados(formulario({ costoLista: '' }), formulario({ costoLista: '0' })).map((c) => c.clave)).toEqual([
      'costoLista',
    ])
    expect(camposCompartidosModificados(formulario({ costoLista: '' }), formulario({ costoLista: '   ' }))).toEqual([])
  })

  it('devuelve cada campo modificado en el orden de CAMPOS_COMPARTIDOS, no en el orden en que se tocaron', () => {
    const modificados = camposCompartidosModificados(formulario(), formulario({ costoNominal: '1', idArea: 50, controlaLote: true }))

    expect(modificados.map((c) => c.clave)).toEqual(['idArea', 'controlaLote', 'costoNominal'])
  })

  it('deshacer un cambio lo deja sin modificar', () => {
    const original = formulario()
    const editado = formulario({ costoLista: '500' })
    const revertido = { ...editado, costoLista: original.costoLista }

    expect(camposCompartidosModificados(original, editado)).toHaveLength(1)
    expect(camposCompartidosModificados(original, revertido)).toEqual([])
  })
})

describe('camposNombradosEnMensaje', () => {
  it('nombra los campos cuyas columnas aparecen en el texto del servidor', () => {
    const mensaje =
      'Los campos compartidos del artículo tienen que ser idénticos a los de la familia "Sabores": difieren id_area, costo_lista.'

    expect(camposNombradosEnMensaje(mensaje).map((c) => c.clave)).toEqual(['idArea', 'costoLista'])
  })

  it('los devuelve en el orden de CAMPOS_COMPARTIDOS y sin repetir', () => {
    expect(camposNombradosEnMensaje('difieren costo_nominal, id_grupo, costo_nominal.').map((c) => c.clave)).toEqual([
      'idGrupo',
      'costoNominal',
    ])
  })

  it('no confunde una columna con otra que la contiene ni con un identificador más largo', () => {
    expect(camposNombradosEnMensaje('difieren xid_area, costo_lista_total, mi_costo_nominal.')).toEqual([])
    expect(camposNombradosEnMensaje('difieren costo_lista.').map((c) => c.clave)).toEqual(['costoLista'])
  })

  it('un texto sin columnas conocidas no nombra ningún campo', () => {
    expect(camposNombradosEnMensaje('La familia está inactiva.')).toEqual([])
    expect(camposNombradosEnMensaje('')).toEqual([])
  })
})

describe('opcionesDeFamilia', () => {
  it('ofrece las familias activas con al menos un miembro vivo, con su cantidad en la etiqueta', () => {
    expect(
      opcionesDeFamilia([
        familiaListado({ id: 1, nombre: 'Sabores', cantidadArticulos: 3 }),
        familiaListado({ id: 2, nombre: 'Talles', cantidadArticulos: 1 }),
      ]),
    ).toEqual([
      { valor: 1, etiqueta: 'Sabores (3 artículos)' },
      { valor: 2, etiqueta: 'Talles (1 artículo)' },
    ])
  })

  it('deja afuera la inactiva: una familia inactiva no admite artículos nuevos', () => {
    expect(opcionesDeFamilia([familiaListado({ id: 1, activo: false })])).toEqual([])
  })

  it('deja afuera la que no tiene miembros vivos: sin referencia no hay valores ni precios que copiar', () => {
    expect(opcionesDeFamilia([familiaListado({ id: 1, cantidadArticulos: 0 })])).toEqual([])
  })

  it('conserva el orden que manda el servidor', () => {
    const ids = opcionesDeFamilia([
      familiaListado({ id: 9, nombre: 'Zeta' }),
      familiaListado({ id: 3, nombre: 'Alfa' }),
      familiaListado({ id: 5, nombre: 'Mu' }),
    ]).map((o) => o.valor)

    expect(ids).toEqual([9, 3, 5])
  })

  it('sin familias no ofrece nada', () => {
    expect(opcionesDeFamilia([])).toEqual([])
  })
})

describe('cantidadDeArticulos', () => {
  it.each([
    [0, '0 artículos'],
    [1, '1 artículo'],
    [2, '2 artículos'],
    [30, '30 artículos'],
  ])('%i → %s', (cantidad, texto) => {
    expect(cantidadDeArticulos(cantidad)).toBe(texto)
  })
})

describe('familiaDelArticulo', () => {
  it('sin familia en el formulario no hay familia', () => {
    expect(familiaDelArticulo('', detalleFixture())).toBeNull()
    expect(familiaDelArticulo('', null)).toBeNull()
  })

  it('con el detalle de esa misma familia conoce su nombre y su cantidad de miembros', () => {
    expect(familiaDelArticulo(7, detalleFixture())).toEqual({ id: 7, nombre: 'Sabores', cantidad: 3 })
  })

  it('un artículo es miembro aunque no se conozca su familia: sin detalle, nombre y cantidad quedan en null', () => {
    expect(familiaDelArticulo(7, null)).toEqual({ id: 7, nombre: null, cantidad: null })
  })

  it('el detalle de OTRA familia no se le atribuye', () => {
    expect(familiaDelArticulo(8, detalleFixture({ id: 7 }))).toEqual({ id: 8, nombre: null, cantidad: null })
  })
})

describe('textos de la familia', () => {
  const conocida = { id: 7, nombre: 'Sabores', cantidad: 4 }

  it('descripcionDeFamilia nombra la familia y cuenta sus artículos', () => {
    expect(descripcionDeFamilia(conocida)).toBe('de la familia "Sabores" (4 artículos)')
    expect(descripcionDeFamilia({ ...conocida, cantidad: 1 })).toBe('de la familia "Sabores" (1 artículo)')
  })

  it('con el nombre conocido y la cantidad no, omite la cantidad; sin nombre dice "de una familia"', () => {
    expect(descripcionDeFamilia({ ...conocida, cantidad: null })).toBe('de la familia "Sabores"')
    expect(descripcionDeFamilia({ id: 7, nombre: null, cantidad: null })).toBe('de una familia')
  })

  it('etiquetaDeFamilia es el rótulo del miembro en el formulario', () => {
    expect(etiquetaDeFamilia(conocida)).toBe('Familia "Sabores" (4 artículos)')
    expect(etiquetaDeFamilia({ ...conocida, cantidad: null })).toBe('Familia "Sabores"')
    expect(etiquetaDeFamilia({ id: 7, nombre: null, cantidad: null })).toBe('Pertenece a una familia')
  })

  it('contextoDeAlcance abre la pregunta nombrando la familia', () => {
    expect(contextoDeAlcance(conocida)).toBe('Este artículo es parte de la familia "Sabores" (4 artículos).')
    expect(contextoDeAlcance({ id: 7, nombre: null, cantidad: null })).toBe('Este artículo es parte de una familia.')
  })
})

describe('altaConFamiliaLista', () => {
  const lista = { detalle: detalleFixture(), cargando: false, error: '' }

  it('sin familia elegida siempre se puede guardar', () => {
    expect(altaConFamiliaLista('', { detalle: null, cargando: true, error: 'x' })).toBe(true)
  })

  it('con la familia elegida cargada y con referencia, se puede guardar', () => {
    expect(altaConFamiliaLista(7, lista)).toBe(true)
  })

  it('mientras la familia carga, no', () => {
    expect(altaConFamiliaLista(7, { ...lista, cargando: true })).toBe(false)
  })

  it('con la carga fallida, no', () => {
    expect(altaConFamiliaLista(7, { detalle: null, cargando: false, error: 'No se pudo cargar la familia.' })).toBe(false)
  })

  it('sin detalle, no', () => {
    expect(altaConFamiliaLista(7, { detalle: null, cargando: false, error: '' })).toBe(false)
  })

  it('con el detalle de otra familia (la elegida todavía no llegó), no', () => {
    expect(altaConFamiliaLista(8, lista)).toBe(false)
  })

  it('con una familia sin referencia (sin miembros vivos), no: no hay valores que copiar', () => {
    expect(altaConFamiliaLista(7, { ...lista, detalle: detalleFixture({ valores: null, articulos: [] }) })).toBe(false)
  })
})

describe('describirEstadoDePrecios', () => {
  it('sin ningún precio es una raya', () => {
    expect(describirEstadoDePrecios({ vigente: null, pendiente: null })).toBe('—')
  })

  it('solo el vigente es su importe', () => {
    expect(describirEstadoDePrecios({ vigente: 1200, pendiente: null })).toBe('$ 1.200,00')
  })

  it('con un pendiente lo agrega con su fecha', () => {
    const iso = '2026-10-12T15:00:00+00:00'

    expect(describirEstadoDePrecios({ vigente: 1200, pendiente: { monto: 1300, vigenteDesde: iso } })).toBe(
      `$ 1.200,00 · programado $ 1.300,00 desde ${formatearFechaHora(iso)}`,
    )
  })

  it('un pendiente sin vigente muestra solo lo programado, sin una raya delante', () => {
    const iso = '2026-10-12T15:00:00+00:00'

    expect(describirEstadoDePrecios({ vigente: null, pendiente: { monto: 1300, vigenteDesde: iso } })).toBe(
      `programado $ 1.300,00 desde ${formatearFechaHora(iso)}`,
    )
  })

  it('un vigente en cero es un precio, no la ausencia de precio', () => {
    expect(describirEstadoDePrecios({ vigente: 0, pendiente: null })).toBe('$ 0,00')
  })
})

describe('mensajes de un guardado con familia', () => {
  it('un alta sin familia no menciona ninguna', () => {
    expect(mensajeDeAlta('Vainilla', 'A0007', null)).toBe('Artículo "Vainilla" creado con código interno A0007.')
  })

  it('un alta dentro de una familia dice que tomó sus valores compartidos y sus precios', () => {
    expect(mensajeDeAlta('Vainilla', 'A0007', { id: 7, nombre: 'Sabores', cantidad: 3 })).toBe(
      'Artículo "Vainilla" creado con código interno A0007. Es parte de la familia "Sabores": tomó sus valores compartidos y sus precios.',
    )
    expect(mensajeDeAlta('Vainilla', 'A0007', { id: 7, nombre: null, cantidad: null })).toContain('Es parte de una familia:')
  })

  it('una edición sin alcance dice solo que se actualizó', () => {
    expect(mensajeDeEdicion('Vainilla', undefined, 'Sabores')).toBe('Artículo "Vainilla" actualizado.')
  })

  it('con "Familia" dice que los campos compartidos llegaron a toda la familia', () => {
    expect(mensajeDeEdicion('Vainilla', 'Familia', 'Sabores')).toBe(
      'Artículo "Vainilla" actualizado. Los cambios en los campos compartidos se aplicaron a toda la familia "Sabores".',
    )
    expect(mensajeDeEdicion('Vainilla', 'Familia', null)).toContain('se aplicaron a toda la familia.')
  })

  it('con "Solo este" dice que salió de la familia y que el cambio quedó solo en él', () => {
    expect(mensajeDeEdicion('Vainilla', 'SoloEste', 'Sabores')).toBe(
      'Artículo "Vainilla" actualizado. Salió de la familia "Sabores" y el cambio quedó solo en él.',
    )
    expect(mensajeDeEdicion('Vainilla', 'SoloEste', null)).toContain('Salió de la familia y')
  })

  it('avisoDeValoresDistintos nombra los campos que difieren, o no los nombra si no se conocen', () => {
    const campos = CAMPOS_COMPARTIDOS.filter((c) => c.clave === 'idArea' || c.clave === 'costoLista')

    expect(avisoDeValoresDistintos(campos)).toBe(
      'Los valores del artículo no coinciden con los de la familia en: Área, Costo de lista. Se volvieron a cargar los de la familia: revisalos y guardá de nuevo.',
    )
    expect(avisoDeValoresDistintos([])).toBe(
      'Los valores del artículo no coinciden con los de la familia. Se volvieron a cargar los de la familia: revisalos y guardá de nuevo.',
    )
  })
})

describe('CODIGOS_DE_CONFLICTO_DE_ALTA', () => {
  it('son los tres rechazos que el servidor da cuando un artículo no puede entrar a la familia elegida', () => {
    expect([...CODIGOS_DE_CONFLICTO_DE_ALTA]).toEqual(['familia_valores_distintos', 'familia_inactiva', 'familia_sin_articulos'])
  })
})
