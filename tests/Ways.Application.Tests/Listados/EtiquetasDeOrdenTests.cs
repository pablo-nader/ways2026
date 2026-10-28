using Ways.Application.Auditoria;
using Ways.Application.Caja;
using Ways.Domain.Gastos;

namespace Ways.Application.Tests.Listados;

/// <summary>Guarda contra el desfase entre las etiquetas del servidor (con las que se ordena) y las de la web
/// (las que se ven). Fija la lista literal de pares código-etiqueta; su espejo es el test de
/// <c>CATALOGO_DE_ACCIONES_AUDITADAS</c> en <c>Ways.Web/src/api/tipos.test.ts</c>, que fija la MISMA lista.</summary>
public class EtiquetasDeAccionTests
{
    private static readonly (string Codigo, string Etiqueta)[] Esperadas =
    [
        ("precio.cambio", "Cambio de precio"),
        ("venta.anulacion", "Anulación de venta"),
        ("compra.anulacion", "Anulación de compra"),
        ("stock.ajuste", "Ajuste de stock"),
        ("stock.decomiso", "Decomiso de stock"),
        ("stock.conteo", "Conteo de inventario"),
        ("cc.reliquidacion", "Reliquidación de cuenta corriente"),
        ("usuario.alta", "Alta de usuario"),
        ("usuario.actualizacion", "Actualización de usuario"),
        ("usuario.baja", "Baja de usuario"),
        ("usuario.desbloqueo", "Desbloqueo de usuario"),
        ("usuario.password", "Cambio de contraseña"),
        ("tenant.baja", "Baja de tenant"),
        ("empresa.baja", "Baja de empresa"),
        ("pv.baja", "Baja de punto de venta"),
    ];

    [Fact]
    public void LasEtiquetasDelServidorSonExactamenteLaListaFijada()
    {
        Assert.Equal(
            Esperadas.Select(e => (e.Codigo, e.Etiqueta)).ToList(),
            ServicioDeConsultaDeAuditoria.EtiquetasDeAccion.Select(p => (p.Key, p.Value)).ToList());
    }
}

/// <summary>Espejo de este test: el de <c>CATEGORIAS_GASTO</c> en <c>Ways.Web/src/api/tipos.test.ts</c>.</summary>
public class EtiquetasDeCategoriaDeGastoTests
{
    [Fact]
    public void LasEtiquetasDelServidorSonExactamenteLaListaFijada()
    {
        (string, string)[] esperadas =
        [
            ("Proveedor", "Proveedores"),
            ("Sueldos", "Sueldos"),
            ("Viaticos", "Viáticos"),
            ("Impuestos", "Impuestos"),
            ("Servicios", "Servicios"),
            ("Otros", "Otros"),
        ];

        Assert.Equal(
            esperadas.ToList(),
            EtiquetasDeCategoriaDeGasto.Todas.Select(p => (p.Key.ToString(), p.Value)).ToList());
    }

    [Fact]
    public void TodoValorDelEnumTieneEtiqueta()
    {
        foreach (var categoria in Enum.GetValues<CategoriaGasto>())
        {
            Assert.True(EtiquetasDeCategoriaDeGasto.Todas.ContainsKey(categoria), $"Falta la etiqueta de {categoria}.");
        }
    }
}
