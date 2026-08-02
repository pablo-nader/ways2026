import { useParams } from 'react-router'
import { descriptorAreas, descriptorGrupos, descriptorMarcas, descriptorMediosPago } from '../api/catalogos'
import { PaginaCatalogo } from './PaginaCatalogo'

/**
 * Resuelve `/catalogos/:recurso` al descriptor concreto (ADR-11). Es un `switch`, no una
 * búsqueda genérica en un registro indexado por string: cada `case` instancia
 * `PaginaCatalogo` con su propio par `TListado`/`TAlta` concreto, evitando perder el chequeo
 * de tipos que tendría un lookup dinámico sobre un registro heterogéneo.
 */
export function RutaCatalogo() {
  const { recurso } = useParams<{ recurso: string }>()

  switch (recurso) {
    case 'areas':
      return <PaginaCatalogo key="areas" definicion={descriptorAreas} />
    case 'marcas':
      return <PaginaCatalogo key="marcas" definicion={descriptorMarcas} />
    case 'grupos':
      return <PaginaCatalogo key="grupos" definicion={descriptorGrupos} />
    case 'medios-pago':
      return <PaginaCatalogo key="medios-pago" definicion={descriptorMediosPago} />
    default:
      return (
        <div className="container-fluid py-4">
          <div className="alert alert-warning rounded-0">Catálogo desconocido: «{recurso}».</div>
        </div>
      )
  }
}
