namespace Ways.Application.Parametros;

public record ParametroResuelto(string Clave, string Valor);

/// <summary><paramref name="Valor"/> viaja como texto JSON crudo (columna <c>jsonb</c>);
/// <see cref="Ways.Domain.Catalogos.ParametroConocido.TipoClr"/> valida que efectivamente
/// deserialice al tipo declarado para esa clave (ADR-13).</summary>
public record ParametroAlta(string Clave, string Valor, int? IdPuntoVenta);

public record ParametroListado(int Id, string Clave, string Valor, int? IdPuntoVenta);
