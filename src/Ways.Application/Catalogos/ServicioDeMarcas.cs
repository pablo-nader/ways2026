using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;

namespace Ways.Application.Catalogos;

public class ServicioDeMarcas(IWaysDbContext db, IRelojDelSistema reloj)
    : ServicioDeCatalogo<Marca, MarcaListado, MarcaAlta>(db, reloj)
{
    protected override DbSet<Marca> Conjunto => Db.Marcas;

    protected override MarcaListado Proyectar(Marca entidad) =>
        new(entidad.Id, entidad.Nombre, entidad.Activo, entidad.IdEmpresa);

    protected override Marca Instanciar(string nombre, int? idEmpresa, bool activo, DateTimeOffset ahora) =>
        new() { Nombre = nombre, IdEmpresa = idEmpresa, Activo = activo, CreatedAt = ahora, UpdatedAt = ahora };

    // Sin columnas propias: marcas no tiene nada más allá de lo que ya mapea la base.
    protected override void AplicarPropios(Marca entidad, MarcaAlta datos)
    {
    }
}
