<?php 
	session_start();
	require_once './conexion.php';
	$conexion = mysqli_connect(HOST, USER, PASSWORD, DATABASE);
	$idTicket=$_GET['id'];
	$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
	$datos = "SELECT * FROM gastos WHERE id_punto_venta = $id_punto_venta AND id = $idTicket";
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
		window.opener.location = 'index.php?menu=facturacion&opc=caja';
		window.close()
	}
	function finalizar2() {
		window.opener.location = 'index.php?menu=facturacion&opc=caja';
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
					window.opener.location = 'index.php?menu=facturacion&opc=caja';
					window.close();
				}
				if(num==27) {
					document.getElementById('header').style.visibility = "hidden";
					document.getElementById('ticket').style.visibility = "hidden";
					document.getElementById('imprimir').style.visibility = "hidden";
					document.getElementById('footer').style.visibility = "hidden";
					window.print();
					window.opener.location = 'index.php?menu=facturacion&opc=caja';
					window.close();
				}
			}
	
	</script>

		<header>
			<div style="visibility: visible" id="imprimir"><h3><a href="" onclick="finalizar()">Imprimir (f9)</a></h3></div>
			<?php 
				echo '
					
					<h4>Retiro en Efectivo</h4>
					<h4>'.$t_numero.'</h4>
					<h4>'.$new_fechahora.'</h4>
				'; 
			?>
			<h1 class="ways-brand">Ways</h1>
		</header>

		<div id="ticket">
			<?php
			$id_usuario=$mostrar['id_usuario'];
			$id_usuario=mysqli_fetch_array(mysqli_query($conexion,"SELECT user FROM usuarios WHERE id='$id_usuario'"));
			echo '
			<div class="detalleimporte" style="text-align:center;">-- RETIRO EN EFECTIVO --</div>
			<div class="detalleimporte" style="text-align:center;">&nbsp;</div>
			<div class="detalle" style="font-weight:bold;">Usuario:</div><div class="importe">'.$id_usuario[0].'</div>
			<div class="detalleimporte" style="text-align:center;">&nbsp;</div>
			<hr>
			<div class="detalle total">TOTAL: </div><div class="importe total">$ '.number_format(($mostrar['importe']),2,".","").'</div> 		
			<div class="detalle total">FIRMA: </div><div class="importe total"></div>
			<br><br><br><br><br><br><br><br><hr>'; 
			?>
		</div>
	</body>
</html>
