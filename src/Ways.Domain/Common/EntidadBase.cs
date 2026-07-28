namespace Ways.Domain.Common;

/// <summary>
/// Base de toda entidad persistida del sistema.
/// Convención del proyecto: todas las tablas llevan created_at, updated_at y deleted_at,
/// y las bajas son siempre lógicas.
/// </summary>
public abstract class EntidadBase
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public bool EstaEliminada => DeletedAt is not null;
}
