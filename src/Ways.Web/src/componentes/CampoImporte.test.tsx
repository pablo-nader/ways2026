import { createRef } from 'react'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { CampoImporte } from './CampoImporte'

function CampoControlado({
  valorInicial = null,
  disabled = false,
  onChange,
}: {
  valorInicial?: number | null
  disabled?: boolean
  onChange?: (valor: number | null) => void
}) {
  // Envoltorio mínimo que reproduce el uso real: el padre guarda el valor y se lo devuelve
  // al campo (controlado), en vez de simular estado dentro del propio test.
  const ref = createRef<HTMLInputElement>()
  return <CampoImporte ref={ref} valor={valorInicial} onChange={onChange ?? (() => {})} disabled={disabled} aria-label="importe" />
}

describe('CampoImporte', () => {
  it('muestra el valor inicial ya formateado', () => {
    render(<CampoControlado valorInicial={1234.5} />)
    expect(screen.getByLabelText('importe')).toHaveValue('1.234,50')
  })

  it('un valor null se muestra vacío', () => {
    render(<CampoControlado valorInicial={null} />)
    expect(screen.getByLabelText('importe')).toHaveValue('')
  })

  it('al tipear, deja pasar dígitos y una coma, y descarta letras', async () => {
    const usuario = userEvent.setup()
    render(<CampoControlado />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1234,5a')
    expect(campo).toHaveValue('1234,5')
  })

  it('al perder el foco, reformatea a "1.234,56"', async () => {
    const usuario = userEvent.setup()
    render(<CampoControlado />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1234,5')
    await usuario.tab()
    expect(campo).toHaveValue('1.234,50')
  })

  it('tolera "." tipeado como miles: se descarta mientras edita y no rompe el número final', async () => {
    const usuario = userEvent.setup()
    render(<CampoControlado />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1.234,56')
    expect(campo).toHaveValue('1234,56')
    await usuario.tab()
    expect(campo).toHaveValue('1.234,56')
  })

  it('onChange recibe el número parseado en cada tecleo', async () => {
    const onChange = vi.fn()
    const usuario = userEvent.setup()
    render(<CampoControlado onChange={onChange} />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '5')
    expect(onChange).toHaveBeenLastCalledWith(5)
    await usuario.type(campo, '0')
    expect(onChange).toHaveBeenLastCalledWith(50)
  })

  it('onChange recibe null mientras el texto no es un importe completo (ej. coma sin decimales)', async () => {
    const onChange = vi.fn()
    const usuario = userEvent.setup()
    render(<CampoControlado onChange={onChange} />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1234,')
    expect(onChange).toHaveBeenLastCalledWith(null)
  })

  it('onChange recibe null cuando el campo queda vacío', async () => {
    const onChange = vi.fn()
    const usuario = userEvent.setup()
    render(<CampoControlado valorInicial={10} onChange={onChange} />)
    const campo = screen.getByLabelText('importe')
    await usuario.clear(campo)
    expect(onChange).toHaveBeenLastCalledWith(null)
  })

  it('en el blur, onChange recibe el número final ya redondeado', async () => {
    const onChange = vi.fn()
    const usuario = userEvent.setup()
    render(<CampoControlado onChange={onChange} />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1234,567')
    await usuario.tab()
    expect(onChange).toHaveBeenLastCalledWith(1234.567)
    expect(campo).toHaveValue('1.234,57')
  })

  it('un texto inválido al perder el foco limpia el campo (—> vacío) y onChange recibe null', async () => {
    const onChange = vi.fn()
    const usuario = userEvent.setup()
    render(<CampoControlado onChange={onChange} />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '-')
    await usuario.tab()
    expect(campo).toHaveValue('')
    expect(onChange).toHaveBeenLastCalledWith(null)
  })

  it('es de solo alineación derecha e inputMode decimal', () => {
    render(<CampoControlado />)
    const campo = screen.getByLabelText('importe')
    expect(campo).toHaveAttribute('inputMode', 'decimal')
    expect(campo.className).toContain('text-end')
  })

  it('pasa disabled', () => {
    render(<CampoControlado disabled />)
    expect(screen.getByLabelText('importe')).toBeDisabled()
  })

  it('ref.focus() funciona (flujo de teclado)', () => {
    const ref = createRef<HTMLInputElement>()
    render(<CampoImporte ref={ref} valor={null} onChange={() => {}} aria-label="importe-ref" />)
    ref.current?.focus()
    expect(ref.current).toHaveFocus()
  })

  it('no reformatea mientras está enfocado, aunque el padre re-renderice con el mismo valor', async () => {
    const usuario = userEvent.setup()
    const { rerender } = render(<CampoImporte valor={null} onChange={() => {}} aria-label="importe" />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1234')
    // Simula al padre re-renderizando con la prop `valor` sin cambios reales
    rerender(<CampoImporte valor={null} onChange={() => {}} aria-label="importe" />)
    expect(campo).toHaveValue('1234')
  })
})
