using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Catalogos;

public class ServicioDeMediosPago(IWaysDbContext db, IRelojDelSistema reloj, GuardaDeReferencias guarda)
    : ServicioDeCatalogo<MedioPago, MedioPagoListado, MedioPagoAlta>(db, reloj, guarda)
{
    protected override DbSet<MedioPago> Conjunto => Db.MediosPago;

    protected override string CodigoEnUso => "medio_pago_en_uso";

    protected override string SujetoDeBaja => "el medio de pago";

    protected override MedioPagoListado Proyectar(MedioPago entidad) => new(
        entidad.Id, entidad.Nombre, entidad.Activo, entidad.IdEmpresa, entidad.Orden,
        entidad.Comportamiento, entidad.AdmiteVuelto, entidad.RequiereReferencia, entidad.RecargoPorcentaje,
        entidad.PorcentajeFacturacion);

    protected override MedioPago Instanciar(string nombre, int? idEmpresa, bool activo, DateTimeOffset ahora) =>
        new() { Nombre = nombre, IdEmpresa = idEmpresa, Activo = activo, CreatedAt = ahora, UpdatedAt = ahora };

    protected override void AplicarPropios(MedioPago entidad, MedioPagoAlta datos)
    {
        ExigirPorcentajeDeFacturacionValido(datos.PorcentajeFacturacion);

        entidad.Orden = datos.Orden;
        entidad.Comportamiento = datos.Comportamiento;
        entidad.AdmiteVuelto = datos.AdmiteVuelto;
        entidad.RequiereReferencia = datos.RequiereReferencia;
        entidad.RecargoPorcentaje = datos.RecargoPorcentaje;
        entidad.PorcentajeFacturacion = datos.PorcentajeFacturacion;
    }

    /// <summary>Entre 0 y 100 con hasta 2 decimales, que es lo que guarda <c>numeric(5,2)</c>: más
    /// decimales se rechazan en vez de redondearse en silencio. El backstop de rango es
    /// <c>ck_medios_pago_porcentaje_facturacion</c>.</summary>
    private static void ExigirPorcentajeDeFacturacionValido(decimal porcentaje)
    {
        if (porcentaje < 0m || porcentaje > 100m || porcentaje != Math.Round(porcentaje, 2))
        {
            throw new ErrorDominio(
                "porcentaje_facturacion_invalido",
                "El porcentaje a facturar tiene que estar entre 0 y 100, con hasta 2 decimales.",
                400);
        }
    }
}
