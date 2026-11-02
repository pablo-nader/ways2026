using Ways.Domain.MercadoPago;

namespace Ways.Domain.Tests.MercadoPago;

/// <summary>
/// El lector del reporte de transacciones de Mercado Pago (E6): columnas por nombre, separador por el encabezado, comillas, decimales
/// con punto, fechas ISO con zona, columnas obligatorias que faltan, y filas ilegibles que se informan sin lanzar.
/// </summary>
public class LectorDeReporteMercadoPagoTests
{
    private const string Encabezado =
        "SOURCE_ID,EXTERNAL_REFERENCE,TRANSACTION_TYPE,TRANSACTION_AMOUNT,FEE_AMOUNT,SETTLEMENT_NET_AMOUNT,TRANSACTION_DATE,MONEY_RELEASE_DATE,EXTERNAL_POS_ID";

    private static ResultadoDeLecturaDeReporteMercadoPago Leer(params string[] lineas) =>
        LectorDeReporteMercadoPago.Leer(string.Join("\n", lineas));

    // Clausula: el separador se decide por el encabezado y la coma y el punto y coma leen lo mismo.
    [Fact]
    public void LaComaYElPuntoYComaLeenLaMismaFila()
    {
        var coma = Leer(Encabezado, "123,W1P2N3I1,SETTLEMENT,150.50,-6.25,144.25,2026-10-31T12:30:00.000-03:00,2026-11-14T00:00:00Z,POS9");
        var puntoYComa = Leer(
            Encabezado.Replace(',', ';'),
            "123;W1P2N3I1;SETTLEMENT;150.50;-6.25;144.25;2026-10-31T12:30:00.000-03:00;2026-11-14T00:00:00Z;POS9");

        Assert.Null(coma.ErrorDeArchivo);
        Assert.Empty(coma.Invalidas);
        Assert.Equal(coma.Filas, puntoYComa.Filas);
        var fila = Assert.Single(coma.Filas);
        Assert.Equal(2, fila.NumeroLinea);
        Assert.Equal("123", fila.SourceId);
        Assert.Equal("W1P2N3I1", fila.ExternalReference);
        Assert.Equal("SETTLEMENT", fila.TipoTransaccion);
        Assert.Equal(150.50m, fila.Importe);
        Assert.Equal(-6.25m, fila.Comision);
        Assert.Equal(144.25m, fila.Neto);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 12, 30, 0, TimeSpan.FromHours(-3)), fila.FechaTransaccion);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero), fila.FechaLiberacion);
        Assert.Equal("POS9", fila.ExternalPosId);
    }

    // Clausula: las columnas se buscan por nombre (sin importar el orden, las mayúsculas ni los espacios) y las que sobran se ignoran.
    [Fact]
    public void LasColumnasSeBuscanPorNombreYLasDeMasSeIgnoran()
    {
        var lectura = Leer(
            "extra, transaction_amount ,Source_Id,TRANSACTION_DATE,otra,transaction_type",
            "x,10.00,77,2026-10-31T00:00:00Z,y,refund");

        var fila = Assert.Single(lectura.Filas);
        Assert.Equal("77", fila.SourceId);
        Assert.Equal(10.00m, fila.Importe);
        Assert.Equal("REFUND", fila.TipoTransaccion);
        Assert.Null(fila.ExternalReference);
        Assert.Null(fila.Comision);
        Assert.Null(fila.Neto);
        Assert.Null(fila.FechaLiberacion);
        Assert.Null(fila.ExternalPosId);
    }

    // Clausula: los campos entre comillas admiten el separador, una comilla escapada y saltos de línea, y la línea informada es la del
    // inicio del registro.
    [Fact]
    public void LosCamposEntreComillasAdmitenSeparadorComillaYSaltoDeLinea()
    {
        var lectura = Leer(
            Encabezado,
            "\"12,3\",\"ref \"\"x\"\"\nmultilínea\",SETTLEMENT,\"1000.00\",,,2026-10-31T00:00:00Z,,",
            "456,,SETTLEMENT,5.00,,,2026-10-31T00:00:00Z,,");

        Assert.Empty(lectura.Invalidas);
        Assert.Equal(2, lectura.Filas.Count);
        Assert.Equal("12,3", lectura.Filas[0].SourceId);
        Assert.Equal("ref \"x\"\nmultilínea", lectura.Filas[0].ExternalReference);
        Assert.Equal(2, lectura.Filas[0].NumeroLinea);
        Assert.Equal(4, lectura.Filas[1].NumeroLinea);
    }

    // Clausula: el BOM, los fines de línea CRLF y las líneas en blanco no cambian nada.
    [Fact]
    public void ElBomLosFinesDeLineaCrlfYLasLineasEnBlancoSeToleran()
    {
        var texto = "﻿" + Encabezado + "\r\n\r\n1,,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,\r\n   \r\n2,,SETTLEMENT,2.00,,,2026-10-31T00:00:00Z,,\r\n";

        var lectura = LectorDeReporteMercadoPago.Leer(texto);

        Assert.Null(lectura.ErrorDeArchivo);
        Assert.Equal(new[] { 3, 5 }, lectura.Filas.Select(f => f.NumeroLinea));
    }

    // Clausula: sin una columna obligatoria el archivo entero se rechaza nombrando las que faltan (no se lanza).
    [Theory]
    [InlineData("SOURCE_ID")]
    [InlineData("TRANSACTION_TYPE")]
    [InlineData("TRANSACTION_AMOUNT")]
    [InlineData("TRANSACTION_DATE")]
    public void FaltarUnaColumnaObligatoriaRechazaElArchivo(string columna)
    {
        var encabezado = string.Join(",", Encabezado.Split(',').Where(c => c != columna));

        var lectura = Leer(encabezado, "1,,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,");

        Assert.Contains(columna, lectura.ErrorDeArchivo);
        Assert.Empty(lectura.Filas);
    }

    // Clausula: una columna repetida, un archivo vacío o solo en blanco y un reporte con demasiadas filas son errores de archivo.
    [Fact]
    public void LosErroresDeArchivoSeInformanSinLanzar()
    {
        Assert.NotNull(LectorDeReporteMercadoPago.Leer(string.Empty).ErrorDeArchivo);
        Assert.NotNull(LectorDeReporteMercadoPago.Leer("  \n \r\n").ErrorDeArchivo);
        Assert.NotNull(LectorDeReporteMercadoPago.Leer((string?)null).ErrorDeArchivo);
        Assert.Contains("SOURCE_ID", Leer(Encabezado + ",source_id", "1").ErrorDeArchivo);

        var enormes = new[] { Encabezado }.Concat(Enumerable.Repeat("1,,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,", LectorDeReporteMercadoPago.MaximoDeFilas + 1));
        Assert.Contains(LectorDeReporteMercadoPago.MaximoDeFilas.ToString(), Leer([.. enormes]).ErrorDeArchivo);
    }

    // Clausula: el lector corta en streaming: pasado el maximo de filas deja de leer (no materializa un archivo de millones de lineas).
    [Fact]
    public void ElLectorSeCortaAlPasarElMaximoDeFilasSinLeerElResto()
    {
        const string fila = "1,,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,\n";
        LectorInfinito? usado = null;

        var lectura = LectorDeReporteMercadoPago.Leer(() => usado = new LectorInfinito(Encabezado + "\n", fila));

        Assert.Contains(LectorDeReporteMercadoPago.MaximoDeFilas.ToString(), lectura.ErrorDeArchivo);
        Assert.InRange(usado!.Leidos, 1, (long)(LectorDeReporteMercadoPago.MaximoDeFilas + 3) * fila.Length + Encabezado.Length);
        Assert.True(usado.Leidos < LectorInfinito.Tope);
    }

    // Clausula: pasado el maximo de filas ilegibles tambien corta: error de archivo en la primera ilegible de mas, sin leer el resto.
    [Fact]
    public void ElLectorSeCortaAlPasarElMaximoDeFilasIlegibles()
    {
        const string ilegible = "x\n";
        LectorInfinito? usado = null;

        var lectura = LectorDeReporteMercadoPago.Leer(() => usado = new LectorInfinito(Encabezado + "\n", ilegible));

        Assert.Contains(LectorDeReporteMercadoPago.MaximoDeInvalidas.ToString(), lectura.ErrorDeArchivo);
        Assert.Empty(lectura.Filas);
        Assert.Empty(lectura.Invalidas);
        Assert.InRange(usado!.Leidos, 1, (long)(LectorDeReporteMercadoPago.MaximoDeInvalidas + 3) * ilegible.Length + Encabezado.Length);
    }

    // Clausula: los bytes se leen como UTF-8 con o sin marca de orden y dan lo mismo que el texto.
    [Fact]
    public void LosBytesSeLeenComoElTextoConOSinMarcaDeOrden()
    {
        var texto = Encabezado + "\n1,ñ,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,\n";
        var sinMarca = System.Text.Encoding.UTF8.GetBytes(texto);
        var conMarca = System.Text.Encoding.UTF8.GetPreamble().Concat(sinMarca).ToArray();

        Assert.Equal("ñ", Assert.Single(LectorDeReporteMercadoPago.Leer(sinMarca).Filas).ExternalReference);
        Assert.Equal("ñ", Assert.Single(LectorDeReporteMercadoPago.Leer(conMarca).Filas).ExternalReference);
    }

    /// <summary>Un reporte que no termina: un encabezado y una fila que se repite hasta <see cref="Tope"/> caracteres; cuenta lo que se leyó.</summary>
    private sealed class LectorInfinito(string encabezado, string fila) : TextReader
    {
        public const long Tope = 50_000_000;

        public long Leidos { get; private set; }

        public override int Peek() => Leidos >= Tope ? -1 : Caracter(Leidos);

        public override int Read() => Leidos >= Tope ? -1 : Caracter(Leidos++);

        private char Caracter(long posicion) =>
            posicion < encabezado.Length ? encabezado[(int)posicion] : fila[(int)((posicion - encabezado.Length) % fila.Length)];
    }

    // Clausula: una fila mal formada no se lanza ni rechaza el archivo: se informa con su línea y su motivo y las demás se leen.
    [Fact]
    public void UnaFilaMalFormadaSeInformaConSuLineaYLasDemasSeLeen()
    {
        var lectura = Leer(
            Encabezado,
            "1,,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,",
            "2,,SETTLEMENT,uno,,,2026-10-31T00:00:00Z,,",
            "3,,SETTLEMENT,1.00,,,ayer,,",
            ",,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,",
            "5,,COMPRA,1.00,,,2026-10-31T00:00:00Z,,",
            "6,,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,");

        Assert.Equal(new[] { 2, 7 }, lectura.Filas.Select(f => f.NumeroLinea));
        Assert.Equal(new[] { 3, 4, 5, 6 }, lectura.Invalidas.Select(i => i.NumeroLinea));
        Assert.Contains("TRANSACTION_AMOUNT", lectura.Invalidas[0].Motivo);
        Assert.Contains("TRANSACTION_DATE", lectura.Invalidas[1].Motivo);
        Assert.Contains("SOURCE_ID", lectura.Invalidas[2].Motivo);
        Assert.Contains("TRANSACTION_TYPE", lectura.Invalidas[3].Motivo);
    }

    // Clausula: los importes llevan "." decimal y a lo sumo dos decimales; una coma, un separador de miles o una notación científica
    // invalidan la fila en lugar de interpretarse mal.
    [Theory]
    [InlineData("1.234", false)]
    [InlineData("\"1,5\"", false)]
    [InlineData("1.500,25", false)]
    [InlineData("1e3", false)]
    [InlineData("--1", false)]
    [InlineData("", false)]
    [InlineData("12", true)]
    [InlineData("-12.5", true)]
    [InlineData("+0.05", true)]
    [InlineData("12345678901234.99", true)]
    public void ElImporteAdmiteSoloPuntoDecimalYDosDecimales(string importe, bool valido)
    {
        var lectura = Leer(Encabezado, $"1,,SETTLEMENT,{importe},,,2026-10-31T00:00:00Z,,");

        Assert.Equal(valido, lectura.Filas.Count == 1);
        Assert.Equal(!valido, lectura.Invalidas.Count == 1);
    }

    // Clausula: la fecha es ISO con zona (Z, ±hh:mm, ±hhmm o ±hh); una fecha sin zona o solo con día no se adivina.
    [Theory]
    [InlineData("2026-10-31T12:00:00Z", true)]
    [InlineData("2026-10-31T12:00:00.123456-03:00", true)]
    [InlineData("2026-10-31T12:00:00-0300", true)]
    [InlineData("2026-10-31T12:00:00+05", true)]
    [InlineData("2026-10-31 12:00:00Z", true)]
    [InlineData("2026-10-31T12:00:00", false)]
    [InlineData("2026-10-31", false)]
    [InlineData("31/10/2026 12:00", false)]
    [InlineData("2026-13-45T12:00:00Z", false)]
    public void LaFechaEsIsoConZona(string fecha, bool valida)
    {
        var lectura = Leer(Encabezado, $"1,,SETTLEMENT,1.00,,,{fecha},,");

        Assert.Equal(valida, lectura.Filas.Count == 1);
        Assert.Equal(!valida, lectura.Invalidas.Count == 1);
    }

    [Fact]
    public void LaFechaConOffsetSeConvierteAlInstanteCorrecto()
    {
        var lectura = Leer(Encabezado, "1,,SETTLEMENT,1.00,,,2026-10-31T23:30:00-0300,,");

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 2, 30, 0, TimeSpan.Zero).UtcDateTime, Assert.Single(lectura.Filas).FechaTransaccion.UtcDateTime);
    }

    // Clausula: una columna opcional vacía es nula, pero una opcional presente y mal formada invalida la fila.
    [Fact]
    public void UnaOpcionalVaciaEsNulaYUnaMalFormadaInvalidaLaFila()
    {
        var lectura = Leer(
            Encabezado,
            "1,,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,",
            "2,,SETTLEMENT,1.00,mucho,,2026-10-31T00:00:00Z,,",
            "3,,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,pronto,");

        Assert.Equal(2, Assert.Single(lectura.Filas).NumeroLinea);
        Assert.Equal(new[] { 3, 4 }, lectura.Invalidas.Select(i => i.NumeroLinea));
        Assert.Contains("FEE_AMOUNT", lectura.Invalidas[0].Motivo);
        Assert.Contains("MONEY_RELEASE_DATE", lectura.Invalidas[1].Motivo);
    }

    // Clausula: el tipo se lee sin distinguir mayúsculas y cada tipo conocido se acepta.
    [Theory]
    [InlineData("settlement")]
    [InlineData("REFUND")]
    [InlineData("Chargeback")]
    [InlineData("DISPUTE")]
    [InlineData("WITHDRAWAL")]
    [InlineData("WITHDRAWAL_CANCEL")]
    [InlineData("PAYOUT")]
    public void LosTiposConocidosSeAceptan(string tipo)
    {
        var lectura = Leer(Encabezado, $"1,,{tipo},1.00,,,2026-10-31T00:00:00Z,,");

        Assert.Equal(tipo.ToUpperInvariant(), Assert.Single(lectura.Filas).TipoTransaccion);
    }

    // Clausula: más celdas que el encabezado con datos invalida la fila; las celdas vacías de más (una coma final) no.
    [Fact]
    public void LasCeldasDeMasConDatosInvalidanLaFilaYLasVaciasNo()
    {
        var lectura = Leer(
            Encabezado,
            "1,,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,,",
            "2,,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,,sobra");

        Assert.Equal(2, Assert.Single(lectura.Filas).NumeroLinea);
        Assert.Equal(3, Assert.Single(lectura.Invalidas).NumeroLinea);
    }

    // Clausula: los topes de largo de los textos invalidan la fila.
    [Fact]
    public void LosTextosMuyLargosInvalidanLaFila()
    {
        var lectura = Leer(
            Encabezado,
            $"{new string('9', LectorDeReporteMercadoPago.LargoMaximoDeSourceId + 1)},,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,",
            $"1,{new string('r', LectorDeReporteMercadoPago.LargoMaximoDeExternalReference + 1)},SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,",
            $"1,,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,{new string('p', LectorDeReporteMercadoPago.LargoMaximoDeExternalPosId + 1)}",
            $"{new string('9', LectorDeReporteMercadoPago.LargoMaximoDeSourceId)},,SETTLEMENT,1.00,,,2026-10-31T00:00:00Z,,");

        Assert.Equal(5, Assert.Single(lectura.Filas).NumeroLinea);
        Assert.Equal(new[] { 2, 3, 4 }, lectura.Invalidas.Select(i => i.NumeroLinea));
    }
}
