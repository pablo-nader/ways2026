<?php
	if(@$_GET['crear']=='Crear Usuario') {
		$nombre=$_GET['nombre'];
		$apellido=$_GET['apellido'];
		$user=$nombre.' '.$apellido;
		if(isset($_GET['dni'])) { 
			$dni=$_GET['dni']; 
			$dni=str_replace(".", "", $dni);
		} else $dni='';
		if(isset($_GET['nacimiento'])) {
			$nacimiento=$_GET['nacimiento'];
		} else $nacimiento='';
		$domicilio=$_GET['domicilio'];
		if(!empty($_GET['tel'])) {
			$tel=$_GET['tel'];
		} else $tel='';
		if(!empty($_GET['cel'])) {
			$cel=$_GET['cel'];
		} else $cel='';
		$mail=$_GET['mail'];
		$acuerdo=$_GET['acuerdo'];
						
		$crearUsuario="INSERT INTO usuarios (tipoUser, nombre, apellido, user, dni, nacimiento, domicilio, tel, cel, mail, acuerdo, lista) 
						VALUES ('1', '$nombre', '$apellido', '$user', '$dni', '$nacimiento', '$domicilio', '$tel', '$cel', '$mail', '$acuerdo', '1')";
		if(mysqli_query($conexion, $crearUsuario)) {
			$user=mysqli_insert_id($conexion);
			$contenido.='<div class="col-lg-12 alert alert-success rounded-0 me-3 ms-3">El Usuario se creó correctamente. En caso de necesitar privilegios adicionales, por favor editalos desde <a href="index.php?menu=usuarios&opc=editar&usuario='.$user.'">acá</a>.</div>';
		}
		else { 	$contenido.='<div class="col-lg-12 alert alert-danger rounded-0 me-3 ms-3">Ocurrió un error, la consulta ejecutada fue: '.$crearUsuario.'</div>'; }
	}
	else {
		$contenido.='
	<form class="row" method="get" name="usuarios" id="usuarios" action="" autocomplete="off">
		<input type="hidden" name="menu" value="usuarios">
		<input type="hidden" name="opc" value="nuevo">

		<div class="col-lg-12">
			<input style="text-align:center;" disabled type="text" id="nombre" name="nombre" value="Creando Usuario Nuevo > Datos del Cliente" class="form-control rounded-0 mb-3">
		</div>
		<label for="nombre" class="form-label col-lg-2 mb-3">Nombre</label>
		<div class="col-lg-4 mb-3">
			<input type="text" id="nombre" name="nombre" class="form-control rounded-0" autofocus required>
		</div>
		<label for="apellido" class="control-label col-lg-2 mb-3">Apellido</label>
		<div class="col-lg-4 mb-3">
			<input type="text" id="apellido" name="apellido" class="form-control rounded-0" required>
		</div>
		<label for="dni" class="form-label col-lg-2 mb-3">DNI</label>
		<div class="col-lg-4 mb-3">
			<input type="text" id="dni" name="dni" class="form-control rounded-0" required>
		</div>
		<label for="nacimiento" class="form-label col-lg-2 mb-3">Fecha Nacimiento</label>
		<div class="col-lg-4 mb-3">
			<input type="date" id="nacimiento" name="nacimiento" class="form-control rounded-0" required>
		</div>
		<label for="domicilio" class="form-label col-lg-2 mb-3">Domicilio</label>
		<div class="col-lg-4 mb-3">
			<input type="text" id="domicilio" name="domicilio" class="form-control rounded-0" required>
		</div>

		<label for="tel" class="form-label col-lg-2 mb-3">Telefono</label>
		<div class="col-lg-4 mb-3">
			<input type="text" id="tel" name="tel" class="form-control rounded-0">
		</div>
		
		<label for="cel" class="form-label col-lg-2 mb-3">Celular</label>
		<div class="col-lg-4 mb-3">
			<input type="text" id="cel" name="cel" class="form-control rounded-0">
		</div>

		<label for="mail" class="form-label col-lg-2 mb-3">E-mail</label>
		<div class="col-lg-4 mb-3">
			<input type="email" id="mail" name="mail"class="form-control rounded-0" required>
		</div>

		<label for="acuerdo" class="form-label col-lg-2 mb-3">Acuerdo</label>
		<div class="col-lg-4 mb-3">
			<input type="number" step="0.01" id="acuerdo" name="acuerdo"class="form-control rounded-0" required>
		</div>
			
		<div class="col-lg-6">
			<input type="submit" id="crear" name="crear" value="Crear Usuario" class="form-control btn btn-success rounded-0">
		</div>
	</form>';
	}