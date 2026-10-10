import { useState } from 'react'
import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { CampoFecha } from './CampoFecha'

function Controlado({
  inicial = '',
  alCambiar,
  ...resto
}: {
  inicial?: string
  alCambiar?: (valor: string) => void
  min?: string
  max?: string
  disabled?: boolean
}) {
  const [valor, setValor] = useState(inicial)
  return (
    <>
      <label htmlFor="f">Fecha</label>
      <CampoFecha
        id="f"
        className="form-control"
        value={valor}
        onChange={(v) => {
          setValor(v)
          alCambiar?.(v)
        }}
        {...resto}
      />
      <output data-testid="valor">{valor}</output>
    </>
  )
}

const campo = () => screen.getByLabelText('Fecha')
const valor = () => screen.getByTestId('valor').textContent

describe('CampoFecha', () => {
  it('muestra el valor ISO como DD/MM/AAAA y deja el placeholder', () => {
    render(<Controlado inicial="2026-08-05" />)
    expect(campo()).toHaveValue('05/08/2026')
    expect(campo()).toHaveAttribute('placeholder', 'dd/mm/aaaa')
  })

  it('un valor vacío se muestra vacío', () => {
    render(<Controlado />)
    expect(campo()).toHaveValue('')
  })

  it('inserta las barras mientras se tipean dígitos y emite el ISO al completar la fecha', async () => {
    const alCambiar = vi.fn()
    render(<Controlado alCambiar={alCambiar} />)

    await userEvent.type(campo(), '05082026')

    expect(campo()).toHaveValue('05/08/2026')
    expect(valor()).toBe('2026-08-05')
    expect(alCambiar).toHaveBeenCalledTimes(1)
    expect(alCambiar).toHaveBeenCalledWith('2026-08-05')
  })

  it('no emite nada mientras la fecha está incompleta', async () => {
    const alCambiar = vi.fn()
    render(<Controlado alCambiar={alCambiar} />)

    await userEvent.type(campo(), '0508')

    expect(campo()).toHaveValue('05/08')
    expect(alCambiar).not.toHaveBeenCalled()
  })

  it('al salir completa día y mes de un dígito y el año de dos', async () => {
    render(<Controlado />)

    await userEvent.type(campo(), '5/8/26')
    await userEvent.tab()

    expect(campo()).toHaveValue('05/08/2026')
    expect(valor()).toBe('2026-08-05')
  })

  it('una fecha inexistente marca is-invalid al salir y no emite un valor', async () => {
    const alCambiar = vi.fn()
    render(<Controlado alCambiar={alCambiar} />)

    await userEvent.type(campo(), '31/02/2026')

    expect(campo()).toHaveClass('is-invalid')
    expect(campo()).toHaveAttribute('aria-invalid', 'true')
    expect(valor()).toBe('')
    expect(alCambiar).not.toHaveBeenCalled()
  })

  it('un texto incompleto no se marca inválido hasta salir del campo', async () => {
    render(<Controlado />)

    await userEvent.type(campo(), '05/08')
    expect(campo()).not.toHaveClass('is-invalid')

    await userEvent.tab()
    expect(campo()).toHaveClass('is-invalid')
  })

  it('vaciar el texto emite vacío', async () => {
    render(<Controlado inicial="2026-08-05" />)

    await userEvent.clear(campo())

    expect(valor()).toBe('')
    expect(campo()).not.toHaveClass('is-invalid')
  })

  it('editar una fecha válida a una incompleta emite vacío, como un input date nativo', async () => {
    render(<Controlado inicial="2026-08-05" />)

    await userEvent.type(campo(), '{Backspace}')

    expect(campo()).toHaveValue('05/08/202')
    expect(valor()).toBe('')
  })

  it('un cambio externo del valor reemplaza el texto', () => {
    const { rerender } = render(<CampoFecha value="2026-08-05" onChange={() => {}} aria-label="f" />)
    rerender(<CampoFecha value="2027-01-02" onChange={() => {}} aria-label="f" />)
    expect(screen.getByLabelText('f')).toHaveValue('02/01/2027')
  })

  it('respeta min y max: una fecha fuera de rango es inválida y no se emite', async () => {
    render(<Controlado min="2026-08-01" max="2026-08-31" />)

    await userEvent.type(campo(), '01/09/2026')
    expect(campo()).toHaveClass('is-invalid')
    expect(valor()).toBe('')

    await userEvent.clear(campo())
    await userEvent.type(campo(), '31/07/2026')
    expect(campo()).toHaveClass('is-invalid')
    expect(valor()).toBe('')

    await userEvent.clear(campo())
    await userEvent.type(campo(), '15/08/2026')
    expect(campo()).not.toHaveClass('is-invalid')
    expect(valor()).toBe('2026-08-15')
  })

  it('reenvía id, atributos y deshabilita también el botón del calendario', () => {
    render(<Controlado disabled max="2026-12-31" />)
    expect(campo()).toBeDisabled()
    expect(campo()).toHaveAttribute('max', '2026-12-31')
    expect(screen.getByRole('button', { name: 'Abrir calendario' })).toBeDisabled()
  })

  it('el botón del calendario abre el selector nativo con showPicker', async () => {
    const showPicker = vi.fn()
    const original = HTMLInputElement.prototype.showPicker
    HTMLInputElement.prototype.showPicker = showPicker
    try {
      render(<Controlado />)
      await userEvent.click(screen.getByRole('button', { name: 'Abrir calendario' }))
      expect(showPicker).toHaveBeenCalledTimes(1)
    } finally {
      HTMLInputElement.prototype.showPicker = original
    }
  })

  it('elegir una fecha en el selector nativo actualiza el texto y emite el ISO', () => {
    const { container } = render(<Controlado />)
    const selector = container.querySelector('input[type="date"]') as HTMLInputElement

    fireEvent.change(selector, { target: { value: '2026-12-24' } })

    expect(campo()).toHaveValue('24/12/2026')
    expect(valor()).toBe('2026-12-24')
  })
})
