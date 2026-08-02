namespace Ways.Domain.Clientes;

/// <summary>
/// Tipo de documento de un cliente (doc 10 §2). Enum nativo de Postgres
/// (<c>tipo_documento</c>), mismo criterio que <c>estado_tenant</c>/<c>estado_usuario</c>.
/// Nullable en <see cref="Cliente"/>: el Consumidor Final no tiene documento.
/// </summary>
public enum TipoDocumento
{
    Dni,
    Cuit,
    Cuil,
    Pasaporte,
    Otro
}
