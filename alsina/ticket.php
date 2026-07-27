<?php 
	session_start();
	$fechahora = explode(" - ", $_SESSION['t_fecha']);
	$new_fecha = explode("/", $fechahora[0]);
	$fecha = $new_fecha[2] . '/' . $new_fecha[1] . '/' . $new_fecha[0];
	$new_hora = explode(":", $fechahora[1]);
	$hora = $new_hora[0] . ':' . $new_hora[1];
	$new_fechahora = $fecha . ' - ' . $hora;
?>
<html>
	<head>
		<link rel="stylesheet" href="./assets/css/ways.css">
		<link rel="stylesheet" href="./assets/css/ticket.css">
		<link rel="stylesheet" href="./assets/lib/font-awesome/css/all.css">
	</head>
	<script>
		var impreso = false;
	</script>
	<body onBlur="imprimir();">
	<script type="text/javascript">
		function imprimir() {
			if (!impreso) {
				impreso = true;
				document.getElementById('imprimir').style.visibility = "hidden";
				document.getElementById('vuelto').style.visibility = "hidden";
				window.print();
				window.opener.location="ticketOk.php";
				window.close()
			} 
		}
		window.onkeydown = tecla;
		function tecla(event) {
			if (event.keyCode == 120 || event.keyCode == 27) {
				event.preventDefault();
				imprimir();
			}
		}
	</script>
		<div id="vuelto" class="footer-ways">VUELTO: $ <?php echo $_SESSION['vuelto']; ?></div>
		<header id="header">
			<div id="imprimir">
				<h3>
					<a href="" onclick="imprimir()">Imprimir (f9)</a>
				</h3>
			</div>
			<?php 
				echo '
					<h4>ORIGINAL</h4>
					<h4 style="display:flex; justify-content:space-between">
					<div>'.$new_fechahora.'</div>
					<div>'.$_SESSION['t_numero'].'</div>
					</h4>
					<h1 class="ways-brand">Ways</h1>
					<h3>
						'.$_SESSION['login']['punto_venta']['domicilio'].'<br />
						Abierto de '.$_SESSION['login']['punto_venta']['horario'].'
					</h3>';
			?>
			<hr />
			<?php 
				if (isset($_SESSION['direccion']) && !empty($_SESSION['direccion'])) {
					echo '
					<h1>
						'.$_SESSION['direccion'].'
					</h1>
					<hr />';
				}
				if($_SESSION['cliente']['id']!='1') {
				if(isset($_SESSION['cliente']['new_saldo'])) {
					$_SESSION['cliente']['new_saldo']=$_SESSION['cliente']['new_saldo'];
				}
				else {
					$_SESSION['cliente']['new_saldo']=$_SESSION['cliente']['saldo'];
				}
				echo '
			<hr />
			<h3 style="text-aling:left;">
				Cliente: '.$_SESSION['cliente']['cliente'].'<br>
				Direccion: '.$_SESSION['cliente']['direccion'].'<br>
				Saldo: $ '.number_format($_SESSION['cliente']['new_saldo'],2,".","").'</h3>
			<hr />
			<hr />
				';
				
			} ?>
			
		</header>
		
		<div id="ticket">
			<?php
			$comanda='';
			$TotalRoti=0;
			if($_SESSION['tipo']=='1') {
				$_SESSION['tipo']=='1';
				$array = $_SESSION['ticket']; 
				foreach($array as $id => $producto) { 
					if ($producto['barra']<10000) { $cant = $producto['precio'].' x '.$producto['cantidad']; }
					else { $cant = $producto['cantidad'].' x $'.$producto['precio']; }
					if ($producto['id_area']==8) { 
						$TotalRoti=$TotalRoti+$producto['precio'];
						$comanda.='<div class="detalle">'.$producto['cantidad'].' x </div><div class="importe">&nbsp;</div>
						<div class="detalleimporte">'.$producto['descripcion'].' </div><br><br><br><br><br>
						<hr>'; 
					}
					echo '
						<div class="detalle">'.$cant.'</div><div class="importe">$ '.$producto['total'].'</div>
						<div class="detalleimporte">'.$producto['descripcion'].' </div>
						<hr>'; 
				}
				$ticketSubtotal=number_format($_SESSION['total'],2,'.','');
				if(isset($_SESSION['descuento'])) { $ticketDescuento=number_format($_SESSION['descuento'],2,'.',''); }
				else { $ticketDescuento='0.00'; }
				$ticketTotal=number_format(($ticketSubtotal+$ticketDescuento),2,'.','');
				echo '<div class="detalle total">TOTAL: </div><div class="importe total">$ '.$ticketTotal.'</div>';
			}
			else { 
				echo 'ERROR';
			}
				?>
		</div>
	
		<footer id="footer">
			<hr />
			<hr />
			<h3>¡¡Gracias por tu compra!! </h3>
			<h4 style="display:flex; justify-content:space-between">
				<div><i class="fab fa-instagram"></i> waysmdq </div>
				<div><i class="fab fa-facebook"></i> WAYS Autoservicio </div>
				<div><i class="fab fa-whatsapp"></i> 223 6969031</div>
			</h4>
							<?php
			
			if($comanda!='') { 
				echo '<h2> -------------- COMANDA --------------</h2>';
				echo '<h4> TICKET: '.$_SESSION['t_numero'].' </h4>';
				if(isset($_SESSION['direccion'])) 
					echo '<h4> DOMICILIO: '.$_SESSION['direccion'].' </h4>';
				echo '<h2> HORA: '.$hora.' </h2>';
				echo $comanda;
				echo '<br>';
				echo 'Total $$: '.$TotalRoti;
			}
				
			
			?>
		</footer>
	</body>
</html>
