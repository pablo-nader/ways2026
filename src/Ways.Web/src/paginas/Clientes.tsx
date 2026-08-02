import { useCallback, useEffect, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeCatalogosFiscales } from '../api/catalogos'
import { clienteDeClientes } from '../api/clientes'
import { TIPOS_DOCUMENTO } from '../api/tipos'
import type {
  AltaCliente,
  ClienteListado,
  CondicionFiscalListado,
  ListaPrecioAsignable,
  PaginaDe,
  TipoDocumento,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

type Formulario = {
  id: number | null
  nombre: string
  apellido: string
  razonSocial: string
  tipoDocumento: TipoDocumento | ''
  numeroDocumento: string
  idCondicionFiscal: number | ''
  nacimiento: string
  domicilio: string
  telefono: string
  celular: string
  email: string
  observaciones: string
  idListaPrecio: number | ''
  limiteCredito: string
  creditoIlimitado: boolean
  activo: boolean
}

function formularioVacio(idListaPrecioPorDefecto: number | ''): Formulario {
  return {
    id: null,
    nombre: '',
    apellido: '',
    razonSocial: '',
    tipoDocumento: '',
    numeroDocumento: '',
    idCondicionFiscal: '',
    nacimiento: '',
    domicilio: '',
    telefono: '',
    celular: '',
    email: '',
    observaciones: '',
    idListaPrecio: idListaPrecioPorDefecto,
    limiteCredito: '0',
    creditoIlimitado: false,
    activo: true,
  }
}

function aFormulario(c: ClienteListado): Formulario {
  return {
    id: c.id,
    nombre: c.nombre,
    apellido: c.apellido ?? '',
    razonSocial: c.razonSocial ?? '',
    tipoDocumento: c.tipoDocumento ?? '',
    numeroDocumento: c.numeroDocumento ?? '',
    idCondicionFiscal: c.idCondicionFiscal,
    nacimiento: c.nacimiento ?? '',
    domicilio: c.domicilio ?? '',
    telefono: c.telefono ?? '',
    celular: c.celular ?? '',
    email: c.email ?? '',
    observaciones: c.observaciones ?? '',
    idListaPrecio: c.idListaPrecio,
    limiteCredito: String(c.limiteCredito),
    creditoIlimitado: c.creditoIlimitado,
    activo: c.activo,
  }
}

function aVacioNulo(valor: string): string | null {
  const limpio = valor.trim()
  return limpio === '' ? null : limpio
}

function aAlta(f: Formulario): AltaCliente {
  return {
    nombre: f.nombre.trim(),
    apellido: aVacioNulo(f.apellido),
    razonSocial: aVacioNulo(f.razonSocial),
    tipoDocumento: f.tipoDocumento === '' ? null : f.tipoDocumento,
    numeroDocumento: aVacioNulo(f.numeroDocumento),
    idCondicionFiscal: f.idCondicionFiscal === '' ? 0 : f.idCondicionFiscal,
    nacimiento: f.nacimiento === '' ? null : f.nacimiento,
    domicilio: aVacioNulo(f.domicilio),
    telefono: aVacioNulo(f.telefono),
    celular: aVacioNulo(f.celular),
    email: aVacioNulo(f.email),
    observaciones: aVacioNulo(f.observaciones),
    idListaPrecio: f.idListaPrecio === '' ? 0 : f.idListaPrecio,
    limiteCredito: f.limiteCredito === '' ? 0 : Number(f.limiteCredito),
    creditoIlimitado: f.creditoIlimitado,
    idEmpresa: null,
    activo: f.activo,
  }
}

/**
 * ABM dedicado de clientes (design decision 1: no la máquina genérica de catálogos). La fila
 * Consumidor Final (`numero = 1`) se muestra siempre, pero sus acciones de editar/eliminar
 * están deshabilitadas del lado del cliente (defensa en profundidad sobre el guard real, que
 * vive en `ServicioDeClientes`/`ReglaDeClientes`).
 */
export function Clientes() {
  const [pagina, setPagina] = useState<PaginaDe<ClienteListado> | null>(null)
  const [condiciones, setCondiciones] = useState<CondicionFiscalListado[]>([])
  const [listas, setListas] = useState<ListaPrecioAsignable[]>([])
  const [busqueda, setBusqueda] = useState('')
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)

  const cargar = useCallback(async (termino: string) => {
    setCargando(true)
    setError('')
    try {
      setPagina(await clienteDeClientes.listar(termino, false))
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los clientes.')
    } finally {
      setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar('')
    clienteDeCatalogosFiscales.condicionesFiscales().then(setCondiciones).catch(() => setCondiciones([]))
    clienteDeClientes.listasDePrecioAsignables().then(setListas).catch(() => setListas([]))
  }, [cargar])

  const idListaPorDefecto = listas.find((l) => l.esDefault)?.id ?? listas[0]?.id ?? ''

  async function guardar() {
    if (!formulario) return

    setGuardando(true)
    setError('')
    setAviso('')

    try {
      const datos = aAlta(formulario)

      if (formulario.id === null) {
        await clienteDeClientes.crear(datos)
        setAviso(`Cliente "${formulario.nombre}" creado.`)
      } else {
        await clienteDeClientes.actualizar(formulario.id, datos)
        setAviso(`Cliente "${formulario.nombre}" actualizado.`)
      }

      setFormulario(null)
      await cargar(busqueda)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
    } finally {
      setGuardando(false)
    }
  }

  async function eliminar(c: ClienteListado) {
    if (c.esConsumidorFinal) return
    if (!confirm(`¿Dar de baja al cliente "${c.nombre}"?`)) return

    setError('')
    setAviso('')
    try {
      await clienteDeClientes.eliminar(c.id)
      setAviso(`Cliente "${c.nombre}" dado de baja.`)
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
        placeholder="Buscar por nombre, apellido, razón social o documento…"
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
          setFormulario(formularioVacio(idListaPorDefecto))
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
      <Box titulo="Clientes" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {formulario && (
          <FormularioCliente
            valor={formulario}
            condiciones={condiciones}
            listas={listas}
            guardando={guardando}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={() => setFormulario(null)}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>N°</th>
                  <th>Nombre</th>
                  <th>Documento</th>
                  <th>Teléfono</th>
                  <th>Email</th>
                  <th>Estado</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {pagina?.items.map((c) => (
                  <tr key={c.id}>
                    <td>{String(c.numero).padStart(4, '0')}</td>
                    <td>
                      {[c.nombre, c.apellido].filter(Boolean).join(' ')}
                      {c.razonSocial && <div className="text-muted small">{c.razonSocial}</div>}
                      {c.esConsumidorFinal && (
                        <span className="badge rounded-0 text-bg-secondary ms-1">Protegido</span>
                      )}
                    </td>
                    <td>
                      {c.tipoDocumento && c.numeroDocumento ? `${c.tipoDocumento}: ${c.numeroDocumento}` : '—'}
                    </td>
                    <td>{c.telefono ?? c.celular ?? '—'}</td>
                    <td>{c.email ?? '—'}</td>
                    <td>
                      <span className={`badge rounded-0 ${c.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                        {c.activo ? 'Activo' : 'Inactivo'}
                      </span>
                    </td>
                    <td className="text-end text-nowrap">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-primary rounded-0 me-1"
                        disabled={c.esConsumidorFinal}
                        title={c.esConsumidorFinal ? 'El Consumidor Final no se puede editar.' : undefined}
                        onClick={() => {
                          setFormulario(aFormulario(c))
                          setAviso('')
                          setError('')
                        }}
                      >
                        Editar
                      </button>
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        disabled={c.esConsumidorFinal}
                        title={c.esConsumidorFinal ? 'El Consumidor Final no se puede eliminar.' : undefined}
                        onClick={() => eliminar(c)}
                      >
                        Baja
                      </button>
                    </td>
                  </tr>
                ))}
                {pagina?.items.length === 0 && (
                  <tr>
                    <td colSpan={7} className="text-center text-muted py-4">
                      No hay clientes que coincidan con la búsqueda.
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

function FormularioCliente({
  valor,
  condiciones,
  listas,
  guardando,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  condiciones: CondicionFiscalListado[]
  listas: ListaPrecioAsignable[]
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
        <strong>{esNuevo ? 'Nuevo cliente' : `Editando cliente ${valor.id}`}</strong>
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-nombre">
          Nombre
        </label>
        <input
          id="c-nombre"
          className="form-control rounded-0"
          maxLength={150}
          value={valor.nombre}
          onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
          required
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-apellido">
          Apellido
        </label>
        <input
          id="c-apellido"
          className="form-control rounded-0"
          maxLength={150}
          value={valor.apellido}
          onChange={(e) => onCambio({ ...valor, apellido: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-razon-social">
          Razón social
        </label>
        <input
          id="c-razon-social"
          className="form-control rounded-0"
          maxLength={150}
          value={valor.razonSocial}
          onChange={(e) => onCambio({ ...valor, razonSocial: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-condicion-fiscal">
          Condición fiscal
        </label>
        <select
          id="c-condicion-fiscal"
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

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-tipo-documento">
          Tipo de documento
        </label>
        <select
          id="c-tipo-documento"
          className="form-select rounded-0"
          value={valor.tipoDocumento}
          onChange={(e) => onCambio({ ...valor, tipoDocumento: e.target.value as Formulario['tipoDocumento'] })}
        >
          <option value="">Sin especificar</option>
          {TIPOS_DOCUMENTO.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-numero-documento">
          N° de documento
        </label>
        <input
          id="c-numero-documento"
          className="form-control rounded-0"
          maxLength={30}
          value={valor.numeroDocumento}
          onChange={(e) => onCambio({ ...valor, numeroDocumento: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-nacimiento">
          Nacimiento
        </label>
        <input
          id="c-nacimiento"
          type="date"
          className="form-control rounded-0"
          value={valor.nacimiento}
          onChange={(e) => onCambio({ ...valor, nacimiento: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-lista-precio">
          Lista de precios
        </label>
        <select
          id="c-lista-precio"
          className="form-select rounded-0"
          value={valor.idListaPrecio}
          onChange={(e) => onCambio({ ...valor, idListaPrecio: Number(e.target.value) })}
          required
        >
          <option value="" disabled>
            Elegir…
          </option>
          {listas.map((l) => (
            <option key={l.id} value={l.id}>
              {l.nombre}
              {l.esDefault ? ' (default)' : ''}
            </option>
          ))}
        </select>
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-domicilio">
          Domicilio
        </label>
        <input
          id="c-domicilio"
          className="form-control rounded-0"
          maxLength={255}
          value={valor.domicilio}
          onChange={(e) => onCambio({ ...valor, domicilio: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-telefono">
          Teléfono
        </label>
        <input
          id="c-telefono"
          className="form-control rounded-0"
          maxLength={50}
          value={valor.telefono}
          onChange={(e) => onCambio({ ...valor, telefono: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-celular">
          Celular
        </label>
        <input
          id="c-celular"
          className="form-control rounded-0"
          maxLength={50}
          value={valor.celular}
          onChange={(e) => onCambio({ ...valor, celular: e.target.value })}
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-email">
          Email
        </label>
        <input
          id="c-email"
          type="email"
          className="form-control rounded-0"
          maxLength={255}
          value={valor.email}
          onChange={(e) => onCambio({ ...valor, email: e.target.value })}
        />
      </div>

      <div className="col-12">
        <label className="form-label" htmlFor="c-observaciones">
          Observaciones
        </label>
        <textarea
          id="c-observaciones"
          className="form-control rounded-0"
          rows={2}
          value={valor.observaciones}
          onChange={(e) => onCambio({ ...valor, observaciones: e.target.value })}
        />
      </div>

      <div className="col-12">
        <strong>Crédito</strong>
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="c-limite-credito">
          Límite de crédito
        </label>
        <input
          id="c-limite-credito"
          type="number"
          step="0.01"
          min="0"
          className="form-control rounded-0"
          value={valor.limiteCredito}
          disabled={valor.creditoIlimitado}
          onChange={(e) => onCambio({ ...valor, limiteCredito: e.target.value })}
        />
      </div>

      <div className="col-md-3 d-flex align-items-end">
        <div className="form-check">
          <input
            id="c-credito-ilimitado"
            type="checkbox"
            className="form-check-input rounded-0"
            checked={valor.creditoIlimitado}
            onChange={(e) => onCambio({ ...valor, creditoIlimitado: e.target.checked })}
          />
          <label className="form-check-label" htmlFor="c-credito-ilimitado">
            Crédito ilimitado
          </label>
        </div>
      </div>

      <div className="col-md-3 d-flex align-items-end">
        <div className="form-check">
          <input
            id="c-activo"
            type="checkbox"
            className="form-check-input rounded-0"
            checked={valor.activo}
            onChange={(e) => onCambio({ ...valor, activo: e.target.checked })}
          />
          <label className="form-check-label" htmlFor="c-activo">
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
