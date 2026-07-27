<?php
if (isset($_POST['abrirCaja'])) {
	if($_POST['abrirCaja']=='Abrir Caja') {
		echo "&nbsp;
			<script> 
				document.body.style.display = 'none';
				window.print();
				window.location = '".$origen."';
			</script>";
	}
}	

if (isset($_GET['menu'])) {
	if ($_GET['menu']=='facturacion') { 
		$facturacionAct='active'; 
		if (isset($_GET['opc'])) {
			if ($_GET['opc']=='ventas') { 
				if(isset($_POST['accion']) && $_POST['accion']=='Siguiente (F9)') {
					$onload=' onload="foco2()"'; 
				}
				else {
					$onload=' onload="foco()"'; 
				}
			}
		}
	}
	elseif ($_GET['menu']=='proveedores') { $proveedoresAct='active'; }
	elseif ($_GET['menu']=='articulos') { $articulosAct='active'; }
	elseif ($_GET['menu']=='usuarios') { $usuariosAct='active'; }
	elseif ($_GET['menu']=='estadisticas') { $estadisticasAct='active'; }
	elseif ($_GET['menu']=='sistema') { $sistemaAct='active'; }
	else { }
}
?>
<body>
	<div class="bg-dark dk" id="wrap">
		<div id="top">
			<nav class="ways-nav color_<?php echo $_SESSION['login']['punto_venta']['id']; ?>">
				
			</nav>
			<nav class="navbar navbar-dark navbar-expand-lg bg-dark border-top-0" >
				<div class="container">
					<a class="navbar-brand ways-brand" href="index.php">Ways</a>
					<button class="navbar-toggler" type="button" data-bs-toggle="collapse" data-bs-target="#navbarNavDropdown" aria-controls="navbarNavDropdown" aria-expanded="false" aria-label="Toggle navigation">
						<span class="navbar-toggler-icon"></span>
					</button>
					<div class="collapse navbar-collapse" id="navbarNavDropdown">
						<ul class="navbar-nav">
							<li class="nav-item dropdown">
								<a class="nav-link dropdown-toggle" href="#" id="navbarDropdownMenuLink" role="button" data-bs-toggle="dropdown" aria-expanded="false">
								Facturación
								</a>
								<ul class="dropdown-menu rounded-0" aria-labelledby="navbarDropdownMenuLink">
									<li><a class="dropdown-item" href="index.php?menu=facturacion&opc=ventas">Ventas / Devoluciones</a></li>
									<li><a class="dropdown-item" href="index.php?menu=facturacion&opc=gastos">Compras / Pagos</a></li>
									<li><a class="dropdown-item" href="index.php?menu=facturacion&opc=caja">Tickets / Caja</a></li>
								</ul>
							</li>
							<li class="nav-item">
								<a class="nav-link active" aria-current="page" href="index.php?menu=articulos">Artículos</a>
							</li>
							<li class="nav-item">
								<a class="nav-link" href="index.php?menu=usuarios">Usuarios</a>
							</li>
							<li class="nav-item">
								<a class="nav-link" href="index.php?menu=estadisticas">Estadísticas</a>
							</li>
						</ul>
						
					</div>
					<form class="d-flex" role="search">
						<a class="btn btn-danger rounded-0" href="logout.php" title="salir"><i class="fa fa-power-off"></i></a>
					</form>
				</div>
			</nav>	
			<?php
			if(isset($_GET['menu']) && $_GET['menu']=='facturacion' && $_GET['opc']=='ventas' && isset($_SESSION['cliente']) && $_SESSION['cliente']['id'] != 1) { 
			echo '				
			<nav class="navbar navbar-inverse ps-3 pe-3" style="display:inherit">	
				<div class="row">
					<div class="col-lg-3 col-sm-6 col-xs-6">
						<div class="input-group">
							<input readonly name="cliente" type="text" class="form-control rounded-0" aria-describedby="button-addon2" value="'.$_SESSION['cliente']['cliente'].'">
							<button class="btn btn-outline-light rounded-0" type="button" id="button-addon2" data-bs-toggle="modal" data-bs-target="#buscarCliente"><i class="fa fa-search"></i></button>
						</div>
					</div> 
					<div class="col-lg-3 col-sm-6 col-xs-6">
						<div class="input-group">
							<input readonly name="direccion" class="form-control rounded-0" type="text" value="'.$_SESSION['cliente']['direccion'].'">
							<span class="input-group-text rounded-0"><i class="fas fa-home"></i></span>
						</div> 
					</div>
					<div class="col-lg-2 col-sm-4 col-xs-4">
						<div class="input-group">
							<input readonly name="tel" class="form-control rounded-0" type="text" value="'.$_SESSION['cliente']['tel'].'">
							<span class="input-group-text rounded-0"><i class="fas fa-phone"></i></span>
						</div> 
					</div>
					<div class="col-lg-2 col-sm-4 col-xs-4">
						<div class="input-group">
							<input readonly name="acuerdo" class="form-control rounded-0" type="text" value="'.$_SESSION['cliente']['acuerdo'].'">
							<span class="input-group-text rounded-0">$</span>
						</div> 
					</div> 
					<div class="col-lg-2 col-sm-4 col-xs-4">
						<div class="input-group">
							<input readonly name="saldo" class="form-control rounded-0" type="text" value="'.$_SESSION['cliente']['saldo'].'">
							<span class="input-group-text rounded-0">$</span>
						</div> 
					</div> 
				</div>
			</nav>
			';
			}					
			?>
		</div>	
		<div id="content">
			<div class="outer">
				<div class="inner bg-light lter">
				<?php
					switch(@$_GET['menu']) {
						case 'facturacion': 
							require_once 'facturacion.php';
							break;
						case 'articulos':
							require_once 'articulos.php';
							break;
						case 'usuarios': 
							require_once 'usuarios.php';
							break;
						case 'actualizar':
							require_once 'actualizar.php';
							break;
						case 'estadisticas': 
							require_once 'estadisticas.php';
							break;
						case 'sistema':
							require_once 'sistema.php';
							break;
						default:
							require_once 'default.php';
							break;
					}
				?>
				</div>
			</div>
		</div>
	</div>
	<footer class="Footer bg-dark dker">
		<p><?php echo $_SESSION['login']['punto_venta']['nombre'] . " :: " . 
			       $_SESSION['login']['punto_venta']['domicilio']; ?></p>
	</footer>

	<div id="abrirCaja" class="modal fade" data-bs-backdrop="static" tabindex="-1" aria-labelledby="exampleModalLabel" aria-hidden="true">
		<div class="modal-dialog modal-l">
			<div class="modal-content rounded-0">
				<div class="modal-header">
					<h5 class="modal-title" id="exampleModalLabel">Razon para abrir la Caja:</h5>
					<button type="button" class="btn-close rounded-0" data-bs-dismiss="modal" aria-label="Close"></button>
				</div>
				<div class="modal-body">
					<form class="row" method="post" action="<?php echo $_SERVER['REQUEST_URI']; ?>" autocomplete="off">
						<div class="col-lg-12">	
							<input type="text" id="detalle" name="detalle" class="form-control mb-3 rounded-0" required minlength="5">
						</div>
						<div class="modal-footer">
							<input name="abrirCaja" type="submit" class="form-control btn btn-success rounded-0" value="Abrir Caja">
						</div>
					</form>
				</div>
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
							<input type="hidden" id="menu" name="menu" value="facturacion">
							<input type="hidden" id="opc" name="opc" value="ventas">
							<input type="text" id="buscarClientes" name="buscarClientes" class="form-control mb-3 rounded-0" onKeyUp="mostrarClientes();">
						</div>
					</form>
				</div>
				<div class="modal-footer">
					<div class="col-lg-12" id="mostrarClientes"></div>
				</div>
			</div>
		</div>	
	</div>

	<script src="https://cdn.jsdelivr.net/npm/bootstrap@5.2.0-beta1/dist/js/bootstrap.bundle.min.js" integrity="sha384-pprn3073KE6tl6bjs2QrFaJGz5/SUsLqktiwsUTF55Jfv3qYSDhgCecCxMW52nD2" crossorigin="anonymous"></script>	
</body>