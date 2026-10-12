import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi, ErrorDeRed } from './cliente'
import type { EmpresaListado, VitrinaAdmin } from './tipos'

const apiMock = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
  putBinario: vi.fn(),
  delete: vi.fn(),
}))

vi.mock('./cliente', async (importarOriginal) => {
  const original = await importarOriginal<typeof import('./cliente')>()

  return { ...original, api: apiMock }
})

const {
  ACEPTAR_LOGO,
  COLOR_DE_MARCA,
  COPIA_RESULTADO_INCIERTO_DE_VITRINA,
  TAMANO_MAXIMO_DEL_LOGO,
  aBorrador,
  aEdicion,
  borradorTieneCambios,
  colorParaElSelector,
  copiaDeFalloDeLecturaDeVitrina,
  copiaDeFalloDeVitrina,
  debeReleerTrasFallo,
  eliminarVitrina,
  esColorHex,
  esTipoDeLogoAdmitido,
  guardarVitrina,
  obtenerVitrina,
  opcionesDeEmpresaParaVitrina,
  quitarLogo,
  subirLogo,
  tiposDeLogoAdmitidos,
  urlDelLogoAdmin,
  validarLogo,
} = await import('./vitrinas')

function vitrina(sobrescribir: Partial<VitrinaAdmin> = {}): VitrinaAdmin {
  return {
    existe: true,
    idEmpresa: 4,
    publicada: true,
    tagline: 'Siempre cerca',
    descripcion: 'Un almacén de barrio.',
    colorAcento: '#336699',
    tema: 'claro',
    tieneLogo: true,
    logoVersion: 1760000000,
    ...sobrescribir,
  }
}

function empresa(id: number, sobrescribir: Partial<EmpresaListado> = {}): EmpresaListado {
  return {
    id,
    idTenant: 1,
    razonSocial: `Razón ${id} SA`,
    nombreFantasia: null,
    cuit: null,
    nombreTenant: 'Comercio Sur',
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
    ...sobrescribir,
  }
}

beforeEach(() => {
  Object.values(apiMock).forEach((m) => m.mockReset())
})

describe('llamadas a la API', () => {
  it('obtenerVitrina lee la vitrina de la empresa', async () => {
    apiMock.get.mockResolvedValue(vitrina())

    await expect(obtenerVitrina(4)).resolves.toEqual(vitrina())
    expect(apiMock.get).toHaveBeenCalledWith('/empresas/4/vitrina')
  })

  it('guardarVitrina manda los cinco campos por PUT', async () => {
    apiMock.put.mockResolvedValue(vitrina())
    const datos = { publicada: true, tagline: 'a', descripcion: 'b', colorAcento: '#336699', tema: 'claro' as const }

    await guardarVitrina(4, datos)

    expect(apiMock.put).toHaveBeenCalledWith('/empresas/4/vitrina', datos)
  })

  it('eliminarVitrina y quitarLogo son DELETE de sus rutas', async () => {
    apiMock.delete.mockResolvedValue(undefined)

    await eliminarVitrina(4)
    await quitarLogo(4)

    expect(apiMock.delete.mock.calls).toEqual([['/empresas/4/vitrina'], ['/empresas/4/vitrina/logo']])
  })

  it('subirLogo manda el archivo crudo por putBinario, no por el PUT de JSON', async () => {
    apiMock.putBinario.mockResolvedValue(vitrina())
    const archivo = new File(['x'], 'logo.png', { type: 'image/png' })

    await subirLogo(4, archivo)

    expect(apiMock.putBinario).toHaveBeenCalledWith('/empresas/4/vitrina/logo', archivo)
    expect(apiMock.put).not.toHaveBeenCalled()
  })
})

describe('urlDelLogoAdmin', () => {
  it('lleva la versión como parámetro para que otra subida cambie la URL', () => {
    expect(urlDelLogoAdmin(4, 1760000000)).toBe('/api/empresas/4/vitrina/logo?v=1760000000')
    expect(urlDelLogoAdmin(4, 1760000000)).not.toBe(urlDelLogoAdmin(4, 1760000001))
  })

  it('sin versión no agrega parámetro', () => {
    expect(urlDelLogoAdmin(4, null)).toBe('/api/empresas/4/vitrina/logo')
  })
})

describe('esColorHex', () => {
  it.each(['#f7991c', '#F7991C', '#000000', '#ffffff'])('acepta %s', (color) => {
    expect(esColorHex(color)).toBe(true)
  })

  it.each(['', 'f7991c', '#f7991', '#f7991cc', '#ggg000', 'rojo', '#f79 91c', ' #f7991c'])('rechaza "%s"', (color) => {
    expect(esColorHex(color)).toBe(false)
  })
})

describe('colorParaElSelector', () => {
  it('un color válido va en minúscula, que es lo único que acepta un input de color', () => {
    expect(colorParaElSelector('#F7991C')).toBe('#f7991c')
    expect(colorParaElSelector(' #336699 ')).toBe('#336699')
  })

  it('vacío o inválido muestra el naranja de Ways', () => {
    expect(colorParaElSelector('')).toBe(COLOR_DE_MARCA)
    expect(colorParaElSelector('#12')).toBe(COLOR_DE_MARCA)
  })
})

describe('tipos de logo', () => {
  it('admite PNG, JPEG y WebP y nada más, SVG incluido', () => {
    expect(tiposDeLogoAdmitidos).toEqual(['image/png', 'image/jpeg', 'image/webp'])
    expect(ACEPTAR_LOGO).toBe('image/png,image/jpeg,image/webp')
    expect(esTipoDeLogoAdmitido('image/webp')).toBe(true)
    expect(esTipoDeLogoAdmitido('image/svg+xml')).toBe(false)
    expect(esTipoDeLogoAdmitido('image/gif')).toBe(false)
    expect(esTipoDeLogoAdmitido('')).toBe(false)
  })
})

describe('validarLogo', () => {
  it('un PNG de 1 MB justo se acepta, igual que en el servidor', () => {
    expect(validarLogo({ type: 'image/png', size: TAMANO_MAXIMO_DEL_LOGO })).toBeNull()
  })

  it('un byte más de 1 MB se rechaza por tamaño', () => {
    expect(validarLogo({ type: 'image/png', size: TAMANO_MAXIMO_DEL_LOGO + 1 })).toBe('El logo no puede pesar más de 1 MB.')
  })

  it('un tipo que no es de imagen admitida se rechaza, también sin tipo', () => {
    expect(validarLogo({ type: 'image/gif', size: 10 })).toBe('El logo tiene que ser una imagen PNG, JPG o WebP.')
    expect(validarLogo({ type: '', size: 10 })).toBe('El logo tiene que ser una imagen PNG, JPG o WebP.')
  })

  it('el tipo se mira antes que el tamaño, como en el servidor', () => {
    expect(validarLogo({ type: 'image/gif', size: TAMANO_MAXIMO_DEL_LOGO + 1 })).toBe(
      'El logo tiene que ser una imagen PNG, JPG o WebP.',
    )
  })
})

describe('aBorrador', () => {
  it('copia lo guardado y pasa los null a texto vacío', () => {
    expect(aBorrador(vitrina({ tagline: null, descripcion: null, colorAcento: null }))).toEqual({
      publicada: true,
      tagline: '',
      descripcion: '',
      colorAcento: '',
      tema: 'claro',
    })
  })

  it('un tema que no es "claro" cae en oscuro', () => {
    expect(aBorrador(vitrina({ tema: 'oscuro' })).tema).toBe('oscuro')
    expect(aBorrador(vitrina({ tema: 'violeta' })).tema).toBe('oscuro')
  })
})

describe('aEdicion', () => {
  it('recorta los textos y manda vacío como null', () => {
    expect(
      aEdicion({ publicada: false, tagline: '  Siempre cerca  ', descripcion: '   ', colorAcento: '', tema: 'oscuro' }),
    ).toEqual({ publicada: false, tagline: 'Siempre cerca', descripcion: null, colorAcento: null, tema: 'oscuro' })
  })

  it('manda el color y la marca de publicada tal como están', () => {
    expect(
      aEdicion({ publicada: true, tagline: '', descripcion: 'hola', colorAcento: ' #336699 ', tema: 'claro' }),
    ).toEqual({ publicada: true, tagline: null, descripcion: 'hola', colorAcento: '#336699', tema: 'claro' })
  })
})

describe('opcionesDeEmpresaParaVitrina', () => {
  it('rinde el nombre de fantasía o, si falta, la razón social, ordenado', () => {
    const opciones = opcionesDeEmpresaParaVitrina([
      empresa(2, { nombreFantasia: 'Zeta' }),
      empresa(1, { razonSocial: 'Almacén Norte SA' }),
      empresa(3, { nombreFantasia: '   ', razonSocial: 'Beta SRL' }),
    ])

    expect(opciones).toEqual([
      { valor: '1', etiqueta: 'Almacén Norte SA' },
      { valor: '3', etiqueta: 'Beta SRL' },
      { valor: '2', etiqueta: 'Zeta' },
    ])
  })

  it('si la lista cruza varios tenants agrega el tenant a cada opción', () => {
    const opciones = opcionesDeEmpresaParaVitrina([
      empresa(1, { nombreFantasia: 'Sur', idTenant: 1, nombreTenant: 'Comercio Sur' }),
      empresa(2, { nombreFantasia: 'Este', idTenant: 2, nombreTenant: null }),
    ])

    expect(opciones.map((o) => o.etiqueta)).toEqual(['Este (sin tenant)', 'Sur (Comercio Sur)'])
  })

  it('dos empresas con la misma etiqueta se desempatan con su id', () => {
    const opciones = opcionesDeEmpresaParaVitrina([
      empresa(7, { nombreFantasia: 'Kiosco' }),
      empresa(8, { nombreFantasia: 'Kiosco' }),
      empresa(9, { nombreFantasia: 'Único' }),
    ])

    expect(opciones.map((o) => o.etiqueta)).toEqual(['Kiosco (empresa 7)', 'Kiosco (empresa 8)', 'Único'])
  })

  it('una lista vacía no tiene opciones', () => {
    expect(opcionesDeEmpresaParaVitrina([])).toEqual([])
  })
})

describe('copiaDeFalloDeVitrina', () => {
  const ENCABEZADO = 'No se pudo guardar la vitrina.'

  it.each([
    ['color_invalido', 400, 'El color de acento tiene que tener la forma #rrggbb.'],
    ['tema_invalido', 400, 'Elegí el tema claro o el oscuro.'],
    ['texto_demasiado_largo', 400, 'El slogan admite hasta 120 caracteres y la descripción hasta 2000.'],
    ['publicada_requerida', 400, 'Indicá si la vitrina está publicada o no.'],
    [
      'vitrina_ya_publicada',
      409,
      'Otra empresa de este comercio ya tiene su vitrina publicada. Despublicala antes de publicar esta.',
    ],
    ['logo_demasiado_grande', 413, 'El logo no puede pesar más de 1 MB.'],
    ['logo_no_admitido', 415, 'El logo tiene que ser una imagen PNG, JPG o WebP.'],
  ])('el código %s rinde su copia propia y no el texto del servidor', (codigo, estado, copia) => {
    expect(copiaDeFalloDeVitrina(new ErrorApi(estado, codigo, 'texto crudo'), ENCABEZADO)).toBe(copia)
  })

  it('el backstop del logo (400 logo_no_admitido) rinde la misma copia que el 415', () => {
    expect(copiaDeFalloDeVitrina(new ErrorApi(400, 'logo_no_admitido', 'x'), ENCABEZADO)).toBe(
      'El logo tiene que ser una imagen PNG, JPG o WebP.',
    )
  })

  it('un 413 o un 415 sin código del servidor dicen lo mismo que sus códigos', () => {
    expect(copiaDeFalloDeVitrina(new ErrorApi(413, 'error', 'Request Entity Too Large'), ENCABEZADO)).toBe(
      'El logo no puede pesar más de 1 MB.',
    )
    expect(copiaDeFalloDeVitrina(new ErrorApi(415, 'error', 'x'), ENCABEZADO)).toBe(
      'El logo tiene que ser una imagen PNG, JPG o WebP.',
    )
  })

  it('un código exótico que existe en el prototipo no se confunde con una copia', () => {
    expect(copiaDeFalloDeVitrina(new ErrorApi(400, 'constructor', 'Rechazo propio.'), ENCABEZADO)).toBe('Rechazo propio.')
  })

  it('un 404 dice que la empresa ya no existe, con el encabezado delante', () => {
    expect(copiaDeFalloDeVitrina(new ErrorApi(404, 'no_encontrado', 'x'), ENCABEZADO)).toBe(
      `${ENCABEZADO} La empresa ya no existe. Actualizá la pantalla.`,
    )
  })

  it('el resultado incierto manda a revisar la vitrina, no un listado que esta pantalla no tiene', () => {
    expect(COPIA_RESULTADO_INCIERTO_DE_VITRINA).toBe(
      'No se pudo confirmar el resultado: revisá la vitrina antes de reintentar.',
    )
    expect(COPIA_RESULTADO_INCIERTO_DE_VITRINA).not.toMatch(/listado/)
  })

  it('un 5xx sin marca manda a verificar', () => {
    expect(copiaDeFalloDeVitrina(new ErrorApi(500, 'error_interno', 'boom'), ENCABEZADO)).toBe(
      `${ENCABEZADO} ${COPIA_RESULTADO_INCIERTO_DE_VITRINA}`,
    )
  })

  it('un 503 resultado_incierto rinde el texto del servidor, que sabe qué verificar', () => {
    expect(copiaDeFalloDeVitrina(new ErrorApi(503, 'resultado_incierto', 'Verificá la vitrina.'), ENCABEZADO)).toBe(
      `${ENCABEZADO} Verificá la vitrina.`,
    )
  })

  it('un resultado_incierto sin texto cae en la copia genérica', () => {
    expect(copiaDeFalloDeVitrina(new ErrorApi(503, 'resultado_incierto', '  '), ENCABEZADO)).toBe(
      `${ENCABEZADO} ${COPIA_RESULTADO_INCIERTO_DE_VITRINA}`,
    )
  })

  it('un error que no es de la API tampoco dice si se escribió', () => {
    expect(copiaDeFalloDeVitrina(new ErrorDeRed(new TypeError('x')), ENCABEZADO)).toBe(
      `${ENCABEZADO} ${COPIA_RESULTADO_INCIERTO_DE_VITRINA}`,
    )
  })

  it('cualquier otro rechazo rinde el mensaje de la API, o el encabezado si viene vacío', () => {
    expect(copiaDeFalloDeVitrina(new ErrorApi(400, 'otro', 'Algo salió mal.'), ENCABEZADO)).toBe('Algo salió mal.')
    expect(copiaDeFalloDeVitrina(new ErrorApi(400, 'otro', ' '), ENCABEZADO)).toBe(ENCABEZADO)
  })
})

describe('copiaDeFalloDeLecturaDeVitrina', () => {
  it('un 404 dice que la empresa ya no existe', () => {
    expect(copiaDeFalloDeLecturaDeVitrina(new ErrorApi(404, 'no_encontrado', 'x'))).toBe(
      'No se pudo cargar la vitrina. La empresa ya no existe. Actualizá la pantalla.',
    )
  })

  it('cualquier otro fallo, también de red, invita a probar de nuevo sin hablar de reintentar una escritura', () => {
    for (const error of [new ErrorApi(500, 'error_interno', 'boom'), new ErrorApi(400, 'otro', 'x'), new ErrorDeRed(new TypeError('x'))]) {
      expect(copiaDeFalloDeLecturaDeVitrina(error)).toBe('No se pudo cargar la vitrina. Probá de nuevo.')
    }
  })
})

describe('borradorTieneCambios', () => {
  const guardada = vitrina({ publicada: true, tagline: 'Siempre cerca', descripcion: null, colorAcento: '#336699', tema: 'claro' })
  const igual = aBorrador(guardada)

  it('el borrador recién armado desde lo guardado no tiene cambios', () => {
    expect(borradorTieneCambios(igual, guardada)).toBe(false)
  })

  it('un espacio de más o un texto de solo espacios no es un cambio', () => {
    expect(borradorTieneCambios({ ...igual, tagline: ' Siempre cerca ', descripcion: '   ', colorAcento: '#336699 ' }, guardada)).toBe(false)
  })

  it.each([
    ['publicada', { publicada: false }],
    ['slogan', { tagline: 'Otro' }],
    ['descripción', { descripcion: 'Algo' }],
    ['color', { colorAcento: '#000000' }],
    ['tema', { tema: 'oscuro' as const }],
  ])('cambiar %s es un cambio', (_campo, cambio) => {
    expect(borradorTieneCambios({ ...igual, ...cambio }, guardada)).toBe(true)
  })

  it('vaciar un texto guardado es un cambio', () => {
    expect(borradorTieneCambios({ ...igual, tagline: '' }, guardada)).toBe(true)
  })

  it('una empresa sin vitrina guardada compara contra el borrador vacío', () => {
    const sinVitrina = vitrina({ existe: false, publicada: false, tagline: null, descripcion: null, colorAcento: null, tema: 'oscuro' })

    expect(borradorTieneCambios(aBorrador(sinVitrina), sinVitrina)).toBe(false)
    expect(borradorTieneCambios({ ...aBorrador(sinVitrina), tagline: 'Hola' }, sinVitrina)).toBe(true)
  })
})

describe('debeReleerTrasFallo', () => {
  it('relee ante un 404, un 5xx o un error que no es de la API', () => {
    expect(debeReleerTrasFallo(new ErrorApi(404, 'no_encontrado', 'x'))).toBe(true)
    expect(debeReleerTrasFallo(new ErrorApi(503, 'resultado_incierto', 'x'))).toBe(true)
    expect(debeReleerTrasFallo(new ErrorDeRed(new TypeError('x')))).toBe(true)
  })

  it('no relee ante un rechazo de validación, que no cambió nada', () => {
    for (const estado of [400, 409, 413, 415]) {
      expect(debeReleerTrasFallo(new ErrorApi(estado, 'x', 'x'))).toBe(false)
    }
  })
})
