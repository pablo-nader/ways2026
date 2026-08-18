import { useCallback, useEffect, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeCatalogosFiscales } from '../api/catalogos'
import { claseDeBadgeDeEstadoPago, clienteDeCompras, etiquetaDeEstadoPago } from '../api/compras'
import { clienteDeProveedores } from '../api/proveedores'
import type { AltaProveedor, CondicionFiscalListado, PaginaDe, ProveedorListado, SaldoDeProveedor } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ResumenSaldoDeProveedor } from '../componentes/ResumenSaldoDeProveedor'

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

// ---- Panel de saldo de proveedor (stage-8-compras-transferencias-inventario, Slice 6, design:
// Web Composition; spec: proveedores / Proveedor Saldo Read Entry Point) ------------------------
// Montado con `key={idProveedor}` desde el padre (react-async-state regla 8): cambiar de
// proveedor remonta el panel entero, sin arrastrar el saldo del anterior mientras carga el nuevo.

type PropsPanelSaldo = { proveedor: ProveedorListado; onCerrar: () => void }

function PanelSaldoDeProveedor({ proveedor, onCerrar }: PropsPanelSaldo) {
  const [saldo, setSaldo] = useState<SaldoDeProveedor | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')

  useEffect(() => {
    let vigente = true
    setCargando(true)
    setError('')

    clienteDeCompras
      .obtenerSaldoDeProveedor(proveedor.id)
      .then((datos) => {
        if (vigente) setSaldo(datos)
      })
      .catch((e) => {
        if (!vigente) return
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el saldo del proveedor.')
      })
      .finally(() => {
        if (vigente) setCargando(false)
      })

    return () => {
      vigente = false
    }
  }, [proveedor.id])

  return (
    <div className="border p-3 mb-4 bg-white">
      <div className="d-flex justify-content-between align-items-start mb-2">
        <strong>Saldo de {proveedor.razonSocial}</strong>
        <button type="button" className="btn btn-sm btn-outline-secondary rounded-0" onClick={onCerrar}>
          Cerrar
        </button>
      </div>

      {cargando && <Cargando />}
      {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}

      {saldo && (
        <>
          <div className="mb-3">
            {/* stage-15-cc-proveedores-ledger (Slice 6, judgment-day hallazgo CRITICAL): este panel
                ya tiene el `ProveedorListado` completo — se lo pasamos a `ResumenSaldoDeProveedor`
                para que el link real cargue con `location.state.proveedor` (mismo patrón que
                `Clientes.tsx`). */}
            <ResumenSaldoDeProveedor saldo={saldo.saldo} idProveedor={proveedor.id} proveedor={proveedor} />
          </div>

          <div className="table-responsive">
            <table className="table table-sm table-bordered align-middle mb-0">
              <thead>
                <tr>
                  <th>Comprobante</th>
                  <th className="text-end">Total</th>
                  <th className="text-end">Pagado (ligado)</th>
                  <th>Estado de pago</th>
                </tr>
              </thead>
              <tbody>
                {saldo.compras.map((c) => (
                  <tr key={c.idComprobanteCompra}>
                    <td>{c.numeroExterno ?? `#${c.idComprobanteCompra}`}</td>
                    <td className="text-end">{formatearMoneda(c.total)}</td>
                    <td className="text-end">{formatearMoneda(c.pagado)}</td>
                    <td>
                      <span className={`badge rounded-0 ${claseDeBadgeDeEstadoPago(c.estadoPago)}`}>
                        {etiquetaDeEstadoPago(c.estadoPago)}
                      </span>
                    </td>
                  </tr>
                ))}
                {saldo.compras.length === 0 && (
                  <tr>
                    <td colSpan={4} className="text-center text-muted py-3">
                      Este proveedor no tiene compras confirmadas.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  )
}

type Formulario = {
  id: number | null
  razonSocial: string
  nombreFantasia: string
  cuit: string
  idCondicionFiscal: number | ''
  domicilio: string
  telefono: string
  email: string
  vendedor: string
  celularVendedor: string
  supervisor: string
  celularSupervisor: string
  margen: string
  observaciones: string
  activo: boolean
}

function formularioVacio(): Formulario {
  return {
    id: null,
    razonSocial: '',
    nombreFantasia: '',
    cuit: '',
    idCondicionFiscal: '',
    domicilio: '',
    telefono: '',
    email: '',
    vendedor: '',
    celularVendedor: '',
    supervisor: '',
    celularSupervisor: '',
    margen: '',
    observaciones: '',
    activo: true,
  }
}

function aFormulario(p: ProveedorListado): Formulario {
  return {
    id: p.id,
    razonSocial: p.razonSocial,
    nombreFantasia: p.nombreFantasia ?? '',
    cuit: p.cuit ?? '',
    idCondicionFiscal: p.idCondicionFiscal,
    domicilio: p.domicilio ?? '',
    telefono: p.telefono ?? '',
    email: p.email ?? '',
    vendedor: p.vendedor ?? '',
    celularVendedor: p.celularVendedor ?? '',
    supervisor: p.supervisor ?? '',
    celularSupervisor: p.celularSupervisor ?? '',
    margen: p.margen === null ? '' : String(p.margen),
    observaciones: p.observaciones ?? '',
    activo: p.activo,
  }
}

function aVacioNulo(valor: string): string | null {
  const limpio = valor.trim()
  return limpio === '' ? null : limpio
}

function aAlta(f: Formulario): AltaProveedor {
  return {
    razonSocial: f.razonSocial.trim(),
    nombreFantasia: aVacioNulo(f.nombreFantasia),
    cuit: aVacioNulo(f.cuit),
    idCondicionFiscal: f.idCondicionFiscal === '' ? 0 : f.idCondicionFiscal,
    domicilio: aVacioNulo(f.domicilio),
    telefono: aVacioNulo(f.telefono),
    email: aVacioNulo(f.email),
    vendedor: aVacioNulo(f.vendedor),
    celularVendedor: aVacioNulo(f.celularVendedor),
    supervisor: aVacioNulo(f.supervisor),
    celularSupervisor: aVacioNulo(f.celularSupervisor),
    margen: f.margen.trim() === '' ? null : Number(f.margen),
    observaciones: aVacioNulo(f.observaciones),
    idEmpresa: null,
    activo: f.activo,
  }
}

/**
 * ABM dedicado de proveedores (design decision 1: no la máquina genérica de catálogos) —
 * mismo shape que `Clientes.tsx`, sin fila protegida (proveedores no tiene equivalente al
 * Consumidor Final).
 */
export function Proveedores() {
  const [pagina, setPagina] = useState<PaginaDe<ProveedorListado> | null>(null)
  const [condiciones, setCondiciones] = useState<CondicionFiscalListado[]>([])
  const [busqueda, setBusqueda] = useState('')
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)
  const [proveedorSaldo, setProveedorSaldo] = useState<ProveedorListado | null>(null)

  const cargar = useCallback(async (termino: string) => {
    setCargando(true)
    setError('')
    try {
      setPagina(await clienteDeProveedores.listar(termino, false))
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los proveedores.')
    } finally {
      setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar('')
    clienteDeCatalogosFiscales.condicionesFiscales().then(setCondiciones).catch(() => setCondiciones([]))
  }, [cargar])

  async function guardar() {
    if (!formulario) return

    setGuardando(true)
    setError('')
    setAviso('')

    try {
      const datos = aAlta(formulario)

      if (formulario.id === null) {
        await clienteDeProveedores.crear(datos)
        setAviso(`Proveedor "${formulario.razonSocial}" creado.`)
      } else {
        await clienteDeProveedores.actualizar(formulario.id, datos)
        setAviso(`Proveedor "${formulario.razonSocial}" actualizado.`)
      }

      setFormulario(null)
      await cargar(busqueda)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
    } finally {
      setGuardando(false)
    }
  }

  async function eliminar(p: ProveedorListado) {
    if (!confirm(`¿Dar de baja al proveedor "${p.razonSocial}"?`)) return

    setError('')
    setAviso('')
    try {
      await clienteDeProveedores.eliminar(p.id)
      setAviso(`Proveedor "${p.razonSocial}" dado de baja.`)
      await cargar(busqueda)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo dar de baja.')
    }
  }

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <input
        type="search"
        className="form-control form-control-sm rounded-0"
        placeholder="Buscar por razón social, nombre de fantasía o CUIT…"
        value={busqueda}
        onChange={(e) => setBusqueda(e.target.value)}
        onKeyDown={(e) => e.key === 'Enter' && cargar(busqueda)}
      />
      <button
        type="button"
        className="btn btn-sm btn-outline-light rounded-0"
        onClick={() => cargar(busqueda)}
      >
        Buscar
      </button>
      <button
        type="button"
        className="btn btn-sm btn-success rounded-0 text-nowrap"
        onClick={() => {
          setFormulario(formularioVacio())
          setAviso('')
          setError('')
        }}
      >
        Nuevo
      </button>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Proveedores" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {formulario && (
          <FormularioProveedor
            valor={formulario}
            condiciones={condiciones}
            guardando={guardando}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={() => setFormulario(null)}
          />
        )}

        {proveedorSaldo && (
          <PanelSaldoDeProveedor key={proveedorSaldo.id} proveedor={proveedorSaldo} onCerrar={() => setProveedorSaldo(null)} />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>Razón social</th>
                  <th>CUIT</th>
                  <th>Teléfono</th>
                  <th>Email</th>
                  <th>Margen</th>
                  <th>Estado</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {pagina?.items.map((p) => (
                  <tr key={p.id}>
                    <td>
                      {p.razonSocial}
                      {p.nombreFantasia && <div className="text-muted small">{p.nombreFantasia}</div>}
                    </td>
                    <td>{p.cuit ?? '—'}</td>
                    <td>{p.telefono ?? '—'}</td>
                    <td>{p.email ?? '—'}</td>
                    <td>{p.margen === null ? '—' : `${p.margen}%`}</td>
                    <td>
                      <span className={`badge rounded-0 ${p.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                        {p.activo ? 'Activo' : 'Inactivo'}
                      </span>
                    </td>
                    <td className="text-end text-nowrap">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-secondary rounded-0 me-1"
                        onClick={() => setProveedorSaldo(p)}
                      >
                        Ver saldo
                      </button>
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-primary rounded-0 me-1"
                        onClick={() => {
                          setFormulario(aFormulario(p))
                          setAviso('')
                          setError('')
                        }}
                      >
                        Editar
                      </button>
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        onClick={() => eliminar(p)}
                      >
                        Baja
                      </button>
                    </td>
                  </tr>
                ))}
                {pagina?.items.length === 0 && (
                  <tr>
                    <td colSpan={7} className="text-center text-muted py-4">
                      No hay proveedores que coincidan con la búsqueda.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </Box>
    </div>
  )
}

function FormularioProveedor({
  valor,
  condiciones,
  guardando,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  condiciones: CondicionFiscalListado[]
  guardando: boolean
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNuevo = valor.id === null

  return (
    <form
      className="row g-3 border p-3 mb-4 bg-white"
      autoComplete="off"
      onSubmit={(e) => {
        e.preventDefault()
        onGuardar()
      }}
    >
      <div className="col-12">
        <strong>{esNuevo ? 'Nuevo proveedor' : `Editando proveedor ${valor.id}`}</strong>
      </div>

      <div className="col-12">
        <strong>Datos fiscales</strong>
      </div>

      <div className="col-md-4">
        <label className="form-label" htmlFor="p-razon-social">
          Razón social
        </label>
        <input
          id="p-razon-social"
          className="form-control rounded-0"
          maxLength={150}
          value={valor.razonSocial}
          onChange={(e) => onCambio({ ...valor, razonSocial: e.target.value })}
          required
        />
      </div>

      <div className="col-md-4">
        <label className="form-label" htmlFor="p-nombre-fantasia">
          Nombre de fantasía
        </label>
        <input
          id="p-nombre-fantasia"
          className="form-control rounded-0"
          maxLength={150}
          value={valor.nombreFantasia}
          onChange={(e) => onCambio({ ...valor, nombreFantasia: e.target.value })}
        />
      </div>

      <div className="col-md-2">
        <label className="form-label" htmlFor="p-cuit">
          CUIT
        </label>
        <input
          id="p-cuit"
          className="form-control rounded-0"
          maxLength={13}
          value={valor.cuit}
          onChange={(e) => onCambio({ ...valor, cuit: e.target.value })}
        />
      </div>

      <div className="col-md-2">
        <label className="form-label" htmlFor="p-condicion-fiscal">
          Condición fiscal
        </label>
        <select
          id="p-condicion-fiscal"
          className="form-select rounded-0"
          value={valor.idCondicionFiscal}
          onChange={(e) => onCambio({ ...valor, idCondicionFiscal: Number(e.target.value) })}
          required
        >
          <option value="" disabled>
            Elegir…
          </option>
          {condiciones.map((cf) => (
            <option key={cf.id} value={cf.id}>
              {cf.nombre}
            </option>
          ))}
        </select>
      </div>

      <div className="col-12">
        <strong>Contacto</strong>
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="p-domicilio">
          Domicilio
        </label>
        <input
          id="p-domicilio"
          className="form-control rounded-0"
          maxLength={255}
          value={valor.domicilio}
          onChange={(e) => onCambio({ ...valor, domicilio: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="p-telefono">
          Teléfono
        </label>
        <input
          id="p-telefono"
          className="form-control rounded-0"
          maxLength={50}
          value={valor.telefono}
          onChange={(e) => onCambio({ ...valor, telefono: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="p-email">
          Email
        </label>
        <input
          id="p-email"
          type="email"
          className="form-control rounded-0"
          maxLength={255}
          value={valor.email}
          onChange={(e) => onCambio({ ...valor, email: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="p-margen">
          Margen (%)
        </label>
        <input
          id="p-margen"
          type="number"
          step="0.01"
          min="0"
          className="form-control rounded-0"
          value={valor.margen}
          onChange={(e) => onCambio({ ...valor, margen: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="p-vendedor">
          Vendedor
        </label>
        <input
          id="p-vendedor"
          className="form-control rounded-0"
          maxLength={150}
          value={valor.vendedor}
          onChange={(e) => onCambio({ ...valor, vendedor: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="p-celular-vendedor">
          Celular del vendedor
        </label>
        <input
          id="p-celular-vendedor"
          className="form-control rounded-0"
          maxLength={50}
          value={valor.celularVendedor}
          onChange={(e) => onCambio({ ...valor, celularVendedor: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="p-supervisor">
          Supervisor
        </label>
        <input
          id="p-supervisor"
          className="form-control rounded-0"
          maxLength={150}
          value={valor.supervisor}
          onChange={(e) => onCambio({ ...valor, supervisor: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="p-celular-supervisor">
          Celular del supervisor
        </label>
        <input
          id="p-celular-supervisor"
          className="form-control rounded-0"
          maxLength={50}
          value={valor.celularSupervisor}
          onChange={(e) => onCambio({ ...valor, celularSupervisor: e.target.value })}
        />
      </div>

      <div className="col-12">
        <label className="form-label" htmlFor="p-observaciones">
          Observaciones
        </label>
        <textarea
          id="p-observaciones"
          className="form-control rounded-0"
          rows={2}
          value={valor.observaciones}
          onChange={(e) => onCambio({ ...valor, observaciones: e.target.value })}
        />
      </div>

      <div className="col-md-3 d-flex align-items-end">
        <div className="form-check">
          <input
            id="p-activo"
            type="checkbox"
            className="form-check-input rounded-0"
            checked={valor.activo}
            onChange={(e) => onCambio({ ...valor, activo: e.target.checked })}
          />
          <label className="form-check-label" htmlFor="p-activo">
            Activo
          </label>
        </div>
      </div>

      <div className="col-12 d-flex gap-2">
        <button type="submit" className="btn btn-success rounded-0" disabled={guardando}>
          {guardando ? 'Guardando…' : 'Guardar'}
        </button>
        <button
          type="button"
          className="btn btn-outline-secondary rounded-0"
          onClick={onCancelar}
          disabled={guardando}
        >
          Cancelar
        </button>
      </div>
    </form>
  )
}
