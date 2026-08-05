namespace Ways.Domain.Caja;

/// <summary>
/// Una línea del resultado de <see cref="CalculadorDeArqueo"/> — un medio arqueable con su
/// <c>importe_esperado</c> ya derivado (design: The Derivation; Interfaces/Contracts). El cierre
/// la combina con el <c>importe_declarado</c> del cajero para armar cada fila de
/// <see cref="ArqueoTurno"/>; el resumen parcial la expone tal cual, sin declarado (D6 parity).
/// </summary>
public sealed record LineaDeArqueo(int IdMedioPago, decimal ImporteEsperado);
