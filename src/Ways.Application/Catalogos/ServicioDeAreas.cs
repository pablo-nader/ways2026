using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Domain.Catalogos;

namespace Ways.Application.Catalogos;

public class ServicioDeAreas(IWaysDbContext db, IRelojDelSistema reloj, GuardaDeReferencias guarda)
    : ServicioDeCatalogo<Area, AreaListado, AreaAlta>(db, reloj, guarda)
{
    protected override DbSet<Area> Conjunto => Db.Areas;

    protected override string CodigoEnUso => "area_en_uso";

    protected override string SujetoDeBaja => "el área";

    protected override AreaListado Proyectar(Area entidad) =>
        new(entidad.Id, entidad.Nombre, entidad.Activo, entidad.IdEmpresa, entidad.Orden);

    protected override Area Instanciar(string nombre, int? idEmpresa, bool activo, DateTimeOffset ahora) =>
        new() { Nombre = nombre, IdEmpresa = idEmpresa, Activo = activo, CreatedAt = ahora, UpdatedAt = ahora };

    protected override void AplicarPropios(Area entidad, AreaAlta datos) =>
        entidad.Orden = datos.Orden;
}
