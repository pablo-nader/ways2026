namespace Ways.Domain.Organizacion;

/// <summary>
/// Invariante "una PC-caja = un punto de venta" (DB CHANGE GATE aprobado): todo punto de venta
/// nace declarando si opera desde el POS de escritorio (Tauri, un único dispositivo vinculado,
/// <see cref="Dispositivo"/>) o desde la web normal — nunca los dos a la vez. Registrado como
/// <c>npgsql.MapEnum&lt;ModoPuntoVenta&gt;("modo_punto_venta")</c> en
/// <c>Ways.Infrastructure.DependencyInjection</c> y <c>WaysDbContextFactory</c>. Ortogonal a
/// <see cref="PuntoVenta.NumeroFiscal"/>: ninguno de los dos condiciona al otro.
/// </summary>
public enum ModoPuntoVenta
{
    Escritorio,
    Web
}
