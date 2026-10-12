import { useState } from 'react'
import type { ReactNode } from 'react'
import type { EmpresaListado, MedioPagoListado, OrigenFondosGasto, PuntoVentaListado } from '../api/tipos'
import { BotonIcono } from '../componentes/BotonIcono'
import { CampoFecha } from '../componentes/CampoFecha'
import { CampoImporte } from '../componentes/CampoImporte'
import { ErrorDeFormulario } from '../componentes/ErrorDeFormulario'
import { Modal } from '../componentes/Modal'
import { formatearImporte } from '../formato/importes'
import {
  avisoDeUbicacionDelPago,
  etiquetaDeOrigen,
  formularioDeAjusteVacio,
  formularioDeHorasVacio,
  formularioDePagoVacio,
  importeDeHoras,
  importeFirmadoDeAjuste,
  LONGITUD_MAXIMA_DE_DETALLE,
  LONGITUD_MAXIMA_DE_REFERENCIA,
  MAXIMO_DE_LINEAS,
  saldoTrasMovimiento,
  saldoTrasPago,
  textoDeSaldo,
  totalDelPago,
  type CampoDeCuenta,
  type ContextoDePago,
  type ErrorDeCuenta,
  type FormularioDeAjuste,
  type FormularioDeHoras,
  type FormularioDePago,
  type LineaDeFormulario,
  type SentidoDeAjuste,
} from './cuentaDePersonalFormularios'

function moneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

type PropsCampo = { id: string; etiqueta: string; error: ErrorDeCuenta | null; campo: CampoDeCuenta; children: ReactNode }

/** Un campo con su etiqueta y, debajo, el rechazo que le toca. El hijo recibe `aria-invalid` y
 * `aria-describedby` por su cuenta: acá solo se arma el marco. */
function Campo({ id, etiqueta, error, campo, children }: PropsCampo) {
  return (
    <>
      <label className="form-label" htmlFor={id}>
        {etiqueta}
      </label>
      {children}
      {error?.campo === campo && (
        <div id={`${id}-error`} role="alert" className="invalid-feedback d-block">
          {error.mensaje}
        </div>
      )}
    </>
  )
}

const atributosDeError = (id: string, error: ErrorDeCuenta | null, campo: CampoDeCuenta) =>
  error?.campo === campo ? { 'aria-invalid': true, 'aria-describedby': `${id}-error` } : {}

const claseDeInput = (error: ErrorDeCuenta | null, campo: CampoDeCuenta) =>
  `form-control${error?.campo === campo ? ' is-invalid' : ''}`

type PropsComunes = {
  error: ErrorDeCuenta | null
  /** Hay una escritura en vuelo: todo queda inerte y la modal no se puede cerrar. */
  escribiendo: boolean
  focoDeReserva: React.RefObject<HTMLElement | null>
  /** Cualquier edición del formulario descarta el rechazo que estaba a la vista. */
  onCambio: () => void
  onCerrar: () => void
}

type PropsDeFormulario = Omit<PropsComunes, 'onCambio'> & { idFormulario: string; titulo: string; textoDeEnvio: string; children: ReactNode; onEnviar: () => void }

function ModalDeFormulario({
  idFormulario,
  titulo,
  textoDeEnvio,
  error,
  escribiendo,
  focoDeReserva,
  onCerrar,
  onEnviar,
  children,
}: PropsDeFormulario) {
  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCerrar} disabled={escribiendo}>
        Cancelar
      </button>
      <button type="submit" form={idFormulario} className="btn btn-success" disabled={escribiendo}>
        {escribiendo ? 'Registrando…' : textoDeEnvio}
      </button>
    </>
  )

  return (
    <Modal titulo={titulo} tamano="lg" desplazable ocupado={escribiendo} focoDeReserva={focoDeReserva} pie={pie} onCerrar={onCerrar}>
      <ErrorDeFormulario mensaje={error?.campo === 'general' ? error.mensaje : ''} />
      <form
        id={idFormulario}
        className="row g-3"
        autoComplete="off"
        noValidate
        onSubmit={(e) => {
          e.preventDefault()
          onEnviar()
        }}
      >
        {children}
      </form>
    </Modal>
  )
}

// ---- Carga de horas ----------------------------------------------------------------------

type PropsDeHoras = PropsComunes & {
  hoy: string
  valorHoraInicial: number
  onEnviar: (f: FormularioDeHoras) => void
}

export function ModalDeHoras({ hoy, valorHoraInicial, onEnviar, onCambio, ...resto }: PropsDeHoras) {
  const [f, setF] = useState<FormularioDeHoras>(() => formularioDeHorasVacio(hoy, valorHoraInicial))
  const { error, escribiendo } = resto
  const importe = importeDeHoras(f.horas, f.valorHora)

  function cambiar(parcial: Partial<FormularioDeHoras>) {
    setF((prev) => ({ ...prev, ...parcial }))
    onCambio()
  }

  return (
    <ModalDeFormulario idFormulario="formulario-horas" titulo="Cargar horas" textoDeEnvio="Cargar horas" onEnviar={() => onEnviar(f)} {...resto}>
      <div className="col-md-4">
        <Campo id="horas-fecha" etiqueta="Día trabajado" error={error} campo="fecha">
          <CampoFecha id="horas-fecha" className={claseDeInput(error, 'fecha')} value={f.fecha} disabled={escribiendo} onChange={(fecha) => cambiar({ fecha })} {...atributosDeError('horas-fecha', error, 'fecha')} />
        </Campo>
      </div>
      <div className="col-md-4">
        <Campo id="horas-horas" etiqueta="Horas" error={error} campo="horas">
          <CampoImporte id="horas-horas" autoFocus className={claseDeInput(error, 'horas')} valor={f.horas} disabled={escribiendo} onChange={(horas) => cambiar({ horas })} {...atributosDeError('horas-horas', error, 'horas')} />
        </Campo>
      </div>
      <div className="col-md-4">
        <Campo id="horas-valor" etiqueta="Valor hora" error={error} campo="valorHora">
          <CampoImporte id="horas-valor" className={claseDeInput(error, 'valorHora')} valor={f.valorHora} disabled={escribiendo} onChange={(valorHora) => cambiar({ valorHora })} {...atributosDeError('horas-valor', error, 'valorHora')} />
        </Campo>
        <div className="form-text">El valor queda fijo en esta carga.</div>
      </div>
      <div className="col-12">
        <Campo id="horas-detalle" etiqueta="Detalle (opcional)" error={error} campo="detalle">
          <input id="horas-detalle" className={claseDeInput(error, 'detalle')} maxLength={LONGITUD_MAXIMA_DE_DETALLE} value={f.detalle} disabled={escribiendo} onChange={(e) => cambiar({ detalle: e.target.value })} {...atributosDeError('horas-detalle', error, 'detalle')} />
        </Campo>
      </div>
      <div className="col-12">
        <div className="fs-6" aria-live="polite">
          Importe: {importe === null ? '—' : moneda(importe)}
        </div>
      </div>
    </ModalDeFormulario>
  )
}

// ---- Ajuste ------------------------------------------------------------------------------

type PropsDeAjuste = PropsComunes & {
  hoy: string
  saldo: number
  onEnviar: (f: FormularioDeAjuste) => void
}

const SENTIDOS: { valor: SentidoDeAjuste; etiqueta: string }[] = [
  { valor: 'aFavor', etiqueta: 'A favor del empleado (aumenta lo que se le debe)' },
  { valor: 'enContra', etiqueta: 'En contra del empleado (reduce lo que se le debe)' },
]

export function ModalDeAjuste({ hoy, saldo, onEnviar, onCambio, ...resto }: PropsDeAjuste) {
  const [f, setF] = useState<FormularioDeAjuste>(() => formularioDeAjusteVacio(hoy))
  const { error, escribiendo } = resto
  const firmado = importeFirmadoDeAjuste(f)

  function cambiar(parcial: Partial<FormularioDeAjuste>) {
    setF((prev) => ({ ...prev, ...parcial }))
    onCambio()
  }

  return (
    <ModalDeFormulario idFormulario="formulario-ajuste" titulo="Ajuste manual" textoDeEnvio="Registrar ajuste" onEnviar={() => onEnviar(f)} {...resto}>
      <div className="col-12">
        <p className="small text-muted mb-0">
          Los movimientos no se editan ni se borran: una carga equivocada se corrige acá, con un ajuste por la diferencia.
        </p>
      </div>
      <div className="col-md-4">
        <Campo id="ajuste-fecha" etiqueta="Fecha" error={error} campo="fecha">
          <CampoFecha id="ajuste-fecha" className={claseDeInput(error, 'fecha')} value={f.fecha} disabled={escribiendo} onChange={(fecha) => cambiar({ fecha })} {...atributosDeError('ajuste-fecha', error, 'fecha')} />
        </Campo>
      </div>
      <fieldset className="col-md-8">
        <legend className="form-label fs-6">Sentido del ajuste</legend>
        {SENTIDOS.map((s) => (
          <div className="form-check" key={s.valor}>
            <input id={`ajuste-sentido-${s.valor}`} type="radio" name="ajuste-sentido" className="form-check-input" checked={f.sentido === s.valor} disabled={escribiendo} onChange={() => cambiar({ sentido: s.valor })} />
            <label className="form-check-label" htmlFor={`ajuste-sentido-${s.valor}`}>
              {s.etiqueta}
            </label>
          </div>
        ))}
      </fieldset>
      <div className="col-md-4">
        <Campo id="ajuste-importe" etiqueta="Importe" error={error} campo="importe">
          <CampoImporte id="ajuste-importe" autoFocus className={claseDeInput(error, 'importe')} valor={f.importe} disabled={escribiendo} onChange={(importe) => cambiar({ importe })} {...atributosDeError('ajuste-importe', error, 'importe')} />
        </Campo>
      </div>
      <div className="col-md-8">
        <Campo id="ajuste-detalle" etiqueta="Detalle (obligatorio, al menos 5 caracteres)" error={error} campo="detalle">
          <input id="ajuste-detalle" className={claseDeInput(error, 'detalle')} maxLength={LONGITUD_MAXIMA_DE_DETALLE} value={f.detalle} disabled={escribiendo} onChange={(e) => cambiar({ detalle: e.target.value })} {...atributosDeError('ajuste-detalle', error, 'detalle')} />
        </Campo>
      </div>
      <div className="col-12">
        <div className="small text-muted">Saldo actual: {textoDeSaldo(saldo)}</div>
        {firmado !== null && <div className="fs-6">Saldo después del ajuste: {textoDeSaldo(saldoTrasMovimiento(saldo, firmado))}</div>}
      </div>
    </ModalDeFormulario>
  )
}

// ---- Pago --------------------------------------------------------------------------------

type PropsDePago = PropsComunes & {
  hoy: string
  saldo: number
  esAdmin: boolean
  puntoVenta: PuntoVentaListado | null
  /** Cliente vinculado y vigente del empleado, o `null` si no tiene. */
  nombreCliente: string | null
  /** Lo que el cliente debe hoy (nunca negativo) o `null` si no se pudo leer. */
  deudaDelCliente: number | null
  medios: MedioPagoListado[]
  /** Solo se carga para el Admin, el único que puede pagar de la tesorería. */
  empresas: EmpresaListado[]
  errorDeCatalogos: string
  onEnviar: (f: FormularioDePago, ctx: ContextoDePago) => void
}

function etiquetaDeEmpresa(e: EmpresaListado): string {
  return e.nombreFantasia?.trim() || e.razonSocial
}

export function ModalDePago({
  hoy,
  saldo,
  esAdmin,
  puntoVenta,
  nombreCliente,
  deudaDelCliente,
  medios,
  empresas,
  errorDeCatalogos,
  onEnviar,
  onCambio,
  ...resto
}: PropsDePago) {
  const [f, setF] = useState<FormularioDePago>(() => {
    const vacio = formularioDePagoVacio(hoy)
    const empresaDelPuntoVenta = empresas.find((e) => e.id === puntoVenta?.idEmpresa)

    return { ...vacio, idEmpresa: empresaDelPuntoVenta?.id ?? null }
  })
  const [proximaClave, setProximaClave] = useState(1)
  const { error, escribiendo } = resto

  const ctx: ContextoDePago = {
    deudaDelCliente,
    esAdmin,
    idPuntoVenta: puntoVenta?.id ?? null,
    idEmpresaDelPuntoVenta: puntoVenta?.idEmpresa ?? null,
  }
  const aviso = avisoDeUbicacionDelPago(f, ctx)
  const total = totalDelPago(f)
  const saldoResultante = saldoTrasPago(saldo, f)
  const tieneLineas = f.lineas.length > 0
  const deudaLeida = deudaDelCliente !== null
  const puedeDescontar = nombreCliente !== null && deudaLeida && deudaDelCliente > 0

  function cambiar(parcial: Partial<FormularioDePago>) {
    setF((prev) => ({ ...prev, ...parcial }))
    onCambio()
  }

  function cambiarLinea(clave: number, parcial: Partial<LineaDeFormulario>) {
    setF((prev) => ({ ...prev, lineas: prev.lineas.map((l) => (l.clave === clave ? { ...l, ...parcial } : l)) }))
    onCambio()
  }

  function agregarLinea() {
    setF((prev) => ({ ...prev, lineas: [...prev.lineas, { clave: proximaClave, idMedioPago: null, importe: null, referencia: '' }] }))
    setProximaClave((n) => n + 1)
    onCambio()
  }

  function quitarLinea(clave: number) {
    setF((prev) => ({ ...prev, lineas: prev.lineas.filter((l) => l.clave !== clave) }))
    onCambio()
  }

  return (
    <ModalDeFormulario idFormulario="formulario-pago" titulo="Registrar pago" textoDeEnvio="Registrar pago" onEnviar={() => onEnviar(f, ctx)} {...resto}>
      {errorDeCatalogos && <div className="col-12"><div className="alert alert-warning py-2 px-3 small mb-0">{errorDeCatalogos}</div></div>}
      <div className="col-md-4">
        <Campo id="pago-fecha" etiqueta="Fecha del pago" error={error} campo="fecha">
          <CampoFecha id="pago-fecha" className={claseDeInput(error, 'fecha')} value={f.fecha} disabled={escribiendo} onChange={(fecha) => cambiar({ fecha })} {...atributosDeError('pago-fecha', error, 'fecha')} />
        </Campo>
      </div>

      {nombreCliente !== null && (
        <div className="col-md-8">
          <Campo id="pago-descuento" etiqueta={`Descuento de la cuenta corriente de ${nombreCliente}`} error={error} campo="descuento">
            <CampoImporte id="pago-descuento" className={claseDeInput(error, 'descuento')} valor={f.descuento} disabled={escribiendo || !puedeDescontar} onChange={(descuento) => cambiar({ descuento })} {...atributosDeError('pago-descuento', error, 'descuento')} />
          </Campo>
          <div className="form-text">
            {!deudaLeida && 'No se pudo leer la deuda del cliente: recargá la pantalla para poder descontar.'}
            {deudaLeida && deudaDelCliente > 0 && `Deuda actual: ${moneda(deudaDelCliente)}. El descuento no mueve dinero.`}
            {deudaLeida && deudaDelCliente === 0 && 'El cliente no tiene deuda que descontar.'}
          </div>
        </div>
      )}

      <div className="col-12">
        <div className="d-flex justify-content-between align-items-center mb-2">
          <span className="form-label mb-0">Entrega de dinero</span>
          <button type="button" className="btn btn-sm btn-outline-secondary" onClick={agregarLinea} disabled={escribiendo || medios.length === 0 || f.lineas.length >= MAXIMO_DE_LINEAS}>
            Agregar línea
          </button>
        </div>
        {!tieneLineas && <p className="small text-muted mb-0">Sin líneas: el pago es solo el descuento de cuenta corriente.</p>}
        {f.lineas.map((l, i) => (
          <div className="row g-2 align-items-start mb-2" key={l.clave}>
            <div className="col-md-4">
              <select aria-label={`Medio de pago de la línea ${i + 1}`} className="form-select" value={l.idMedioPago ?? ''} disabled={escribiendo} onChange={(e) => cambiarLinea(l.clave, { idMedioPago: e.target.value === '' ? null : Number(e.target.value) })}>
                <option value="">Elegí el medio de pago…</option>
                {medios.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.nombre}
                  </option>
                ))}
              </select>
            </div>
            <div className="col-md-3">
              <CampoImporte aria-label={`Importe de la línea ${i + 1}`} className="form-control" valor={l.importe} disabled={escribiendo} onChange={(importe) => cambiarLinea(l.clave, { importe })} />
            </div>
            <div className="col-md-4">
              <input aria-label={`Referencia de la línea ${i + 1}`} placeholder="Referencia (opcional)" className="form-control" maxLength={LONGITUD_MAXIMA_DE_REFERENCIA} value={l.referencia} disabled={escribiendo} onChange={(e) => cambiarLinea(l.clave, { referencia: e.target.value })} />
            </div>
            <div className="col-md-1 text-end">
              <BotonIcono icono="eliminar" etiqueta={`Quitar la línea ${i + 1}`} onClick={() => quitarLinea(l.clave)} disabled={escribiendo} />
            </div>
          </div>
        ))}
        {error?.campo === 'lineas' && (
          <div role="alert" className="invalid-feedback d-block">
            {error.mensaje}
          </div>
        )}
      </div>

      {tieneLineas && (
        <>
          <div className="col-md-6">
            <Campo id="pago-origen" etiqueta="Origen del dinero" error={error} campo="origen">
              <select id="pago-origen" className={`form-select${error?.campo === 'origen' ? ' is-invalid' : ''}`} value={f.origen} disabled={escribiendo} onChange={(e) => cambiar({ origen: e.target.value as OrigenFondosGasto | '' })} {...atributosDeError('pago-origen', error, 'origen')}>
                <option value="">Elegí de dónde sale el dinero…</option>
                <option value="CajaTurno">{etiquetaDeOrigen('CajaTurno')}</option>
                {esAdmin && <option value="Tesoreria">{etiquetaDeOrigen('Tesoreria')}</option>}
              </select>
            </Campo>
            {f.origen === 'CajaTurno' && (
              <div className="form-text">
                {puntoVenta ? `Sale del turno abierto en ${puntoVenta.nombre}.` : 'Necesita el punto de venta de la sesión.'}
              </div>
            )}
          </div>
          {f.origen === 'Tesoreria' && (
            <div className="col-md-6">
              <Campo id="pago-empresa" etiqueta="Empresa que paga" error={error} campo="empresa">
                <select id="pago-empresa" className={`form-select${error?.campo === 'empresa' ? ' is-invalid' : ''}`} value={f.idEmpresa ?? ''} disabled={escribiendo} onChange={(e) => cambiar({ idEmpresa: e.target.value === '' ? null : Number(e.target.value) })} {...atributosDeError('pago-empresa', error, 'empresa')}>
                  <option value="">Elegí la empresa…</option>
                  {empresas.map((e) => (
                    <option key={e.id} value={e.id}>
                      {etiquetaDeEmpresa(e)}
                    </option>
                  ))}
                </select>
              </Campo>
            </div>
          )}
        </>
      )}

      {aviso && (
        <div className="col-12">
          <div className="alert alert-warning py-2 px-3 small mb-0">{aviso}</div>
        </div>
      )}

      <div className="col-12">
        <Campo id="pago-detalle" etiqueta="Detalle (opcional)" error={error} campo="detalle">
          <input id="pago-detalle" className={claseDeInput(error, 'detalle')} maxLength={LONGITUD_MAXIMA_DE_DETALLE} value={f.detalle} disabled={escribiendo} onChange={(e) => cambiar({ detalle: e.target.value })} {...atributosDeError('pago-detalle', error, 'detalle')} />
        </Campo>
      </div>

      <div className="col-12" aria-live="polite">
        <div className="small text-muted">Saldo actual: {textoDeSaldo(saldo)}</div>
        <div className="fs-6">Total del pago: {moneda(total)}</div>
        <div className="fs-6">Saldo después del pago: {textoDeSaldo(saldoResultante)}</div>
      </div>
    </ModalDeFormulario>
  )
}
