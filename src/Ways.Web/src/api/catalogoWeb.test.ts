import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi, ErrorDeRed } from './cliente'

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
  ACEPTAR_IMAGEN,
  COPIA_IMAGEN_DEMASIADO_GRANDE,
  COPIA_IMAGEN_NO_ADMITIDA,
  COPIA_RESULTADO_INCIERTO_DE_TIENDA,
  TAMANO_MAXIMO_DE_LA_IMAGEN,
  copiaDeFalloDeTienda,
  debeReleerTrasFalloDeTienda,
  publicarEnLaWeb,
  quitarImagenDeArticulo,
  subirImagenDeArticulo,
  urlDeImagenDeArticuloAdmin,
  validarImagenDeArticulo,
} = await import('./catalogoWeb')

beforeEach(() => {
  Object.values(apiMock).forEach((m) => m.mockReset())
})

describe('llamadas a la API', () => {
  it('publicarEnLaWeb manda solo { publicado } por PUT a la ruta del artículo', async () => {
    apiMock.put.mockResolvedValue({ idArticulo: 7, publicado: true })

    await expect(publicarEnLaWeb(7, true)).resolves.toEqual({ idArticulo: 7, publicado: true })
    await publicarEnLaWeb(7, false)

    expect(apiMock.put.mock.calls).toEqual([
      ['/articulos/7/web', { publicado: true }],
      ['/articulos/7/web', { publicado: false }],
    ])
  })

  it('subirImagenDeArticulo manda el archivo crudo por putBinario, no por el PUT de JSON', async () => {
    apiMock.putBinario.mockResolvedValue({ idArticulo: 7, tieneImagen: true, imagenVersion: 1 })
    const archivo = new File(['x'], 'foto.png', { type: 'image/png' })

    await subirImagenDeArticulo(7, archivo)

    expect(apiMock.putBinario).toHaveBeenCalledWith('/articulos/7/imagen', archivo)
    expect(apiMock.put).not.toHaveBeenCalled()
  })

  it('quitarImagenDeArticulo es un DELETE de la ruta de la imagen', async () => {
    apiMock.delete.mockResolvedValue(undefined)

    await quitarImagenDeArticulo(7)

    expect(apiMock.delete).toHaveBeenCalledWith('/articulos/7/imagen')
  })
})

describe('urlDeImagenDeArticuloAdmin', () => {
  it('lleva la versión como parámetro para que otra subida cambie la URL', () => {
    expect(urlDeImagenDeArticuloAdmin(7, 1760000000)).toBe('/api/articulos/7/imagen?v=1760000000')
    expect(urlDeImagenDeArticuloAdmin(7, 1760000000)).not.toBe(urlDeImagenDeArticuloAdmin(7, 1760000001))
  })

  it('sin versión no agrega parámetro', () => {
    expect(urlDeImagenDeArticuloAdmin(7, null)).toBe('/api/articulos/7/imagen')
  })
})

describe('el selector de archivo', () => {
  it('solo ofrece PNG, JPG y WebP', () => {
    expect(ACEPTAR_IMAGEN).toBe('image/png,image/jpeg,image/webp')
  })

  it('el tope es 1 MB', () => {
    expect(TAMANO_MAXIMO_DE_LA_IMAGEN).toBe(1024 * 1024)
  })
})

describe('validarImagenDeArticulo', () => {
  it.each(['image/png', 'image/jpeg', 'image/webp'])('acepta %s', (type) => {
    expect(validarImagenDeArticulo({ type, size: 10 })).toBeNull()
  })

  it.each(['image/gif', 'image/svg+xml', 'application/pdf', ''])('rechaza el tipo "%s"', (type) => {
    expect(validarImagenDeArticulo({ type, size: 10 })).toBe(COPIA_IMAGEN_NO_ADMITIDA)
  })

  it('rechaza más de 1 MB y acepta exactamente 1 MB', () => {
    expect(validarImagenDeArticulo({ type: 'image/png', size: TAMANO_MAXIMO_DE_LA_IMAGEN + 1 })).toBe(
      COPIA_IMAGEN_DEMASIADO_GRANDE,
    )
    expect(validarImagenDeArticulo({ type: 'image/png', size: TAMANO_MAXIMO_DE_LA_IMAGEN })).toBeNull()
  })

  it('el tipo se mira antes que el tamaño, como en el servidor', () => {
    expect(validarImagenDeArticulo({ type: 'image/gif', size: TAMANO_MAXIMO_DE_LA_IMAGEN + 1 })).toBe(
      COPIA_IMAGEN_NO_ADMITIDA,
    )
  })
})

describe('copiaDeFalloDeTienda', () => {
  const ENCABEZADO = 'No se pudo subir la imagen.'

  it.each([
    ['imagen_demasiado_grande', 413, 'La imagen no puede pesar más de 1 MB.'],
    ['imagen_no_admitida', 415, 'La imagen tiene que ser PNG, JPG o WebP.'],
    ['imagen_no_admitida', 400, 'La imagen tiene que ser PNG, JPG o WebP.'],
  ])('el código %s (%i) rinde su copia propia y no el texto del servidor', (codigo, estado, copia) => {
    expect(copiaDeFalloDeTienda(new ErrorApi(estado, codigo, 'texto crudo'), ENCABEZADO)).toBe(copia)
  })

  it('un 413 o un 415 sin código del servidor dicen lo mismo que sus códigos', () => {
    expect(copiaDeFalloDeTienda(new ErrorApi(413, 'error', 'Request Entity Too Large'), ENCABEZADO)).toBe(
      COPIA_IMAGEN_DEMASIADO_GRANDE,
    )
    expect(copiaDeFalloDeTienda(new ErrorApi(415, 'error', 'Unsupported Media Type'), ENCABEZADO)).toBe(
      COPIA_IMAGEN_NO_ADMITIDA,
    )
  })

  it('un 404 dice que el artículo ya no existe, con el encabezado', () => {
    expect(copiaDeFalloDeTienda(new ErrorApi(404, 'no_encontrado', 'No existe el artículo 7.'), ENCABEZADO)).toBe(
      'No se pudo subir la imagen. El artículo ya no existe. Actualizá la pantalla.',
    )
  })

  it('un 5xx sin marca de resultado incierto manda a verificar y no repite el texto del servidor', () => {
    const copia = copiaDeFalloDeTienda(new ErrorApi(500, 'error', 'Error 500.'), ENCABEZADO)

    expect(copia).toBe(`${ENCABEZADO} ${COPIA_RESULTADO_INCIERTO_DE_TIENDA}`)
    expect(copia).not.toContain('Error 500')
  })

  it('un 5xx marcado como resultado incierto rinde el texto del servidor, que es lo único que dice qué hacer', () => {
    expect(copiaDeFalloDeTienda(new ErrorApi(503, 'resultado_incierto', 'Revisá el artículo.'), ENCABEZADO)).toBe(
      `${ENCABEZADO} Revisá el artículo.`,
    )
  })

  it('un error que no es de la API manda a verificar', () => {
    expect(copiaDeFalloDeTienda(new Error('boom'), ENCABEZADO)).toBe(`${ENCABEZADO} ${COPIA_RESULTADO_INCIERTO_DE_TIENDA}`)
    expect(copiaDeFalloDeTienda(new ErrorDeRed(new TypeError('Failed to fetch')), ENCABEZADO)).toBe(
      `${ENCABEZADO} ${COPIA_RESULTADO_INCIERTO_DE_TIENDA}`,
    )
  })

  it('cualquier otro rechazo rinde el mensaje de la API, o el encabezado si viene vacío', () => {
    expect(copiaDeFalloDeTienda(new ErrorApi(403, 'prohibido', 'No tenés permiso.'), ENCABEZADO)).toBe('No tenés permiso.')
    expect(copiaDeFalloDeTienda(new ErrorApi(403, 'prohibido', '   '), ENCABEZADO)).toBe(ENCABEZADO)
  })
})

describe('debeReleerTrasFalloDeTienda', () => {
  it('relee ante un 404, un 5xx o un error que no es de la API', () => {
    expect(debeReleerTrasFalloDeTienda(new ErrorApi(404, 'no_encontrado', 'x'))).toBe(true)
    expect(debeReleerTrasFalloDeTienda(new ErrorApi(500, 'error', 'x'))).toBe(true)
    expect(debeReleerTrasFalloDeTienda(new ErrorApi(503, 'resultado_incierto', 'x'))).toBe(true)
    expect(debeReleerTrasFalloDeTienda(new Error('sin red'))).toBe(true)
  })

  it('no relee ante un rechazo de validación, que no cambió nada', () => {
    for (const estado of [400, 403, 409, 413, 415]) {
      expect(debeReleerTrasFalloDeTienda(new ErrorApi(estado, 'x', 'x'))).toBe(false)
    }
  })
})
