using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;

namespace Ways.Application.Catalogos;

public class ServicioDeGrupos(IWaysDbContext db, IRelojDelSistema reloj)
    : ServicioDeCatalogo<Grupo, GrupoListado, GrupoAlta>(db, reloj)
{
    protected override DbSet<Grupo> Conjunto => Db.Grupos;

    protected override GrupoListado Proyectar(Grupo entidad) =>
        new(entidad.Id, entidad.Nombre, entidad.Activo, entidad.IdEmpresa, entidad.Margen);

    protected override Grupo Instanciar(string nombre, int? idEmpresa, bool activo, DateTimeOffset ahora) =>
        new() { Nombre = nombre, IdEmpresa = idEmpresa, Activo = activo, CreatedAt = ahora, UpdatedAt = ahora };

    protected override void AplicarPropios(Grupo entidad, GrupoAlta datos) =>
        entidad.Margen = datos.Margen;
}
