import { describe, expect, it } from 'vitest'
import { ErrorApi } from '../api/cliente'
import type { DiaDeCargaSemanal, FilaDeCargaSemanal, SemanaDeCargaDeHoras } from '../api/tipos'
import {
  armarSolicitud,
  claveDeCelda,
  editarCelda,
  erroresDeCeldas,
  errorDeGuardadoSemanal,
  etiquetaDeDia,
  etiquetaDeSemana,
  importeDeFila,
  lunesDe,
  sumarDias,
  textoBaseDeDia,
  textoDeCelda,
  totalDeHorasDeFila,
  totalDeHorasDelDia,
  type Ediciones,
} from './semanaDeCargaDeHoras'

const FECHAS = ['2026-10-26', '2026-10-27', '2026-10-28', '2026-10-29', '2026-10-30', '2026-10-31', '2026-11-01']

function dia(indice: number, parche: Partial<DiaDeCargaSemanal> = {}): DiaDeCargaSemanal {
  return {
    fecha: FECHAS[indice],
    horasCargadas: 0,
    confirmado: false,
    horas: null,
    precargado: false,
    futuro: false,
    ...parche,
  }
}

function fila(id: number, dias: DiaDeCargaSemanal[], parche: Partial<FilaDeCargaSemanal> = {}): FilaDeCargaSemanal {
  return { idPersonal: id, nombre: `Empleado ${id}`, valorHora: 1000, horarioHabitual: null, dias, ...parche }
}

function semana(filas: FilaDeCargaSemanal[]): SemanaDeCargaDeHoras {
  return { desde: FECHAS[0], hasta: FECHAS[6], hoy: '2026-10-28', filas }
}

const SIN_EDICIONES: Ediciones = new Map()

describe('semanas', () => {
  it('lunesDe devuelve el mismo día si ya es lunes y retrocede desde el resto', () => {
    expect(lunesDe('2026-10-26')).toBe('2026-10-26')
    expect(lunesDe('2026-10-28')).toBe('2026-10-26')
  })

  it('lunesDe trata el domingo como el último día de la semana, no como el primero', () => {
    expect(lunesDe('2026-11-01')).toBe('2026-10-26')
  })

  it('lunesDe cruza el límite de año', () => {
    expect(lunesDe('2027-01-01')).toBe('2026-12-28')
  })

  it('sumarDias mueve de a semanas, incluso cruzando mes y año', () => {
    expect(sumarDias('2026-10-26', 7)).toBe('2026-11-02')
    expect(sumarDias('2026-10-26', -7)).toBe('2026-10-19')
    expect(sumarDias('2026-12-28', 7)).toBe('2027-01-04')
  })

  it('etiquetas de día y de semana', () => {
    expect(etiquetaDeDia('2026-10-26')).toBe('Lun 26/10')
    expect(etiquetaDeDia('2026-10-28')).toBe('Mié 28/10')
    expect(etiquetaDeDia('2026-10-31')).toBe('Sáb 31/10')
    expect(etiquetaDeDia('2026-11-01')).toBe('Dom 01/11')
    expect(etiquetaDeSemana('2026-10-26', '2026-11-01')).toBe('26/10 – 01/11')
  })
})

describe('textoBaseDeDia', () => {
  it('formatea las horas con coma y deja vacío lo que no tiene valor', () => {
    expect(textoBaseDeDia(dia(0, { horas: 7.5 }))).toBe('7,5')
    expect(textoBaseDeDia(dia(0, { horas: 8 }))).toBe('8')
    expect(textoBaseDeDia(dia(0, { horas: 0, confirmado: true }))).toBe('0')
    expect(textoBaseDeDia(dia(0, { horas: null }))).toBe('')
  })
})

describe('edición de celdas', () => {
  const f = fila(1, [dia(0, { horas: 8, precargado: true }), dia(1), dia(2, { futuro: true })])

  it('una edición distinta de lo mostrado queda registrada y se ve en la celda', () => {
    const ediciones = editarCelda(SIN_EDICIONES, f.idPersonal, f.dias[0], '6,5')

    expect(textoDeCelda(f.idPersonal, f.dias[0], ediciones)).toBe('6,5')
    expect(ediciones.size).toBe(1)
  })

  it('volver a escribir el valor original deja de contar como cambio', () => {
    const editada = editarCelda(SIN_EDICIONES, f.idPersonal, f.dias[0], '6')
    const vuelta = editarCelda(editada, f.idPersonal, f.dias[0], '8')

    expect(vuelta.size).toBe(0)
  })

  it('descarta letras y espacios al tipear', () => {
    const ediciones = editarCelda(SIN_EDICIONES, f.idPersonal, f.dias[1], '7 h')

    expect(textoDeCelda(f.idPersonal, f.dias[1], ediciones)).toBe('7')
  })

  it('no muta el mapa original', () => {
    editarCelda(SIN_EDICIONES, f.idPersonal, f.dias[1], '7')

    expect(SIN_EDICIONES.size).toBe(0)
  })
})

describe('erroresDeCeldas', () => {
  const f = fila(1, [dia(0), dia(1), dia(2)])
  const s = semana([f])

  it('sin ediciones no hay errores', () => {
    expect(erroresDeCeldas(s, SIN_EDICIONES).size).toBe(0)
  })

  it('más de 24 horas es un error de esa celda', () => {
    const ediciones = editarCelda(SIN_EDICIONES, 1, f.dias[0], '25')

    expect(erroresDeCeldas(s, ediciones).get(claveDeCelda(1, FECHAS[0]))).toMatch(/24/)
  })

  it('un número mal formado es un error', () => {
    const ediciones = editarCelda(SIN_EDICIONES, 1, f.dias[1], '1,2,3')

    expect(erroresDeCeldas(s, ediciones).has(claveDeCelda(1, FECHAS[1]))).toBe(true)
  })

  it('más de dos decimales es un error', () => {
    const ediciones = editarCelda(SIN_EDICIONES, 1, f.dias[1], '1,234')

    expect(erroresDeCeldas(s, ediciones).has(claveDeCelda(1, FECHAS[1]))).toBe(true)
  })

  it('24 exacto y un decimal válido no son errores', () => {
    const a = editarCelda(SIN_EDICIONES, 1, f.dias[0], '24')
    const b = editarCelda(a, 1, f.dias[1], '7,25')

    expect(erroresDeCeldas(s, b).size).toBe(0)
  })
})

describe('armarSolicitud', () => {
  it('manda el día precargado sin que el usuario lo toque: confirma el horario habitual', () => {
    const s = semana([fila(1, [dia(0, { horas: 8, precargado: true })])])

    expect(armarSolicitud(s, SIN_EDICIONES)).toEqual({ dias: [{ idPersonal: 1, fecha: FECHAS[0], horas: 8 }] })
  })

  it('un precargado de franco (0) también se manda', () => {
    const s = semana([fila(1, [dia(5, { horas: 0, precargado: true })])])

    expect(armarSolicitud(s, SIN_EDICIONES).dias).toEqual([{ idPersonal: 1, fecha: FECHAS[5], horas: 0 }])
  })

  it('no manda lo confirmado que nadie tocó ni lo vacío sin horario habitual', () => {
    const s = semana([fila(1, [dia(0, { horas: 6, confirmado: true, horasCargadas: 6 }), dia(1)])])

    expect(armarSolicitud(s, SIN_EDICIONES).dias).toEqual([])
  })

  it('manda lo editado, sea una celda vacía que se completa o una confirmada que se corrige', () => {
    const f = fila(1, [dia(0, { horas: 6, confirmado: true, horasCargadas: 6 }), dia(1)])
    let ediciones = editarCelda(SIN_EDICIONES, 1, f.dias[0], '5,5')
    ediciones = editarCelda(ediciones, 1, f.dias[1], '4')

    expect(armarSolicitud(semana([f]), ediciones).dias).toEqual([
      { idPersonal: 1, fecha: FECHAS[0], horas: 5.5 },
      { idPersonal: 1, fecha: FECHAS[1], horas: 4 },
    ])
  })

  it('vaciar una celda confirmada manda 0 para dejar el día en cero', () => {
    const f = fila(1, [dia(0, { horas: 6, confirmado: true, horasCargadas: 6 })])
    const ediciones = editarCelda(SIN_EDICIONES, 1, f.dias[0], '')

    expect(armarSolicitud(semana([f]), ediciones).dias).toEqual([{ idPersonal: 1, fecha: FECHAS[0], horas: 0 }])
  })

  it('vaciar una celda precargada manda 0: el usuario dijo que ese día no se trabajó', () => {
    const f = fila(1, [dia(0, { horas: 8, precargado: true })])
    const ediciones = editarCelda(SIN_EDICIONES, 1, f.dias[0], '')

    expect(armarSolicitud(semana([f]), ediciones).dias).toEqual([{ idPersonal: 1, fecha: FECHAS[0], horas: 0 }])
  })

  it('nunca manda un día futuro, ni siquiera editado', () => {
    const f = fila(1, [dia(4, { futuro: true, horas: 8, precargado: true }), dia(5, { futuro: true })])
    const ediciones = editarCelda(SIN_EDICIONES, 1, f.dias[1], '3')

    expect(armarSolicitud(semana([f]), ediciones).dias).toEqual([])
  })

  it('una celda precargada y editada manda lo editado, no el horario habitual', () => {
    const f = fila(1, [dia(0, { horas: 8, precargado: true })])
    const ediciones = editarCelda(SIN_EDICIONES, 1, f.dias[0], '9')

    expect(armarSolicitud(semana([f]), ediciones).dias).toEqual([{ idPersonal: 1, fecha: FECHAS[0], horas: 9 }])
  })

  it('recorre las filas en orden y cada empleado manda lo suyo', () => {
    const a = fila(1, [dia(0, { horas: 8, precargado: true })])
    const b = fila(2, [dia(0, { horas: 4, precargado: true })])

    expect(armarSolicitud(semana([a, b]), SIN_EDICIONES).dias.map((d) => d.idPersonal)).toEqual([1, 2])
  })
})

describe('totales', () => {
  const f = fila(1, [dia(0, { horas: 8, precargado: true }), dia(1, { horas: 6.5, confirmado: true }), dia(2), dia(3, { futuro: true })], {
    valorHora: 1000,
  })
  const s = semana([f, fila(2, [dia(0, { horas: 2, confirmado: true }), dia(1), dia(2), dia(3)])])

  it('suma lo que se ve en la fila, con las ediciones aplicadas', () => {
    expect(totalDeHorasDeFila(f, SIN_EDICIONES)).toBe(14.5)

    const ediciones = editarCelda(SIN_EDICIONES, 1, f.dias[2], '1,25')
    expect(totalDeHorasDeFila(f, ediciones)).toBe(15.75)
  })

  it('una celda con error no suma', () => {
    const ediciones = editarCelda(SIN_EDICIONES, 1, f.dias[2], '1,2,3')

    expect(totalDeHorasDeFila(f, ediciones)).toBe(14.5)
  })

  it('el importe estimado es el total de horas por el valor hora', () => {
    expect(importeDeFila(f, SIN_EDICIONES)).toBe(14500)
  })

  it('suma el día entre empleados', () => {
    expect(totalDeHorasDelDia(s, 0, SIN_EDICIONES)).toBe(10)
    expect(totalDeHorasDelDia(s, 1, SIN_EDICIONES)).toBe(6.5)
  })
})

describe('errorDeGuardadoSemanal', () => {
  it('traduce los códigos de la API por código, nunca por mensaje', () => {
    expect(errorDeGuardadoSemanal(new ErrorApi(400, 'fecha_futura', 'x'))).toMatch(/futur/)
    expect(errorDeGuardadoSemanal(new ErrorApi(400, 'horas_del_dia_excedidas', 'x'))).toMatch(/24/)
    expect(errorDeGuardadoSemanal(new ErrorApi(400, 'semana_invalida', 'x'))).toMatch(/semana/)
    expect(errorDeGuardadoSemanal(new ErrorApi(400, 'horas_invalidas', 'x'))).toMatch(/horas/i)
  })

  it('distingue permiso y empleado inexistente', () => {
    expect(errorDeGuardadoSemanal(new ErrorApi(403, 'prohibido', 'x'))).toMatch(/permiso/)
    expect(errorDeGuardadoSemanal(new ErrorApi(404, 'no_encontrado', 'x'))).toMatch(/empleado/)
  })

  it('un código desconocido cae al mensaje del servidor y, sin mensaje, a una copia genérica', () => {
    expect(errorDeGuardadoSemanal(new ErrorApi(400, 'otro', 'Mensaje del servidor'))).toBe('Mensaje del servidor')
    expect(errorDeGuardadoSemanal(new ErrorApi(400, 'otro', '  '))).toBe('No se pudo guardar la semana.')
  })

  it('un código heredado del prototipo no resuelve contra el mapa', () => {
    expect(errorDeGuardadoSemanal(new ErrorApi(400, 'constructor', 'Mensaje'))).toBe('Mensaje')
  })
})
