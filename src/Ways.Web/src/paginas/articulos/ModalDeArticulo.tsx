import type {
  AlicuotaIvaListado,
  AreaListado,
  CategoriaListado,
  EmpresaListado,
  GrupoListado,
  ListaPrecioListado,
  MarcaListado,
  ProveedorListado,
} from '../../api/tipos'
import { Cargando } from '../../componentes/Cargando'
import { Modal } from '../../componentes/Modal'
import type { AccionesDeFamiliaDelFormulario, EstadoDeFamiliaDelFormulario, EstadoDeUnionAFamilia } from './familia'
import { FormularioArticulo, type CodigoDeProveedorDelFormulario, type ErrorDeTecla, type Formulario } from './FormularioArticulo'

export type PropsDeModalDeArticulo = {
  clave: number | 'nuevo'
  formulario: Formulario | null
  cargandoDetalle: boolean
  /** Error de carga (id no numérico, no encontrado, falla de red al pedir el detalle): reemplaza
   * TODO el cuerpo del modal — no hay formulario que mostrar. */
  errorDetalle: string
  guardando: boolean
  ocupado: boolean
  /** Error del guardado (alta o edición): banner ARRIBA del formulario, que sigue visible — slot
   * propio, nunca compartido con `errorDetalle` (react-async-state regla 14: cada fuente
   * asíncrona reporta a su propio estado). El éxito sale como toast desde el hook. */
  errorGuardado: string
  bloqueadoPorCatalogos: boolean
  /** Mismo texto que el aviso de la grilla (`Articulos.tsx`): el modal tapa la grilla con el
   * backdrop, así que el motivo del bloqueo tiene que verse también DENTRO del diálogo — si no,
   * el usuario solo ve un "Guardar" deshabilitado sin ninguna explicación. */
  erroresCatalogosRequeridos: string[]
  avisoListasPrecio: string
  /** Cómo reintentar cuando un catálogo no cargó: depende de dónde vive el formulario. */
  consejoDeReintento: string
  areas: AreaListado[]
  categorias: CategoriaListado[]
  marcas: MarcaListado[]
  grupos: GrupoListado[]
  proveedores: ProveedorListado[]
  proveedoresTruncados: boolean
  alicuotasIva: AlicuotaIvaListado[]
  empresas: EmpresaListado[]
  listasPrecio: ListaPrecioListado[]
  familia: EstadoDeFamiliaDelFormulario
  accionesDeFamilia: AccionesDeFamiliaDelFormulario
  union: EstadoDeUnionAFamilia
  errorDeFamiliaNueva: string
  errorDeTecla: ErrorDeTecla | null
  sinGuardar: boolean
  preguntaDeAlcanceAbierta: boolean
  /** Solo en un alta abierta desde una compra: el código con el que el proveedor habitual conoce al artículo. */
  codigoProveedor?: CodigoDeProveedorDelFormulario
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: Formulario) => void
  actualizarFormulario: (actualizar: (previo: Formulario) => Formulario) => void
  onGuardar: () => void
  onCerrar: () => void
  alDeEscribir: (enCurso: boolean) => void
  alCambiarTienda: () => void
  onCategoriaCreada: (categoria: CategoriaListado) => void
  onMarcaCreada: (marca: MarcaListado) => void
  onGrupoCreada: (grupo: GrupoListado) => void
  onProveedorCreado: (proveedor: ProveedorListado) => void
}

/**
 * Modal de alta/edición de artículo (etapa articulos-en-modal): aloja el mismo
 * `FormularioArticulo` de siempre — identificación, clasificación, costos, disponibilidad,
 * códigos de barra y precios — pero ahora en un `Modal` en vez de apilado arriba de la grilla.
 * Quien lo muestra (`Articulos.tsx`, o el alta desde una compra) es dueño de TODO el estado
 * (`useFormularioDeArticulo`: formulario, catálogos, avisos): este componente es
 * una capa de presentación + wiring de foco, sin fetch ni escritura propios — así una URL que pasa
 * de `/articulos/create` a `/articulos/edit/{id}` tras un alta exitosa no fuerza un remonte que
 * tiraría el aviso de éxito y el foco (ver el comentario de `rutaModal.ts` y el efecto de apertura
 * en `Articulos.tsx`).
 */
export function ModalDeArticulo({
  clave,
  formulario,
  cargandoDetalle,
  errorDetalle,
  guardando,
  ocupado,
  errorGuardado,
  bloqueadoPorCatalogos,
  erroresCatalogosRequeridos,
  avisoListasPrecio,
  consejoDeReintento,
  areas,
  categorias,
  marcas,
  grupos,
  proveedores,
  proveedoresTruncados,
  alicuotasIva,
  empresas,
  listasPrecio,
  familia,
  accionesDeFamilia,
  union,
  errorDeFamiliaNueva,
  errorDeTecla,
  sinGuardar,
  preguntaDeAlcanceAbierta,
  codigoProveedor,
  focoDeReserva,
  onCambio,
  actualizarFormulario,
  onGuardar,
  onCerrar,
  alDeEscribir,
  alCambiarTienda,
  onCategoriaCreada,
  onMarcaCreada,
  onGrupoCreada,
  onProveedorCreado,
}: PropsDeModalDeArticulo) {
  const titulo =
    formulario === null ? 'Artículo' : formulario.id === null ? 'Nuevo artículo' : `Editando artículo ${formulario.codigoInterno}`

  return (
    <Modal titulo={titulo} tamano="xl" desplazable ocupado={ocupado} focoDeReserva={focoDeReserva} onCerrar={onCerrar}>
      {errorDetalle ? (
        <>
          <div className="alert alert-danger">{errorDetalle}</div>
          <button type="button" className="btn btn-outline-secondary" onClick={onCerrar}>
            Volver al listado
          </button>
        </>
      ) : cargandoDetalle || formulario === null ? (
        <Cargando texto="Cargando artículo…" />
      ) : (
        <>
          {erroresCatalogosRequeridos.length > 0 && (
            <div className="alert alert-warning">
              {erroresCatalogosRequeridos.join(' ')} El guardado (alta o edición) de artículos va a quedar bloqueado
              hasta que se puedan cargar — {consejoDeReintento}.
            </div>
          )}
          {avisoListasPrecio && <div className="alert alert-warning">{avisoListasPrecio}</div>}
          {errorGuardado && <div className="alert alert-danger">{errorGuardado}</div>}
          <FormularioArticulo
            // Clave por artículo (id, o 'nuevo' para el alta): switching entre dos artículos
            // distintos (back/forward entre dos URLs de edición) resetea el subárbol y su
            // estado por-artículo (sugerencia de margen, historial de precios, códigos de
            // barra cargados). Un alta exitosa NO cambia esta clave (queda en 'nuevo'): es el
            // MISMO artículo ganando id, no un cambio de entidad — ver `Articulos.tsx`.
            key={clave}
            valor={formulario}
            areas={areas}
            categorias={categorias}
            marcas={marcas}
            grupos={grupos}
            proveedores={proveedores}
            proveedoresTruncados={proveedoresTruncados}
            alicuotasIva={alicuotasIva}
            empresas={empresas}
            listasPrecio={listasPrecio}
            guardando={guardando}
            ocupado={ocupado}
            bloqueadoPorCatalogos={bloqueadoPorCatalogos}
            familia={familia}
            accionesDeFamilia={accionesDeFamilia}
            union={union}
            errorDeFamiliaNueva={errorDeFamiliaNueva}
            errorDeTecla={errorDeTecla}
            sinGuardar={sinGuardar}
            preguntaDeAlcanceAbierta={preguntaDeAlcanceAbierta}
            codigoProveedor={codigoProveedor}
            onCambio={onCambio}
            actualizarFormulario={actualizarFormulario}
            onGuardar={onGuardar}
            onCancelar={onCerrar}
            alDeEscribir={alDeEscribir}
            alCambiarTienda={alCambiarTienda}
            onCategoriaCreada={onCategoriaCreada}
            onMarcaCreada={onMarcaCreada}
            onGrupoCreada={onGrupoCreada}
            onProveedorCreado={onProveedorCreado}
          />
        </>
      )}
    </Modal>
  )
}
