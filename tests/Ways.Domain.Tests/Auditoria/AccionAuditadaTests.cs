using System.Text.RegularExpressions;
using Ways.Domain.Auditoria;

namespace Ways.Domain.Tests.Auditoria;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 1 (task 1.13, design decisión 4): el catálogo genérico
/// — 30 entradas, sin duplicados, naming <c>&lt;dominio&gt;.&lt;operacion&gt;</c>, <c>Entidad</c>
/// no vacía. Las tres de <c>pv.baja</c>/<c>tenant.baja</c>/<c>empresa.baja</c> las agregó la etapa
/// 20 slice 4 (judgment-day ronda 1, hallazgo C1): las bajas de organización no dejaban rastro.
/// <c>pv.modo</c> la agregó stage-desktop-pos (DB CHANGE GATE aprobado): el flip administrativo de
/// modo también deja rastro. <c>venta.discrepancia</c> la agregó stage-pos-venta-offline-backend:
/// una venta offline cuyo precio congelado no coincide con lo que el servidor hubiera cobrado.
/// <c>caja.forzado</c> la agregó la guarda de rendición de cola de dispositivos: un cierre de
/// turno forzado por encima de ella. <c>venta.sobrelimite</c> la agregó la venta local a clientes
/// identificados: una venta con cuenta corriente que superó el límite sin poder validarlo.
/// <c>pago.edicion</c>/<c>pago.baja</c> las agregó la edición y baja de pagos.
/// <c>pv.alta</c> la agregó el alta de puntos de venta sobre una empresa existente. Las cuatro de sucursales (<c>sucursal.alta|edicion|baja</c> y <c>pv.sucursal</c>) las agregó el ABM de sucursales; <c>sucursal.fusion</c>, la fusión de sucursales; <c>sucursal.mercadopago</c>, el registro de la sucursal como local de Mercado Pago; <c>pv.mercadopago</c>, el del punto de venta como caja.
/// <c>compra.descarte</c> la agregó el descarte (borrado físico) de un borrador de compra.
/// <c>compra.edicion</c> la agregó la edición de una compra confirmada.
/// Los abrevian el dominio como <c>cc.reliquidacion</c> — el formato congelado no admite guiones
/// bajos en el dominio.
/// </summary>
public partial class AccionAuditadaTests
{
    [GeneratedRegex(@"^[a-z]+\.[a-z]+$")]
    private static partial Regex FormatoDeAccion();

    [Fact]
    public void TieneTreintaYNueveEntradas()
    {
        Assert.Equal(39, AccionAuditada.Todas.Count);
    }

    [Fact]
    public void NoHayDuplicados()
    {
        Assert.Equal(AccionAuditada.Todas.Count, AccionAuditada.Todas.Distinct().Count());
    }

    [Fact]
    public void CadaAccionRespetaElFormatoDominioPuntoOperacion()
    {
        foreach (var accion in AccionAuditada.Todas)
        {
            Assert.True(
                FormatoDeAccion().IsMatch(accion.Accion),
                $"'{accion.Accion}' no respeta el formato '<dominio>.<operacion>'.");
        }
    }

    [Fact]
    public void CadaEntidadEsNoVacia()
    {
        foreach (var accion in AccionAuditada.Todas)
        {
            Assert.False(string.IsNullOrWhiteSpace(accion.Entidad));
        }
    }

    /// <summary>judgment-day, slice 1 ronda 2, finding 3 (juez B): el tipo NO impide
    /// <c>new AccionAuditada(...)</c> inline (el <c>record</c> posicional genera un constructor
    /// público) — este test es el único freno real contra un typo en el catálogo, congelando los
    /// pares exactos que el resto del código asume.</summary>
    [Fact]
    public void ElCatalogoTieneExactamenteLosParesEsperados()
    {
        (string Accion, string Entidad)[] esperado =
        [
            ("precio.cambio", "articulo"),
            ("venta.anulacion", "comprobante_venta"),
            ("compra.anulacion", "comprobante_compra"),
            ("compra.descarte", "comprobante_compra"),
            ("stock.ajuste", "articulo"),
            ("stock.decomiso", "articulo"),
            ("stock.conteo", "articulo"),
            ("cc.reliquidacion", "cliente"),
            ("usuario.alta", "usuario"),
            ("usuario.actualizacion", "usuario"),
            ("usuario.baja", "usuario"),
            ("usuario.desbloqueo", "usuario"),
            ("usuario.password", "usuario"),
            ("tenant.baja", "tenant"),
            ("empresa.baja", "empresa"),
            ("pv.baja", "punto_venta"),
            ("pv.modo", "punto_venta"),
            ("venta.discrepancia", "comprobante_venta"),
            ("caja.forzado", "turno_caja"),
            ("venta.sobrelimite", "comprobante_venta"),
            ("pago.edicion", "pago"),
            ("pago.baja", "pago"),
            ("pago.aplicacion", "pago"),
            ("pago.desaplicacion", "pago"),
            ("compra.redondeo", "comprobante_compra"),
            ("pago.redondeo", "pago"),
            ("pago.reasignacion", "pago"),
            ("pv.alta", "punto_venta"),
            ("compra.edicion", "comprobante_compra"),
            ("cobro.aplicacion", "comprobante_venta"),
            ("cobro.desaplicacion", "comprobante_venta"),
            ("sucursal.alta", "sucursal"),
            ("sucursal.edicion", "sucursal"),
            ("sucursal.baja", "sucursal"),
            ("pv.sucursal", "punto_venta"),
            ("sucursal.fusion", "sucursal"),
            ("sucursal.mercadopago", "sucursal"),
            ("pv.mercadopago", "punto_venta"),
            ("venta.medios", "comprobante_venta")
        ];

        var real = AccionAuditada.Todas.Select(a => (a.Accion, a.Entidad));

        Assert.Equal(esperado, real);
    }
}
