<?php 
	session_start();
	require_once './conexion.php';
	$conexion = mysqli_connect(HOST, USER, PASSWORD, DATABASE);
	$idTicket=$_GET['id'];
	$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
	$datos = "SELECT * FROM ventas WHERE id_punto_venta = $id_punto_venta AND id = $idTicket";
	$mostrar=mysqli_fetch_assoc(mysqli_query($conexion,$datos));
	$fechahora=explode(" ",$mostrar['fecha']);
	$fecha=explode("-",$fechahora[0]);
	$new_fecha=$fecha[2].'/'.$fecha[1].'/'.$fecha[0];
	$hora=explode(":",$fechahora[1]);
	$new_hora=$hora[0].':'.$hora[1];
	$new_fechahora=$new_fecha.' - '.$new_hora;
	$t_numero= str_pad($id_punto_venta, 4, "0", STR_PAD_LEFT).' - '.str_pad($mostrar['id'], 8, "0", STR_PAD_LEFT);
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
					window.opener.location="ticketOk.php";
					window.close();
				}
				if(num==27) {
					window.opener.location="ticketOk.php";
					window.close();
				}
			}
	
	</script>

		<header>
			<div style="visibility: visible" id="imprimir"><h3><a href="" onclick="finalizar()">Imprimir (f9)</a></h3></div>
			<?php 
				echo '					
					<h4>DUPLICADO</h4>
					<h4 style="display:flex; justify-content:space-between">
						<div>'.$new_fechahora.'</div>
						<div>'.$t_numero.'</div>
					</h4>
					<h1 class="ways-brand">Ways</h1>
					<h3>
						'.$_SESSION['login']['punto_venta']['domicilio'].'<br />
						Abierto de '.$_SESSION['login']['punto_venta']['horario'].'
					</h3>';
			?>
			<hr />
			<hr />
		</header>

		<div id="ticket">
			<?php
				if($mostrar['tipo']==1 || $mostrar['tipo']==2 || $mostrar['tipo']==4) {
					$array = explode("*",$mostrar['articulos']);
					foreach($array as $id => $producto) {
						$art = explode("/",$producto);
						if ($art[0]>10000) { $cant = $art[3].' x $'.$art[1]; }
						else { $cant = $art[1].' x $'.$art[3]; }
						echo '
							<div class="detalle">'.$cant.'</div><div class="importe">$ '.$art[4].'</div>
							<div class="detalleimporte">'.$art[2].' </div>
							<hr>'; 
					}
					$ticketSubtotal=number_format($mostrar['subtotal'],2,'.','');
					$ticketDescuento=number_format($mostrar['descuento'],2,'.','');
					$ticketTotal=number_format($mostrar['total'],2,'.','');
					echo '<div class="detalle total">SUBTOTAL: </div><div class="importe total">$ '.$ticketSubtotal.'</div>';
					echo '<div class="detalle total">DESCUENTO: </div><div class="importe total">$ '.$ticketDescuento.'</div>';
					echo '<div class="detalle total">TOTAL: </div><div class="importe total">$ '.$ticketTotal.'</div>';
				}
				elseif ($mostrar['tipo']==3) {
					$id_usuario=$mostrar['id_usuario'];
					$id_usuario=mysqli_fetch_array(mysqli_query($conexion,"SELECT user FROM usuarios WHERE id='$id_usuario'"));
					$cliente=$mostrar['cliente'];
					$cliente=mysqli_fetch_array(mysqli_query($conexion,"SELECT nombre, apellido FROM usuarios WHERE id='$cliente'"));
					echo '
						<div class="detalleimporte" style="text-align:center;">-- PAGO A CUENTA --</div>
						<div class="detalleimporte" style="text-align:center;">&nbsp;</div>
						<div class="detalle" style="font-weight:bold;">Cliente:</div><div class="importe">'.$cliente[0].' '.$cliente[1].'</div>
						<div class="detalle" style="font-weight:bold;">Usuario:</div><div class="importe">'.$id_usuario[0].'</div>
						<div class="detalleimporte" style="text-align:center;">&nbsp;</div>
						<hr>
						<div class="detalle total">TOTAL: </div><div class="importe total">$ '.number_format(($mostrar['c_corriente']*(-1)),2,".","").'</div> 		
						<div class="detalle total">FIRMA: </div><div class="importe total"></div>
						<br><br><br><br><br><br><br><br>'; 		
				}
				?>
		</div>
	
		<footer>
			<hr />
			<hr />
			<h3>¡¡Gracias por tu compra!! </h3>
			<h4 style="display:flex; justify-content:space-between">
				<div><i class="fab fa-instagram"></i> waysmdq </div>
				<div><i class="fab fa-facebook"></i> WAYS Autoservicio </div>
				<div><i class="fab fa-whatsapp"></i> 223 6969031</div>
			</h4>
			<span style="font-size:5px">.</span>
		</footer>
	</body>
</html>
