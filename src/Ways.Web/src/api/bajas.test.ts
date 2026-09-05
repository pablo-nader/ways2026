import { describe, expect, it } from 'vitest'
import { arrastreDeTenant, copiaDeFalloDeBaja } from './bajas'
import { ErrorApi } from './cliente'

// stage-20-organizacion-relaciones-y-bajas, slice 5 (tareas 5.2, 5.7 y 5.8).

/** Los seis códigos de la etapa 20, en el orden del spec (`BO-R11`). */
const SEIS_CODIGOS = [
  'tenant_en_uso',
  'empresa_en_uso',
  'punto_venta_en_uso',
  'usuario_en_uso',
  'ultima_empresa_del_tenant',
  'ultimo_punto_venta_de_la_empresa',
] as const

describe('copiaDeFalloDeBaja — la copia se elige por código', () => {
  /**
   * Cláusula bajo prueba: el `GUIA_POR_CODIGO[error.codigo]`. Con un mapa vacío —o con una copia
   * genérica compartida— los seis rechazos rendirían el mismo texto y el operador no sabría cuál
   * de las dos familias le tocó (uso vs. mínimo estructural), que llevan acciones distintas.
   *
   * Se asserta que las seis son PAIRWISE distintas, no solo que existen: dos entradas iguales del
   * mapa pasan cualquier test que mire una sola.
   */
  it('los seis códigos rinden seis copias distintas entre sí', () => {
    const copias = SEIS_CODIGOS.map((codigo) =>
      copiaDeFalloDeBaja(new ErrorApi(409, codigo, 'mensaje del servidor'), 'el tenant'),
    )

    expect(new Set(copias).size).toBe(SEIS_CODIGOS.length)
  })

  /**
   * Cláusula bajo prueba: que la SELECCIÓN sea `error.codigo` y no `error.message` (spec
   * `bajas-de-organizacion` → *The web maps copy from the code*). El mensaje cambia entero entre
   * las dos llamadas; lo que la web agrega tiene que quedar byte a byte igual.
   */
  it('cambiar el mensaje no cambia la copia que se elige', () => {
    const conMensajeLargo = copiaDeFalloDeBaja(
      new ErrorApi(409, 'ultima_empresa_del_tenant', 'Es la única empresa del tenant.'),
      'la empresa',
    )
    const conMensajeDegradado = copiaDeFalloDeBaja(
      new ErrorApi(409, 'ultima_empresa_del_tenant', 'Conflicto.'),
      'la empresa',
    )

    const guia = 'La baja del tenant se hace desde la pantalla de Tenants.'
    expect(conMensajeLargo).toContain(guia)
    expect(conMensajeDegradado).toContain(guia)
    expect(conMensajeLargo).not.toBe(conMensajeDegradado)
  })

  /**
   * Cláusula bajo prueba: el `detalle` que antecede a la guía. El mensaje del servidor es lo único
   * que nombra QUÉ bloquea (la tabla, el punto de venta, la cantidad); tragarlo en un error
   * genérico dejaba al operador sin nada accionable.
   */
  it('rinde el mensaje del servidor, que es lo único que nombra el bloqueo', () => {
    const copia = copiaDeFalloDeBaja(
      new ErrorApi(409, 'tenant_en_uso', 'No se puede dar de baja el tenant porque tiene 3 ventas.'),
      'el tenant',
    )

    expect(copia).toContain('porque tiene 3 ventas')
    expect(copia).toContain('Dá de baja o reasigná esos datos antes de eliminar el tenant.')
  })

  it('un mensaje vacío no deja el cartel sin texto', () => {
    const copia = copiaDeFalloDeBaja(new ErrorApi(409, 'empresa_en_uso', '   '), 'la empresa')

    expect(copia).toContain('No se pudo dar de baja la empresa.')
    expect(copia).toContain('Dá de baja o reasigná esos datos antes de eliminar la empresa.')
  })

  /**
   * Cláusula bajo prueba: la rama `error.estado === 404`, ANTES de mirar el código. Un admin de
   * tenant que apunta a una entidad de otro tenant recibe este mismo 404 (ADR-8), así que la copia
   * no puede insinuar nada sobre el uso ni sobre el alcance — y el mensaje del servidor NO se
   * anexa, para que un futuro texto del servidor no filtre por acá (spec BO-R12).
   */
  it('un 404 rinde la copia neutra de inexistencia y no filtra el mensaje del servidor', () => {
    const copia = copiaDeFalloDeBaja(
      new ErrorApi(404, 'no_encontrado', 'No existe la empresa 77.'),
      'la empresa',
    )

    expect(copia).toBe('No se pudo dar de baja la empresa. Ya no existe o no está a tu alcance. Actualizá el listado.')
    expect(copia).not.toContain('77')
    expect(copia).not.toMatch(/en uso|tiene|ventas/i)
  })

  /**
   * Cláusula bajo prueba: la rama `error.estado >= 500`. Las tres bajas corren SIN reintento
   * automático, así que un commit cuyo ACK se perdió llega como 500 sobre una baja que sí quedó
   * hecha: la copia manda a verificar el listado antes de reintentar, nunca a reintentar a ciegas.
   * (Entrada arrastrada de la slice 4, punto 5.)
   */
  it('un 500 avisa que el resultado es incierto y manda a verificar el listado', () => {
    const copia = copiaDeFalloDeBaja(
      new ErrorApi(500, 'error_interno', 'Ocurrió un error inesperado.'),
      'el punto de venta',
    )

    expect(copia).toContain('verificá el listado antes de reintentar')
    expect(copia).not.toMatch(/reintentá ahora|volvé a intentar/i)
  })

  it('un error que no es de la API comparte la copia del resultado incierto', () => {
    expect(copiaDeFalloDeBaja(new TypeError('Failed to fetch'), 'el usuario')).toContain(
      'verificá el listado antes de reintentar',
    )
  })

  /** Un código que la web no conoce (403, un séptimo código futuro) no rompe: rinde el mensaje. */
  it('un código desconocido rinde el mensaje del servidor sin guía inventada', () => {
    const copia = copiaDeFalloDeBaja(
      new ErrorApi(409, 'codigo_del_futuro', 'Algo nuevo bloquea la baja.'),
      'el tenant',
    )

    expect(copia).toBe('Algo nuevo bloquea la baja.')
  })
})

describe('arrastreDeTenant', () => {
  /** Contadores pairwise-distintos: con valores iguales, intercambiar dos líneas no se vería. */
  it('nombra las tres familias de hijos con su cantidad', () => {
    expect(arrastreDeTenant({ cantidadEmpresas: 2, cantidadPuntosVenta: 3, cantidadUsuarios: 4 })).toEqual([
      '2 empresas',
      '3 puntos de venta',
      '4 usuarios',
    ])
  })

  it('usa el singular cuando hay uno solo', () => {
    expect(arrastreDeTenant({ cantidadEmpresas: 1, cantidadPuntosVenta: 1, cantidadUsuarios: 1 })).toEqual([
      '1 empresa',
      '1 punto de venta',
      '1 usuario',
    ])
  })

  it('no lista las familias vacías', () => {
    expect(arrastreDeTenant({ cantidadEmpresas: 1, cantidadPuntosVenta: 0, cantidadUsuarios: 0 })).toEqual([
      '1 empresa',
    ])
    expect(arrastreDeTenant({ cantidadEmpresas: 0, cantidadPuntosVenta: 0, cantidadUsuarios: 0 })).toEqual([])
  })
})
