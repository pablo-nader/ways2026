<?php 
	$mensaje = '';
	$error = false;
	if (@$_POST['login']=='login') {
		$punto_venta = $_POST['punto_venta'];
		if (empty($punto_venta)) { 
			$mensaje = 'Tenés que elegir un local';
			$error = true; 
		} else {
			$id_usuario = $_SESSION['login']['id'];
			$query = "	SELECT 	p.id, p.nombre, p.domicilio, p.horario
						FROM 	puntos_venta p
							JOIN usuario_rol_puntoventa urp ON p.id = urp.id_punto_venta AND urp.id_usuario = $id_usuario
						WHERE	p.id = $punto_venta";

			$result = mysqli_query($conexion, $query);
			if (mysqli_num_rows($result) == 1) {
				$puntoVenta = mysqli_fetch_assoc($result);
				$_SESSION['login']['status'] = 'ready';
				$_SESSION['login']['punto_venta'] = array(
						'id' => $puntoVenta['id'],
						'nombre' => $puntoVenta['nombre'],
						'domicilio' => $puntoVenta['domicilio'],
						'horario' => $puntoVenta['horario']
				);
				echo '<script language="javascript">window.location="index.php?menu=facturacion&opc=ventas"</script>;';
			} else {
				$mensaje = 'El local ingresado es incorrecto'; 
				$error = true; 
			}			
		}
	}
	$id_usuario = $_SESSION['login']['id'];
	$query = "	SELECT 	p.id, p.nombre, p.domicilio 
				FROM 	puntos_venta p
					JOIN usuario_rol_puntoventa urp ON p.id = urp.id_punto_venta AND urp.id_usuario = $id_usuario";
	$puntos_venta = mysqli_query($conexion, $query);
?>
<body class="login">
	<div class="form-signin rounded-0">
		<h1 class="text-center ways-brand">
			Ways
		</h1>
		<hr>
		<div class="tab-content">
			<div id="login" class="tab-pane active">
				<form name="login" action="index.php?menu=login" method="post">
					<p class="text-muted text-center">Elegí el local</p>
					<select name="punto_venta" id="punto_venta" class="form-select mb-3 rounded-0" required autofocus>
						<option value="">Seleccioná el local</option>
						<?php 
						while($punto_venta = mysqli_fetch_assoc($puntos_venta)) {
							echo '<option value="'.$punto_venta['id'].'">'.$punto_venta['nombre'].' ('.$punto_venta['domicilio'].')</option>';
						}
						?>
					</select>
					<?php 
					if (isset($error) && $error) { 
						echo '<div class="text-danger text-center mb-3">'.$mensaje.'</div>'; 
					}
					?>
					<input type="hidden" name="login" id="login" value="login">
					<button class="btn btn-lg btn-success form-control rounded-0" type="submit">Entrar</button>
				</form>
			</div>
		</div>
	</div>
	<script src="https://cdn.jsdelivr.net/npm/bootstrap@5.2.0-beta1/dist/js/bootstrap.bundle.min.js" integrity="sha384-pprn3073KE6tl6bjs2QrFaJGz5/SUsLqktiwsUTF55Jfv3qYSDhgCecCxMW52nD2" crossorigin="anonymous"></script>	
</body>