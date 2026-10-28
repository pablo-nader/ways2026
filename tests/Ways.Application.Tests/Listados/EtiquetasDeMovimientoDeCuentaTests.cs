using Ways.Application.Listados;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Plantel;

namespace Ways.Application.Tests.Listados;

/// <summary>Guarda contra el desfase entre las etiquetas de la columna Tipo de los libros que el servidor usa para
/// ordenar y las que muestra la web. Fija la lista literal; su espejo es <c>ordenDeCajaYCuentas.test.ts</c> (bloque de etiquetas de la columna Tipo)
/// en <c>Ways.Web/src/api</c>, que fija la MISMA lista.</summary>
public class EtiquetasDeMovimientoDeCuentaTests
{
    [Fact]
    public void LasEtiquetasDeProveedorSonExactamenteLaListaFijada()
    {
        Assert.Equal(
            [("Apertura", "Apertura"), ("Compra", "Compra"), ("Pago", "Pago"), ("Ajuste", "Ajuste manual")],
            EtiquetasDeMovimientoDeCuenta.Proveedor.Select(p => (p.Key.ToString(), p.Value)).ToList());
        Assert.Equal("Contramov. de anulación", EtiquetasDeMovimientoDeCuenta.ContramovimientoDeAnulacion);
    }

    [Fact]
    public void LasEtiquetasDePersonalSonExactamenteLaListaFijada()
    {
        Assert.Equal(
            [("Horas", "Horas trabajadas"), ("Pago", "Pago"), ("Ajuste", "Ajuste")],
            EtiquetasDeMovimientoDeCuenta.Personal.Select(p => (p.Key.ToString(), p.Value)).ToList());
    }

    [Fact]
    public void TodoValorDeCadaEnumTieneEtiqueta()
    {
        Assert.All(
            Enum.GetValues<TipoMovimientoCcProveedor>(),
            e => Assert.True(EtiquetasDeMovimientoDeCuenta.Proveedor.ContainsKey(e), $"Falta {e}."));
        Assert.All(
            Enum.GetValues<TipoMovimientoPersonal>(),
            e => Assert.True(EtiquetasDeMovimientoDeCuenta.Personal.ContainsKey(e), $"Falta {e}."));
    }

    [Fact]
    public void ElSelectorDeProveedorRotulaUnAjusteConCompraComoContramovimientoDeAnulacion()
    {
        var selector = EtiquetasDeMovimientoDeCuenta.SelectorDeTipoDeProveedor().Compile();

        string Etiqueta(TipoMovimientoCcProveedor tipo, int? compra) =>
            selector(new MovimientoCuentaCorrienteProveedor { Tipo = tipo, IdComprobanteCompra = compra });

        Assert.Equal("Ajuste manual", Etiqueta(TipoMovimientoCcProveedor.Ajuste, null));
        Assert.Equal("Contramov. de anulación", Etiqueta(TipoMovimientoCcProveedor.Ajuste, 3));
        Assert.Equal("Compra", Etiqueta(TipoMovimientoCcProveedor.Compra, 3));
        Assert.Equal("Apertura", Etiqueta(TipoMovimientoCcProveedor.Apertura, null));
        Assert.Equal("Pago", Etiqueta(TipoMovimientoCcProveedor.Pago, null));
    }
}
