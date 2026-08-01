namespace Ways.Domain.Common;

/// <summary>
/// Base de toda entidad que pertenece a un tenant. Deliberadamente una clase aparte de
/// <see cref="EntidadBase"/> (no un <c>IdTenant</c> nullable ahí): heredar de acá es la
/// declaración visible de "esta tabla está scopeada", y es de lo que se cuelgan el query
/// filter de EF, el estampado en <c>SaveChanges</c> y la cobertura de RLS.
///
/// <see cref="Ways.Domain.Usuarios.Usuario"/> (tenant nullable = plataforma) y
/// <see cref="Ways.Domain.Organizacion.Tenant"/> (su propia PK es el alcance) no heredan
/// de acá a propósito: ninguna de las dos tiene la semántica "IdTenant NOT NULL".
/// </summary>
public abstract class EntidadTenant : EntidadBase
{
    public int IdTenant { get; set; }
}
