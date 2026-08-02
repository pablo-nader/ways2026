using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;

namespace Ways.Application.Catalogos;

public class ServicioDeAreas(IWaysDbContext db, IRelojDelSistema reloj)
    : ServicioDeCatalogo<Area, AreaListado, AreaAlta>(db, reloj)
{
    protected override DbSet<Area> Conjunto => Db.Areas;

    protected override AreaListado Proyectar(Area entidad) =>
        new(entidad.Id, entidad.Nombre, entidad.Activo, entidad.IdEmpresa, entidad.Orden);

    protected override Area Instanciar(string nombre, int? idEmpresa, bool activo, DateTimeOffset ahora) =>
        new() { Nombre = nombre, IdEmpresa = idEmpresa, Activo = activo, CreatedAt = ahora, UpdatedAt = ahora };

    protected override void AplicarPropios(Area entidad, AreaAlta datos) =>
        entidad.Orden = datos.Orden;
}
