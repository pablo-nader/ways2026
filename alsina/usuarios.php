<?php
	$autofocus1='autofocus';
	$subtitulo='';
	$contenido='';
	$mensaje='';

	switch(@$_GET['opc']) {
		case 'nuevo': require_once 'modulos/usuarios/nuevo.php'; 
			break;
		case 'editar': require_once 'modulos/usuarios/editar.php'; 
			break;
		case 'cc': require_once 'modulos/usuarios/cuenta-corriente.php'; 
			break;
		default: require_once 'modulos/usuarios/index.php';
	}
?>
<div class="box">
	<header>
		<div class="icons iconsW">
			<a title="Ver Usuarios" style="color:#333;" class="btn-lg" href="index.php?menu=usuarios">
				<i class="fa fa-search"></i>
				<span class="menuW">Ver Usuarios</span>
		</div>
		<div class="icons iconsW">
			<a title="Ver Cuentas Corriente" style="color:#333;" data-bs-toggle="modal" class="btn-lg" href="#" data-bs-target="#buscarCliente">
				<i class="fab fa-creative-commons"></i>
				<span class="menuW">Cuenta Corriente</span>
			</a>
		</div>		
		<div class="icons iconsW" style="width:86px;">
			<a title="Crear Usuario" style="color:#333;" class="btn-lg" href="index.php?menu=usuarios&opc=nuevo">
				<i class="fa fa-user-plus"></i>
				<span class="menuW">Crear Usuario</span>
			</a>
		</div>		
	</header>
	<div class="body" style="min-height:400px;">
		<div class="row">
			<?php echo $contenido; ?>
		</div>
	</div>
</div>

<div id="buscarCliente" class="modal fade" data-bs-backdrop="static" tabindex="-1" aria-labelledby="exampleModalLabel" aria-hidden="true">
	<div class="modal-dialog modal-xl">
		<div class="modal-content rounded-0">
			<div class="modal-header">
				<h5 class="modal-title" id="exampleModalLabel">Ingrese el nombre o número de Cliente:</h5>
				<button type="button" class="btn-close rounded-0" data-bs-dismiss="modal" aria-label="Close"></button>
			</div>
			<div class="modal-body">
				<form class="row" name="codigo" id="codigo" method="get" action="" autocomplete="off">
					<div class="col-lg-12">	
						<input type="hidden" id="menu" name="menu" value="usuarios">
						<input type="hidden" id="opc" name="opc" value="cc">
						<input type="text" id="buscarClientes" name="buscarClientes" class="form-control mb-3 rounded-0" onKeyUp="mostrarClientesCC();">
					</div>
				</form>
			</div>
			<div class="modal-footer">
				<div class="col-lg-12" id="mostrarClientes"></div>
			</div>
		</div>
	</div>	
</div>

<div id="ingresarPago" class="modal fade" data-bs-backdrop="static" tabindex="-1" aria-labelledby="exampleModalLabel" aria-hidden="true">
	<div class="modal-dialog modal-xl">
		<div class="modal-content rounded-0">
			<div class="modal-header">
				<h5 class="modal-title">Cargar Pago > Ingresar el monto:</h5>
				<button type="button" class="btn-close rounded-0" data-bs-dismiss="modal" aria-label="Close"></button>
			</div>
			<div class="modal-body">
				<form class="row" name="pago" id="pago" method="get" action="" autocomplete="off">	
					<div class="col-lg-2"></div>
					<div class="col-lg-4">
						<input type="hidden" id="menu" name="menu" value="usuarios" class="form-control">
						<input type="hidden" id="opc" name="opc" value="cc" class="form-control">
						<input type="hidden" id="usuario" name="usuario" value="<?php echo @$id; ?>" class="form-control">
						<input type="number" step="0.25" id="efectivo" name="efectivo" value="" class="form-control rounded-0">
					</div>
					<div class="col-lg-4">
						<input type="number" step="0.01" id="tarjetas" name="tarjetas" value="" class="form-control rounded-0">
					</div>
					<div class="col-lg-1">
						<input type="submit" id="pago" name="pago" value="Cargar" class="btn btn-success rounded-0">
					</div>
					<div class="col-lg-1"></div>

					<div class="col-lg-2"></div>
					<div class="col-lg-4" style="text-align:center;">
						<strong>Efectivo</strong>
					</div>
					<div class="col-lg-4" style="text-align:center;">
						<strong>Tarjetas</strong>
					</div>
					<div class="col-lg-1"></div>
					<div class="col-lg-1"></div>
				</form>
			</div>
		</div>
	</div>
</div>

<div id="ajustePersonalizado" class="modal fade" data-bs-backdrop="static" tabindex="-1" aria-labelledby="exampleModalLabel" aria-hidden="true">
	<div class="modal-dialog modal-xl">
		<div class="modal-content rounded-0">
			<div class="modal-header">
				<h5 class="modal-title">Ajuste > Ingresar detalle:</h5>
				<button type="button" class="btn-close rounded-0" data-bs-dismiss="modal" aria-label="Close"></button>
			</div>
			<div class="modal-body">
				<form class="row" name="pago" id="pago" method="get" action="" autocomplete="off">	
					<div class="col-lg-2"></div>
					<div class="col-lg-4">
						<input type="hidden" id="menu" name="menu" value="usuarios" class="form-control">
						<input type="hidden" id="opc" name="opc" value="cc" class="form-control">
						<input type="hidden" id="usuario" name="usuario" value="<?php echo @$id; ?>" class="form-control">
						<input type="text" id="detalle" name="detalle" value="" class="form-control rounded-0">
					</div>
					<div class="col-lg-4">
						<input type="number" step="0.01" id="importe" name="importe" value="" class="form-control rounded-0">
					</div>
					<div class="col-lg-1">
						<input type="submit" id="ajuste" name="ajuste" value="Cargar" class="btn btn-success rounded-0">
					</div>
					<div class="col-lg-1"></div>

					<div class="col-lg-2"></div>
					<div class="col-lg-4" style="text-align:center;">
						<strong>Detalle</strong>
					</div>
					<div class="col-lg-4" style="text-align:center;">
						<strong>Importe</strong>
					</div>
					<div class="col-lg-1"></div>
					<div class="col-lg-1"></div>
				</form>
			</div>
		</div>
	</div>
</div>

<div id="actualizarPrecios" class="modal fade" data-bs-backdrop="static" tabindex="-1" aria-labelledby="exampleModalLabel" aria-hidden="true">
	<div class="modal-dialog modal-lg">
		<div class="modal-content rounded-0">
			<div class="modal-header">
				<h5 class="modal-title">¡¡ Se actualizaran los precios de la cuenta corriente al dia de la fecha !!</h5>
				<button type="button" class="btn-close rounded-0" data-bs-dismiss="modal" aria-label="Close"></button>
			</div>
			<div class="modal-body">
				<form class="row" name="precios" id="precios" method="get" action="" autocomplete="off">	
					<div class="col-lg-2"></div>
					<div class="col-lg-4">
					<input type="hidden" id="menu" name="menu" value="usuarios" class="form-control">
							<input type="hidden" id="opc" name="opc" value="cc" class="form-control">
							<input type="hidden" id="usuario" name="usuario" value="<?php echo @$id; ?>" class="form-control">
							<input type="submit" id="actualizar" name="actualizar" value="Actualizar" class="form-control btn btn-lg btn-success rounded-0">
							
					</div>
					<div class="col-lg-4">
						<input type="submit" id="actualizar" name="actualizar" value="Cancelar" class="form-control btn btn-lg btn-dark rounded-0">
					</div>
					<div class="col-lg-2"></div>
				</form>
			</div>
		</div>
	</div>
</div>