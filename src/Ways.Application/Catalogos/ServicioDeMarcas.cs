using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Domain.Catalogos;

namespace Ways.Application.Catalogos;

public class ServicioDeMarcas(IWaysDbContext db, IRelojDelSistema reloj, GuardaDeReferencias guarda)
    : ServicioDeCatalogo<Marca, MarcaListado, MarcaAlta>(db, reloj, guarda)
{
    protected override DbSet<Marca> Conjunto => Db.Marcas;

    protected override string CodigoEnUso => "marca_en_uso";

    protected override string SujetoDeBaja => "la marca";

    protected override MarcaListado Proyectar(Marca entidad) =>
        new(entidad.Id, entidad.Nombre, entidad.Activo, entidad.IdEmpresa);

    protected override Marca Instanciar(string nombre, int? idEmpresa, bool activo, DateTimeOffset ahora) =>
        new() { Nombre = nombre, IdEmpresa = idEmpresa, Activo = activo, CreatedAt = ahora, UpdatedAt = ahora };

    // Sin columnas propias: marcas no tiene nada más allá de lo que ya mapea la base.
    protected override void AplicarPropios(Marca entidad, MarcaAlta datos)
    {
    }
}
