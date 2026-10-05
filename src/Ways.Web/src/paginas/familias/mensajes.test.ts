import { describe, expect, it } from 'vitest'
import { ErrorApi } from '../../api/cliente'
import { mensajeDeError, mensajeDeFalloDeEscritura, mensajeDeProblema } from './mensajes'

describe('mensajeDeProblema', () => {
  it.each([
    ['familia_precio_inalineable', 'Un precio nunca se quita'],
    ['articulo_en_otra_familia', 'Agrupar no mueve a nadie de su familia'],
    ['referencia_invalida', 'Revisá que todos los artículos existan'],
    ['demasiados_articulos', 'Elegí menos artículos'],
    ['familia_nombre_duplicado', 'Elegí otro nombre.'],
    ['familia_inactiva', 'Activala desde el listado de familias'],
    ['familia_sin_articulos', 'Una familia sin artículos vivos no tiene referencia'],
  ])('%s: el mensaje del servidor va primero y la ayuda detrás', (codigo, ayuda) => {
    const texto = mensajeDeProblema(codigo, 'Dice el servidor.')

    expect(texto.startsWith('Dice el servidor. ')).toBe(true)
    expect(texto).toContain(ayuda)
  })

  it('un código que no conoce deja el mensaje del servidor tal cual', () => {
    expect(mensajeDeProblema('codigo_nuevo', 'Dice el servidor.')).toBe('Dice el servidor.')
  })

  it('el mensaje se recorta y nunca se tira', () => {
    expect(mensajeDeProblema('codigo_nuevo', '  Dice el servidor.  ')).toBe('Dice el servidor.')
  })

  it('sin mensaje del servidor queda la ayuda sola, no un aviso vacío', () => {
    expect(mensajeDeProblema('familia_nombre_duplicado', '   ')).toBe('Elegí otro nombre.')
  })

  /** Cláusula bajo prueba: la ayuda se busca en un `Map` y no en un objeto: el código viene del servidor y sobre
   * un objeto `AYUDA['constructor']` resolvería contra el prototipo. */
  it.each(['constructor', 'toString', '__proto__', 'hasOwnProperty'])('el código exótico "%s" no se confunde con una ayuda', (codigo) => {
    expect(mensajeDeProblema(codigo, 'Dice el servidor.')).toBe('Dice el servidor.')
  })

  it('la ayuda se elige por el código, no por lo que diga el mensaje', () => {
    expect(mensajeDeProblema('codigo_nuevo', 'Ya existe una familia con ese nombre.')).toBe('Ya existe una familia con ese nombre.')
  })
})

describe('mensajeDeError', () => {
  it('un rechazo del servidor rinde su mensaje con la ayuda de su código', () => {
    expect(mensajeDeError(new ErrorApi(409, 'familia_nombre_duplicado', 'Ya existe una familia llamada "Sabores".'), 'crear la familia')).toBe(
      'Ya existe una familia llamada "Sabores". Elegí otro nombre.',
    )
  })

  it('un rechazo sin mensaje ni ayuda conocida cae en el texto de la acción', () => {
    expect(mensajeDeError(new ErrorApi(500, 'error', ''), 'crear la familia')).toBe('No se pudo crear la familia.')
  })

  it('un error que no es de la API (la red se cayó) dice qué no se pudo hacer', () => {
    expect(mensajeDeError(new TypeError('Failed to fetch'), 'previsualizar la agrupación')).toBe('No se pudo previsualizar la agrupación.')
  })
})

const COPIA_INCIERTA = 'No se pudo confirmar el resultado: verificá el listado antes de reintentar.'

describe('mensajeDeFalloDeEscritura', () => {
  /** Cláusula bajo prueba: la rama `!(error instanceof ErrorApi)`: si la red se cayó no se sabe si el servidor llegó a
   * commitear, así que no alcanza con decir "no se pudo". */
  it('un error que no es de la API (la red se cayó) comparte la copia del resultado incierto de las bajas', () => {
    expect(mensajeDeFalloDeEscritura(new TypeError('Failed to fetch'), 'crear la familia')).toBe(`No se pudo crear la familia. ${COPIA_INCIERTA}`)
  })

  /** Cláusula bajo prueba: la rama `estado >= 500` sin `resultado_incierto`: el mensaje de un `error_interno` no agrega
   * nada y no se anexa. */
  it('un 5xx sin código estable rinde la copia del resultado incierto y no el mensaje del servidor', () => {
    const texto = mensajeDeFalloDeEscritura(new ErrorApi(500, 'error_interno', 'Se cayó el servidor.'), 'guardar la familia')

    expect(texto).toBe(`No se pudo guardar la familia. ${COPIA_INCIERTA}`)
    expect(texto).not.toContain('Se cayó el servidor.')
  })

  /** Cláusula bajo prueba: `codigo === 'resultado_incierto'` dentro de la rama de 5xx: ahí el mensaje del servidor es
   * lo único que dice qué verificar. */
  it('un 503 resultado_incierto rinde el mensaje del servidor', () => {
    expect(
      mensajeDeFalloDeEscritura(new ErrorApi(503, 'resultado_incierto', 'No se pudo confirmar la creación: buscá la familia en el listado.'), 'crear la familia'),
    ).toBe('No se pudo crear la familia. No se pudo confirmar la creación: buscá la familia en el listado.')
  })

  it('un resultado_incierto con el mensaje vacío cae en la copia local, no en un aviso sin guía', () => {
    expect(mensajeDeFalloDeEscritura(new ErrorApi(503, 'resultado_incierto', '   '), 'sacar el artículo de la familia')).toBe(
      `No se pudo sacar el artículo de la familia. ${COPIA_INCIERTA}`,
    )
  })

  /** Cláusula bajo prueba: `estado < 500`: un rechazo del servidor sabe que no escribió nada y rinde su mensaje con la
   * ayuda de su código, igual que `mensajeDeError`. */
  it('un rechazo 4xx del servidor rinde su mensaje con la ayuda de su código, sin la copia del resultado incierto', () => {
    const texto = mensajeDeFalloDeEscritura(new ErrorApi(409, 'familia_nombre_duplicado', 'Ya existe una familia llamada "Sabores".'), 'crear la familia')

    expect(texto).toBe('Ya existe una familia llamada "Sabores". Elegí otro nombre.')
    expect(texto).not.toContain(COPIA_INCIERTA)
  })

  it('un 4xx sin mensaje ni ayuda conocida cae en el texto de la acción', () => {
    expect(mensajeDeFalloDeEscritura(new ErrorApi(400, 'codigo_nuevo', ''), 'agregar los artículos a la familia')).toBe(
      'No se pudo agregar los artículos a la familia.',
    )
  })
})
