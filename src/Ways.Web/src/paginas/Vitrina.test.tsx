import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Vitrina } from './Vitrina'
import { ErrorApi } from '../api/cliente'
import type { EmpresaListado, SitioPublico, VitrinaAdmin, VitrinaEdicion } from '../api/tipos'

// Vitrina de una empresa: lectura, guardado, logo (subir y quitar) y baja.

const apiGetMock = vi.fn()
const apiPutMock = vi.fn()
const apiPutBinarioMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
    putBinario: (...args: unknown[]) => apiPutBinarioMock(...(args as [string, File])),
    delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
    post: vi.fn(),
  },
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

function empresa(id: number, nombreFantasia: string): EmpresaListado {
  return {
    id,
    idTenant: 1,
    razonSocial: `${nombreFantasia} SA`,
    nombreFantasia,
    cuit: null,
    nombreTenant: 'Comercio Sur',
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
  }
}

function vitrina(idEmpresa: number, sobrescribir: Partial<VitrinaAdmin> = {}): VitrinaAdmin {
  return {
    existe: true,
    idEmpresa,
    publicada: false,
    tagline: null,
    descripcion: null,
    colorAcento: null,
    tema: 'oscuro',
    tieneLogo: false,
    logoVersion: null,
    ...sobrescribir,
  }
}

const empresaNorte = empresa(4, 'Almacén Norte')
const empresaEste = empresa(5, 'Kiosco Este')

const vitrinaNorte = vitrina(4, {
  publicada: true,
  tagline: 'Siempre cerca',
  descripcion: 'Un almacén de barrio.',
  colorAcento: '#336699',
  tema: 'claro',
  tieneLogo: true,
  logoVersion: 100,
})

const SITIO_DE_COMERCIO: SitioPublico = { esPlataforma: false, nombreComercio: 'Comercio Sur', admiteUsuario: true }
const SITIO_DE_PLATAFORMA: SitioPublico = { esPlataforma: true, nombreComercio: null, admiteUsuario: false }

type Mundo = {
  empresas: EmpresaListado[]
  vitrinas: Record<number, VitrinaAdmin>
  sitio: SitioPublico | 'falla'
}

/** El "servidor": las lecturas devuelven lo que haya en `mundo` al momento de la llamada y las
 * escrituras lo mutan, así un test que quiere que la relectura traiga otra cosa lo cambia antes. */
function prepararMundo(parcial: Partial<Mundo> = {}) {
  const mundo: Mundo = {
    empresas: [empresaNorte],
    vitrinas: { 4: vitrinaNorte },
    sitio: SITIO_DE_COMERCIO,
    ...parcial,
  }

  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve(mundo.empresas)
    if (ruta === '/publico/sitio') {
      return mundo.sitio === 'falla' ? Promise.reject(new Error('sin red')) : Promise.resolve(mundo.sitio)
    }

    const coincidencia = /^\/empresas\/(\d+)\/vitrina$/.exec(ruta)
    if (coincidencia) {
      const id = Number(coincidencia[1])

      return Promise.resolve(mundo.vitrinas[id] ?? vitrina(id, { existe: false }))
    }

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })

  apiPutMock.mockImplementation((ruta: string, cuerpo: VitrinaEdicion) => {
    const id = Number(/^\/empresas\/(\d+)\/vitrina$/.exec(ruta)![1])
    mundo.vitrinas[id] = { ...(mundo.vitrinas[id] ?? vitrina(id)), ...cuerpo, existe: true }

    return Promise.resolve(mundo.vitrinas[id])
  })

  apiPutBinarioMock.mockImplementation((ruta: string) => {
    const id = Number(/^\/empresas\/(\d+)\/vitrina\/logo$/.exec(ruta)![1])
    const previa = mundo.vitrinas[id] ?? vitrina(id)
    mundo.vitrinas[id] = { ...previa, existe: true, tieneLogo: true, logoVersion: (previa.logoVersion ?? 0) + 1 }

    return Promise.resolve(mundo.vitrinas[id])
  })

  apiDeleteMock.mockResolvedValue(undefined)

  return mundo
}

function montar(parcial: Partial<Mundo> = {}, envolver = (nodo: React.ReactElement) => nodo) {
  const mundo = prepararMundo(parcial)

  return { mundo, ...render(envolver(<Vitrina />)) }
}

function diferida<T>() {
  let resolver: (valor: T) => void = () => {}
  let rechazar: (motivo: unknown) => void = () => {}
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })

  return { promesa, resolver, rechazar }
}

type Usuario = ReturnType<typeof userEvent.setup>

/** Con una sola empresa queda elegida sola y el formulario existe recién cuando llegó la vitrina
 * (`Cargando` antes), así que encontrarlo ya prueba que el dato aterrizó. */
async function esperarFormulario() {
  return screen.findByLabelText('Slogan')
}

async function elegirEmpresa(usuario: Usuario, id: number) {
  await usuario.selectOptions(await screen.findByLabelText('Empresa'), String(id))
}

function llamadasA(mock: ReturnType<typeof vi.fn>, ruta: string) {
  return mock.mock.calls.filter((c) => c[0] === ruta)
}

function archivoDeLogo(tipo = 'image/png', bytes = 10, nombre = 'logo.png') {
  return new File([new Uint8Array(bytes)], nombre, { type: tipo })
}

const RUTA_VITRINA = '/empresas/4/vitrina'
const RUTA_LOGO = '/empresas/4/vitrina/logo'

beforeEach(() => {
  apiGetMock.mockReset()
  apiPutMock.mockReset()
  apiPutBinarioMock.mockReset()
  apiDeleteMock.mockReset()
})

describe('Vitrina — lectura', () => {
  it('con una sola empresa queda elegida y rinde lo guardado en cada campo', async () => {
    montar()

    expect(await esperarFormulario()).toHaveValue('Siempre cerca')
    expect(screen.getByLabelText('Empresa')).toHaveValue('4')
    expect(screen.getByLabelText('Publicada')).toBeChecked()
    expect(screen.getByLabelText('Descripción')).toHaveValue('Un almacén de barrio.')
    expect(screen.getByLabelText('Color de acento')).toHaveValue('#336699')
    expect(screen.getByLabelText('Elegir color de acento')).toHaveValue('#336699')
    expect(screen.getByLabelText('Claro')).toBeChecked()
    expect(screen.getByLabelText('Oscuro')).not.toBeChecked()
    expect(llamadasA(apiGetMock, RUTA_VITRINA)).toHaveLength(1)
  })

  it('con dos empresas no elige ninguna ni lee ninguna vitrina hasta que se elige', async () => {
    const usuario = userEvent.setup()
    montar({ empresas: [empresaNorte, empresaEste], vitrinas: { 4: vitrinaNorte, 5: vitrina(5, { tagline: 'Abierto todo el día' }) } })

    expect(await screen.findByLabelText('Empresa')).toHaveValue('')
    expect(screen.queryByLabelText('Slogan')).not.toBeInTheDocument()
    expect(llamadasA(apiGetMock, RUTA_VITRINA)).toHaveLength(0)

    await elegirEmpresa(usuario, 5)

    expect(await esperarFormulario()).toHaveValue('Abierto todo el día')
    expect(apiGetMock).toHaveBeenCalledWith('/empresas/5/vitrina')
  })

  it('una empresa sin vitrina guardada arranca sin publicar, en tema oscuro, y sin ofrecer eliminar', async () => {
    montar({ vitrinas: {} })

    expect(await esperarFormulario()).toHaveValue('')
    expect(screen.getByLabelText('Publicada')).not.toBeChecked()
    expect(screen.getByLabelText('Oscuro')).toBeChecked()
    expect(screen.getByLabelText('Color de acento')).toHaveValue('')
    expect(screen.queryByRole('button', { name: 'Eliminar vitrina' })).not.toBeInTheDocument()
    expect(screen.getByText('Todavía no hay un logo cargado.')).toBeInTheDocument()
  })

  it('si fallan las empresas lo dice', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'Falló la carga.'))
    render(<Vitrina />)

    expect(await screen.findByRole('alert')).toHaveTextContent('Falló la carga.')
  })

  it('si falla la vitrina ofrece reintentar y el reintento la pide de nuevo', async () => {
    prepararMundo()
    const original = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === RUTA_VITRINA ? Promise.reject(new ErrorApi(500, 'error_interno', 'boom')) : original(ruta),
    )
    render(<Vitrina />)

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo cargar la vitrina. Probá de nuevo.')
    expect(screen.queryByText(/listado/)).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Slogan')).not.toBeInTheDocument()

    apiGetMock.mockImplementation(original)
    await userEvent.setup().click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await esperarFormulario()).toHaveValue('Siempre cerca')
    expect(screen.queryByText(/No se pudo cargar la vitrina\./)).not.toBeInTheDocument()
  })

  it('si la empresa de la vitrina ya no existe, la lectura lo dice', async () => {
    prepararMundo()
    const original = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === RUTA_VITRINA ? Promise.reject(new ErrorApi(404, 'no_encontrado', 'x')) : original(ruta),
    )
    render(<Vitrina />)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'No se pudo cargar la vitrina. La empresa ya no existe. Actualizá la pantalla.',
    )
  })

  /**
   * Cláusula bajo prueba: la respuesta de una empresa ya superada no pisa la vitrina de la
   * empresa elegida después. Gana la última lectura iniciada, no la última resuelta.
   */
  it('una vitrina vieja que llega tarde no pisa la de la empresa elegida después', async () => {
    const usuario = userEvent.setup()
    const pendientes = new Map<number, (v: VitrinaAdmin) => void>()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/empresas') return Promise.resolve([empresaNorte, empresaEste])
      if (ruta === '/publico/sitio') return Promise.resolve(SITIO_DE_COMERCIO)

      const id = Number(/^\/empresas\/(\d+)\/vitrina$/.exec(ruta)![1])

      return new Promise<VitrinaAdmin>((resolver) => pendientes.set(id, resolver))
    })
    render(<Vitrina />)

    await elegirEmpresa(usuario, 4)
    await waitFor(() => expect(pendientes.has(4)).toBe(true))
    await elegirEmpresa(usuario, 5)
    await waitFor(() => expect(pendientes.has(5)).toBe(true))

    await act(async () => pendientes.get(5)!(vitrina(5, { tagline: 'La del Este' })))
    expect(await screen.findByDisplayValue('La del Este')).toBeInTheDocument()

    await act(async () => pendientes.get(4)!(vitrinaNorte))

    expect(screen.queryByDisplayValue('Siempre cerca')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Slogan')).toHaveValue('La del Este')
    expect(screen.getByLabelText('Empresa')).toHaveValue('5')
  })

  it('bajo StrictMode la empresa única queda elegida y su vitrina se rinde', async () => {
    montar({}, (nodo) => <StrictMode>{nodo}</StrictMode>)

    expect(await esperarFormulario()).toHaveValue('Siempre cerca')
    expect(screen.getByLabelText('Empresa')).toHaveValue('4')
  })
})

describe('Vitrina — formulario', () => {
  it('cuenta los caracteres del slogan y de la descripción contra su máximo', async () => {
    const usuario = userEvent.setup()
    montar({ vitrinas: {} })
    const slogan = await esperarFormulario()

    expect(slogan).toHaveAttribute('maxlength', '120')
    expect(screen.getByLabelText('Descripción')).toHaveAttribute('maxlength', '2000')
    expect(slogan).toHaveAccessibleDescription('0/120 caracteres')

    await usuario.type(slogan, 'Hola')
    await usuario.type(screen.getByLabelText('Descripción'), 'Una descripción')

    expect(slogan).toHaveAccessibleDescription('4/120 caracteres')
    expect(screen.getByLabelText('Descripción')).toHaveAccessibleDescription('15/2000 caracteres')
  })

  it('aclara que un color vacío usa el naranja de Ways', async () => {
    montar({ vitrinas: {} })
    await esperarFormulario()

    expect(screen.getByLabelText('Color de acento')).toHaveAccessibleDescription(/Vacío usa el naranja de Ways \(#F7991C\)/)
    expect(screen.getByLabelText('Elegir color de acento')).toHaveValue('#f7991c')
  })

  it('el selector de color escribe su valor en el campo de texto', async () => {
    montar({ vitrinas: {} })
    await esperarFormulario()

    fireEvent.change(screen.getByLabelText('Elegir color de acento'), { target: { value: '#00aa55' } })

    expect(screen.getByLabelText('Color de acento')).toHaveValue('#00aa55')
  })

  it('un color mal escrito se explica junto al campo y deshabilita Guardar', async () => {
    const usuario = userEvent.setup()
    montar({ vitrinas: {} })
    await esperarFormulario()

    await usuario.type(screen.getByLabelText('Color de acento'), '#12')

    expect(screen.getByLabelText('Color de acento')).toHaveAccessibleDescription(/Usá el formato #rrggbb/)
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    expect(screen.getByLabelText('Elegir color de acento')).toHaveValue('#f7991c')

    await usuario.type(screen.getByLabelText('Color de acento'), '3456')

    expect(screen.getByLabelText('Color de acento')).not.toHaveAccessibleDescription(/Usá el formato/)
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })
})

describe('Vitrina — guardado', () => {
  it('manda los cinco campos recortados, aplica lo que devuelve el servidor y avisa', async () => {
    const usuario = userEvent.setup()
    montar({ vitrinas: {} })
    const slogan = await esperarFormulario()

    await usuario.type(slogan, '  Siempre cerca ')
    await usuario.type(screen.getByLabelText('Descripción'), '   ')
    fireEvent.change(screen.getByLabelText('Color de acento'), { target: { value: '#00AA55' } })
    await usuario.click(screen.getByLabelText('Claro'))
    await usuario.click(screen.getByLabelText('Publicada'))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Se guardó la vitrina.')).toBeInTheDocument()
    expect(apiPutMock).toHaveBeenCalledWith(RUTA_VITRINA, {
      publicada: true,
      tagline: 'Siempre cerca',
      descripcion: null,
      colorAcento: '#00AA55',
      tema: 'claro',
    })
    expect(screen.getByLabelText('Slogan')).toHaveValue('Siempre cerca')
    expect(screen.getByLabelText('Descripción')).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Eliminar vitrina' })).toBeEnabled()
    expect(llamadasA(apiGetMock, RUTA_VITRINA)).toHaveLength(1)
  })

  it.each([
    ['color_invalido', 'Color de acento', /tiene que tener la forma #rrggbb/],
    ['tema_invalido', 'Oscuro', null],
  ])('el rechazo %s se explica junto a su campo y el formulario conserva lo escrito', async (codigo, campo, patron) => {
    const usuario = userEvent.setup()
    montar()
    await usuario.type(await esperarFormulario(), ' y más')
    apiPutMock.mockRejectedValue(new ErrorApi(400, codigo, 'texto crudo'))

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    const alerta = await screen.findByRole('alert')
    expect(alerta).toHaveTextContent(patron ? patron : 'Elegí el tema claro o el oscuro.')
    expect(alerta).not.toHaveTextContent('texto crudo')
    if (patron) expect(screen.getByLabelText(campo)).toHaveAccessibleDescription(patron)
    expect(screen.getByLabelText('Slogan')).toHaveValue('Siempre cerca y más')
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(llamadasA(apiGetMock, RUTA_VITRINA)).toHaveLength(1)
  })

  it('texto_demasiado_largo se explica junto a las acciones, con el máximo de cada texto', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiPutMock.mockRejectedValue(new ErrorApi(400, 'texto_demasiado_largo', 'texto crudo'))

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'El slogan admite hasta 120 caracteres y la descripción hasta 2000.',
    )
    expect(screen.getByLabelText('Descripción')).toHaveValue('Un almacén de barrio.')
  })

  it('vitrina_ya_publicada se muestra y el formulario queda como estaba, sin releer', async () => {
    const usuario = userEvent.setup()
    montar({ vitrinas: { 4: vitrina(4, { tagline: 'Siempre cerca' }) } })
    await esperarFormulario()
    apiPutMock.mockRejectedValue(new ErrorApi(409, 'vitrina_ya_publicada', 'texto crudo'))
    await usuario.click(screen.getByLabelText('Publicada'))

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/Otra empresa de este comercio ya tiene su vitrina publicada/)
    expect(screen.getByLabelText('Publicada')).toBeChecked()
    expect(screen.getByLabelText('Slogan')).toHaveValue('Siempre cerca')
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(llamadasA(apiGetMock, RUTA_VITRINA)).toHaveLength(1)
  })

  it('editar el formulario borra el rechazo, que hablaba de lo que se mandó antes', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiPutMock.mockRejectedValue(new ErrorApi(409, 'vitrina_ya_publicada', 'texto crudo'))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Slogan'), '!')

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('un fallo incierto relee la vitrina, conserva lo escrito y deja el mensaje a la vista', async () => {
    const usuario = userEvent.setup()
    const { mundo } = montar()
    await usuario.type(await esperarFormulario(), ' mío')
    apiPutMock.mockImplementation(() => {
      mundo.vitrinas[4] = { ...vitrinaNorte, tagline: 'Lo que quedó en el servidor', publicada: false }

      return Promise.reject(new ErrorApi(503, 'resultado_incierto', 'No se pudo confirmar.'))
    })

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo guardar la vitrina. No se pudo confirmar.')
    // La relectura aterrizó cuando lo guardado pasa a "sin publicar": el enlace ya no se ofrece.
    expect(await screen.findByText(/todavía no está publicada/)).toBeInTheDocument()
    expect(llamadasA(apiGetMock, RUTA_VITRINA)).toHaveLength(2)
    expect(screen.getByLabelText('Slogan')).toHaveValue('Siempre cerca mío')
    expect(screen.getByLabelText('Publicada')).toBeChecked()
  })

  it('un 404 relee y el mensaje de que la empresa ya no existe queda a la vista', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiPutMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'texto crudo'))

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/La empresa ya no existe/)
    await waitFor(() => expect(llamadasA(apiGetMock, RUTA_VITRINA)).toHaveLength(2))
  })

  it('dos envíos del mismo tick salen como una sola request', async () => {
    montar()
    await esperarFormulario()
    apiPutMock.mockReturnValue(new Promise(() => {}))
    const boton = screen.getByRole('button', { name: 'Guardar' })

    act(() => {
      boton.click()
      boton.click()
    })

    expect(apiPutMock).toHaveBeenCalledTimes(1)
  })

  it('mientras la escritura está en vuelo toda la pantalla queda inerte', async () => {
    const usuario = userEvent.setup()
    montar({ empresas: [empresaNorte, empresaEste] })
    await elegirEmpresa(usuario, 4)
    await esperarFormulario()
    const pendiente = diferida<VitrinaAdmin>()
    apiPutMock.mockReturnValue(pendiente.promesa)

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    for (const control of ['Empresa', 'Publicada', 'Slogan', 'Descripción', 'Color de acento', 'Elegir color de acento', 'Claro', 'Oscuro', 'Reemplazar el logo']) {
      expect(screen.getByLabelText(control)).toBeDisabled()
    }
    expect(screen.getByRole('button', { name: 'Eliminar vitrina' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar logo' })).toBeDisabled()

    await act(async () => pendiente.resolver(vitrinaNorte))

    await waitFor(() => expect(screen.getByLabelText('Empresa')).toBeEnabled())
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })
})

describe('Vitrina — logo', () => {
  it('rinde la vista previa del logo con su versión en la URL', async () => {
    montar()
    await esperarFormulario()

    expect(screen.getByRole('img', { name: 'Logo actual' })).toHaveAttribute('src', '/api/empresas/4/vitrina/logo?v=100')
  })

  it('un archivo válido se sube crudo y la vista previa pasa a la versión nueva sin tocar el borrador', async () => {
    const usuario = userEvent.setup()
    montar()
    const slogan = await esperarFormulario()
    await usuario.type(slogan, ' sin guardar')
    const archivo = archivoDeLogo('image/webp', 2048, 'nuevo.webp')

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivo)

    await waitFor(() =>
      expect(screen.getByRole('img', { name: 'Logo actual' })).toHaveAttribute('src', '/api/empresas/4/vitrina/logo?v=101'),
    )
    expect(apiPutBinarioMock).toHaveBeenCalledWith(RUTA_LOGO, archivo)
    expect(screen.getByText('Se subió el logo.')).toBeInTheDocument()
    expect(screen.getByLabelText('Slogan')).toHaveValue('Siempre cerca sin guardar')
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('subir un logo a una empresa sin vitrina muestra la vista previa', async () => {
    const usuario = userEvent.setup()
    montar({ vitrinas: {} })
    await esperarFormulario()

    await usuario.upload(screen.getByLabelText('Subir un logo'), archivoDeLogo())

    expect(await screen.findByRole('img', { name: 'Logo actual' })).toHaveAttribute('src', '/api/empresas/4/vitrina/logo?v=1')
    expect(screen.queryByText('Todavía no hay un logo cargado.')).not.toBeInTheDocument()
  })

  it('el 415 del servidor se explica junto al logo y no relee', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiPutBinarioMock.mockRejectedValue(new ErrorApi(415, 'logo_no_admitido', 'texto crudo'))

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo())

    expect(await screen.findByRole('alert')).toHaveTextContent('El logo tiene que ser una imagen PNG, JPG o WebP.')
    expect(screen.getByLabelText('Reemplazar el logo')).toHaveAccessibleDescription(/El logo tiene que ser una imagen/)
    expect(llamadasA(apiGetMock, RUTA_VITRINA)).toHaveLength(1)
  })

  it('el 413 del servidor se explica junto al logo', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiPutBinarioMock.mockRejectedValue(new ErrorApi(413, 'logo_demasiado_grande', 'texto crudo'))

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo())

    expect(await screen.findByRole('alert')).toHaveTextContent('El logo no puede pesar más de 1 MB.')
  })

  /**
   * Cláusula bajo prueba: el tamaño se rechaza en el cliente, sin request. 1 MB justo sí sale.
   */
  it('un archivo de más de 1 MB se rechaza sin salir a la red', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo('image/png', 1024 * 1024 + 1))

    expect(await screen.findByRole('alert')).toHaveTextContent('El logo no puede pesar más de 1 MB.')
    expect(apiPutBinarioMock).not.toHaveBeenCalled()
    expect(screen.getByRole('img', { name: 'Logo actual' })).toHaveAttribute('src', '/api/empresas/4/vitrina/logo?v=100')
  })

  it('un archivo de exactamente 1 MB se sube', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo('image/png', 1024 * 1024))

    await waitFor(() => expect(apiPutBinarioMock).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('un tipo que no es PNG, JPG ni WebP se rechaza sin salir a la red', async () => {
    const usuario = userEvent.setup({ applyAccept: false })
    montar()
    await esperarFormulario()

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo('image/svg+xml', 10, 'logo.svg'))

    expect(await screen.findByRole('alert')).toHaveTextContent('El logo tiene que ser una imagen PNG, JPG o WebP.')
    expect(apiPutBinarioMock).not.toHaveBeenCalled()
  })

  it('el selector de archivo solo ofrece PNG, JPG y WebP', async () => {
    montar()
    await esperarFormulario()

    expect(screen.getByLabelText('Reemplazar el logo')).toHaveAttribute('accept', 'image/png,image/jpeg,image/webp')
  })

  it('un fallo incierto de la subida relee la vitrina y deja el mensaje a la vista', async () => {
    const usuario = userEvent.setup()
    const { mundo } = montar()
    await esperarFormulario()
    apiPutBinarioMock.mockImplementation(() => {
      mundo.vitrinas[4] = { ...vitrinaNorte, logoVersion: 777 }

      return Promise.reject(new ErrorApi(503, 'resultado_incierto', 'No se pudo confirmar.'))
    })

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo())

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo subir el logo. No se pudo confirmar.')
    await waitFor(() =>
      expect(screen.getByRole('img', { name: 'Logo actual' })).toHaveAttribute('src', '/api/empresas/4/vitrina/logo?v=777'),
    )
  })

  it('dos cambios del mismo tick salen como una sola subida', async () => {
    montar()
    await esperarFormulario()
    apiPutBinarioMock.mockReturnValue(new Promise(() => {}))
    const entrada = screen.getByLabelText('Reemplazar el logo')
    const archivo = archivoDeLogo()

    act(() => {
      fireEvent.change(entrada, { target: { files: [archivo] } })
      fireEvent.change(entrada, { target: { files: [archivo] } })
    })

    expect(apiPutBinarioMock).toHaveBeenCalledTimes(1)
  })

  it('mientras el logo se sube la pantalla queda inerte', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    const pendiente = diferida<VitrinaAdmin>()
    apiPutBinarioMock.mockReturnValue(pendiente.promesa)

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo())

    expect(screen.getByLabelText('Slogan')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar logo' })).toBeDisabled()
    expect(screen.getByLabelText('Reemplazar el logo')).toBeDisabled()

    await act(async () => pendiente.resolver(vitrinaNorte))

    await waitFor(() => expect(screen.getByLabelText('Slogan')).toBeEnabled())
  })

  it('quitar el logo pide confirmación y recién al confirmar borra; la vista previa desaparece con la relectura', async () => {
    const usuario = userEvent.setup()
    const { mundo } = montar()
    await usuario.type(await esperarFormulario(), ' sin guardar')

    await usuario.click(screen.getByRole('button', { name: 'Quitar logo' }))

    expect(screen.getByRole('alertdialog', { name: 'Confirmar quitar logo' })).toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()

    mundo.vitrinas[4] = { ...vitrinaNorte, tieneLogo: false, logoVersion: null }
    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar logo' }))

    await waitFor(() => expect(screen.queryByRole('img', { name: 'Logo actual' })).not.toBeInTheDocument())
    expect(apiDeleteMock).toHaveBeenCalledWith(RUTA_LOGO)
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('Se quitó el logo.')).toBeInTheDocument()
    expect(screen.getByLabelText('Slogan')).toHaveValue('Siempre cerca sin guardar')
  })

  it('cancelar la puerta del logo no toca la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()

    await usuario.click(screen.getByRole('button', { name: 'Quitar logo' }))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  it('con la puerta abierta el resto de la pantalla queda inerte', async () => {
    const usuario = userEvent.setup()
    montar({ empresas: [empresaNorte, empresaEste] })
    await elegirEmpresa(usuario, 4)
    await esperarFormulario()

    await usuario.click(screen.getByRole('button', { name: 'Quitar logo' }))

    for (const control of ['Empresa', 'Publicada', 'Slogan', 'Reemplazar el logo']) {
      expect(screen.getByLabelText(control)).toBeDisabled()
    }
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Eliminar vitrina' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar logo' })).toBeDisabled()
  })

  it('un rechazo deja la puerta abierta con el motivo al lado', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'texto crudo'))

    await usuario.click(screen.getByRole('button', { name: 'Quitar logo' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar logo' }))

    expect(await screen.findByText(/No se pudo quitar el logo\./)).toBeInTheDocument()
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    await waitFor(() => expect(llamadasA(apiGetMock, RUTA_VITRINA)).toHaveLength(2))
  })

  it('dos confirmaciones del mismo tick salen como un solo DELETE', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiDeleteMock.mockReturnValue(new Promise(() => {}))
    await usuario.click(screen.getByRole('button', { name: 'Quitar logo' }))
    const confirmar = screen.getByRole('button', { name: 'Confirmar quitar logo' })

    act(() => {
      confirmar.click()
      confirmar.click()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  it('si el borrado commiteó pero la relectura falla, no se informa como fallido', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    const original = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === RUTA_VITRINA ? Promise.reject(new ErrorApi(500, 'error_interno', 'Falló.')) : original(ruta),
    )

    await usuario.click(screen.getByRole('button', { name: 'Quitar logo' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar logo' }))

    expect(
      await screen.findByText('Se quitó el logo, pero no se pudo actualizar la vista. Recargá la pantalla.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/No se pudo quitar/)).not.toBeInTheDocument()
  })
})

describe('Vitrina — eliminar', () => {
  it('pide confirmación y recién al confirmar elimina; la relectura devuelve el formulario en blanco', async () => {
    const usuario = userEvent.setup()
    const { mundo } = montar()
    await esperarFormulario()

    await usuario.click(screen.getByRole('button', { name: 'Eliminar vitrina' }))

    expect(screen.getByRole('alertdialog', { name: 'Confirmar eliminación' })).toBeInTheDocument()
    expect(screen.getByText(/¿Eliminar la vitrina de "Almacén Norte"\?/)).toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()

    mundo.vitrinas = {}
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))

    await waitFor(() => expect(screen.getByLabelText('Slogan')).toHaveValue(''))
    expect(apiDeleteMock).toHaveBeenCalledWith(RUTA_VITRINA)
    expect(screen.getByLabelText('Publicada')).not.toBeChecked()
    expect(screen.queryByRole('button', { name: 'Eliminar vitrina' })).not.toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('Se eliminó la vitrina.')).toBeInTheDocument()
  })

  it('si la baja commiteó pero la relectura falla, no se informa como fallida', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    const original = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === RUTA_VITRINA ? Promise.reject(new ErrorApi(500, 'error_interno', 'Falló.')) : original(ruta),
    )

    await usuario.click(screen.getByRole('button', { name: 'Eliminar vitrina' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))

    expect(
      await screen.findByText('Se eliminó la vitrina, pero no se pudo actualizar la vista. Recargá la pantalla.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/No se pudo eliminar/)).not.toBeInTheDocument()
  })

  it('cancelar la puerta no toca la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()

    await usuario.click(screen.getByRole('button', { name: 'Eliminar vitrina' }))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  it('un rechazo deja la puerta abierta con el motivo al lado', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiDeleteMock.mockRejectedValue(new ErrorApi(400, 'otro', 'No se puede.'))

    await usuario.click(screen.getByRole('button', { name: 'Eliminar vitrina' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))

    expect(await screen.findByText('No se puede.')).toBeInTheDocument()
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    expect(llamadasA(apiGetMock, RUTA_VITRINA)).toHaveLength(1)
  })

  it('dos confirmaciones del mismo tick salen como un solo DELETE', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiDeleteMock.mockReturnValue(new Promise(() => {}))
    await usuario.click(screen.getByRole('button', { name: 'Eliminar vitrina' }))
    const confirmar = screen.getByRole('button', { name: 'Confirmar eliminación' })

    act(() => {
      confirmar.click()
      confirmar.click()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })
})

describe('Vitrina — enlace a la página', () => {
  it('bajo el dominio de un comercio y con la vitrina publicada ofrece abrirla en otra pestaña', async () => {
    montar()
    await esperarFormulario()

    const enlace = await screen.findByRole('link', { name: /^Ver la página/ })
    expect(enlace).toHaveAttribute('href', '/')
    expect(enlace).toHaveAttribute('target', '_blank')
    expect(enlace).toHaveAttribute('rel', 'noopener noreferrer')
    expect(enlace).toHaveAccessibleName('Ver la página (se abre en una pestaña nueva)')
  })

  it('bajo la plataforma no hay enlace y la ayuda dice dónde se ve la página; mientras se lee el sitio no dice nada', async () => {
    const sitio = diferida<SitioPublico>()
    prepararMundo()
    const original = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) => (ruta === '/publico/sitio' ? sitio.promesa : original(ruta)))
    render(<Vitrina />)
    await esperarFormulario()

    expect(screen.queryByText(/se ve bajo el dominio propio del comercio/)).not.toBeInTheDocument()
    expect(screen.queryByText(/No pudimos comprobar el dominio/)).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /^Ver la página/ })).not.toBeInTheDocument()

    await act(async () => sitio.resolver(SITIO_DE_PLATAFORMA))

    expect(screen.queryByRole('link', { name: /^Ver la página/ })).not.toBeInTheDocument()
    expect(screen.getByText(/se ve bajo el dominio propio del comercio/)).toBeInTheDocument()
  })

  it('si el sitio no se pudo leer no hay enlace y lo dice, sin atribuirlo a la plataforma', async () => {
    montar({ sitio: 'falla' })
    await esperarFormulario()

    expect(await screen.findByText('No pudimos comprobar el dominio del comercio.')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /^Ver la página/ })).not.toBeInTheDocument()
    expect(screen.queryByText(/se ve bajo el dominio propio del comercio/)).not.toBeInTheDocument()
  })

  it('con la vitrina sin publicar no hay enlace y la ayuda dice qué falta; publicarla y guardar lo habilita', async () => {
    const usuario = userEvent.setup()
    montar({ vitrinas: { 4: vitrina(4, { tagline: 'Hola' }) } })
    await esperarFormulario()
    expect(await screen.findByText(/todavía no está publicada/)).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /^Ver la página/ })).not.toBeInTheDocument()

    await usuario.click(screen.getByLabelText('Publicada'))
    expect(screen.queryByRole('link', { name: /^Ver la página/ })).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('link', { name: /^Ver la página/ })).toBeInTheDocument()
  })

  it('desmarcar Publicada sin guardar no quita el enlace de lo que sigue publicado', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    await screen.findByRole('link', { name: /^Ver la página/ })

    await usuario.click(screen.getByLabelText('Publicada'))

    expect(screen.getByRole('link', { name: /^Ver la página/ })).toBeInTheDocument()
  })
})

describe('Vitrina — el error de una relectura fallida no sobrevive a una escritura que sale bien', () => {
  const ERROR_DE_LECTURA = 'No se pudo cargar la vitrina. Probá de nuevo.'

  /** La relectura de la vitrina falla; todo lo demás sigue funcionando. Devuelve cómo repararla. */
  function romperLaRelectura() {
    const original = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === RUTA_VITRINA ? Promise.reject(new ErrorApi(500, 'error_interno', 'boom')) : original(ruta),
    )

    return () => apiGetMock.mockImplementation(original)
  }

  const INCIERTO = new ErrorApi(503, 'resultado_incierto', 'No se pudo confirmar.')

  it('un guardado que sale bien lo quita', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    romperLaRelectura()
    apiPutMock.mockRejectedValueOnce(INCIERTO)

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(await screen.findByText(ERROR_DE_LECTURA)).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Se guardó la vitrina.')).toBeInTheDocument()
    expect(screen.queryByText(ERROR_DE_LECTURA)).not.toBeInTheDocument()
  })

  it('una subida de logo que sale bien lo quita', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    romperLaRelectura()
    apiPutBinarioMock.mockRejectedValueOnce(INCIERTO)

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo())
    expect(await screen.findByText(ERROR_DE_LECTURA)).toBeInTheDocument()

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo())

    expect(await screen.findByText('Se subió el logo.')).toBeInTheDocument()
    expect(screen.queryByText(ERROR_DE_LECTURA)).not.toBeInTheDocument()
  })

  it('quitar el logo bien lo quita, aunque la relectura de después vuelva a fallar', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    romperLaRelectura()
    apiDeleteMock.mockRejectedValueOnce(INCIERTO)

    await usuario.click(screen.getByRole('button', { name: 'Quitar logo' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar logo' }))
    expect(await screen.findByText(ERROR_DE_LECTURA)).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar logo' }))

    expect(
      await screen.findByText('Se quitó el logo, pero no se pudo actualizar la vista. Recargá la pantalla.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(ERROR_DE_LECTURA)).not.toBeInTheDocument()
  })
})

describe('Vitrina — cambios sin guardar al elegir otra empresa', () => {
  const vitrinaEste = vitrina(5, { tagline: 'Abierto todo el día' })

  async function conCambiosSinGuardar() {
    const usuario = userEvent.setup()
    montar({ empresas: [empresaNorte, empresaEste], vitrinas: { 4: vitrinaNorte, 5: vitrinaEste } })
    await elegirEmpresa(usuario, 4)
    await usuario.type(await esperarFormulario(), ' mío')

    return usuario
  }

  it('con cambios pregunta antes de cambiar, deja la pantalla inerte y no lee la otra vitrina', async () => {
    const usuario = await conCambiosSinGuardar()

    await elegirEmpresa(usuario, 5)

    expect(screen.getByRole('alert')).toHaveTextContent('Hay cambios sin guardar en la vitrina de "Almacén Norte"')
    expect(screen.getByLabelText('Empresa')).toHaveValue('4')
    expect(screen.getByLabelText('Empresa')).toBeDisabled()
    expect(screen.getByLabelText('Slogan')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Seguir editando' })).toHaveFocus()
    expect(llamadasA(apiGetMock, '/empresas/5/vitrina')).toHaveLength(0)
  })

  it('descartar cambia de empresa y lee su vitrina', async () => {
    const usuario = await conCambiosSinGuardar()
    await elegirEmpresa(usuario, 5)

    await usuario.click(screen.getByRole('button', { name: 'Descartar cambios' }))

    expect(await screen.findByDisplayValue('Abierto todo el día')).toBeInTheDocument()
    expect(screen.getByLabelText('Empresa')).toHaveValue('5')
    expect(screen.queryByRole('button', { name: 'Descartar cambios' })).not.toBeInTheDocument()
    expect(screen.queryByDisplayValue('Siempre cerca mío')).not.toBeInTheDocument()
  })

  it('seguir editando se queda en la empresa con lo escrito intacto y el foco en el selector', async () => {
    const usuario = await conCambiosSinGuardar()
    await elegirEmpresa(usuario, 5)

    await usuario.click(screen.getByRole('button', { name: 'Seguir editando' }))

    expect(screen.queryByRole('button', { name: 'Descartar cambios' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Empresa')).toHaveValue('4')
    expect(screen.getByLabelText('Empresa')).toBeEnabled()
    expect(screen.getByLabelText('Empresa')).toHaveFocus()
    expect(screen.getByLabelText('Slogan')).toHaveValue('Siempre cerca mío')
    expect(screen.getByLabelText('Slogan')).toBeEnabled()
    expect(llamadasA(apiGetMock, '/empresas/5/vitrina')).toHaveLength(0)
  })

  it('sin cambios cambia de empresa sin preguntar', async () => {
    const usuario = userEvent.setup()
    montar({ empresas: [empresaNorte, empresaEste], vitrinas: { 4: vitrinaNorte, 5: vitrinaEste } })
    await elegirEmpresa(usuario, 4)
    await esperarFormulario()

    await elegirEmpresa(usuario, 5)

    expect(await screen.findByDisplayValue('Abierto todo el día')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Descartar cambios' })).not.toBeInTheDocument()
  })

  it('un espacio al final no cuenta como cambio', async () => {
    const usuario = userEvent.setup()
    montar({ empresas: [empresaNorte, empresaEste], vitrinas: { 4: vitrinaNorte, 5: vitrinaEste } })
    await elegirEmpresa(usuario, 4)
    await usuario.type(await esperarFormulario(), ' ')

    await elegirEmpresa(usuario, 5)

    expect(await screen.findByDisplayValue('Abierto todo el día')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Descartar cambios' })).not.toBeInTheDocument()
  })

  it('después de guardar ya no hay cambios que perder', async () => {
    const usuario = await conCambiosSinGuardar()
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(await screen.findByText('Se guardó la vitrina.')).toBeInTheDocument()

    await elegirEmpresa(usuario, 5)

    expect(await screen.findByDisplayValue('Abierto todo el día')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Descartar cambios' })).not.toBeInTheDocument()
  })
})

describe('Vitrina — accesibilidad', () => {
  it('el aviso de éxito es un estado y los errores son alertas', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Se guardó la vitrina.')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('el rechazo de la baja es una alerta', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiDeleteMock.mockRejectedValue(new ErrorApi(400, 'otro', 'No se puede.'))

    await usuario.click(screen.getByRole('button', { name: 'Eliminar vitrina' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No se puede.')
  })

  it('si la vista previa del logo no carga, se oculta la imagen rota y se dice; una subida nueva la trae de vuelta', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()

    fireEvent.error(screen.getByRole('img', { name: 'Logo actual' }))

    expect(screen.queryByRole('img', { name: 'Logo actual' })).not.toBeInTheDocument()
    expect(screen.getByText('No se pudo mostrar el logo.')).toBeInTheDocument()

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo())

    expect(await screen.findByRole('img', { name: 'Logo actual' })).toHaveAttribute('src', '/api/empresas/4/vitrina/logo?v=101')
    expect(screen.queryByText('No se pudo mostrar el logo.')).not.toBeInTheDocument()
  })
})

describe('Vitrina — cada acción borra lo que dijo la anterior', () => {
  it('guardar borra el aviso de éxito de una subida anterior', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo())
    expect(await screen.findByText('Se subió el logo.')).toBeInTheDocument()

    apiPutMock.mockReturnValue(new Promise(() => {}))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(screen.queryByText('Se subió el logo.')).not.toBeInTheDocument()
  })

  it('subir un logo borra el rechazo de un guardado anterior', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiPutMock.mockRejectedValue(new ErrorApi(409, 'vitrina_ya_publicada', 'texto crudo'))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()

    apiPutBinarioMock.mockReturnValue(new Promise(() => {}))
    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo())

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('elegir un archivo inválido borra el rechazo de un guardado anterior', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiPutMock.mockRejectedValue(new ErrorApi(409, 'vitrina_ya_publicada', 'texto crudo'))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(await screen.findByText(/Otra empresa de este comercio/)).toBeInTheDocument()

    await usuario.upload(screen.getByLabelText('Reemplazar el logo'), archivoDeLogo('image/png', 1024 * 1024 + 1))

    expect(screen.queryByText(/Otra empresa de este comercio/)).not.toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('El logo no puede pesar más de 1 MB.')
  })

  it('abrir la puerta de eliminar borra el aviso de éxito de una acción anterior', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(await screen.findByText('Se guardó la vitrina.')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Eliminar vitrina' }))

    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    expect(screen.queryByText('Se guardó la vitrina.')).not.toBeInTheDocument()
  })

  it('reintentar la baja borra el rechazo anterior', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(400, 'otro', 'No se puede.'))
    await usuario.click(screen.getByRole('button', { name: 'Eliminar vitrina' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))
    expect(await screen.findByText('No se puede.')).toBeInTheDocument()

    apiDeleteMock.mockReturnValue(new Promise(() => {}))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))

    expect(screen.queryByText('No se puede.')).not.toBeInTheDocument()
  })

  it('cancelar la puerta borra el rechazo que quedó junto a ella', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFormulario()
    apiDeleteMock.mockRejectedValue(new ErrorApi(400, 'otro', 'No se puede.'))
    await usuario.click(screen.getByRole('button', { name: 'Eliminar vitrina' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))
    expect(await screen.findByText('No se puede.')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByText('No se puede.')).not.toBeInTheDocument()
  })

  it('elegir otra empresa borra el aviso aunque su vitrina siga pendiente', async () => {
    const usuario = userEvent.setup()
    montar({ empresas: [empresaNorte, empresaEste] })
    await elegirEmpresa(usuario, 4)
    await esperarFormulario()
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(await screen.findByText('Se guardó la vitrina.')).toBeInTheDocument()
    const original = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/empresas/5/vitrina' ? new Promise(() => {}) : original(ruta),
    )

    await elegirEmpresa(usuario, 5)

    expect(screen.queryByText('Se guardó la vitrina.')).not.toBeInTheDocument()
  })
})
