import { useEffect, useRef, useState } from 'react'
import { clienteDeCatalogosFiscales } from '../../api/catalogos'
import { ErrorApi } from '../../api/cliente'
import { clienteDeProveedores } from '../../api/proveedores'
import type { CondicionFiscalListado, ProveedorListado } from '../../api/tipos'
import { Cargando } from '../../componentes/Cargando'
import { Modal } from '../../componentes/Modal'

type Props = {
  onCreado: (proveedor: ProveedorListado) => void
  onCancelar: () => void
}

/**
 * Alta rápida de proveedor desde el formulario de artículo — solo los campos que
 * `ServicioDeProveedores.CrearAsync` exige de verdad (razón social, condición fiscal:
 * `ExigirIdRequerido`/`NormalizarRequerido`) más el nombre de fantasía. El resto viaja con los
 * mismos defaults que usa `Proveedores.tsx` para un alta en blanco (`null`, `idEmpresa: null`,
 * `activo: true`).
 */
export function AltaRapidaProveedor({ onCreado, onCancelar }: Props) {
  const [condiciones, setCondiciones] = useState<CondicionFiscalListado[] | null>(null)
  const [cargandoCondiciones, setCargandoCondiciones] = useState(true)
  const [errorCondiciones, setErrorCondiciones] = useState('')
  const [razonSocial, setRazonSocial] = useState('')
  const [nombreFantasia, setNombreFantasia] = useState('')
  const [idCondicionFiscal, setIdCondicionFiscal] = useState<number | ''>('')
  const [guardando, setGuardando] = useState(false)
  const [error, setError] = useState('')
  const bloqueadoRef = useRef(false)
  // Token de la carga de condiciones fiscales: "Reintentar" invalida cualquier carga anterior en
  // vuelo, y el desmontaje invalida la última — sin esto una respuesta tardía de un intento previo
  // podía pisar el resultado de un reintento posterior (react-async-state regla 2/3).
  const tokenCondicionesRef = useRef(0)

  function cargarCondicionesFiscales() {
    const token = (tokenCondicionesRef.current += 1)
    setCargandoCondiciones(true)
    setErrorCondiciones('')
    clienteDeCatalogosFiscales
      .condicionesFiscales()
      .then((lista) => {
        if (tokenCondicionesRef.current !== token) return
        setCondiciones(lista)
      })
      .catch(() => {
        if (tokenCondicionesRef.current !== token) return
        setErrorCondiciones('No se pudieron cargar las condiciones fiscales.')
      })
      .finally(() => {
        if (tokenCondicionesRef.current === token) setCargandoCondiciones(false)
      })
  }

  useEffect(() => {
    cargarCondicionesFiscales()
    return () => {
      tokenCondicionesRef.current += 1
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function guardar(evento: React.FormEvent<HTMLFormElement>) {
    evento.preventDefault()
    evento.stopPropagation()
    if (bloqueadoRef.current) return
    bloqueadoRef.current = true
    setGuardando(true)
    setError('')
    try {
      const creado = await clienteDeProveedores.crear({
        razonSocial: razonSocial.trim(),
        nombreFantasia: nombreFantasia.trim() === '' ? null : nombreFantasia.trim(),
        cuit: null,
        idCondicionFiscal: idCondicionFiscal === '' ? 0 : idCondicionFiscal,
        domicilio: null,
        telefono: null,
        email: null,
        vendedor: null,
        celularVendedor: null,
        supervisor: null,
        celularSupervisor: null,
        margen: null,
        observaciones: null,
        idEmpresa: null,
        activo: true,
      })
      onCreado(creado)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo crear el proveedor.')
    } finally {
      bloqueadoRef.current = false
      setGuardando(false)
    }
  }

  return (
    <Modal titulo="Nuevo proveedor" ocupado={guardando} onCerrar={onCancelar}>
      <form onSubmit={guardar}>
        {errorCondiciones && (
          <div className="alert alert-warning rounded-0 py-1 px-2 small d-flex justify-content-between align-items-center gap-2">
            <span>{errorCondiciones}</span>
            <button
              type="button"
              className="btn btn-sm btn-outline-secondary rounded-0"
              onClick={cargarCondicionesFiscales}
              disabled={cargandoCondiciones}
            >
              Reintentar
            </button>
          </div>
        )}
        {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}

        <div className="mb-3">
          <label className="form-label" htmlFor="alta-rapida-proveedor-razon-social">
            Razón social
          </label>
          <input
            id="alta-rapida-proveedor-razon-social"
            className="form-control rounded-0"
            maxLength={150}
            value={razonSocial}
            disabled={guardando}
            onChange={(e) => setRazonSocial(e.target.value)}
            autoFocus
            required
          />
        </div>

        <div className="mb-3">
          <label className="form-label" htmlFor="alta-rapida-proveedor-nombre-fantasia">
            Nombre de fantasía
          </label>
          <input
            id="alta-rapida-proveedor-nombre-fantasia"
            className="form-control rounded-0"
            maxLength={150}
            value={nombreFantasia}
            disabled={guardando}
            onChange={(e) => setNombreFantasia(e.target.value)}
          />
        </div>

        <div className="mb-3">
          <label className="form-label" htmlFor="alta-rapida-proveedor-condicion-fiscal">
            Condición fiscal
          </label>
          {condiciones === null ? (
            errorCondiciones ? null : <Cargando texto="Cargando condiciones fiscales…" />
          ) : (
            <select
              id="alta-rapida-proveedor-condicion-fiscal"
              className="form-select rounded-0"
              value={idCondicionFiscal}
              disabled={guardando}
              onChange={(e) => setIdCondicionFiscal(e.target.value === '' ? '' : Number(e.target.value))}
              required
            >
              <option value="" disabled>
                Elegir…
              </option>
              {condiciones.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.nombre}
                </option>
              ))}
            </select>
          )}
        </div>

        <div className="d-flex gap-2">
          <button type="submit" className="btn btn-success rounded-0" disabled={guardando || condiciones === null}>
            {guardando ? 'Creando…' : 'Crear'}
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
    </Modal>
  )
}
