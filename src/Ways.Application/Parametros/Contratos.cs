namespace Ways.Application.Parametros;

public record ParametroResuelto(string Clave, string Valor);

/// <summary><paramref name="Valor"/> viaja como texto JSON crudo (columna <c>jsonb</c>);
/// <see cref="Ways.Domain.Catalogos.ParametroConocido.TipoClr"/> valida que efectivamente
/// deserialice al tipo declarado para esa clave (ADR-13). Una clave de alcance caja admite
/// <paramref name="IdPuntoVenta"/> (o ninguno, para el default de la empresa); una de alcance
/// sucursal admite <paramref name="IdSucursal"/> (o ninguno). Mandar los dos, o el que no
/// corresponde a la clave, se rechaza con 400.</summary>
public record ParametroAlta(string Clave, string Valor, int? IdPuntoVenta, int? IdSucursal = null);

public record ParametroListado(int Id, string Clave, string Valor, int? IdPuntoVenta, int? IdSucursal = null);
