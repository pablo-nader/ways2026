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
   * Cláusula bajo prueba: la rama `error.estado >= 500` SIN `resultado_incierto`. Un
   * `error_interno` no trae detalle útil, así que rinde el fallback local: verificar el listado
   * antes de reintentar, nunca reintentar a ciegas. (Entrada arrastrada de la slice 4, punto 5.)
   */
  it('un 500 sin resultado_incierto rinde el fallback local', () => {
    const copia = copiaDeFalloDeBaja(
      new ErrorApi(500, 'error_interno', 'Ocurrió un error inesperado.'),
      'el punto de venta',
    )

    expect(copia).toBe(
      'No se pudo dar de baja el punto de venta. No se pudo confirmar el resultado: verificá el listado antes de reintentar.',
    )
    expect(copia).not.toContain('Ocurrió un error inesperado')
    expect(copia).not.toMatch(/reintentá ahora|volvé a intentar/i)
  })

  /**
   * Cláusula bajo prueba: el `error.codigo === 'resultado_incierto'` dentro de la rama de 5xx.
   * Antes, `estado >= 500` cortocircuitaba y el `mensaje` del servidor se perdía SIEMPRE. Ahora el
   * servidor es el que clasifica el commit ambiguo (`ManejadorDeErrores` → `503
   * resultado_incierto`) y hay sitios cuya copia agrega un paso que la web no puede conocer: el
   * alta de tenant manda a restablecer la contraseña del admin, porque el `passwordTemporal` se
   * devuelve una sola vez y se fue con la respuesta que nunca llegó.
   *
   * El valor discriminante es esa frase: el fallback local NO la contiene, así que si el
   * cortocircuito volviera, esta prueba se pone roja (`mutation-proof-tests` regla 4).
   */
  it('un 503 resultado_incierto rinde la copia del servidor y no el fallback local', () => {
    const copia = copiaDeFalloDeBaja(
      new ErrorApi(
        503,
        'resultado_incierto',
        'No se pudo confirmar el alta del tenant: verificá el listado; si ya existe, restablecé la contraseña del admin antes de reintentar.',
      ),
      'el tenant',
    )

    expect(copia).toBe(
      'No se pudo dar de baja el tenant. No se pudo confirmar el alta del tenant: verificá el listado; si ya existe, restablecé la contraseña del admin antes de reintentar.',
    )
    expect(copia).toContain('restablecé la contraseña del admin')
  })

  /** Un `resultado_incierto` con el mensaje vacío no puede dejar un alert sin guía: cae al fallback. */
  it('un 503 resultado_incierto sin mensaje cae al fallback local', () => {
    const copia = copiaDeFalloDeBaja(new ErrorApi(503, 'resultado_incierto', '   '), 'la empresa')

    expect(copia).toBe(
      'No se pudo dar de baja la empresa. No se pudo confirmar el resultado: verificá el listado antes de reintentar.',
    )
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

  /**
   * Cláusula bajo prueba: que el mapa sea un `Map` y no un objeto literal (judgment-day ronda 1,
   * C6). El `codigo` viene del SERVIDOR: sobre un objeto, `GUIA['constructor']` resuelve contra el
   * prototipo y devuelve una función, así que `guia ? … : …` daba verdadero y la copia terminaba
   * concatenando el `[Function]` — o algo peor con `toString`. Con `Map.get` solo existen las
   * claves propias y estos tres caen por el fallback como cualquier código desconocido.
   */
  it('una clave del prototipo no se hace pasar por guía', () => {
    for (const codigo of ['constructor', 'toString', '__proto__', 'hasOwnProperty']) {
      expect(copiaDeFalloDeBaja(new ErrorApi(409, codigo, 'Algo bloquea la baja.'), 'el tenant')).toBe(
        'Algo bloquea la baja.',
      )
    }
  })
})

// fix/web-bajas-catalogos: guarda de referencias de catálogos de tenant y proveedores.

/** Los SIETE `*_en_uso` de catálogos/proveedores más los DOS propios de listas de precio, en el
 * orden en que el backend los declara (`ServicioDeAreas`, `ServicioDeMarcas`, `ServicioDeGrupos`,
 * `ServicioDeMediosPago`, `ServicioDeCategorias`, `ServicioDeListasPrecio`, `ServicioDeProveedores`). */
const NUEVE_CODIGOS_DE_CATALOGOS_Y_PROVEEDORES = [
  'area_en_uso',
  'categoria_en_uso',
  'marca_en_uso',
  'grupo_en_uso',
  'medio_pago_en_uso',
  'lista_precio_en_uso',
  'proveedor_en_uso',
  'lista_default_no_se_puede_eliminar',
  'lista_referenciada_como_base',
] as const

describe('copiaDeFalloDeBaja — códigos de catálogos de tenant y proveedores (fix/web-bajas-catalogos)', () => {
  /**
   * Cláusula bajo prueba: el `GUIA_POR_CODIGO` para cada uno de los nueve códigos nuevos. Con una
   * entrada faltante o compartida, dos de los nueve rendirían el mismo texto y el operador no
   * sabría qué acción corresponde (reasignar/desactivar vs. resolver el estado default/base de la
   * lista). Pairwise-distintas entre sí, no solo respecto de las seis de la etapa 20.
   */
  it('los nueve códigos rinden nueve copias distintas entre sí', () => {
    const copias = NUEVE_CODIGOS_DE_CATALOGOS_Y_PROVEEDORES.map((codigo) =>
      copiaDeFalloDeBaja(new ErrorApi(409, codigo, 'mensaje del servidor'), 'la marca'),
    )

    expect(new Set(copias).size).toBe(NUEVE_CODIGOS_DE_CATALOGOS_Y_PROVEEDORES.length)
  })

  it('marca_en_uso antepone el mensaje del servidor —que nombra la tabla que bloquea— a su guía', () => {
    const copia = copiaDeFalloDeBaja(
      new ErrorApi(409, 'marca_en_uso', 'No se puede dar de baja la marca porque tiene artículos.'),
      'la marca',
    )

    expect(copia).toContain('No se puede dar de baja la marca porque tiene artículos.')
    expect(copia).toContain('Reasigná esos datos o desactivá la marca para que no se ofrezca más.')
  })

  it('categoria_en_uso rinde su propia guía, distinta de la de marca_en_uso', () => {
    const copia = copiaDeFalloDeBaja(
      new ErrorApi(409, 'categoria_en_uso', 'No se puede dar de baja la categoría porque tiene subcategorías.'),
      'la categoría',
    )

    expect(copia).toContain('porque tiene subcategorías')
    expect(copia).toContain('Reasigná esos datos o desactivá la categoría para que no se ofrezca más.')
  })

  it('proveedor_en_uso rinde la guía de reasignar/desactivar el proveedor', () => {
    const copia = copiaDeFalloDeBaja(
      new ErrorApi(409, 'proveedor_en_uso', 'No se puede dar de baja el proveedor porque tiene compras.'),
      'el proveedor',
    )

    expect(copia).toBe(
      'No se puede dar de baja el proveedor porque tiene compras. Reasigná esos datos o desactivá el proveedor '
        + 'para que no se ofrezca más.',
    )
  })

  /**
   * Cláusula bajo prueba: `lista_default_no_se_puede_eliminar` es un mínimo estructural (no se
   * puede quedar sin lista default), no un `*_en_uso` — su guía manda a resolver el estado default,
   * no a reasignar artículos.
   */
  it('lista_default_no_se_puede_eliminar manda a asignar el default a otra lista', () => {
    const copia = copiaDeFalloDeBaja(
      new ErrorApi(409, 'lista_default_no_se_puede_eliminar', 'No se puede eliminar la lista default.'),
      'la lista de precios',
    )

    expect(copia).toBe(
      'No se puede eliminar la lista default. Asigná el estado default a otra lista primero.',
    )
  })

  /** Cláusula bajo prueba: `lista_referenciada_como_base`, la OTRA guarda estructural de listas de
   * precio — desactivar la base de listas derivadas activas, no reasignar artículos. */
  it('lista_referenciada_como_base manda a desactivar o cambiar la base de las derivadas', () => {
    const copia = copiaDeFalloDeBaja(
      new ErrorApi(
        409,
        'lista_referenciada_como_base',
        'No se puede desactivar una lista referenciada como base por una lista derivada activa.',
      ),
      'la lista de precios',
    )

    expect(copia).toBe(
      'No se puede desactivar una lista referenciada como base por una lista derivada activa. Desactivá o cambiá '
        + 'la base de las listas derivadas primero.',
    )
  })

  /** Mismo contrato que las seis de la etapa 20: cambiar el mensaje no cambia la guía elegida. */
  it('cambiar el mensaje del servidor no cambia la guía de grupo_en_uso', () => {
    const conMensajeLargo = copiaDeFalloDeBaja(
      new ErrorApi(409, 'grupo_en_uso', 'No se puede dar de baja el grupo porque tiene 5 artículos.'),
      'el grupo',
    )
    const conMensajeDegradado = copiaDeFalloDeBaja(new ErrorApi(409, 'grupo_en_uso', 'Conflicto.'), 'el grupo')

    const guia = 'Reasigná esos datos o desactivá el grupo para que no se ofrezca más.'
    expect(conMensajeLargo).toContain(guia)
    expect(conMensajeDegradado).toContain(guia)
    expect(conMensajeLargo).not.toBe(conMensajeDegradado)
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
