import { describe, expect, it } from 'vitest'
import { ErrorApi } from '../api/cliente'
import type { ClienteListado, PersonalListado, UsuarioListado } from '../api/tipos'
import {
  aDatos,
  aFormulario,
  copiaDeBajaDePersonal,
  errorAlGuardar,
  formularioVacio,
  opcionesDeClientes,
  opcionesDeUsuarios,
  validarFormulario,
  type Formulario,
} from './personalFormulario'

function personal(sobrescribir: Partial<PersonalListado> = {}): PersonalListado {
  return {
    id: 7,
    nombre: 'Ana Gómez',
    documento: '30111222',
    telefono: '1144445555',
    observaciones: 'Turno tarde',
    valorHora: 3500.5,
    saldo: 1200,
    idEmpresa: 4,
    idUsuario: 11,
    nombreUsuario: 'ana',
    idCliente: 21,
    nombreCliente: 'Ana Gómez',
    horarioHabitual: null,
    ...sobrescribir,
  }
}

function formulario(sobrescribir: Partial<Formulario> = {}): Formulario {
  return { ...formularioVacio(), nombre: 'Ana', valorHora: 100, ...sobrescribir }
}

function usuario(sobrescribir: Partial<UsuarioListado> = {}): UsuarioListado {
  return {
    id: 1,
    usuario: 'ana',
    mail: 'ana@ways.test',
    rolId: 4,
    rol: 'Vendedor',
    estado: 'Activo',
    ultimaConexion: null,
    createdAt: '2026-10-01T00:00:00Z',
    idTenant: 1,
    nombreTenant: 'Tenant',
    ...sobrescribir,
  }
}

function cliente(sobrescribir: Partial<ClienteListado> = {}): ClienteListado {
  return {
    id: 1,
    numero: 12,
    nombre: 'Luis',
    apellido: 'Paz',
    razonSocial: null,
    tipoDocumento: null,
    numeroDocumento: null,
    idCondicionFiscal: 1,
    nacimiento: null,
    domicilio: null,
    telefono: null,
    celular: null,
    email: null,
    observaciones: null,
    idListaPrecio: 1,
    limiteCredito: 0,
    creditoIlimitado: false,
    saldo: 0,
    activo: true,
    idEmpresa: null,
    esConsumidorFinal: false,
    ...sobrescribir,
  }
}

describe('horario habitual', () => {
  const vacio = ['', '', '', '', '', '', '']

  it('todo vacío viaja como null', () => {
    expect(aDatos(formulario({ horario: vacio })).horarioHabitual).toBeNull()
    expect(validarFormulario(formulario({ horario: vacio }))).toBeNull()
  })

  it('con algún día cargado, los vacíos son franco (0)', () => {
    const datos = aDatos(formulario({ horario: ['8', '', '8,5', '', '', '4.25', ''] }))

    expect(datos.horarioHabitual).toEqual([8, 0, 8.5, 0, 0, 4.25, 0])
  })

  it('acepta los límites 0 y 24', () => {
    const f = formulario({ horario: ['0', '24', '', '', '', '', ''] })

    expect(validarFormulario(f)).toBeNull()
    expect(aDatos(f).horarioHabitual).toEqual([0, 24, 0, 0, 0, 0, 0])
  })

  it.each([['24,01'], ['25'], ['1,234'], ['-1'], ['1,2,3']])('%s es inválido y se marca en horario', (texto) => {
    const error = validarFormulario(formulario({ horario: ['8', texto, '', '', '', '', ''] }))

    expect(error?.campo).toBe('horario')
  })

  it('aFormulario carga el horario existente con coma decimal y null deja todo vacío', () => {
    expect(aFormulario(personal({ horarioHabitual: [8, 8, 7.5, 0, 0, 0, 0] })).horario).toEqual(['8', '8', '7,5', '0', '0', '0', '0'])
    expect(aFormulario(personal({ horarioHabitual: null })).horario).toEqual(vacio)
  })

  it('el código horario_habitual_invalido se ubica en horario', () => {
    expect(errorAlGuardar(new ErrorApi(400, 'horario_habitual_invalido', 'x'))?.campo).toBe('horario')
  })
})

describe('formularioVacio / aFormulario', () => {
  it('el formulario vacío no tiene id, ni vínculos, ni valor hora', () => {
    expect(formularioVacio()).toEqual({
      id: null,
      nombre: '',
      documento: '',
      telefono: '',
      observaciones: '',
      valorHora: null,
      idEmpresa: null,
      idUsuario: null,
      idCliente: null,
      horario: ['', '', '', '', '', '', ''],
    })
  })

  it('aFormulario copia cada campo y conserva la empresa', () => {
    expect(aFormulario(personal())).toEqual({
      id: 7,
      nombre: 'Ana Gómez',
      documento: '30111222',
      telefono: '1144445555',
      observaciones: 'Turno tarde',
      valorHora: 3500.5,
      idEmpresa: 4,
      idUsuario: 11,
      idCliente: 21,
      horario: ['', '', '', '', '', '', ''],
    })
  })

  it('los nulos opcionales pasan a cadena vacía y los vínculos sueltos quedan en null', () => {
    const f = aFormulario(
      personal({ documento: null, telefono: null, observaciones: null, idUsuario: null, idCliente: null, idEmpresa: null }),
    )

    expect(f).toMatchObject({ documento: '', telefono: '', observaciones: '', idUsuario: null, idCliente: null, idEmpresa: null })
  })
})

describe('aDatos', () => {
  it('recorta el texto y manda cada campo del DTO', () => {
    const datos = aDatos(
      formulario({
        nombre: '  Ana  ',
        documento: ' 30111222 ',
        telefono: ' 11 4444 ',
        observaciones: ' nota ',
        valorHora: 250.75,
        idEmpresa: 4,
        idUsuario: 11,
        idCliente: 21,
      }),
    )

    expect(datos).toEqual({
      nombre: 'Ana',
      documento: '30111222',
      telefono: '11 4444',
      observaciones: 'nota',
      valorHora: 250.75,
      idEmpresa: 4,
      idUsuario: 11,
      idCliente: 21,
      horarioHabitual: null,
    })
  })

  it('un texto vacío o en blanco viaja como null y no se inventan claves fuera del DTO', () => {
    const datos = aDatos(formulario({ documento: '', telefono: '   ', observaciones: '' }))

    expect(datos.documento).toBeNull()
    expect(datos.telefono).toBeNull()
    expect(datos.observaciones).toBeNull()
    expect(Object.keys(datos).sort()).toEqual(
      ['documento', 'horarioHabitual', 'idCliente', 'idEmpresa', 'idUsuario', 'nombre', 'observaciones', 'telefono', 'valorHora'],
    )
  })

  it('los dos vínculos se pueden quitar: null viaja como null', () => {
    const datos = aDatos(formulario({ idUsuario: null, idCliente: null }))

    expect(datos.idUsuario).toBeNull()
    expect(datos.idCliente).toBeNull()
  })

  it('un valor hora vacío no es un cuerpo válido: lo frena validarFormulario, no se manda 0', () => {
    expect(() => aDatos(formulario({ valorHora: null }))).toThrow()
  })
})

describe('validarFormulario', () => {
  it('un formulario completo no tiene error', () => {
    expect(validarFormulario(formulario())).toBeNull()
  })

  it('el valor hora 0 es válido', () => {
    expect(validarFormulario(formulario({ valorHora: 0 }))).toBeNull()
  })

  it('el nombre en blanco se marca en el campo nombre', () => {
    expect(validarFormulario(formulario({ nombre: '   ' }))).toEqual({
      campo: 'nombre',
      mensaje: 'Ingresá el nombre.',
    })
  })

  it('un valor hora vacío, negativo o por encima del tope se marca en valorHora', () => {
    for (const valorHora of [null, -1, 1_000_000_000_000]) {
      expect(validarFormulario(formulario({ valorHora }))?.campo).toBe('valorHora')
    }
  })

  it('el tope inclusive del servidor es válido', () => {
    expect(validarFormulario(formulario({ valorHora: 999_999_999_999.99 }))).toBeNull()
  })
})

describe('errorAlGuardar', () => {
  const cada: [number, string, string, string][] = [
    [409, 'usuario_ya_vinculado', 'idUsuario', 'Ese usuario ya está vinculado a otro personal.'],
    [409, 'cliente_ya_vinculado', 'idCliente', 'Ese cliente ya está vinculado a otro personal.'],
    [400, 'cliente_consumidor_final', 'idCliente', 'El consumidor final no puede vincularse a un personal.'],
    [400, 'usuario_invalido', 'idUsuario', 'El usuario elegido ya no existe. Actualizá la pantalla y elegí otro.'],
    [400, 'nombre_requerido', 'nombre', 'Ingresá el nombre.'],
    [400, 'valor_hora_invalido', 'valorHora', 'El valor hora debe estar entre 0 y 999.999.999.999,99, con hasta dos decimales.'],
    [
      400,
      'referencia_invalida',
      'general',
      'Alguno de los datos elegidos ya no existe. Actualizá la pantalla e intentá de nuevo.',
    ],
  ]

  it.each(cada)('%i %s se muestra en el campo %s', (estado, codigo, campo, mensaje) => {
    expect(errorAlGuardar(new ErrorApi(estado, codigo, 'texto del servidor'))).toEqual({ campo, mensaje })
  })

  it('un 404 dice que el personal ya no existe', () => {
    expect(errorAlGuardar(new ErrorApi(404, 'no_encontrado', 'x'))).toEqual({
      campo: 'general',
      mensaje: 'El personal ya no existe. Actualizá la pantalla.',
    })
  })

  it('un código desconocido rinde el mensaje del servidor a nivel formulario', () => {
    expect(errorAlGuardar(new ErrorApi(400, 'otro_codigo', 'Mensaje del servidor.'))).toEqual({
      campo: 'general',
      mensaje: 'Mensaje del servidor.',
    })
  })

  it('un código desconocido sin mensaje cae en la copia genérica', () => {
    expect(errorAlGuardar(new ErrorApi(500, 'otro_codigo', '   ')).mensaje).toBe('No se pudo guardar.')
  })

  it('un error que no es de la API cae en la copia genérica', () => {
    expect(errorAlGuardar(new Error('boom'))).toEqual({ campo: 'general', mensaje: 'No se pudo guardar.' })
  })

  it('un código heredado del prototipo no se confunde con uno conocido', () => {
    expect(errorAlGuardar(new ErrorApi(400, 'constructor', 'Mensaje.')).campo).toBe('general')
  })
})

describe('copiaDeBajaDePersonal', () => {
  it('personal_en_uso nombra al empleado y dice que tiene movimientos', () => {
    const copia = copiaDeBajaDePersonal(new ErrorApi(409, 'personal_en_uso', 'texto del servidor'), 'Ana Gómez')

    expect(copia).toBe(
      'No se puede dar de baja a "Ana Gómez": tiene movimientos registrados en su cuenta (horas, pagos o ajustes).',
    )
  })

  it('un 404 usa la copia neutra', () => {
    expect(copiaDeBajaDePersonal(new ErrorApi(404, 'no_encontrado', 'x'), 'Ana')).toContain('Ya no existe')
  })

  it('un 5xx sin código de resultado incierto manda a verificar el listado', () => {
    expect(copiaDeBajaDePersonal(new ErrorApi(500, 'error_interno', 'x'), 'Ana')).toContain('verificá el listado')
  })

  it('un 5xx resultado_incierto rinde el texto del servidor', () => {
    expect(copiaDeBajaDePersonal(new ErrorApi(503, 'resultado_incierto', 'Verificá la baja.'), 'Ana')).toContain(
      'Verificá la baja.',
    )
  })

  it('otro rechazo rinde el mensaje del servidor y, si viene vacío, el encabezado', () => {
    expect(copiaDeBajaDePersonal(new ErrorApi(400, 'otro', 'Mensaje.'), 'Ana')).toBe('Mensaje.')
    expect(copiaDeBajaDePersonal(new ErrorApi(400, 'otro', ''), 'Ana')).toBe('No se pudo dar de baja a "Ana".')
  })

  it('un error que no es de la API manda a verificar el listado', () => {
    expect(copiaDeBajaDePersonal(new TypeError('Failed to fetch'), 'Ana')).toContain('verificá el listado')
  })
})

describe('opcionesDeUsuarios', () => {
  it('ofrece solo usuarios activos con la forma { valor, etiqueta }', () => {
    const opciones = opcionesDeUsuarios(
      [usuario({ id: 1, usuario: 'ana' }), usuario({ id: 2, usuario: 'luis', mail: 'l@x.test', estado: 'Inactivo' })],
      null,
    )

    expect(opciones).toEqual([{ id: 1, etiqueta: 'ana (ana@ways.test)' }])
  })

  it('un usuario no activo que ya está vinculado sigue ofrecido para no perder el vínculo', () => {
    const opciones = opcionesDeUsuarios([usuario({ id: 2, estado: 'Bloqueado' })], { id: 2, nombre: 'ana' })

    expect(opciones.map((o) => o.id)).toEqual([2])
  })

  it('si el vínculo actual no vino en la lista se agrega con el nombre de la fila', () => {
    const opciones = opcionesDeUsuarios([usuario({ id: 1 })], { id: 99, nombre: 'zeta' })

    expect(opciones).toContainEqual({ id: 99, etiqueta: 'zeta' })
  })

  it('sin vínculo actual no agrega nada extra', () => {
    expect(opcionesDeUsuarios([], null)).toEqual([])
  })
})

describe('opcionesDeClientes', () => {
  it('excluye al consumidor final y a los inactivos', () => {
    const opciones = opcionesDeClientes(
      [
        cliente({ id: 1 }),
        cliente({ id: 2, esConsumidorFinal: true, nombre: 'Consumidor final', apellido: null }),
        cliente({ id: 3, activo: false }),
      ],
      null,
    )

    expect(opciones.map((o) => o.id)).toEqual([1])
  })

  it('arma la etiqueta con número y nombre completo, o razón social si la hay', () => {
    const opciones = opcionesDeClientes(
      [cliente({ id: 1, numero: 12 }), cliente({ id: 2, numero: 13, razonSocial: 'Kiosco SA' })],
      null,
    )

    expect(opciones).toEqual([
      { id: 1, etiqueta: '#12 — Luis Paz' },
      { id: 2, etiqueta: '#13 — Kiosco SA' },
    ])
  })

  it('el cliente ya vinculado se conserva aunque esté inactivo o fuera de la lista', () => {
    const inactivo = opcionesDeClientes([cliente({ id: 3, activo: false })], { id: 3, nombre: 'Luis Paz' })
    const ausente = opcionesDeClientes([], { id: 50, nombre: 'Otro Cliente' })

    expect(inactivo.map((o) => o.id)).toEqual([3])
    expect(ausente).toEqual([{ id: 50, etiqueta: 'Otro Cliente' }])
  })
})
