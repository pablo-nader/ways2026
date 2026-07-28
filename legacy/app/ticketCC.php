<?php 
	session_start();
	require_once './conexion.php';
	$conexion = mysqli_connect(HOST, USER, PASSWORD, DATABASE);
	$idTicket = $_GET['id'];
	$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
	$datos = "SELECT * FROM cajas WHERE id = $idTicket AND id_punto_venta = $id_punto_venta";
	$mostrar=mysqli_fetch_assoc(mysqli_query($conexion,$datos));
	$fechahora=explode(" ",$mostrar['fecha']);
	$fecha=explode("-",$fechahora[0]);
	$new_fecha=$fecha[2].'/'.$fecha[1].'/'.$fecha[0];
	$hora=explode(":",$fechahora[1]);
	$new_hora=$hora[0].':'.$hora[1];
	$new_fechahora=$new_fecha.' - '.$new_hora;
	$t_numero='N° '.str_pad($mostrar['id'], 8, "0", STR_PAD_LEFT);
?>
<html>
	<head>
		<link rel="stylesheet" type="text/css" href="./assets/css/ways.css">
		<link rel="stylesheet" type="text/css" href="./assets/css/ticket.css">
	</head>
	<body onunload="finalizar2()" onBlur="finalizar2();window.close();">
	<script type="text/javascript">
	function finalizar() {
		document.getElementById('imprimir').style.visibility = "hidden";
		window.print();
		window.opener.location.href;
		window.close();
	}
	function finalizar2() {
		window.opener.location.href;
		window.close();
	}
	
	window.onkeydown = tecla;
	function tecla(event) {
		var array= [112,113,114,115,116,117,118,119,120,121,122,123];
		if(array.includes(event.keyCode)) {
			event.preventDefault();
		}
		num = event.keyCode;
		if(num==120) {
			document.getElementById('imprimir').style.visibility = "hidden";
			window.print();
			window.opener.location.href;
			window.close();
		}
		if(num==27) {
			window.opener.location.href;
			window.close();
		}
	}
	
	</script>

		<header>
			<div style="visibility: visible" id="imprimir"><h3><a href="" onclick="finalizar()">Imprimir (f9)</a></h3></div>
			<?php 
				echo '
					
					<h4>Cierre de Caja</h4>
					<h4>'.$t_numero.'</h4>
					<h4>'.$new_fechahora.'</h4>
				'; 
			?>
			<h1 class="ways-brand">Ways</h1>
			<h2>Autoservicio</h2>
		</header>

		<div id="ticket">
			<?php
			$totalCaja=number_format((($mostrar['total']-$mostrar['tarjetas']-$mostrar['c_corriente']+$mostrar['saldo']-$mostrar['retiros'])*-1),2,".","");
			echo '
			<h3>Ventas por Categoria</h3>
			<div class="detalle">Verdulería</div><div class="importe">$ '.$mostrar['c2'].'</div>
			<div class="detalle">Fiambrería</div><div class="importe">$ '.$mostrar['c3'].'</div>
			<div class="detalle">Cigarrillos</div><div class="importe">$ '.$mostrar['c4'].'</div>
			<div class="detalle">Carga Virtual</div><div class="importe">$ '.$mostrar['c5'].'</div>
			<div class="detalle">SUBE</div><div class="importe">$ '.$mostrar['c6'].'</div>
			<div class="detalle">Sorteo</div><div class="importe">$ '.$mostrar['c8'].'</div>
			<div class="detalle">Almacen</div><div class="importe">$ '.$mostrar['c1'].'</div>
			<div class="detalleimporte total">TOTAL: </div>
			<div class="detalleimporte total" style="text-align:right;">$ '.$mostrar['total'].'</div>

			<h3>Compras por Categoria</h3>
			<div class="detalle">Verdulería</div><div class="importe">$ '.$mostrar['g_c2'].'</div>
			<div class="detalle">Fiambrería</div><div class="importe">$ '.$mostrar['g_c3'].'</div>
			<div class="detalle">Cigarrillos</div><div class="importe">$ '.$mostrar['g_c4'].'</div>
			<div class="detalle">Carga Virtual</div><div class="importe">$ '.$mostrar['g_c5'].'</div>
			<div class="detalle">SUBE</div><div class="importe">$ '.$mostrar['g_c6'].'</div>
			<div class="detalle">Sorteo</div><div class="importe">$ '.$mostrar['g_c8'].'</div>
			<div class="detalle">Almacen</div><div class="importe">$ '.$mostrar['g_c1'].'</div>
			<div class="detalleimporte total">TOTAL: </div>
			<div class="detalleimporte total" style="text-align:right;">$ '.$mostrar['gTotal'].'</div>
			
			<h3>Resumen Caja</h3>
			<div class="detalle">Efectivo</div><div class="importe">$ '.$mostrar['efectivo'].'</div>
			<div class="detalle">Tarjetas</div><div class="importe">$ '.$mostrar['tarjetas'].'</div>
			<div class="detalle">Cuenta Corriente</div><div class="importe">$ '.$mostrar['c_corriente'].'</div>
			<div class="detalle">Saldo</div><div class="importe">$ '.$mostrar['saldo'].'</div>
			<div class="detalle">Retiros</div><div class="importe">$ '.$mostrar['retiros'].'</div>
			
			<div class="detalleimporte total">DIFERENCIA DE CAJA: </div>
			<div class="detalleimporte total" style="text-align:right;">$ '.$totalCaja.'</div>
			';
				?>
		</div>
	</body>
</html>
