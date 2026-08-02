using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;

namespace Ways.Application.Catalogos;

public class ServicioDeMediosPago(IWaysDbContext db, IRelojDelSistema reloj)
    : ServicioDeCatalogo<MedioPago, MedioPagoListado, MedioPagoAlta>(db, reloj)
{
    protected override DbSet<MedioPago> Conjunto => Db.MediosPago;

    protected override MedioPagoListado Proyectar(MedioPago entidad) => new(
        entidad.Id, entidad.Nombre, entidad.Activo, entidad.IdEmpresa, entidad.Orden,
        entidad.Comportamiento, entidad.AdmiteVuelto, entidad.RequiereReferencia, entidad.RecargoPorcentaje);

    protected override MedioPago Instanciar(string nombre, int? idEmpresa, bool activo, DateTimeOffset ahora) =>
        new() { Nombre = nombre, IdEmpresa = idEmpresa, Activo = activo, CreatedAt = ahora, UpdatedAt = ahora };

    protected override void AplicarPropios(MedioPago entidad, MedioPagoAlta datos)
    {
        entidad.Orden = datos.Orden;
        entidad.Comportamiento = datos.Comportamiento;
        entidad.AdmiteVuelto = datos.AdmiteVuelto;
        entidad.RequiereReferencia = datos.RequiereReferencia;
        entidad.RecargoPorcentaje = datos.RecargoPorcentaje;
    }
}
