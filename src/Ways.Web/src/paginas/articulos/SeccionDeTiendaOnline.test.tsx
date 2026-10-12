import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { SeccionDeTiendaOnline } from './SeccionDeTiendaOnline'
import { ErrorApi } from '../../api/cliente'
import { ROL, type ArticuloListado, type UsuarioAutenticado } from '../../api/tipos'
import { AuthContext } from '../../auth/AuthContext'
import { Modal } from '../../componentes/Modal'

// Sección "Tienda online" del formulario de artículo: la publicación y la imagen escriben por su cuenta.

const apiGetMock = vi.fn()
const apiPutMock = vi.fn()
const apiPutBinarioMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../../api/cliente', async (importarOriginal) => {
  const original = await importarOriginal<typeof import('../../api/cliente')>()

  return {
    ...original,
    api: {
      get: (...args: unknown[]) => apiGetMock(...(args as [string])),
      put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
      putBinario: (...args: unknown[]) => apiPutBinarioMock(...(args as [string, File])),
      delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
    },
  }
})

const alDeEscribir = vi.fn()
const alCambiar = vi.fn()

function sesion(rolId: number | null) {
  const usuario: UsuarioAutenticado | null =
    rolId === null
      ? null
      : { id: 1, usuario: 'ana', mail: 'ana@comercio.test', rolId, rol: 'Rol', ultimaConexion: null, idTenant: 1 }

  return { usuario, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }
}

type Opciones = {
  rolId?: number | null
  publicado?: boolean
  tieneImagen?: boolean
  bloqueadoPorPadre?: boolean
}

function montar({ rolId = ROL.Admin, publicado = false, tieneImagen = false, bloqueadoPorPadre = false }: Opciones = {}) {
  return render(
    <AuthContext.Provider value={sesion(rolId)}>
      <SeccionDeTiendaOnline
        idArticulo={7}
        inicial={{ publicadoWeb: publicado, tieneImagen }}
        bloqueadoPorPadre={bloqueadoPorPadre}
        alDeEscribir={alDeEscribir}
        alCambiar={alCambiar}
      />
    </AuthContext.Provider>,
  )
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

function imagen(tipo = 'image/png', bytes = 10, nombre = 'foto.png') {
  return new File([new Uint8Array(bytes)], nombre, { type: tipo })
}

const SWITCH = 'Publicado en la tienda'
const RUTA_WEB = '/articulos/7/web'
const RUTA_IMAGEN = '/articulos/7/imagen'
const UN_MB = 1024 * 1024

function articuloLeido(sobrescribir: Partial<ArticuloListado> = {}) {
  return { id: 7, publicadoWeb: false, tieneImagen: false, ...sobrescribir }
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPutMock.mockReset()
  apiPutBinarioMock.mockReset()
  apiDeleteMock.mockReset()
  alDeEscribir.mockReset()
  alCambiar.mockReset()
})

describe('SeccionDeTiendaOnline — quién la ve', () => {
  it('un admin ve la sección', () => {
    montar({ rolId: ROL.Admin })

    expect(screen.getByRole('switch', { name: SWITCH })).toBeInTheDocument()
    expect(screen.getByLabelText('Subir una imagen')).toBeInTheDocument()
  })

  it.each([
    ['vendedor', ROL.Vendedor],
    ['supervisor', ROL.Supervisor],
    ['root', ROL.Root],
    ['sin sesión', null],
  ])('%s no la ve', (_nombre, rolId) => {
    montar({ rolId })

    expect(screen.queryByText('Tienda online')).not.toBeInTheDocument()
    expect(screen.queryByRole('switch')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Subir una imagen')).not.toBeInTheDocument()
  })

  it('sin un AuthProvider por encima tampoco la ve', () => {
    render(
      <SeccionDeTiendaOnline
        idArticulo={7}
        inicial={{ publicadoWeb: false, tieneImagen: false }}
        bloqueadoPorPadre={false}
        alDeEscribir={alDeEscribir}
        alCambiar={alCambiar}
      />,
    )

    expect(screen.queryByRole('switch')).not.toBeInTheDocument()
  })
})

describe('SeccionDeTiendaOnline — publicar', () => {
  it('arranca con lo que trajo el artículo', () => {
    montar({ publicado: true })

    expect(screen.getByRole('switch', { name: SWITCH })).toBeChecked()
  })

  it('el interruptor escribe con su propio PUT, refleja lo que contestó el servidor y avisa a la grilla', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValue({ idArticulo: 7, publicado: true })
    montar()

    await usuario.click(screen.getByRole('switch', { name: SWITCH }))

    expect(await screen.findByText('El artículo quedó publicado en la tienda.')).toBeInTheDocument()
    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(apiPutMock).toHaveBeenCalledWith(RUTA_WEB, { publicado: true })
    expect(screen.getByRole('switch', { name: SWITCH })).toBeChecked()
    expect(screen.getByRole('switch', { name: SWITCH })).toBeEnabled()
    expect(alCambiar).toHaveBeenCalledTimes(1)
    expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
  })

  it('despublicar manda publicado: false', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValue({ idArticulo: 7, publicado: false })
    montar({ publicado: true })

    await usuario.click(screen.getByRole('switch', { name: SWITCH }))

    expect(await screen.findByText('El artículo quedó fuera de la tienda.')).toBeInTheDocument()
    expect(apiPutMock).toHaveBeenCalledWith(RUTA_WEB, { publicado: false })
    expect(screen.getByRole('switch', { name: SWITCH })).not.toBeChecked()
  })

  it('el interruptor muestra lo que quedó guardado, no lo que se pidió', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValue({ idArticulo: 7, publicado: false })
    montar()

    await usuario.click(screen.getByRole('switch', { name: SWITCH }))

    await waitFor(() => expect(alCambiar).toHaveBeenCalled())
    expect(screen.getByRole('switch', { name: SWITCH })).not.toBeChecked()
  })

  /**
   * Cláusula bajo prueba: la guarda de reentrancia es un ref. Dos clics en el mismo tick leen el mismo render, así
   * que una guarda sobre estado dejaría pasar los dos.
   */
  it('dos clics del mismo tick salen como una sola request', async () => {
    apiPutMock.mockReturnValue(new Promise(() => {}))
    montar()
    const interruptor = screen.getByRole('switch', { name: SWITCH })

    act(() => {
      fireEvent.click(interruptor)
      fireEvent.click(interruptor)
    })

    expect(apiPutMock).toHaveBeenCalledTimes(1)
  })

  it('un rechazo conserva el estado, explica el motivo y no avisa a la grilla ni relee', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso para publicar.'))
    montar()

    await usuario.click(screen.getByRole('switch', { name: SWITCH }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No tenés permiso para publicar.')
    expect(screen.getByRole('switch', { name: SWITCH })).not.toBeChecked()
    expect(screen.getByRole('switch', { name: SWITCH })).toBeEnabled()
    expect(alCambiar).not.toHaveBeenCalled()
    expect(apiGetMock).not.toHaveBeenCalled()
    expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
  })

  it('un 404 dice que el artículo ya no existe y la grilla se entera, aunque la relectura también dé 404', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el artículo 7.'))
    apiGetMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el artículo 7.'))
    montar()

    await usuario.click(screen.getByRole('switch', { name: SWITCH }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo publicar el artículo. El artículo ya no existe.')
    await waitFor(() => expect(alCambiar).toHaveBeenCalledTimes(1))
  })

  it('un fallo incierto relee el artículo, muestra el estado real, deja el mensaje a la vista y avisa a la grilla DESPUÉS de releer', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new ErrorApi(500, 'error', 'Error 500.'))
    apiGetMock.mockResolvedValue(articuloLeido({ publicadoWeb: true }))
    montar()

    await usuario.click(screen.getByRole('switch', { name: SWITCH }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo confirmar el resultado')
    await waitFor(() => expect(alCambiar).toHaveBeenCalledTimes(1))
    expect(apiGetMock).toHaveBeenCalledWith('/articulos/7')
    expect(screen.getByRole('switch', { name: SWITCH })).toBeChecked()
    expect(apiGetMock.mock.invocationCallOrder[0]).toBeLessThan(alCambiar.mock.invocationCallOrder[0])
  })

  it('si la relectura también falla, el mensaje del fallo sigue a la vista y la grilla igual se entera', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new Error('sin red'))
    apiGetMock.mockRejectedValue(new Error('sin red'))
    montar()

    await usuario.click(screen.getByRole('switch', { name: SWITCH }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo publicar el artículo. No se pudo confirmar el resultado')
    await waitFor(() => expect(alCambiar).toHaveBeenCalledTimes(1))
    expect(screen.getByRole('switch', { name: SWITCH })).toBeEnabled()
  })

  it('mientras se escribe, todo lo de la sección queda inerte', async () => {
    const usuario = userEvent.setup()
    const pendiente = diferida<{ idArticulo: number; publicado: boolean }>()
    apiPutMock.mockReturnValue(pendiente.promesa)
    montar({ tieneImagen: true })

    await usuario.click(screen.getByRole('switch', { name: SWITCH }))

    expect(screen.getByRole('switch', { name: SWITCH })).toBeDisabled()
    expect(screen.getByLabelText('Reemplazar la imagen')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar imagen' })).toBeDisabled()

    await act(async () => pendiente.resolver({ idArticulo: 7, publicado: true }))

    await waitFor(() => expect(screen.getByRole('switch', { name: SWITCH })).toBeEnabled())
  })

  it('si el formulario está ocupado con otra cosa, los controles quedan inertes', () => {
    montar({ tieneImagen: true, bloqueadoPorPadre: true })

    expect(screen.getByRole('switch', { name: SWITCH })).toBeDisabled()
    expect(screen.getByLabelText('Reemplazar la imagen')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar imagen' })).toBeDisabled()
  })
})

describe('SeccionDeTiendaOnline — imagen', () => {
  it('sin imagen lo dice y no ofrece quitar', () => {
    montar({ tieneImagen: false })

    expect(screen.getByText('Todavía no hay una imagen cargada.')).toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Quitar imagen' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Subir una imagen')).toHaveAttribute('accept', 'image/png,image/jpeg,image/webp')
  })

  it('con imagen la muestra desde la ruta del artículo', () => {
    montar({ tieneImagen: true })

    expect(screen.getByRole('img', { name: 'Imagen del artículo' })).toHaveAttribute('src', '/api/articulos/7/imagen')
  })

  it('un archivo válido se sube crudo, la vista previa pasa a la versión nueva y la grilla se entera', async () => {
    const usuario = userEvent.setup()
    apiPutBinarioMock.mockResolvedValue({ idArticulo: 7, tieneImagen: true, imagenVersion: 1760000001 })
    montar()
    const archivo = imagen()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), archivo)

    expect(await screen.findByText('Se subió la imagen.')).toBeInTheDocument()
    expect(apiPutBinarioMock).toHaveBeenCalledTimes(1)
    expect(apiPutBinarioMock).toHaveBeenCalledWith(RUTA_IMAGEN, archivo)
    expect(apiPutMock).not.toHaveBeenCalled()
    expect(screen.getByRole('img', { name: 'Imagen del artículo' })).toHaveAttribute(
      'src',
      '/api/articulos/7/imagen?v=1760000001',
    )
    expect(screen.getByLabelText('Reemplazar la imagen')).toBeEnabled()
    expect(alCambiar).toHaveBeenCalledTimes(1)
    expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
  })

  it('reemplazar cambia la versión de la URL', async () => {
    const usuario = userEvent.setup()
    apiPutBinarioMock.mockResolvedValue({ idArticulo: 7, tieneImagen: true, imagenVersion: 1760000009 })
    montar({ tieneImagen: true })

    await usuario.upload(screen.getByLabelText('Reemplazar la imagen'), imagen('image/webp', 10, 'foto.webp'))

    await waitFor(() =>
      expect(screen.getByRole('img', { name: 'Imagen del artículo' })).toHaveAttribute(
        'src',
        '/api/articulos/7/imagen?v=1760000009',
      ),
    )
  })

  it.each([
    [413, 'imagen_demasiado_grande', 'La imagen no puede pesar más de 1 MB.'],
    [415, 'imagen_no_admitida', 'La imagen tiene que ser PNG, JPG o WebP.'],
    [400, 'imagen_no_admitida', 'La imagen tiene que ser PNG, JPG o WebP.'],
  ])('el %s del servidor (%s) se explica junto a la imagen, sin texto crudo ni relectura', async (estado, codigo, copia) => {
    const usuario = userEvent.setup()
    apiPutBinarioMock.mockRejectedValue(new ErrorApi(estado, codigo, 'texto crudo'))
    montar()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen())

    const alerta = await screen.findByRole('alert')
    expect(alerta).toHaveTextContent(copia)
    expect(alerta).not.toHaveTextContent('texto crudo')
    expect(screen.getByLabelText('Subir una imagen')).toHaveAccessibleDescription(new RegExp(`${copia}$`))
    expect(screen.getByText('Todavía no hay una imagen cargada.')).toBeInTheDocument()
    expect(apiGetMock).not.toHaveBeenCalled()
    expect(alCambiar).not.toHaveBeenCalled()
  })

  /**
   * Cláusula bajo prueba: el tamaño se rechaza en el cliente, sin request. 1 MB justo sí sale.
   */
  it('un archivo de más de 1 MB se rechaza sin salir a la red', async () => {
    const usuario = userEvent.setup()
    montar()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen('image/png', UN_MB + 1))

    expect(await screen.findByRole('alert')).toHaveTextContent('La imagen no puede pesar más de 1 MB.')
    expect(apiPutBinarioMock).not.toHaveBeenCalled()
    expect(alDeEscribir).not.toHaveBeenCalled()
  })

  it('un archivo de exactamente 1 MB se sube', async () => {
    const usuario = userEvent.setup()
    apiPutBinarioMock.mockResolvedValue({ idArticulo: 7, tieneImagen: true, imagenVersion: 1 })
    montar()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen('image/png', UN_MB))

    await waitFor(() => expect(apiPutBinarioMock).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('un tipo que no es PNG, JPG ni WebP se rechaza sin salir a la red', async () => {
    const usuario = userEvent.setup({ applyAccept: false })
    montar()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen('image/svg+xml', 10, 'foto.svg'))

    expect(await screen.findByRole('alert')).toHaveTextContent('La imagen tiene que ser PNG, JPG o WebP.')
    expect(apiPutBinarioMock).not.toHaveBeenCalled()
  })

  it('el tipo se mira antes que el tamaño', async () => {
    const usuario = userEvent.setup({ applyAccept: false })
    montar()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen('image/gif', UN_MB + 1, 'foto.gif'))

    expect(await screen.findByRole('alert')).toHaveTextContent('La imagen tiene que ser PNG, JPG o WebP.')
  })

  it('dos cambios del mismo tick salen como una sola subida', () => {
    apiPutBinarioMock.mockReturnValue(new Promise(() => {}))
    montar()
    const entrada = screen.getByLabelText('Subir una imagen')
    const archivo = imagen()

    act(() => {
      fireEvent.change(entrada, { target: { files: [archivo] } })
      fireEvent.change(entrada, { target: { files: [archivo] } })
    })

    expect(apiPutBinarioMock).toHaveBeenCalledTimes(1)
  })

  it('un fallo incierto de la subida relee el artículo y deja el mensaje a la vista', async () => {
    const usuario = userEvent.setup()
    apiPutBinarioMock.mockRejectedValue(new ErrorApi(503, 'error', 'Error 503.'))
    apiGetMock.mockResolvedValue(articuloLeido({ tieneImagen: true }))
    montar()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen())

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo subir la imagen. No se pudo confirmar el resultado')
    expect(apiGetMock).toHaveBeenCalledWith('/articulos/7')
    expect(await screen.findByRole('img', { name: 'Imagen del artículo' })).toBeInTheDocument()
  })

  it('tras un fallo incierto la vista previa se vuelve a pedir con una versión nueva y se levanta el aviso de imagen rota', async () => {
    const usuario = userEvent.setup()
    const ahora = vi.spyOn(Date, 'now').mockReturnValue(1760000777)
    try {
      apiPutBinarioMock.mockRejectedValue(new ErrorApi(503, 'error', 'Error 503.'))
      apiGetMock.mockResolvedValue(articuloLeido({ tieneImagen: true }))
      montar({ tieneImagen: true })
      expect(screen.getByRole('img', { name: 'Imagen del artículo' })).toHaveAttribute('src', '/api/articulos/7/imagen')
      fireEvent.error(screen.getByRole('img', { name: 'Imagen del artículo' }))
      expect(screen.getByText('No se pudo mostrar la imagen.')).toBeInTheDocument()

      await usuario.upload(screen.getByLabelText('Reemplazar la imagen'), imagen())

      expect(await screen.findByRole('img', { name: 'Imagen del artículo' })).toHaveAttribute(
        'src',
        '/api/articulos/7/imagen?v=1760000777',
      )
      expect(screen.queryByText('No se pudo mostrar la imagen.')).not.toBeInTheDocument()
    } finally {
      ahora.mockRestore()
    }
  })

  it('si la relectura dice que no quedó imagen, se muestra que no hay y no queda vista previa', async () => {
    const usuario = userEvent.setup()
    apiPutBinarioMock.mockRejectedValue(new ErrorApi(503, 'error', 'Error 503.'))
    apiGetMock.mockResolvedValue(articuloLeido({ tieneImagen: false }))
    montar({ tieneImagen: true })

    await usuario.upload(screen.getByLabelText('Reemplazar la imagen'), imagen())

    expect(await screen.findByText('Todavía no hay una imagen cargada.')).toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })

  it('si la vista previa no carga, se oculta la imagen rota y se dice; una subida nueva la trae de vuelta', async () => {
    const usuario = userEvent.setup()
    apiPutBinarioMock.mockResolvedValue({ idArticulo: 7, tieneImagen: true, imagenVersion: 5 })
    montar({ tieneImagen: true })

    fireEvent.error(screen.getByRole('img', { name: 'Imagen del artículo' }))

    expect(screen.queryByRole('img', { name: 'Imagen del artículo' })).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('No se pudo mostrar la imagen.')

    await usuario.upload(screen.getByLabelText('Reemplazar la imagen'), imagen())

    expect(await screen.findByRole('img', { name: 'Imagen del artículo' })).toHaveAttribute('src', '/api/articulos/7/imagen?v=5')
  })
})

describe('SeccionDeTiendaOnline — quitar la imagen', () => {
  it('pide confirmación y recién al confirmar borra; la vista previa desaparece y la grilla se entera', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockResolvedValue(undefined)
    montar({ tieneImagen: true })

    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    expect(screen.getByRole('alertdialog')).toHaveTextContent('¿Quitar la imagen del artículo?')
    expect(apiDeleteMock).not.toHaveBeenCalled()

    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar imagen' }))

    expect(await screen.findByText('Se quitó la imagen.')).toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(apiDeleteMock).toHaveBeenCalledWith(RUTA_IMAGEN)
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
    expect(screen.getByText('Todavía no hay una imagen cargada.')).toBeInTheDocument()
    expect(alCambiar).toHaveBeenCalledTimes(1)
  })

  it('cancelar la puerta no toca la API ni a la grilla', async () => {
    const usuario = userEvent.setup()
    montar({ tieneImagen: true })
    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(alCambiar).not.toHaveBeenCalled()
    expect(screen.getByRole('img', { name: 'Imagen del artículo' })).toBeInTheDocument()
  })

  it('con la puerta abierta el interruptor y el selector de archivo quedan inertes', async () => {
    const usuario = userEvent.setup()
    montar({ tieneImagen: true })

    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    expect(screen.getByRole('switch', { name: SWITCH })).toBeDisabled()
    expect(screen.getByLabelText('Reemplazar la imagen')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar imagen' })).toBeDisabled()
  })

  it('dos confirmaciones del mismo tick salen como un solo DELETE', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockReturnValue(new Promise(() => {}))
    montar({ tieneImagen: true })
    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))
    const confirmar = screen.getByRole('button', { name: 'Confirmar quitar imagen' })

    act(() => {
      confirmar.click()
      confirmar.click()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  it('un rechazo deja la puerta abierta con el motivo al lado', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso.'))
    montar({ tieneImagen: true })
    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar imagen' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No tenés permiso.')
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar quitar imagen' })).toBeEnabled()
    expect(alCambiar).not.toHaveBeenCalled()
  })

  it('cancelar la puerta borra el rechazo que quedó junto a ella', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso.'))
    montar({ tieneImagen: true })
    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar imagen' }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('SeccionDeTiendaOnline — la puerta de "Quitar imagen" deja inerte todo el modal', () => {
  /** El padre cuenta las escrituras en vuelo: lo que importa es el saldo de `true` menos `false`. */
  function saldo() {
    return alDeEscribir.mock.calls.reduce((suma, [enCurso]) => suma + (enCurso ? 1 : -1), 0)
  }

  it('abrir la puerta avisa al padre y cancelar lo suelta', async () => {
    const usuario = userEvent.setup()
    montar({ tieneImagen: true })

    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    expect(saldo()).toBe(1)

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(saldo()).toBe(0)
  })

  it('confirmar deja el saldo en cero: la puerta y la escritura se sueltan por separado', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockResolvedValue(undefined)
    montar({ tieneImagen: true })
    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar imagen' }))

    expect(await screen.findByText('Se quitó la imagen.')).toBeInTheDocument()
    await waitFor(() => expect(saldo()).toBe(0))
  })

  it('un rechazo deja la puerta abierta y el saldo en 1', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso.'))
    montar({ tieneImagen: true })
    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar imagen' }))

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    await waitFor(() => expect(saldo()).toBe(1))
  })

  it('desmontar la sección con la puerta abierta deja el saldo en cero', async () => {
    const usuario = userEvent.setup()
    const { unmount } = montar({ tieneImagen: true })
    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))
    expect(saldo()).toBe(1)

    unmount()

    expect(saldo()).toBe(0)
  })
})

/**
 * La sección vive dentro del `Modal` del artículo (en un portal): ahí la puerta de "Quitar imagen" comparte el Escape
 * con el editor y no tiene una `Box` a la que devolver el foco.
 */
describe('SeccionDeTiendaOnline — dentro del modal del artículo', () => {
  function montarEnModal() {
    const alCerrarElModal = vi.fn()
    render(
      <AuthContext.Provider value={sesion(ROL.Admin)}>
        <Modal titulo="Editar artículo" onCerrar={alCerrarElModal}>
          <SeccionDeTiendaOnline
            idArticulo={7}
            inicial={{ publicadoWeb: false, tieneImagen: true }}
            bloqueadoPorPadre={false}
            alDeEscribir={alDeEscribir}
            alCambiar={alCambiar}
          />
        </Modal>
      </AuthContext.Provider>,
    )

    return { alCerrarElModal }
  }

  /**
   * Cláusulas bajo prueba: la captura + corte de propagación de `ConfirmacionDeBaja` y el `defaultPrevented` de
   * `Modal` (cada una tiene además su prueba propia en su componente). Con la puerta abierta Escape solo la cierra;
   * ya cerrada, el siguiente Escape sí cierra el editor.
   */
  it('Escape cierra la puerta de "Quitar imagen" y no el editor; sin la puerta, el editor se cierra', async () => {
    const usuario = userEvent.setup()
    const confirmar = vi.spyOn(window, 'confirm')
    const { alCerrarElModal } = montarEnModal()
    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()

    await usuario.keyboard('{Escape}')

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(alCerrarElModal).not.toHaveBeenCalled()
    expect(confirmar).not.toHaveBeenCalled()
    expect(apiDeleteMock).not.toHaveBeenCalled()

    await usuario.keyboard('{Escape}')

    expect(alCerrarElModal).toHaveBeenCalledTimes(1)
    confirmar.mockRestore()
  })

  /**
   * jsdom no aplica la *focus fixup rule*, pero acá el disparador se DESMONTA (ya no hay imagen que quitar), que sí es
   * observable: sin el destino explícito, la puerta no encuentra una `Box` en el portal y el foco cae en `<body>`.
   */
  it('al confirmar, el botón "Quitar imagen" desaparece y el foco va al título de la sección', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockResolvedValue(undefined)
    montarEnModal()
    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar imagen' }))

    expect(await screen.findByText('Se quitó la imagen.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Quitar imagen' })).not.toBeInTheDocument()
    expect(screen.getByText('Tienda online')).toHaveFocus()
  })

  it('al cancelar, el foco vuelve al botón "Quitar imagen"', async () => {
    const usuario = userEvent.setup()
    montarEnModal()
    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.getByRole('button', { name: 'Quitar imagen' })).toHaveFocus()
  })
})

describe('SeccionDeTiendaOnline — cada acción borra lo que dijo la anterior', () => {
  it('subir una imagen borra el rechazo del interruptor', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso para publicar.'))
    apiPutBinarioMock.mockReturnValue(new Promise(() => {}))
    montar()
    await usuario.click(screen.getByRole('switch', { name: SWITCH }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen())

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('publicar borra el aviso de éxito de la subida anterior', async () => {
    const usuario = userEvent.setup()
    apiPutBinarioMock.mockResolvedValue({ idArticulo: 7, tieneImagen: true, imagenVersion: 1 })
    apiPutMock.mockReturnValue(new Promise(() => {}))
    montar()
    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen())
    expect(await screen.findByText('Se subió la imagen.')).toBeInTheDocument()

    await usuario.click(screen.getByRole('switch', { name: SWITCH }))

    expect(screen.queryByText('Se subió la imagen.')).not.toBeInTheDocument()
  })

  it('elegir un archivo inválido borra el rechazo del interruptor', async () => {
    const usuario = userEvent.setup({ applyAccept: false })
    apiPutMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso para publicar.'))
    montar()
    await usuario.click(screen.getByRole('switch', { name: SWITCH }))
    expect(await screen.findByText('No tenés permiso para publicar.')).toBeInTheDocument()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen('image/gif', 10, 'foto.gif'))

    expect(screen.queryByText('No tenés permiso para publicar.')).not.toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('La imagen tiene que ser PNG, JPG o WebP.')
  })

  it('abrir la puerta borra el aviso de éxito de una acción anterior', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValue({ idArticulo: 7, publicado: true })
    montar({ tieneImagen: true })
    await usuario.click(screen.getByRole('switch', { name: SWITCH }))
    expect(await screen.findByText('El artículo quedó publicado en la tienda.')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    expect(screen.queryByText('El artículo quedó publicado en la tienda.')).not.toBeInTheDocument()
  })
})
