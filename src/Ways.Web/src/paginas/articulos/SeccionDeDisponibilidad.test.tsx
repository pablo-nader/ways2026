import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { SeccionDeDisponibilidad } from './SeccionDeDisponibilidad'
import { ErrorApi } from '../../api/cliente'
import { ROL, type ArticuloListado, type UsuarioAutenticado } from '../../api/tipos'
import { AuthContext } from '../../auth/AuthContext'
import { Modal } from '../../componentes/Modal'
import { Toaster } from '../../toasts/Toaster'

// Sección "Venta y tienda online" del formulario de artículo: los dos interruptores y la imagen escriben por su cuenta.

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
  venta?: boolean
  online?: boolean
  tieneImagen?: boolean
  bloqueadoPorPadre?: boolean
}

function montar({
  rolId = ROL.Admin,
  venta = true,
  online = true,
  tieneImagen = false,
  bloqueadoPorPadre = false,
}: Opciones = {}) {
  return render(
    <AuthContext.Provider value={sesion(rolId)}>
      <SeccionDeDisponibilidad
        idArticulo={7}
        inicial={{ disponibleVenta: venta, disponibleOnline: online, tieneImagen }}
        bloqueadoPorPadre={bloqueadoPorPadre}
        alDeEscribir={alDeEscribir}
        alCambiar={alCambiar}
      />
      <Toaster />
    </AuthContext.Provider>,
  )
}

/** El resultado de una escritura se avisa con un toast de éxito, nunca con un banner. */
async function toastDeExito(texto: string) {
  const aviso = await screen.findByText(texto)
  expect(aviso.closest('.ws-toaster')).not.toBeNull()
  expect(aviso.closest('[role="status"]')).not.toBeNull()
  expect(document.body.querySelector('.alert-success')).toBeNull()
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

const VENTA = 'Disponible a la venta'
const ONLINE = 'Disponible online'
const RUTA_DISPONIBILIDAD = '/articulos/7/disponibilidad'
const RUTA_IMAGEN = '/articulos/7/imagen'
const UN_MB = 1024 * 1024

function articuloLeido(sobrescribir: Partial<ArticuloListado> = {}) {
  return { id: 7, disponibleVenta: true, disponibleOnline: true, tieneImagen: false, ...sobrescribir }
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPutMock.mockReset()
  apiPutBinarioMock.mockReset()
  apiDeleteMock.mockReset()
  alDeEscribir.mockReset()
  alCambiar.mockReset()
})

describe('SeccionDeDisponibilidad — quién la ve', () => {
  it('un admin ve la sección', () => {
    montar({ rolId: ROL.Admin })

    expect(screen.getByRole('switch', { name: ONLINE })).toBeInTheDocument()
    expect(screen.getByLabelText('Subir una imagen')).toBeInTheDocument()
  })

  it.each([
    ['vendedor', ROL.Vendedor],
    ['supervisor', ROL.Supervisor],
    ['root', ROL.Root],
    ['sin sesión', null],
  ])('%s no la ve', (_nombre, rolId) => {
    montar({ rolId })

    expect(screen.queryByText('Venta y tienda online')).not.toBeInTheDocument()
    expect(screen.queryByRole('switch')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Subir una imagen')).not.toBeInTheDocument()
  })

  it('sin un AuthProvider por encima tampoco la ve', () => {
    render(
      <SeccionDeDisponibilidad
        idArticulo={7}
        inicial={{ disponibleVenta: true, disponibleOnline: true, tieneImagen: false }}
        bloqueadoPorPadre={false}
        alDeEscribir={alDeEscribir}
        alCambiar={alCambiar}
      />,
    )

    expect(screen.queryByRole('switch')).not.toBeInTheDocument()
  })
})

describe('SeccionDeDisponibilidad — interruptores', () => {
  it('arrancan con lo que trajo el artículo', () => {
    montar({ venta: false, online: true })

    expect(screen.getByRole('switch', { name: VENTA })).not.toBeChecked()
    expect(screen.getByRole('switch', { name: ONLINE })).toBeChecked()
  })

  it('cada interruptor explica qué hace', () => {
    montar()

    expect(screen.getByRole('switch', { name: VENTA })).toHaveAccessibleDescription(
      /Desmarcalo para un artículo de uso interno: se compra y se stockea, pero no se vende\./,
    )
    expect(screen.getByRole('switch', { name: ONLINE })).toHaveAccessibleDescription(
      /Desmarcalo para que no aparezca en la tienda online\./,
    )
  })

  /**
   * Cláusula bajo prueba: el PUT lleva SIEMPRE los dos valores, el que se cambió y el otro tal como está en pantalla
   * (el servidor exige ambos). Cada caso cambia un interruptor y deja el otro en un valor distinto del que tendría por
   * defecto, para que un body con un valor fijo o intercambiado no coincida.
   */
  it('el interruptor de la venta escribe con su propio PUT, mandando la venta nueva y lo online actual', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValue({ idArticulo: 7, disponibleVenta: false, disponibleOnline: false })
    montar({ venta: true, online: false })

    await usuario.click(screen.getByRole('switch', { name: VENTA }))

    await toastDeExito('El artículo quedó fuera de la venta.')
    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(apiPutMock).toHaveBeenCalledWith(RUTA_DISPONIBILIDAD, { disponibleVenta: false, disponibleOnline: false })
    expect(screen.getByRole('switch', { name: VENTA })).not.toBeChecked()
    expect(screen.getByRole('switch', { name: VENTA })).toBeEnabled()
    expect(alCambiar).toHaveBeenCalledTimes(1)
    expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
  })

  it('el interruptor online escribe con su propio PUT, mandando lo online nuevo y la venta actual', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValue({ idArticulo: 7, disponibleVenta: false, disponibleOnline: true })
    montar({ venta: false, online: false })

    await usuario.click(screen.getByRole('switch', { name: ONLINE }))

    await toastDeExito('El artículo quedó disponible en la tienda online.')
    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(apiPutMock).toHaveBeenCalledWith(RUTA_DISPONIBILIDAD, { disponibleVenta: false, disponibleOnline: true })
    expect(screen.getByRole('switch', { name: ONLINE })).toBeChecked()
    expect(screen.getByRole('switch', { name: ONLINE })).toBeEnabled()
    expect(alCambiar).toHaveBeenCalledTimes(1)
  })

  it('sacar el artículo de la tienda manda disponibleOnline: false y lo avisa', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValue({ idArticulo: 7, disponibleVenta: true, disponibleOnline: false })
    montar({ venta: true, online: true })

    await usuario.click(screen.getByRole('switch', { name: ONLINE }))

    await toastDeExito('El artículo quedó fuera de la tienda online.')
    expect(apiPutMock).toHaveBeenCalledWith(RUTA_DISPONIBILIDAD, { disponibleVenta: true, disponibleOnline: false })
    expect(screen.getByRole('switch', { name: ONLINE })).not.toBeChecked()
  })

  it('habilitar la venta manda disponibleVenta: true y lo avisa', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValue({ idArticulo: 7, disponibleVenta: true, disponibleOnline: true })
    montar({ venta: false, online: true })

    await usuario.click(screen.getByRole('switch', { name: VENTA }))

    await toastDeExito('El artículo quedó disponible a la venta.')
    expect(apiPutMock).toHaveBeenCalledWith(RUTA_DISPONIBILIDAD, { disponibleVenta: true, disponibleOnline: true })
  })

  it('los interruptores muestran lo que quedó guardado, no lo que se pidió', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValue({ idArticulo: 7, disponibleVenta: true, disponibleOnline: true })
    montar({ venta: true, online: false })

    await usuario.click(screen.getByRole('switch', { name: ONLINE }))
    await waitFor(() => expect(alCambiar).toHaveBeenCalled())
    expect(screen.getByRole('switch', { name: ONLINE })).toBeChecked()

    apiPutMock.mockResolvedValue({ idArticulo: 7, disponibleVenta: true, disponibleOnline: true })
    await usuario.click(screen.getByRole('switch', { name: VENTA }))
    await waitFor(() => expect(alCambiar).toHaveBeenCalledTimes(2))

    // Se pidió quitar la venta y el servidor contestó que sigue vendiéndose.
    expect(screen.getByRole('switch', { name: VENTA })).toBeChecked()
  })

  it('dos escrituras seguidas arrastran lo que dejó la anterior', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValueOnce({ idArticulo: 7, disponibleVenta: true, disponibleOnline: false })
    apiPutMock.mockResolvedValueOnce({ idArticulo: 7, disponibleVenta: false, disponibleOnline: false })
    montar({ venta: true, online: true })

    await usuario.click(screen.getByRole('switch', { name: ONLINE }))
    await waitFor(() => expect(screen.getByRole('switch', { name: ONLINE })).not.toBeChecked())
    await usuario.click(screen.getByRole('switch', { name: VENTA }))
    await waitFor(() => expect(alCambiar).toHaveBeenCalledTimes(2))

    expect(apiPutMock.mock.calls).toEqual([
      [RUTA_DISPONIBILIDAD, { disponibleVenta: true, disponibleOnline: false }],
      [RUTA_DISPONIBILIDAD, { disponibleVenta: false, disponibleOnline: false }],
    ])
  })

  /**
   * Cláusula bajo prueba: la guarda de reentrancia es un ref. Dos clics en el mismo tick leen el mismo render, así
   * que una guarda sobre estado dejaría pasar los dos.
   */
  it('dos clics del mismo tick salen como una sola request', async () => {
    apiPutMock.mockReturnValue(new Promise(() => {}))
    montar()
    const interruptor = screen.getByRole('switch', { name: ONLINE })

    act(() => {
      fireEvent.click(interruptor)
      fireEvent.click(interruptor)
    })

    expect(apiPutMock).toHaveBeenCalledTimes(1)
  })

  it('un clic en cada interruptor dentro del mismo tick también sale como una sola request', () => {
    apiPutMock.mockReturnValue(new Promise(() => {}))
    montar()

    act(() => {
      fireEvent.click(screen.getByRole('switch', { name: VENTA }))
      fireEvent.click(screen.getByRole('switch', { name: ONLINE }))
    })

    expect(apiPutMock).toHaveBeenCalledTimes(1)
  })

  it('un rechazo conserva el estado, explica el motivo y no avisa a la grilla ni relee', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso para cambiar la disponibilidad.'))
    montar({ venta: true, online: true })

    await usuario.click(screen.getByRole('switch', { name: VENTA }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No tenés permiso para cambiar la disponibilidad.')
    expect(screen.getByRole('switch', { name: VENTA })).toBeChecked()
    expect(screen.getByRole('switch', { name: VENTA })).toBeEnabled()
    expect(screen.getByRole('switch', { name: ONLINE })).toBeChecked()
    expect(alCambiar).not.toHaveBeenCalled()
    expect(apiGetMock).not.toHaveBeenCalled()
    expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
  })

  /**
   * Cláusula bajo prueba: el interruptor que escribió recupera el foco cuando la escritura termina, salga bien o mal
   * (`react-async-state` regla 12). jsdom no implementa el "focus fixup" del navegador, que al deshabilitar el control
   * enfocado lo pasa a `<body>`: el test simula esa pérdida llevando el foco a otro elemento dentro de `act`. Mutación:
   * quitar el `focus()` del efecto deja el foco en ese otro elemento y las cuatro variantes fallan.
   */
  it.each([
    [VENTA, 'bien'],
    [VENTA, 'mal'],
    [ONLINE, 'bien'],
    [ONLINE, 'mal'],
  ])('el interruptor "%s" recupera el foco cuando su escritura sale %s', async (etiqueta, desenlace) => {
    const usuario = userEvent.setup()
    const pendiente = diferida<{ idArticulo: number; disponibleVenta: boolean; disponibleOnline: boolean }>()
    apiPutMock.mockReturnValue(pendiente.promesa)
    montar()
    const interruptor = screen.getByRole('switch', { name: etiqueta })

    await usuario.click(interruptor)
    expect(interruptor).toBeDisabled()
    // `blur()` no hace nada sobre un control deshabilitado en jsdom: el foco "perdido" se simula llevándolo al título.
    act(() => screen.getByText('Venta y tienda online').focus())
    expect(interruptor).not.toHaveFocus()

    await act(async () => {
      if (desenlace === 'bien') pendiente.resolver({ idArticulo: 7, disponibleVenta: true, disponibleOnline: true })
      else pendiente.rechazar(new ErrorApi(403, 'prohibido', 'No tenés permiso para cambiar la disponibilidad.'))
    })

    await waitFor(() => expect(interruptor).toBeEnabled())
    expect(interruptor).toHaveFocus()
  })

  it('subir una imagen no le roba el foco a ningún interruptor', async () => {
    const usuario = userEvent.setup()
    apiPutBinarioMock.mockResolvedValue({ idArticulo: 7, tieneImagen: true, imagenVersion: 1 })
    montar()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen())

    await toastDeExito('Se subió la imagen.')
    expect(screen.getByRole('switch', { name: VENTA })).not.toHaveFocus()
    expect(screen.getByRole('switch', { name: ONLINE })).not.toHaveFocus()
  })

  /**
   * Cláusula bajo prueba: el rechazo de un interruptor queda asociado, con `aria-describedby`, solo al que falló y junto
   * a su texto de ayuda. Mutación: asociar el error a los dos interruptores hace fallar la aserción del otro.
   */
  it.each([
    [VENTA, ONLINE],
    [ONLINE, VENTA],
  ])('el rechazo del interruptor "%s" queda asociado a él y no al otro', async (fallido, otro) => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso para cambiar la disponibilidad.'))
    montar()
    expect(screen.getByRole('switch', { name: fallido })).not.toHaveAccessibleDescription(/No tenés permiso/)

    await usuario.click(screen.getByRole('switch', { name: fallido }))

    const alerta = await screen.findByRole('alert')
    expect(screen.getByRole('switch', { name: fallido })).toHaveAttribute(
      'aria-describedby',
      expect.stringContaining(alerta.id),
    )
    expect(screen.getByRole('switch', { name: fallido })).toHaveAccessibleDescription(
      /Desmarcalo.*No tenés permiso para cambiar la disponibilidad\./,
    )
    expect(screen.getByRole('switch', { name: otro })).not.toHaveAccessibleDescription(/No tenés permiso/)
  })

  it('cambiar el interruptor de nuevo suelta la asociación del rechazo anterior', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValueOnce(new ErrorApi(403, 'prohibido', 'No tenés permiso para cambiar la disponibilidad.'))
    apiPutMock.mockReturnValueOnce(new Promise(() => {}))
    montar()
    await usuario.click(screen.getByRole('switch', { name: VENTA }))
    await screen.findByRole('alert')

    await usuario.click(screen.getByRole('switch', { name: VENTA }))

    expect(screen.getByRole('switch', { name: VENTA })).not.toHaveAttribute('aria-describedby', expect.stringContaining('error'))
  })

  it.each([
    ['disponible_venta_requerido', 'La disponibilidad a la venta es obligatoria.'],
    ['disponible_online_requerido', 'La disponibilidad online es obligatoria.'],
  ])('un 400 %s muestra el mensaje del servidor y no cambia nada', async (codigo, mensaje) => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new ErrorApi(400, codigo, mensaje))
    montar({ venta: true, online: false })

    await usuario.click(screen.getByRole('switch', { name: ONLINE }))

    expect(await screen.findByRole('alert')).toHaveTextContent(mensaje)
    expect(screen.getByRole('switch', { name: ONLINE })).not.toBeChecked()
    expect(apiGetMock).not.toHaveBeenCalled()
  })

  it('un 404 dice que el artículo ya no existe y la grilla se entera, aunque la relectura también dé 404', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el artículo 7.'))
    apiGetMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el artículo 7.'))
    montar()

    await usuario.click(screen.getByRole('switch', { name: ONLINE }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'No se pudo sacar el artículo de la tienda online. El artículo ya no existe.',
    )
    await waitFor(() => expect(alCambiar).toHaveBeenCalledTimes(1))
  })

  it('un fallo incierto relee el artículo, muestra el estado real de los dos interruptores, deja el mensaje a la vista y avisa a la grilla DESPUÉS de releer', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new ErrorApi(500, 'error', 'Error 500.'))
    apiGetMock.mockResolvedValue(articuloLeido({ disponibleVenta: false, disponibleOnline: true }))
    montar({ venta: true, online: false })

    await usuario.click(screen.getByRole('switch', { name: ONLINE }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo confirmar el resultado')
    await waitFor(() => expect(alCambiar).toHaveBeenCalledTimes(1))
    expect(apiGetMock).toHaveBeenCalledWith('/articulos/7')
    expect(screen.getByRole('switch', { name: ONLINE })).toBeChecked()
    expect(screen.getByRole('switch', { name: VENTA })).not.toBeChecked()
    expect(apiGetMock.mock.invocationCallOrder[0]).toBeLessThan(alCambiar.mock.invocationCallOrder[0])
  })

  it('si la relectura también falla, el mensaje del fallo sigue a la vista y la grilla igual se entera', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new Error('sin red'))
    apiGetMock.mockRejectedValue(new Error('sin red'))
    montar({ venta: true, online: false })

    await usuario.click(screen.getByRole('switch', { name: ONLINE }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'No se pudo mostrar el artículo en la tienda online. No se pudo confirmar el resultado',
    )
    await waitFor(() => expect(alCambiar).toHaveBeenCalledTimes(1))
    expect(screen.getByRole('switch', { name: ONLINE })).toBeEnabled()
  })

  it('mientras se escribe, todo lo de la sección queda inerte', async () => {
    const usuario = userEvent.setup()
    const pendiente = diferida<{ idArticulo: number; disponibleVenta: boolean; disponibleOnline: boolean }>()
    apiPutMock.mockReturnValue(pendiente.promesa)
    montar({ tieneImagen: true })

    await usuario.click(screen.getByRole('switch', { name: ONLINE }))

    expect(screen.getByRole('switch', { name: ONLINE })).toBeDisabled()
    expect(screen.getByRole('switch', { name: VENTA })).toBeDisabled()
    expect(screen.getByLabelText('Reemplazar la imagen')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar imagen' })).toBeDisabled()

    await act(async () => pendiente.resolver({ idArticulo: 7, disponibleVenta: true, disponibleOnline: false }))

    await waitFor(() => expect(screen.getByRole('switch', { name: ONLINE })).toBeEnabled())
  })

  it('si el formulario está ocupado con otra cosa, los controles quedan inertes', () => {
    montar({ tieneImagen: true, bloqueadoPorPadre: true })

    expect(screen.getByRole('switch', { name: ONLINE })).toBeDisabled()
    expect(screen.getByRole('switch', { name: VENTA })).toBeDisabled()
    expect(screen.getByLabelText('Reemplazar la imagen')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar imagen' })).toBeDisabled()
  })
})

describe('SeccionDeDisponibilidad — imagen', () => {
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

    await toastDeExito('Se subió la imagen.')
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

describe('SeccionDeDisponibilidad — quitar la imagen', () => {
  it('pide confirmación y recién al confirmar borra; la vista previa desaparece y la grilla se entera', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockResolvedValue(undefined)
    montar({ tieneImagen: true })

    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    expect(screen.getByRole('alertdialog')).toHaveTextContent('¿Quitar la imagen del artículo?')
    expect(apiDeleteMock).not.toHaveBeenCalled()

    await usuario.click(screen.getByRole('button', { name: 'Confirmar quitar imagen' }))

    await toastDeExito('Se quitó la imagen.')
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

    expect(screen.getByRole('switch', { name: ONLINE })).toBeDisabled()
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

describe('SeccionDeDisponibilidad — la puerta de "Quitar imagen" deja inerte todo el modal', () => {
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

    await toastDeExito('Se quitó la imagen.')
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
describe('SeccionDeDisponibilidad — dentro del modal del artículo', () => {
  function montarEnModal() {
    const alCerrarElModal = vi.fn()
    render(
      <AuthContext.Provider value={sesion(ROL.Admin)}>
        <Modal titulo="Editar artículo" onCerrar={alCerrarElModal}>
          <SeccionDeDisponibilidad
            idArticulo={7}
            inicial={{ disponibleVenta: true, disponibleOnline: true, tieneImagen: true }}
            bloqueadoPorPadre={false}
            alDeEscribir={alDeEscribir}
            alCambiar={alCambiar}
          />
        </Modal>
        <Toaster />
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

    await toastDeExito('Se quitó la imagen.')
    expect(screen.queryByRole('button', { name: 'Quitar imagen' })).not.toBeInTheDocument()
    expect(screen.getByText('Venta y tienda online')).toHaveFocus()
  })

  it('al cancelar, el foco vuelve al botón "Quitar imagen"', async () => {
    const usuario = userEvent.setup()
    montarEnModal()
    await usuario.click(screen.getByRole('button', { name: 'Quitar imagen' }))

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.getByRole('button', { name: 'Quitar imagen' })).toHaveFocus()
  })
})

describe('SeccionDeDisponibilidad — cada acción borra lo que dijo la anterior', () => {
  it('subir una imagen borra el rechazo del interruptor', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso para cambiar la disponibilidad.'))
    apiPutBinarioMock.mockReturnValue(new Promise(() => {}))
    montar()
    await usuario.click(screen.getByRole('switch', { name: ONLINE }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen())

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('elegir un archivo inválido borra el rechazo del interruptor', async () => {
    const usuario = userEvent.setup({ applyAccept: false })
    apiPutMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso para cambiar la disponibilidad.'))
    montar()
    await usuario.click(screen.getByRole('switch', { name: ONLINE }))
    expect(await screen.findByText('No tenés permiso para cambiar la disponibilidad.')).toBeInTheDocument()

    await usuario.upload(screen.getByLabelText('Subir una imagen'), imagen('image/gif', 10, 'foto.gif'))

    expect(screen.queryByText('No tenés permiso para cambiar la disponibilidad.')).not.toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('La imagen tiene que ser PNG, JPG o WebP.')
  })

  it('cambiar un interruptor borra el rechazo que dejó el otro', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValueOnce(new ErrorApi(403, 'prohibido', 'No tenés permiso para cambiar la disponibilidad.'))
    apiPutMock.mockReturnValueOnce(new Promise(() => {}))
    montar()
    await usuario.click(screen.getByRole('switch', { name: VENTA }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()

    await usuario.click(screen.getByRole('switch', { name: ONLINE }))

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})
