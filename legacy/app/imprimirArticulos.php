<?php 
session_start();
// Este archivo apuntaba a una base de desarrollo local (root@127.0.0.1, base "ways").
// Ahora usa la misma conexión por variables de entorno que el resto del sistema.
require_once __DIR__ . '/conexion.php';
$conexion = mysqli_connect(HOST, USER, PASSWORD, DATABASE);
if(!$conexion) echo "<h3>No se ha podido conectar PHP - MySQL, verifique sus datos.</h3>"; 
$id=$_GET['id'];
$tipo=$_GET['tipo'];
if($tipo==1) { $datos="SELECT * FROM articulos WHERE existencia<=0 AND id_proveedor='$id'"; $titulo='PRODUCTOS SIN STOCK'; }
elseif ($tipo==2) { $datos="SELECT * FROM articulos WHERE existencia>0 AND existencia < existenciaMinima AND id_proveedor='$id'"; $titulo='PRODUCTOS DEBAJO DEL MINIMO'; }
$consulta=mysqli_query($conexion,$datos);
$proveedor=mysqli_fetch_array(mysqli_query($conexion,"SELECT nombre FROM proveedores WHERE id='$id'"));
?>
<html>
	<head>
		<link rel="stylesheet" href="./assets/css/ways.css">
		<link rel="stylesheet" href="./assets/css/ticket.css">
	</head>
	<body style="width:160px;" onunload="finalizar2()" onBlur="finalizar2();window.close();">
	<script type="text/javascript">
	function finalizar() {
		document.getElementById('imprimir').style.visibility = "hidden";
		window.print();
		window.opener.location = window.opener.location.href;
		window.close()
	}
	function finalizar2() {
		window.opener.location = window.opener.location.href;
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
					window.opener.location = window.opener.location.href;
					window.close();
				}
				if(num==27) {
					window.close();
				}
			}
	
	</script>

		<header>
			<div style="visibility: visible" id="imprimir"><h3><a href="" onclick="finalizar()">Imprimir (f9)</a></h3></div>
			<?php 
			echo '
				<h4>'.$titulo.'</h4>
				<h4>'.$proveedor[0].'</h4>
				<br>'; ?>
		
		</header>

		<div id="ticket">
			<?php
			while($mostrar=mysqli_fetch_assoc($consulta)) {
				echo'<div class="detalleimporte">'.$mostrar['nombre'].'</div>';
			}
			?>
		</div>
	</body>
</html>
