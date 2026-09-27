using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using RedAJP.Services;
using Stripe;
using Stripe.Checkout;
using System.IO;
using System.Security.Claims;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

namespace RedAJP.Controllers
{
    public class TiendaController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly IConfiguration _configuration; // Necesario para pasar al cálculo de comisiones
        private Parametros.Modulo Modulo = Parametros.Modulos.Tienda;
        private readonly Cloudinary _cloudinary;

        public TiendaController(IConfiguration configuration, IWebHostEnvironment env)
        {
            _configuration = configuration;
            _cadenaConexion = _configuration.GetConnectionString("MiConexion");
            StripeConfiguration.ApiKey = _configuration["StripeTienda:SecretKey"];

            // NUEVO: Configuración Cloudinary
            CloudinaryDotNet.Account account = new CloudinaryDotNet.Account(
                _configuration["Cloudinary:CloudName"],
                _configuration["Cloudinary:ApiKey"],
                _configuration["Cloudinary:ApiSecret"]
            );
            _cloudinary = new Cloudinary(account);
            _cloudinary.Api.Secure = true;
        }

        // =========================================================
        // 1. CATÁLOGO (PÚBLICO)
        // =========================================================
        // =========================================================
        // 1. CATÁLOGO (PÚBLICO)
        // =========================================================
        public async Task<IActionResult> Index(int? cat, [FromServices] ISolicitudesService solicitudesService)
        {
            if (!await Funciones.EsModuloActivo(_cadenaConexion, Modulo))
                return RedirigirAtras("El módulo de Tienda se encuentra temporalmente inhabilitado.");

            // 1. Total de publicaciones (es público, todos lo ven)
            ViewBag.TotalPubsTianguis = await solicitudesService.ObtenerContadorTianguisPublicacionesAsync();

            // 2. Alertas personales (Protegido: solo si el usuario tiene sesión)
            if (User.Identity != null && User.Identity.IsAuthenticated)
            {
                int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                ViewBag.AlertasTianguis = await solicitudesService.ObtenerContadorTianguisAlertasAsync(idUser);
            }
            else
            {
                ViewBag.AlertasTianguis = 0;
            }

            var modelo = new CatalogoViewModel { CategoriaActual = cat };
            var productosTianguis = new List<dynamic>(); // <--- NUEVA LISTA PARA TIANGUIS

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // --- CONSULTA 1: TIENDA OFICIAL (Intacta) ---
                    using (var cmd = new NpgsqlCommand("SELECT \"Id_Categoria\", \"Nombre\", \"Icono\" FROM \"Tienda_Cat_Categorias\" WHERE \"Activo\"=TRUE ORDER BY \"Nombre\"", conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                            modelo.Categorias.Add(new { Id = (int)r["Id_Categoria"], Nombre = r["Nombre"].ToString(), Icono = r["Icono"].ToString() });
                    }

                    string sql = @"SELECT p.*, c.""Nombre"" as ""NomCat"",
                   m.""UrlImagen"" as ""ImgUrl"",
                   COALESCE((SELECT SUM(""Stock"") FROM ""Tienda_Productos_Medidas"" pm WHERE pm.""Id_Producto"" = p.""Id_Producto""), 0) as ""StockTotal""
                   FROM ""Tienda_Productos_Venta"" p
                   INNER JOIN ""Tienda_Cat_Categorias"" c ON p.""Id_Categoria"" = c.""Id_Categoria""
                   LEFT JOIN ""Tienda_Multimedia_Productos"" m ON p.""Id_Producto"" = m.""Id_Producto"" AND m.""Es_Principal"" = TRUE
                   WHERE p.""Activo"" = TRUE";

                    if (cat.HasValue) sql += " AND p.\"Id_Categoria\" = @idCat";
                    sql += " AND (SELECT SUM(\"Stock\") FROM \"Tienda_Productos_Medidas\" pm WHERE pm.\"Id_Producto\" = p.\"Id_Producto\") > 0";
                    sql += " ORDER BY p.\"Nombre_Comercial\" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        if (cat.HasValue) cmd.Parameters.AddWithValue("@idCat", cat.Value);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.Productos.Add(new ProductoFrontItem
                                {
                                    Id_Producto = (int)r["Id_Producto"],
                                    Nombre = r["Nombre_Comercial"].ToString(),
                                    Categoria = r["NomCat"].ToString(),
                                    Precio = (decimal)r["Precio_Venta"],
                                    Es_Personalizable = (bool)r["Es_Personalizable"],
                                    Stock = Convert.ToInt32(r["StockTotal"]),
                                    ImagenUrl = r["ImgUrl"]?.ToString()
                                });
                            }
                        }
                    }

                    // --- CONSULTA 2: TIANGUIS (El nuevo Merge) ---
                    // Solo traemos artículos activos, ordenados por los más recientes.
                    string sqlTianguis = @"
                SELECT p.""IdPublicacion"", p.""Titulo"", p.""PrecioBase"", p.""IdTipoVenta"", c.""Nombre"" as ""NomCat"",
                       (SELECT MAX(""OfertaActual"") FROM ""Tianguis_Interesados"" WHERE ""IdPublicacion"" = p.""IdPublicacion"") as ""MejorOferta"",
                       (SELECT ""UrlImagen"" FROM ""Tianguis_Imagenes"" WHERE ""IdPublicacion"" = p.""IdPublicacion"" ORDER BY ""Orden"" ASC LIMIT 1) as ""ImgUrl""
                FROM ""Tianguis_Publicaciones"" p
                JOIN ""Tianguis_Categorias"" c ON p.""IdCategoria"" = c.""IdCategoria""
                WHERE p.""IdEstado"" IN ('ACT', 'RES') AND p.""Activo"" = TRUE
                ORDER BY p.""FechaPublicacion"" DESC LIMIT 12"; // Limité a 12 para que no abrume la tienda oficial

                    using (var cmdT = new NpgsqlCommand(sqlTianguis, conexion))
                    {
                        using (var rT = await cmdT.ExecuteReaderAsync())
                        {
                            while (await rT.ReadAsync())
                            {
                                decimal precioBase = (decimal)rT["PrecioBase"];
                                decimal mejorOferta = rT["MejorOferta"] != DBNull.Value ? (decimal)rT["MejorOferta"] : 0;

                                productosTianguis.Add(new
                                {
                                    sId = Funciones.EncriptarId((int)rT["IdPublicacion"]), // Clave para el controlador de Tianguis
                                    Nombre = rT["Titulo"].ToString(),
                                    Categoria = rT["NomCat"].ToString(),
                                    Precio = (rT["IdTipoVenta"].ToString() == "SUB" && mejorOferta > precioBase) ? mejorOferta : precioBase,
                                    ImagenUrl = rT["ImgUrl"]?.ToString() ?? "/Images/default-tianguis.jpg",
                                    TipoVenta = rT["IdTipoVenta"].ToString()
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            // Pasamos los productos de Tianguis a la vista
            ViewBag.ProductosTianguis = productosTianguis;

            return View(modelo);
        }

        // =========================================================
        // 2. DETALLE DE PRODUCTO (PÚBLICO)
        // =========================================================
        public async Task<IActionResult> Detalle(int id, int? idDiseno = null)
        {
            var modelo = new DetalleProductoViewModel { Id_Producto = id };
            if (idDiseno.HasValue) modelo.Id_Solicitud_Diseno = idDiseno.Value;

            bool esAdmin = User.TienePermiso(Parametros.Modulos.TiendaConfig, Parametros.Permisos.Editar);
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Datos Generales (Ya no leemos Stock_Tienda porque no existe)
                    string sql = @"SELECT p.*, m.""UrlImagen"" as ""ImgUrl""
                           FROM ""Tienda_Productos_Venta"" p
                           LEFT JOIN ""Tienda_Multimedia_Productos"" m ON p.""Id_Producto"" = m.""Id_Producto"" AND m.""Es_Principal"" = TRUE
                           WHERE p.""Id_Producto"" = @id";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.Nombre = r["Nombre_Comercial"].ToString();
                                modelo.Descripcion = r["Descripcion"]?.ToString();
                                modelo.Precio = (decimal)r["Precio_Venta"];
                                modelo.Es_Personalizable = (bool)r["Es_Personalizable"];
                                modelo.ImagenUrl = r["ImgUrl"]?.ToString();
                            }
                            else return RedirectToAction("Index");
                        }
                    }

                    // 2. Diseño Personalizado (Igual)
                    if (idDiseno.HasValue && User.Identity.IsAuthenticated)
                    {
                        var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                        string sqlDiseno = @"SELECT ""Imagen_Previo_Url"" FROM ""Tienda_Solicitudes_Diseno"" WHERE ""Id_Solicitud"" = @ids AND ""Id_Usuario"" = @uid AND ""Id_Estatus_Diseno"" = 2";
                        using (var cmd = new NpgsqlCommand(sqlDiseno, conexion))
                        {
                            cmd.Parameters.AddWithValue("@ids", idDiseno.Value);
                            cmd.Parameters.AddWithValue("@uid", idUser);
                            var imgCustom = await cmd.ExecuteScalarAsync();
                            if (imgCustom != null)
                            {
                                modelo.ImagenUrl = imgCustom.ToString();
                                modelo.Es_Personalizable = false;
                                modelo.Nombre += " (Tu Diseño)";
                            }
                        }
                    }

                    // 3. Tallas y Stock (Aquí obtenemos la verdad absoluta)
                    string sqlTallas = @"SELECT ""Nombre"", ""Stock"" FROM ""Tienda_Productos_Medidas"" WHERE ""Id_Producto"" = @id ORDER BY ""Id_Medida"" ASC";
                    using (var cmd = new NpgsqlCommand(sqlTallas, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int s = (int)r["Stock"];
                                modelo.TallasInfo.Add(new TallaInfo { Nombre = r["Nombre"].ToString(), Stock = s });
                                modelo.Stock += s; // Sumamos para el total visual
                            }
                        }
                    }

                    // 3.5 Galería de Imágenes Adicionales
                    string sqlGaleria = @"SELECT ""UrlImagen"" as ""ImgUrl"" 
                                  FROM ""Tienda_Multimedia_Productos"" 
                                  WHERE ""Id_Producto"" = @id AND ""Es_Principal"" = FALSE 
                                  ORDER BY ""Id_Imagen"" ASC";

                    using (var cmdG = new NpgsqlCommand(sqlGaleria, conexion))
                    {
                        cmdG.Parameters.AddWithValue("@id", id);
                        using (var rG = await cmdG.ExecuteReaderAsync())
                        {
                            while (await rG.ReadAsync())
                            {
                                modelo.GaleriaUrls.Add(rG["ImgUrl"].ToString());
                            }
                        }
                    }

                    //4. Preguntas y respuestas
                    string sqlPreguntas = @"
                        SELECT p.""Id_Pregunta"", p.""Id_Usuario"", p.""Mensaje"", p.""Fecha_Registro"", 
                               p.""Id_Pregunta_Padre"", p.""Es_Admin"", u.""NombreCompleto""
                        FROM ""Tienda_Producto_Preguntas"" p
                        LEFT JOIN ""Sist_Usuarios"" u ON p.""Id_Usuario"" = u.""Id_Usuario""
                        WHERE p.""Id_Producto"" = @id
                        ORDER BY p.""Fecha_Registro"" ASC";

                    using (var cmdP = new NpgsqlCommand(sqlPreguntas, conexion))
                    {
                        cmdP.Parameters.AddWithValue("@id", id);
                        var todasLasInteracciones = new List<dynamic>();

                        var dicUsuariosAliases = new Dictionary<int, string>();
                        int contadorUsuarios = 1;

                        using (var rP = await cmdP.ExecuteReaderAsync())
                        {
                            while (await rP.ReadAsync())
                            {
                                int idUserIteracion = (int)rP["Id_Usuario"];
                                bool esAdminInteracion = (bool)rP["Es_Admin"];
                                // Extraemos el nombre real de la base de datos
                                string nombreReal = rP["NombreCompleto"]?.ToString() ?? "Administrador";

                                string aliasAsignado = "";

                                // LÓGICA DE NOMBRES:
                                if (esAdminInteracion)
                                {
                                    if (esAdmin)
                                    {
                                        // Si es admin, mostramos su nombre real. 
                                        aliasAsignado = ": " + nombreReal.Split(' ')[0];
                                    }
                                    else
                                    {
                                        aliasAsignado = "";
                                    }
                                }
                                else
                                {
                                    // Si es cliente, validamos el diccionario para mantener su anonimato
                                    if (!dicUsuariosAliases.ContainsKey(idUserIteracion))
                                    {
                                        dicUsuariosAliases.Add(idUserIteracion, $"Usuario {contadorUsuarios}");
                                        contadorUsuarios++;
                                    }
                                    aliasAsignado = dicUsuariosAliases[idUserIteracion];
                                }

                                todasLasInteracciones.Add(new
                                {
                                    Id = (int)rP["Id_Pregunta"],
                                    IdUsuario = idUserIteracion,
                                    Mensaje = rP["Mensaje"].ToString(),
                                    Fecha = (DateTime)rP["Fecha_Registro"],
                                    IdPadre = rP["Id_Pregunta_Padre"] as int?,
                                    EsAdmin = esAdminInteracion,
                                    Alias = aliasAsignado
                                });
                            }
                        }

                        int currentUserId = User.Identity != null && User.Identity.IsAuthenticated ? int.Parse(User.FindFirst("IdUsuario").Value) : 0;

                        // Procesar Padres (Preguntas principales)
                        var padres = todasLasInteracciones.Where(x => x.IdPadre == null).ToList();
                        foreach (var p in padres)
                        {
                            var hilo = new HiloPreguntaViewModel
                            {
                                Id_Pregunta = p.Id,
                                Mensaje = p.Mensaje,
                                Fecha = p.Fecha,
                                Es_Propia = p.IdUsuario == currentUserId,
                                NombreUsuario = p.Alias // Pasamos el alias al ViewModel
                            };

                            // Procesar Hijos (Respuestas a este hilo)
                            var hijos = todasLasInteracciones.Where(x => x.IdPadre == p.Id).ToList();
                            foreach (var h in hijos)
                            {
                                hilo.Respuestas.Add(new RespuestaViewModel
                                {
                                    Mensaje = h.Mensaje,
                                    Fecha = h.Fecha,
                                    Es_Admin = h.EsAdmin,
                                    NombreUsuario = h.Alias // Pasamos el alias a la respuesta
                                });
                            }
                            modelo.Preguntas.Add(hilo);
                        }

                        modelo.Preguntas.Reverse();
                    }
                }
            }
            catch { return RedirectToAction("Index"); }
            return View(modelo);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AgregarPregunta(int idProducto, string mensaje, int? idPreguntaPadre)
        {
            if (string.IsNullOrWhiteSpace(mensaje)) {
                MostrarMensaje("Error", "No se recibió la pregunta", TipoMensaje.Error);
                return RedirectToAction("Detalle", new { id = idProducto }); 
            }

            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            bool esAdmin = User.TienePermiso(Parametros.Modulos.TiendaConfig, Parametros.Permisos.Editar);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. VALIDACIÓN: Comprobar que el producto exista y esté activo
                    string sqlCheck = @"SELECT ""Activo"" FROM ""Tienda_Productos_Venta"" WHERE ""Id_Producto"" = @prod";
                    using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@prod", idProducto);
                        var resultado = await cmdCheck.ExecuteScalarAsync();

                        if (resultado == null || !(bool)resultado)
                        {
                            MostrarMensaje("Acción no permitida", "Este producto ya no se encuentra activo o disponible.", TipoMensaje.Alerta);
                            return RedirectToAction("Index"); // Lo mandamos al catálogo si el producto ya no existe/está inactivo
                        }
                    }

                    // 2. Insertar el comentario si pasó la validación
                    string sql = @"INSERT INTO ""Tienda_Producto_Preguntas"" 
                          (""Id_Producto"", ""Id_Usuario"", ""Mensaje"", ""Id_Pregunta_Padre"", ""Es_Admin"")
                          VALUES (@prod, @uid, @msj, @padre, @admin)";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@prod", idProducto);
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        cmd.Parameters.AddWithValue("@msj", mensaje.Trim());
                        cmd.Parameters.AddWithValue("@padre", (object)idPreguntaPadre ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@admin", esAdmin);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                MostrarMensaje("Enviado", "Tu comentario ha sido publicado.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo publicar el comentario.", TipoMensaje.Error);
            }

            return RedirectToAction("Detalle", new { id = idProducto });
        }

        // =========================================================
        // 3. AGREGAR AL CARRITO (A TABLA Tienda_Carrito)
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AgregarCarrito(DetalleProductoViewModel form)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // VALIDACIÓN ESTRICTA: Consultamos el stock exacto de la talla solicitada
                    // Si el usuario inyecta una talla que no existe o manipula el HTML, esto devolverá 0 o null.
                    string sqlCheck = @"SELECT ""Stock"" FROM ""Tienda_Productos_Medidas"" 
                                        WHERE ""Id_Producto"" = @id AND ""Nombre"" = @talla";

                    int stockDisponible = 0;
                    using (var cmd = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", form.Id_Producto);
                        cmd.Parameters.AddWithValue("@talla", form.TallaSeleccionada ?? "Unitalla"); // Fallback seguro

                        object result = await cmd.ExecuteScalarAsync();
                        if (result != null) stockDisponible = Convert.ToInt32(result);
                    }

                    // EL HACKER SE DETIENE AQUÍ:
                    if (stockDisponible < form.Cantidad)
                    {
                        MostrarMensaje("Stock Insuficiente", $"Lo sentimos, la talla '{form.TallaSeleccionada}' ya no tiene suficientes unidades (Quedan: {stockDisponible}).", TipoMensaje.Error);
                        return RedirectToAction("Detalle", new { id = form.Id_Producto });
                    }

                    // Insertar en Tienda_Carrito
                    string instruccion = (string.IsNullOrEmpty(form.TallaSeleccionada) || form.TallaSeleccionada == "Unitalla") ? "" : $"Talla: {form.TallaSeleccionada}";

                    string sqlInsert = @"INSERT INTO ""Tienda_Carrito"" 
                                       (""Id_Usuario"", ""Id_Producto"", ""Cantidad"", ""Instrucciones"", ""Es_Personalizado"", ""Id_Solicitud_Diseno"")
                                       VALUES (@uid, @prod, @cant, @inst, @pers, @idDis)";

                    using (var cmd = new NpgsqlCommand(sqlInsert, conexion))
                    {
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        cmd.Parameters.AddWithValue("@prod", form.Id_Producto);
                        cmd.Parameters.AddWithValue("@cant", form.Cantidad);
                        cmd.Parameters.AddWithValue("@inst", instruccion);
                        cmd.Parameters.AddWithValue("@pers", form.Id_Solicitud_Diseno.HasValue);
                        cmd.Parameters.AddWithValue("@idDis", (object)form.Id_Solicitud_Diseno ?? DBNull.Value);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    MostrarMensaje("Carrito Actualizado", "Producto añadido correctamente.", TipoMensaje.Exito);
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Carrito");
        }

        // =========================================================
        // 4. VER CARRITO
        // =========================================================
        [Authorize]
        public async Task<IActionResult> Carrito()
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            // Si el usuario ya pagó una orden pendiente, esto limpiará su carrito
            // antes de mostrárselo, evitando que compre dos veces lo mismo.
            await VerificarPedidosPendientes();

            var modelo = new CarritoViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Consultar Items (Igual que antes)
                    string sql = @"SELECT c.""Id_Carrito"", c.""Cantidad"", c.""Instrucciones"", 
                                  c.""Es_Personalizado"", c.""Id_Solicitud_Diseno"",
                                  p.""Nombre_Comercial"", p.""Precio_Venta"", p.""Id_Producto"",
                                  m.""UrlImagen"" as ""ImgUrl"",
                                  dis.""Imagen_Previo_Url"" as ""ImgDiseno""
                                   FROM ""Tienda_Carrito"" c
                                   JOIN ""Tienda_Productos_Venta"" p ON c.""Id_Producto"" = p.""Id_Producto""
                                   LEFT JOIN ""Tienda_Multimedia_Productos"" m ON p.""Id_Producto"" = m.""Id_Producto"" AND m.""Es_Principal"" = TRUE
                                   LEFT JOIN ""Tienda_Solicitudes_Diseno"" dis ON c.""Id_Solicitud_Diseno"" = dis.""Id_Solicitud""
                                   WHERE c.""Id_Usuario"" = @uid 
                                   ORDER BY c.""Id_Carrito"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string imgFinal = r["ImgUrl"]?.ToString();
                                if (r["Id_Solicitud_Diseno"] != DBNull.Value && r["ImgDiseno"] != DBNull.Value)
                                    imgFinal = r["ImgDiseno"].ToString();

                                string instrucciones = r["Instrucciones"].ToString();

                                var item = new ItemCarrito
                                {
                                    IdDetalle = (int)r["Id_Carrito"],
                                    IdProducto = (int)r["Id_Producto"],
                                    Nombre = r["Nombre_Comercial"].ToString(),
                                    Cantidad = (int)r["Cantidad"],
                                    Precio = (decimal)r["Precio_Venta"],
                                    EsPersonalizado = (bool)r["Es_Personalizado"],
                                    Instrucciones = instrucciones,
                                    ImagenUrl = imgFinal
                                };
                                modelo.Items.Add(item);
                            }
                        }
                    }

                    // 2. ENRIQUECIMIENTO: Tallas + Stock para el Dropdown
                    foreach (var item in modelo.Items)
                    {
                        string tallaActual = item.TallaActual;

                        // A. Stock de la talla seleccionada actualmente (Para validar max input)
                        string sqlStock = "SELECT \"Stock\" FROM \"Tienda_Productos_Medidas\" WHERE \"Id_Producto\"=@id AND \"Nombre\"=@talla";
                        using (var cmdS = new NpgsqlCommand(sqlStock, conexion))
                        {
                            cmdS.Parameters.AddWithValue("@id", item.IdProducto);
                            cmdS.Parameters.AddWithValue("@talla", string.IsNullOrEmpty(tallaActual) || tallaActual == "N/A" ? "Unitalla" : tallaActual);
                            object res = await cmdS.ExecuteScalarAsync();
                            item.StockMaximo = res == null ? 0 : Convert.ToInt32(res);
                        }

                        // B. CAMBIO: Cargar lista de tallas CON SU STOCK
                        string sqlTallas = "SELECT \"Nombre\", \"Stock\" FROM \"Tienda_Productos_Medidas\" WHERE \"Id_Producto\" = @id ORDER BY \"Id_Medida\" ASC";
                        using (var cmdT = new NpgsqlCommand(sqlTallas, conexion))
                        {
                            cmdT.Parameters.AddWithValue("@id", item.IdProducto);
                            using (var rt = await cmdT.ExecuteReaderAsync())
                            {
                                while (await rt.ReadAsync())
                                {
                                    item.TallasDisponibles.Add(new TallaInfo
                                    {
                                        Nombre = rt["Nombre"].ToString(),
                                        Stock = Convert.ToInt32(rt["Stock"])
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return View(modelo);
        }

        // =========================================================
        // 4.1 ACTUALIZAR ITEM CARRITO 
        // =========================================================
        // =========================================================
        // 4.1 ACTUALIZAR ITEM CARRITO - CON AUTO-AJUSTE DE CANTIDAD
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActualizarItemCarrito(int idDetalle, int nuevaCantidad, string nuevaTalla)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Obtener producto asociado al item
                    int idProducto = 0;
                    using (var cmd = new NpgsqlCommand("SELECT \"Id_Producto\" FROM \"Tienda_Carrito\" WHERE \"Id_Carrito\"=@id AND \"Id_Usuario\"=@uid", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idDetalle);
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        object res = await cmd.ExecuteScalarAsync();
                        if (res == null) return Json(new { success = false, message = "El artículo ya no existe en tu carrito." });
                        idProducto = (int)res;
                    }

                    // 2. Verificar Stock de la TALLA SOLICITADA
                    string nombreTallaCheck = (string.IsNullOrEmpty(nuevaTalla) || nuevaTalla == "N/A") ? "Unitalla" : nuevaTalla;

                    string sqlStock = "SELECT \"Stock\" FROM \"Tienda_Productos_Medidas\" WHERE \"Id_Producto\"=@id AND \"Nombre\"=@talla";
                    int stockReal = 0;

                    using (var cmd = new NpgsqlCommand(sqlStock, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idProducto);
                        cmd.Parameters.AddWithValue("@talla", nombreTallaCheck);
                        object res = await cmd.ExecuteScalarAsync();
                        if (res != null) stockReal = (int)res;
                    }

                    // 3. LÓGICA DE AUTO-AJUSTE (El cambio clave)
                    bool fueAjustado = false;

                    if (stockReal == 0)
                    {
                        // Si de plano no hay nada, no podemos ajustar a 0 (sería ilógico tener item con cantidad 0)
                        // Aquí sí mantenemos el error para que el usuario elija otra talla.
                        return Json(new { success = false, message = $"La talla {nombreTallaCheck} está agotada." });
                    }

                    if (nuevaCantidad > stockReal)
                    {
                        // AQUÍ ESTÁ LA MAGIA: En lugar de error, ajustamos al máximo posible.
                        nuevaCantidad = stockReal;
                        fueAjustado = true;
                    }

                    if (nuevaCantidad < 1)
                    {
                        return Json(new { success = false, message = "La cantidad mínima es 1." });
                    }

                    // 4. Actualizar en BD
                    string instruccionFinal = (nombreTallaCheck == "Unitalla") ? "" : $"Talla: {nombreTallaCheck}";

                    string sqlUpdate = @"UPDATE ""Tienda_Carrito"" SET ""Cantidad"" = @cant, ""Instrucciones"" = @inst WHERE ""Id_Carrito"" = @id";
                    using (var cmd = new NpgsqlCommand(sqlUpdate, conexion))
                    {
                        cmd.Parameters.AddWithValue("@cant", nuevaCantidad);
                        cmd.Parameters.AddWithValue("@inst", instruccionFinal);
                        cmd.Parameters.AddWithValue("@id", idDetalle);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // 5. Notificar al usuario si hubo ajuste
                    if (fueAjustado)
                    {
                        // Usamos la función global. Al recargar la página (success=true), el Layout mostrará este mensaje.
                        MostrarMensaje("Cantidad Ajustada", $"Solo hay {stockReal} unidades disponibles en talla {nombreTallaCheck}. Hemos ajustado tu pedido al máximo disponible.", TipoMensaje.Alerta);
                    }

                    return Json(new { success = true });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error técnico: " + ex.Message });
            }
        }

        // =========================================================
        // 5. ELIMINAR DEL CARRITO
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarItem(int idDetalle)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"DELETE FROM ""Tienda_Carrito"" WHERE ""Id_Carrito"" = @id AND ""Id_Usuario"" = @uid";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idDetalle);
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Carrito");
        }

        // =========================================================
        // 6. CHECKOUT (VISTA) - VALIDACIÓN ESTRICTA (SERVIDOR)
        // =========================================================
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Pagar()
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new CheckoutViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Obtener Items del Carrito (CORREGIDO CON DISEÑO PERSONALIZADO)
                    string sqlItems = @"
                        SELECT c.""Cantidad"", c.""Instrucciones"", c.""Id_Producto"",
                               p.""Nombre_Comercial"", p.""Precio_Venta"",
                               m.""UrlImagen"" as ""ImgUrl"",
                               dis.""Imagen_Previo_Url"" as ""ImgDiseno""
                        FROM ""Tienda_Carrito"" c
                        JOIN ""Tienda_Productos_Venta"" p ON c.""Id_Producto"" = p.""Id_Producto""
                        LEFT JOIN ""Tienda_Multimedia_Productos"" m ON p.""Id_Producto"" = m.""Id_Producto"" AND m.""Es_Principal"" = TRUE
                        LEFT JOIN ""Tienda_Solicitudes_Diseno"" dis ON c.""Id_Solicitud_Diseno"" = dis.""Id_Solicitud""
                        WHERE c.""Id_Usuario"" = @uid";

                    using (var cmd = new NpgsqlCommand(sqlItems, conexion))
                    {
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                // Lógica de selección de imagen
                                string imgFinal = r["ImgUrl"]?.ToString();

                                // Si tiene diseño personalizado, lo usamos (viene en Base64 directo o URL)
                                if (r["ImgDiseno"] != DBNull.Value && !string.IsNullOrEmpty(r["ImgDiseno"].ToString()))
                                {
                                    imgFinal = r["ImgDiseno"].ToString();
                                }

                                var item = new ItemCarrito
                                {
                                    IdProducto = (int)r["Id_Producto"],
                                    Nombre = r["Nombre_Comercial"].ToString(),
                                    Cantidad = (int)r["Cantidad"],
                                    Precio = (decimal)r["Precio_Venta"],
                                    Instrucciones = r["Instrucciones"].ToString(),
                                    ImagenUrl = imgFinal // Ahora sí lleva la personalizada
                                };
                                modelo.ItemsResumen.Add(item);
                            }
                        }
                    }

                    // 2. VALIDACIÓN ATÓMICA DE INTEGRIDAD
                    if (modelo.ItemsResumen.Count == 0)
                    {
                        MostrarMensaje("Carrito Vacío", "No tienes productos para procesar.", TipoMensaje.Alerta);
                        return RedirectToAction("Carrito");
                    }

                    foreach (var item in modelo.ItemsResumen)
                    {
                        // REGLA 1: CANTIDAD MÍNIMA (Corrige el bug de cantidad 0)
                        if (item.Cantidad <= 0)
                        {
                            MostrarMensaje("Cantidad Inválida", $"El producto '{item.Nombre}' tiene cantidad 0. Elimínalo o agrega unidades.", TipoMensaje.Error);
                            return RedirectToAction("Carrito");
                        }

                        // REGLA 2: VALIDAR STOCK REAL EN BD
                        string talla = "Unitalla";
                        if (item.Instrucciones.Contains("Talla: "))
                            talla = item.Instrucciones.Replace("Talla: ", "").Trim();

                        string sqlStock = "SELECT \"Stock\" FROM \"Tienda_Productos_Medidas\" WHERE \"Id_Producto\"=@id AND \"Nombre\"=@talla";
                        int stockReal = 0;

                        using (var cmdS = new NpgsqlCommand(sqlStock, conexion))
                        {
                            cmdS.Parameters.AddWithValue("@id", item.IdProducto);
                            cmdS.Parameters.AddWithValue("@talla", talla);
                            object res = await cmdS.ExecuteScalarAsync();
                            // Si devuelve null, es que la talla ya no existe (borrada/cambiada)
                            stockReal = res != null ? Convert.ToInt32(res) : 0;
                        }

                        if (stockReal < item.Cantidad)
                        {
                            MostrarMensaje("Stock Insuficiente", $"El producto {item.Nombre} ({talla}) ya no tiene stock suficiente (Quedan {stockReal}).", TipoMensaje.Error);
                            return RedirectToAction("Carrito");
                        }

                        // Sumar totales solo si pasó las validaciones
                        modelo.TotalPagar += item.Subtotal;
                        modelo.CantidadArticulos += item.Cantidad;
                    }

                    // REGLA 3: TOTAL POSITIVO
                    if (modelo.TotalPagar <= 0)
                    {
                        MostrarMensaje("Error", "El total de la orden no es válido.", TipoMensaje.Error);
                        return RedirectToAction("Carrito");
                    }

                    // 3. Cargar Datos de Contacto
                    string sqlLast = @"SELECT ""Telefono_Contacto"" FROM ""Tienda_Pedidos"" WHERE ""Id_Usuario_Solicita"" = @uid ORDER BY ""Id_Pedido"" DESC LIMIT 1";
                    using (var cmd = new NpgsqlCommand(sqlLast, conexion))
                    {
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        var res = await cmd.ExecuteScalarAsync();
                        if (res != null && res != DBNull.Value) modelo.TelefonoContacto = res.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error Técnico", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Carrito");
            }

            return View(modelo);
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ValidarCuponTienda(string codigo)
        {
            if (string.IsNullOrWhiteSpace(codigo))
                return Json(new { valido = false, mensaje = "Por favor escribe un código." });

            try
            {
                int idUser = int.Parse(User.FindFirst("IdUsuario").Value);

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. CALCULAR SUBTOTALES SEPARADOS
                    decimal subTotalConComision = 0;
                    decimal subTotalSinComision = 0;

                    // LECTURA DE LA NUEVA COLUMNA: p."Cobrar_Comision_Extra"
                    string sqlItems = @"SELECT c.""Cantidad"", p.""Precio_Venta"", p.""Cobrar_Comision_Extra""
                        FROM ""Tienda_Carrito"" c
                        JOIN ""Tienda_Productos_Venta"" p ON c.""Id_Producto"" = p.""Id_Producto""
                        WHERE c.""Id_Usuario"" = @uid AND p.""Activo"" = TRUE";

                    using (var cmd = new NpgsqlCommand(sqlItems, conexion))
                    {
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                decimal precio = (decimal)r["Precio_Venta"];
                                int cant = (int)r["Cantidad"];
                                bool cobraComision = (bool)r["Cobrar_Comision_Extra"];

                                if (cobraComision) subTotalConComision += (precio * cant);
                                else subTotalSinComision += (precio * cant);
                            }
                        }
                    }

                    decimal subTotalBase = subTotalConComision + subTotalSinComision;

                    if (subTotalBase <= 0)
                        return Json(new { valido = false, mensaje = "Tu carrito está vacío." });

                    // -----------------------------------------------------------------
                    // 3. BUSCAR CUPÓN Y VALIDAR LISTA DE USUARIOS (EL CAMBIO CLAVE)
                    // -----------------------------------------------------------------
                    var datosCupon = new
                    {
                        Id = 0,
                        Tipo = 0,
                        Valor = 0m,
                        Tope = (decimal?)null,
                        Minimo = 0m,
                        Limite = 0,
                        Usados = 0,
                        AplicaTienda = false,
                        // Nuevas propiedades lógicas
                        TieneListaPrivada = false,
                        UsuarioEstaAutorizado = false
                    };

                    string sqlCupon = @"SELECT c.*,
                        (SELECT COUNT(*) FROM ""Sist_Cupones_Usuarios"" u WHERE u.""Id_Cupon"" = c.""Id_Cupon"") as ""TotalEnLista"",
                        (SELECT COUNT(*) FROM ""Sist_Cupones_Usuarios"" u WHERE u.""Id_Cupon"" = c.""Id_Cupon"" AND u.""Id_Usuario"" = @uid) as ""SoyYo""
                        FROM ""Sist_Cupones"" c 
                        WHERE UPPER(c.""Codigo"") = UPPER(@cod) AND c.""Activo"" = TRUE AND NOW() BETWEEN c.""Fecha_Inicio"" AND c.""Fecha_Fin""";

                    using (var cmdC = new NpgsqlCommand(sqlCupon, conexion))
                    {
                        cmdC.Parameters.AddWithValue("@cod", codigo.Trim());
                        cmdC.Parameters.AddWithValue("@uid", idUser);

                        using (var rC = await cmdC.ExecuteReaderAsync())
                        {
                            if (await rC.ReadAsync())
                            {
                                datosCupon = new
                                {
                                    Id = (int)rC["Id_Cupon"],
                                    Tipo = (int)rC["Tipo_Descuento"],
                                    Valor = (decimal)rC["Valor"],
                                    Tope = rC["Tope_Maximo_Descuento"] as decimal?,
                                    Minimo = (decimal)rC["Monto_Minimo_Compra"],
                                    Limite = (int)rC["Limite_Usos"],
                                    Usados = (int)rC["Conteo_Usados"],
                                    AplicaTienda = (bool)rC["Aplica_Tienda"],
                                    TieneListaPrivada = Convert.ToInt64(rC["TotalEnLista"]) > 0,
                                    UsuarioEstaAutorizado = Convert.ToInt64(rC["SoyYo"]) > 0
                                };
                            }
                            else return Json(new { valido = false, mensaje = "El cupón no existe o ha expirado." });
                        }
                    }

                    // 4. VALIDACIONES DE REGLAS
                    if (!datosCupon.AplicaTienda) return Json(new { valido = false, mensaje = "Este cupón no es válido para la Tienda Online." });
                    if (datosCupon.Usados >= datosCupon.Limite) return Json(new { valido = false, mensaje = "Este cupón se ha agotado." });
                    if (subTotalBase < datosCupon.Minimo) return Json(new { valido = false, mensaje = $"La compra mínima para este cupón es de ${datosCupon.Minimo:N2}." });
                    if (datosCupon.TieneListaPrivada && !datosCupon.UsuarioEstaAutorizado) return Json(new { valido = false, mensaje = "Este cupón es exclusivo y tu cuenta no está autorizada para usarlo." });

                    string sqlUso = @"SELECT COUNT(*) FROM ""Sist_Cupones_Uso"" WHERE ""Id_Cupon"" = @idC AND ""Id_Usuario"" = @idU";
                    using (var cmdU = new NpgsqlCommand(sqlUso, conexion))
                    {
                        cmdU.Parameters.AddWithValue("@idC", datosCupon.Id);
                        cmdU.Parameters.AddWithValue("@idU", idUser);
                        if ((long)await cmdU.ExecuteScalarAsync() > 0)
                            return Json(new { valido = false, mensaje = "Ya utilizaste este cupón en una compra anterior." });
                    }

                    // 5. CÁLCULOS FINALES CON SEPARACIÓN DE COMISIÓN
                    decimal descuento = Funciones.CalcularMontoDescuento(subTotalBase, datosCupon.Tipo, datosCupon.Valor, datosCupon.Tope);
                    decimal baseConDescuento = subTotalBase - descuento;
                    if (baseConDescuento < 0) baseConDescuento = 0;

                    // LÓGICA DE COMISIÓN: Restamos al monto comisionable la parte proporcional del descuento que le toca
                    decimal baseComisionable = subTotalConComision;
                    if (descuento > 0 && subTotalBase > 0)
                    {
                        decimal proporcionConComision = subTotalConComision / subTotalBase;
                        baseComisionable = subTotalConComision - (descuento * proporcionConComision);
                    }

                    decimal comision = 0;
                    if (baseComisionable > 0)
                    {
                        decimal totalConComisionParaBase = Funciones.CalcularPagoConComision(baseComisionable, _configuration);
                        comision = totalConComisionParaBase - baseComisionable;
                    }

                    return Json(new
                    {
                        valido = true,
                        mensaje = "¡Cupón aplicado correctamente!",
                        descuento = descuento,
                        nuevoSubtotal = baseConDescuento,
                        comision = comision, // Si el carrito solo tiene productos "sin comisión", devolverá $0
                        nuevoTotal = baseConDescuento + comision
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { valido = false, mensaje = "Error técnico al validar: " + ex.Message });
            }
        }


        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarOrden(CheckoutViewModel form)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string emailUser = User.FindFirst(ClaimTypes.Email)?.Value ?? "";

            try
            {
                var lineItemsStripe = new List<SessionLineItemOptions>();
                int idPedidoGenerado = 0;

                decimal subTotalConComision = 0;
                decimal subTotalSinComision = 0;

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // A. LEER CARRITO (Agregando p."Cobrar_Comision_Extra")
                            var itemsCarrito = new List<dynamic>();
                            string sqlCheck = @"SELECT c.""Cantidad"", p.""Nombre_Comercial"", p.""Precio_Venta"", p.""Id_Producto"", 
                       p.""Cobrar_Comision_Extra"", c.""Instrucciones"", c.""Es_Personalizado"", c.""Id_Solicitud_Diseno""
                FROM ""Tienda_Carrito"" c
                JOIN ""Tienda_Productos_Venta"" p ON c.""Id_Producto"" = p.""Id_Producto""
                WHERE c.""Id_Usuario"" = @uid AND p.""Activo"" = TRUE";

                            using (var cmd = new NpgsqlCommand(sqlCheck, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@uid", idUser);
                                using (var r = await cmd.ExecuteReaderAsync())
                                {
                                    while (await r.ReadAsync())
                                    {
                                        itemsCarrito.Add(new
                                        {
                                            Cantidad = (int)r["Cantidad"],
                                            Nombre = r["Nombre_Comercial"].ToString(),
                                            Precio = (decimal)r["Precio_Venta"],
                                            IdProd = (int)r["Id_Producto"],
                                            Inst = r["Instrucciones"].ToString(),
                                            EsPers = (bool)r["Es_Personalizado"],
                                            IdDiseno = r["Id_Solicitud_Diseno"] == DBNull.Value ? (int?)null : (int)r["Id_Solicitud_Diseno"],
                                            CobraComision = (bool)r["Cobrar_Comision_Extra"]
                                        });
                                    }
                                }
                            }

                            if (itemsCarrito.Count == 0) return BadRequest(new { message = "El carrito está vacío o los productos cambiaron." });

                            // B. PREPARAR ITEMS Y VALIDAR DISEÑOS
                            foreach (var item in itemsCarrito)
                            {
                                if (item.Cantidad <= 0) return BadRequest(new { message = $"Error: {item.Nombre} cantidad inválida." });

                                if (item.EsPers && item.IdDiseno != null)
                                {
                                    int idDiseno = (int)item.IdDiseno;
                                    string sqlDiseno = @"SELECT 1 FROM ""Tienda_Solicitudes_Diseno"" WHERE ""Id_Solicitud"" = @id AND ""Id_Usuario"" = @uid";
                                    using (var cmdDis = new NpgsqlCommand(sqlDiseno, conexion, trans))
                                    {
                                        cmdDis.Parameters.AddWithValue("@id", idDiseno);
                                        cmdDis.Parameters.AddWithValue("@uid", idUser);
                                        if (await cmdDis.ExecuteScalarAsync() == null)
                                            return BadRequest(new { message = $"Diseño inválido para '{item.Nombre}'." });
                                    }
                                }

                                // SUMAMOS A LOS SUBTOTALES SEPARADOS
                                decimal subtotalItem = (decimal)(item.Precio * item.Cantidad);
                                if (item.CobraComision) subTotalConComision += subtotalItem;
                                else subTotalSinComision += subtotalItem;

                                lineItemsStripe.Add(new SessionLineItemOptions
                                {
                                    PriceData = new SessionLineItemPriceDataOptions
                                    {
                                        UnitAmountDecimal = (long)(item.Precio * 100),
                                        Currency = "mxn",
                                        ProductData = new SessionLineItemPriceDataProductDataOptions { Name = item.Nombre, Description = item.Inst }
                                    },
                                    Quantity = item.Cantidad
                                });
                            }

                            decimal subTotalBase = subTotalConComision + subTotalSinComision;
                            if (subTotalBase <= 0) return BadRequest(new { message = "El total de la orden es 0." });

                            // C. VALIDACIÓN DE CUPONES
                            decimal descuentoAplicado = 0;
                            int? idCuponAplicado = null;
                            string codigoGuardar = null;

                            if (!string.IsNullOrWhiteSpace(form.CodigoCupon))
                            {
                                string sqlCupon = @"SELECT c.*,
                    (SELECT COUNT(*) FROM ""Sist_Cupones_Usuarios"" u WHERE u.""Id_Cupon"" = c.""Id_Cupon"") as ""TotalLista"",
                    (SELECT COUNT(*) FROM ""Sist_Cupones_Usuarios"" u WHERE u.""Id_Cupon"" = c.""Id_Cupon"" AND u.""Id_Usuario"" = @uid) as ""TengoPermiso""
                    FROM ""Sist_Cupones"" c
                    WHERE UPPER(c.""Codigo"") = UPPER(@cod) AND c.""Activo"" = TRUE AND NOW() BETWEEN c.""Fecha_Inicio"" AND c.""Fecha_Fin"" FOR UPDATE";

                                using (var cmdC = new NpgsqlCommand(sqlCupon, conexion, trans))
                                {
                                    cmdC.Parameters.AddWithValue("@cod", form.CodigoCupon.Trim());
                                    cmdC.Parameters.AddWithValue("@uid", idUser);

                                    using (var rC = await cmdC.ExecuteReaderAsync())
                                    {
                                        if (await rC.ReadAsync())
                                        {
                                            int limite = (int)rC["Limite_Usos"];
                                            int usados = (int)rC["Conteo_Usados"];
                                            decimal minimo = (decimal)rC["Monto_Minimo_Compra"];
                                            bool aplicaTienda = (bool)rC["Aplica_Tienda"];

                                            bool tieneLista = Convert.ToInt64(rC["TotalLista"]) > 0;
                                            bool tengoPermiso = Convert.ToInt64(rC["TengoPermiso"]) > 0;

                                            if (usados < limite && subTotalBase >= minimo && aplicaTienda && (!tieneLista || tengoPermiso))
                                            {
                                                int tipo = (int)rC["Tipo_Descuento"];
                                                decimal valor = (decimal)rC["Valor"];
                                                decimal? tope = rC["Tope_Maximo_Descuento"] as decimal?;

                                                descuentoAplicado = Funciones.CalcularMontoDescuento(subTotalBase, tipo, valor, tope);
                                                idCuponAplicado = (int)rC["Id_Cupon"];
                                                codigoGuardar = form.CodigoCupon.ToUpper();
                                            }
                                        }
                                    }
                                }

                                if (idCuponAplicado.HasValue)
                                {
                                    await new NpgsqlCommand($"UPDATE \"Sist_Cupones\" SET \"Conteo_Usados\" = \"Conteo_Usados\" + 1 WHERE \"Id_Cupon\" = {idCuponAplicado}", conexion, trans).ExecuteNonQueryAsync();
                                }
                                else return BadRequest(new { message = "Cupón no válido o requisitos no cumplidos." });
                            }

                            // D. CÁLCULOS DEL PEDIDO CON COMISIÓN SEPARADA
                            decimal totalBaseMenosDescuento = subTotalBase - descuentoAplicado;
                            if (totalBaseMenosDescuento < 0) totalBaseMenosDescuento = 0;

                            decimal baseComisionable = subTotalConComision;
                            if (descuentoAplicado > 0 && subTotalBase > 0)
                            {
                                decimal proporcion = subTotalConComision / subTotalBase;
                                baseComisionable = subTotalConComision - (descuentoAplicado * proporcion);
                            }

                            decimal montoComision = 0;
                            if (baseComisionable > 0)
                            {
                                decimal totalIdealConComision = Funciones.CalcularPagoConComision(baseComisionable, _configuration);
                                montoComision = totalIdealConComision - baseComisionable;
                            }

                            decimal totalConComisionFinal = totalBaseMenosDescuento + montoComision;

                            // CREAR PEDIDO
                            string sqlPedido = @"INSERT INTO ""Tienda_Pedidos"" 
               (""Id_Usuario_Solicita"", ""Fecha_Solicitud"", ""Id_Estatus"", ""Telefono_Contacto"", ""Comentarios_Cliente"", 
                ""Total_Estimado"", ""Id_Cupon"", ""Codigo_Cupon_Aplicado"", ""Monto_Descuento"")
               VALUES (@uid, NOW(), 15, @tel, @com, @tot, @idC, @codC, @montC) 
               RETURNING ""Id_Pedido""";

                            using (var cmd = new NpgsqlCommand(sqlPedido, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@uid", idUser);
                                cmd.Parameters.AddWithValue("@tel", form.TelefonoContacto ?? "");
                                cmd.Parameters.AddWithValue("@com", form.Comentarios ?? "");
                                cmd.Parameters.AddWithValue("@tot", totalConComisionFinal);
                                cmd.Parameters.AddWithValue("@idC", (object)idCuponAplicado ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@codC", (object)codigoGuardar ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@montC", descuentoAplicado);
                                idPedidoGenerado = (int)await cmd.ExecuteScalarAsync();
                            }

                            // E. REGISTRAR USO DE CUPÓN
                            if (idCuponAplicado.HasValue)
                            {
                                try
                                {
                                    string sqlLock = @"INSERT INTO ""Sist_Cupones_Uso"" (""Id_Cupon"", ""Id_Usuario"", ""Id_Pedido_Tienda"", ""Fecha_Uso"") VALUES (@idc, @idu, @idp, NOW())";
                                    using (var cmdLock = new NpgsqlCommand(sqlLock, conexion, trans))
                                    {
                                        cmdLock.Parameters.AddWithValue("@idc", idCuponAplicado);
                                        cmdLock.Parameters.AddWithValue("@idu", idUser);
                                        cmdLock.Parameters.AddWithValue("@idp", idPedidoGenerado);
                                        await cmdLock.ExecuteNonQueryAsync();
                                    }
                                }
                                catch { return BadRequest(new { message = "Ya utilizaste este cupón anteriormente." }); }
                            }

                            // F. DETALLES DEL PEDIDO
                            foreach (var item in itemsCarrito)
                            {
                                string sqlDet = @"INSERT INTO ""Tienda_Detalles_Pedido""
                (""Id_Pedido"", ""Id_Producto_Venta"", ""Cantidad"", ""Precio_Unitario"", ""Es_Personalizado"", ""Instrucciones_Especiales"", ""Id_Solicitud_Diseno"")
                VALUES (@ped, @prod, @cant, @prec, @pers, @inst, @idDis)";

                                using (var cmd = new NpgsqlCommand(sqlDet, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@ped", idPedidoGenerado);
                                    cmd.Parameters.AddWithValue("@prod", item.IdProd);
                                    cmd.Parameters.AddWithValue("@cant", item.Cantidad);
                                    cmd.Parameters.AddWithValue("@prec", item.Precio);
                                    cmd.Parameters.AddWithValue("@pers", item.EsPers);
                                    cmd.Parameters.AddWithValue("@inst", item.Inst);
                                    cmd.Parameters.AddWithValue("@idDis", (object)item.IdDiseno ?? DBNull.Value);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }

                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.PagoTienda, $"Pedido #{idPedidoGenerado} iniciado.", HttpContext.Connection.RemoteIpAddress?.ToString(), trans);

                            // G. STRIPE
                            string baseUrl = $"{Request.Scheme}://{Request.Host}";
                            string referenciaUnica = $"SHOP_{idPedidoGenerado}_{Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper()}";

                            var options = new SessionCreateOptions
                            {
                                PaymentMethodTypes = new List<string> { "card" },
                                LineItems = lineItemsStripe,
                                Mode = "payment",
                                SuccessUrl = $"{baseUrl}/Tienda/PagoExitoso?session_id={{CHECKOUT_SESSION_ID}}",
                                CancelUrl = $"{baseUrl}/Tienda/Carrito",
                                ClientReferenceId = referenciaUnica,
                                CustomerEmail = !string.IsNullOrEmpty(emailUser) ? emailUser : null,
                                ExpiresAt = DateTime.UtcNow.AddMinutes(30)
                            };

                            if (descuentoAplicado > 0)
                            {
                                var couponOptions = new Stripe.CouponCreateOptions { AmountOff = (long)(descuentoAplicado * 100), Currency = "mxn", Duration = "once", Name = $"Cupon: {codigoGuardar}" };
                                var couponService = new CouponService();
                                var stripeCoupon = await couponService.CreateAsync(couponOptions);
                                options.Discounts = new List<SessionDiscountOptions> { new SessionDiscountOptions { Coupon = stripeCoupon.Id } };
                            }

                            // Stripe solo cobra el renglón de "Gastos de Gestión" si el monto es mayor a cero.
                            if (montoComision > 0)
                            {
                                options.LineItems.Add(new SessionLineItemOptions
                                {
                                    PriceData = new SessionLineItemPriceDataOptions { UnitAmountDecimal = (long)(montoComision * 100), Currency = "mxn", ProductData = new SessionLineItemPriceDataProductDataOptions { Name = "Gastos de Gestión" } },
                                    Quantity = 1
                                });
                            }

                            var service = new SessionService();
                            Session session = await service.CreateAsync(options);

                            string sqlUpdRef = @"UPDATE ""Tienda_Pedidos"" SET ""Ref_Pasarela"" = @sid, ""External_Reference"" = @ext WHERE ""Id_Pedido"" = @id";
                            using (var cmd = new NpgsqlCommand(sqlUpdRef, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@sid", session.Id);
                                cmd.Parameters.AddWithValue("@ext", referenciaUnica);
                                cmd.Parameters.AddWithValue("@id", idPedidoGenerado);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            await trans.CommitAsync();
                            return Json(new { url = session.Url });
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Error: {ex.Message}" });
            }
        }

        // =========================================================
        // 16. REACTIVAR PEDIDO (Retomar pago pendiente)
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReactivarPedido(int idPedido)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                string sessionId = null;

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Buscar el Session ID de Stripe guardado en el pedido
                    string sql = @"SELECT ""Ref_Pasarela"" FROM ""Tienda_Pedidos"" 
                           WHERE ""Id_Pedido"" = @id AND ""Id_Usuario_Solicita"" = @uid AND ""Id_Estatus"" = 15";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idPedido);
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        var result = await cmd.ExecuteScalarAsync();

                        if (result != null && result != DBNull.Value)
                        {
                            sessionId = result.ToString();
                        }
                    }
                }

                if (!string.IsNullOrEmpty(sessionId))
                {
                    // Consultar a Stripe para obtener la URL de pago actual
                    var service = new Stripe.Checkout.SessionService();
                    var session = await service.GetAsync(sessionId);

                    if (session.PaymentStatus == "unpaid" && session.Status == "open")
                    {
                        // Si sigue abierto, lo mandamos a pagar
                        return Redirect(session.Url);
                    }
                    else if (session.PaymentStatus == "paid")
                    {
                        // Si ya pagó, lo procesamos y mandamos a éxito
                        await PagoExitoso(sessionId);
                        return RedirectToAction("MisCompras");
                    }
                }

                MostrarMensaje("Expirado", "La sesión de pago expiró. Por favor realiza el pedido nuevamente.", TipoMensaje.Alerta);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                MostrarMensaje("Error", "No se pudo reactivar el pedido.", TipoMensaje.Error);
            }

            return RedirectToAction("MisCompras");
        }

        // =========================================================
        // 8. PAGO EXITOSO (FINALIZACIÓN Y LIMPIEZA)
        // =========================================================
        [Authorize]
        public async Task<IActionResult> PagoExitoso(string session_id)
        {
            if (string.IsNullOrEmpty(session_id)) {
                MostrarMensaje("Error", "No se recibió la session del pago", TipoMensaje.Error);
                return RedirectToAction("Index"); 
            }

            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                var service = new SessionService();
                var session = await service.GetAsync(session_id);

                if (session.PaymentStatus == "paid")
                {
                    // 1. Extraemos el ID del ClientReferenceId solo para buscar en BD (Split)
                    string[] partesRef = session.ClientReferenceId?.Split('_');

                    if (partesRef != null && partesRef.Length >= 2 && int.TryParse(partesRef[1], out int idPedido))
                    {
                        using (var conexion = new NpgsqlConnection(_cadenaConexion))
                        {
                            await conexion.OpenAsync();

                            // 2. Traemos Total y la REFERENCIA QUE GUARDAMOS ANTES
                            var cmd = new NpgsqlCommand($@"SELECT ""Total_Estimado"", ""External_Reference"" FROM ""Tienda_Pedidos"" WHERE ""Id_Pedido""={idPedido}", conexion);

                            decimal totalEsperado = 0;
                            string referenciaGuardada = "";

                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    totalEsperado = (decimal)r["Total_Estimado"];
                                    referenciaGuardada = r["External_Reference"]?.ToString();
                                }
                            }

                            // 3. COMPARACIÓN DE SEGURIDAD (El candado final)
                            if (referenciaGuardada == session.ClientReferenceId)
                            {
                                decimal montoPagadoStripe = (session.AmountTotal ?? 0) / 100m;
                                await ProcesarPagoInterno(idPedido, session.PaymentIntentId ?? session.Id, idUser, "Web-Retorno-Stripe", totalEsperado, montoPagadoStripe);
                                MostrarMensaje("¡Compra Exitosa!", "Tu pago ha sido procesado correctamente.", TipoMensaje.Exito);
                            }
                            else
                            {
                                // Si el ID existe pero la referencia aleatoria no coincide (ej. BD reiniciada)
                                MostrarMensaje("Error de Seguridad", "La referencia del pago no coincide con el registro actual.", TipoMensaje.Error);
                            }
                        }
                    }
                }
                else
                {
                    MostrarMensaje("Atención", "El pago aún no se confirma.", TipoMensaje.Alerta);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                MostrarMensaje("Pago en Proceso", "Estamos verificando tu pago.", TipoMensaje.Alerta);
            }

            return RedirectToAction("MisCompras");
        }

        // =========================================================
        // MOTOR PRIVADO DE PROCESAMIENTO 
        // =========================================================
        private async Task ProcesarPagoInterno(int idPedido, string refPagoExterno, int idUsuarioBitacora, string origen, decimal totalEsperadoEnBd, decimal montoPagadoReal)
        {
            if (Math.Abs(montoPagadoReal - totalEsperadoEnBd) > 1.0m) throw new Exception("Monto pagado insuficiente.");
            decimal montoNetoFinal = montoPagadoReal;

            if (!string.IsNullOrEmpty(refPagoExterno))
            {
                try
                {
                    if (refPagoExterno.StartsWith("cs_"))
                    {
                        var service = new Stripe.Checkout.SessionService();
                        var options = new Stripe.Checkout.SessionGetOptions();
                        options.AddExpand("payment_intent.latest_charge.balance_transaction");
                        var session = await service.GetAsync(refPagoExterno, options);

                        if (session.PaymentIntent?.LatestCharge?.BalanceTransaction != null)
                            montoNetoFinal = session.PaymentIntent.LatestCharge.BalanceTransaction.Net / 100.0m;
                        else
                            montoNetoFinal = 0; // Bandera secreta, Stripe va lento
                    }
                    else if (refPagoExterno.StartsWith("pi_"))
                    {
                        var service = new Stripe.PaymentIntentService();
                        var options = new Stripe.PaymentIntentGetOptions();
                        options.AddExpand("latest_charge.balance_transaction");
                        var intent = await service.GetAsync(refPagoExterno, options);

                        if (intent.LatestCharge?.BalanceTransaction != null)
                            montoNetoFinal = intent.LatestCharge.BalanceTransaction.Net / 100.0m;
                        else
                            montoNetoFinal = 0; // Bandera secreta, Stripe va lento
                    }
                }
                catch
                {
                    // Como todo en la Tienda es por Stripe, si falla siempre marcamos 0 para auditar después
                    montoNetoFinal = 0;
                }
            }

            using (var conexion = new NpgsqlConnection(_cadenaConexion))
            {
                await conexion.OpenAsync();
                using (var trans = await conexion.BeginTransactionAsync())
                {
                    try
                    {
                        int estatusActual = Convert.ToInt32(await new NpgsqlCommand($"SELECT \"Id_Estatus\" FROM \"Tienda_Pedidos\" WHERE \"Id_Pedido\"={idPedido} FOR UPDATE", conexion, trans).ExecuteScalarAsync());
                        if (estatusActual >= 30) { await trans.RollbackAsync(); return; }

                        string codigo = Funciones.GenerarCodigoEntrega();
                        string sqlUpd1 = @"UPDATE ""Tienda_Pedidos"" 
                          SET ""Id_Estatus"" = 30, 
                              ""Codigo_Entrega"" = @cod, 
                              ""Ref_Pasarela"" = @ref,
                              ""Total_Neto"" = @neto
                          WHERE ""Id_Pedido"" = @id";

                        using (var cmd = new NpgsqlCommand(sqlUpd1, conexion, trans))
                        {
                            cmd.Parameters.AddWithValue("@cod", codigo);
                            cmd.Parameters.AddWithValue("@ref", refPagoExterno);
                            cmd.Parameters.AddWithValue("@neto", montoNetoFinal);
                            cmd.Parameters.AddWithValue("@id", idPedido);
                            await cmd.ExecuteNonQueryAsync();
                        }

                        // DESCONTAR STOCK POR TALLA (AQUÍ ES EL ÚNICO LUGAR DONDE SE RESTA)
                        var items = new List<dynamic>();
                        using (var cmdI = new NpgsqlCommand("SELECT \"Id_Producto_Venta\", \"Cantidad\", \"Instrucciones_Especiales\" FROM \"Tienda_Detalles_Pedido\" WHERE \"Id_Pedido\"=@id", conexion, trans))
                        {
                            cmdI.Parameters.AddWithValue("@id", idPedido);
                            using (var r = await cmdI.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync()) items.Add(new { Id = (int)r["Id_Producto_Venta"], Cant = (int)r["Cantidad"], Inst = r["Instrucciones_Especiales"].ToString() });
                            }
                        }

                        foreach (var item in items)
                        {
                            string talla = "Unitalla";
                            if (item.Inst.Contains("Talla: ")) talla = item.Inst.Replace("Talla: ", "").Trim();

                            // Sin la restricción de stock >= cant (Permite backorders / negativos)
                            string sqlUpd2 = "UPDATE \"Tienda_Productos_Medidas\" SET \"Stock\" = \"Stock\" - @cant WHERE \"Id_Producto\" = @pid AND \"Nombre\" = @talla";
                            using (var cmdU = new NpgsqlCommand(sqlUpd2, conexion, trans))
                            {
                                cmdU.Parameters.AddWithValue("@cant", item.Cant);
                                cmdU.Parameters.AddWithValue("@pid", item.Id);
                                cmdU.Parameters.AddWithValue("@talla", talla);
                                await cmdU.ExecuteNonQueryAsync();
                            }
                        }

                        int idDueno = Convert.ToInt32(await new NpgsqlCommand($"SELECT \"Id_Usuario_Solicita\" FROM \"Tienda_Pedidos\" WHERE \"Id_Pedido\"={idPedido}", conexion, trans).ExecuteScalarAsync());
                        await new NpgsqlCommand($"DELETE FROM \"Tienda_Carrito\" WHERE \"Id_Usuario\"={idDueno}", conexion, trans).ExecuteNonQueryAsync();

                        await Funciones.RegistrarBitacora(conexion, idUsuarioBitacora == 0 ? 1 : idUsuarioBitacora, Modulo, Parametros.AccionesBitacora.PagoTiendaConfirmado, $"Pago Confirmado. Pedido #{idPedido}", "Sistema", trans);

                        await trans.CommitAsync();

                        try
                        {
                            string emailCliente = "";
                            string nombreCliente = "";
                            string codEntrega = "";

                            string sqlDatos = @"SELECT u.""Email"", u.""NombreCompleto"", p.""Codigo_Entrega"" 
                        FROM ""Tienda_Pedidos"" p
                        JOIN ""Sist_Usuarios"" u ON p.""Id_Usuario_Solicita"" = u.""Id_Usuario""
                        WHERE p.""Id_Pedido"" = @id";
                            using (var cmdD = new NpgsqlCommand(sqlDatos, conexion))
                            {
                                cmdD.Parameters.AddWithValue("@id", idPedido);
                                using (var r = await cmdD.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        emailCliente = r["Email"]?.ToString();
                                        nombreCliente = r["NombreCompleto"]?.ToString();
                                        codEntrega = r["Codigo_Entrega"]?.ToString();
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(emailCliente))
                            {
                                string asunto = $"¡Pago Confirmado! Pedido #{idPedido} - Tienda Oficial";
                                string html = $@"
            <h2 style='color:#198754;'>¡Gracias por tu compra, {nombreCliente}!</h2>
            <p>Hemos recibido tu pago por la cantidad de <strong>${montoNetoFinal:N2}</strong>.</p>
            <p>Tu código de entrega seguro es: <strong style='font-size:1.5rem; color:#00B8D4; letter-spacing: 2px;'>{codEntrega}</strong></p>
            <p>Por favor, presenta este código al recibir el producto para poder confirmar la entrega.</p>";

                                await Funciones.EnviarCorreo(_configuration, emailCliente, asunto, html);
                            }
                        }
                        catch { /* Ignorar errores de SMTP para no romper el flujo de Stripe */ }

                        string urlAdmin = Url.Action("GestionPedidos", "AdminTienda", null, Request.Scheme);
                        string htmlVentas = $@"
                    <div style='font-family: sans-serif; border: 1px solid #eee; padding: 20px; border-radius: 10px;'>
                        <h2 style='color: #198754;'>🛒 Nuevo Pedido Pagado</h2>
                        <p>¡Se ha confirmado una nueva compra en la tienda!</p>
                        <p><strong>Folio del Pedido:</strong> #{idPedido}</p>
                        <p><strong>Total Pagado:</strong> {montoPagadoReal:C2}</p>
                        <hr style='border: 0; border-top: 1px solid #eee; margin: 20px 0;' />
                        <p>Por favor, ingresa al panel de administración de la tienda para preparar los artículos y gestionar la entrega o envío.</p>
                        <p align='center' style='margin-top: 25px;'>
                            <a href='{urlAdmin}' style='background: #0d6efd; color: #fff; padding: 12px 25px; text-decoration: none; border-radius: 5px; font-weight: bold; display: inline-block;'>
                                📦 Ver Detalles del Pedido
                            </a>
                        </p>
                    </div>";

                        await Funciones.EnviarAlertaPorBaseDatos(_configuration, "NUEVA_COMPRA_TIENDA", $"Nuevo Pedido Pagado #{idPedido}", htmlVentas);
                    }
                    catch { await trans.RollbackAsync(); throw; }
                }
            }
        }

        // =========================================================
        // HELPER: VERIFICACIÓN "TIEMPO REAL" (IDEMPOTENTE Y SEGURA)
        // =========================================================
        private async Task VerificarPedidosPendientes(int? idUsuarioActual = null)
        {
            try
            {
                // 1. AUDITORÍA SILENCIOSA DE COMISIONES FALTANTES (Optimistic Fulfillment)
                try
                {
                    var pedidosPorAuditar = new List<dynamic>();
                    using (var conexionAud = new NpgsqlConnection(_cadenaConexion))
                    {
                        await conexionAud.OpenAsync();

                        // Buscamos pedidos pagados con Neto 0 que sean tanto cs_ como pi_
                        string sqlAuditoria = @"
                    SELECT ""Id_Pedido"", ""Ref_Pasarela"" 
                    FROM ""Tienda_Pedidos"" 
                    WHERE ""Id_Estatus"" IN (30, 50)
                      AND ""Total_Neto"" = 0 
                      AND (""Ref_Pasarela"" LIKE 'cs_%' OR ""Ref_Pasarela"" LIKE 'pi_%')";

                        using (var cmdAud = new NpgsqlCommand(sqlAuditoria, conexionAud))
                        {
                            using var rAud = await cmdAud.ExecuteReaderAsync();
                            while (await rAud.ReadAsync())
                            {
                                pedidosPorAuditar.Add(new
                                {
                                    IdPedido = (int)rAud["Id_Pedido"],
                                    SessionId = rAud["Ref_Pasarela"].ToString()
                                });
                            }
                        }

                        if (pedidosPorAuditar.Any())
                        {
                            var sessionServiceAud = new Stripe.Checkout.SessionService();
                            var piServiceAud = new Stripe.PaymentIntentService();

                            foreach (var p in pedidosPorAuditar)
                            {
                                decimal netoReal = 0;
                                string refPasarela = p.SessionId;

                                // Lógica para obtener el neto real dependiendo del tipo de referencia
                                if (refPasarela.StartsWith("cs_"))
                                {
                                    var sessionAud = await sessionServiceAud.GetAsync(refPasarela);
                                    if (!string.IsNullOrEmpty(sessionAud.PaymentIntentId))
                                    {
                                        var optionsPI = new Stripe.PaymentIntentGetOptions();
                                        optionsPI.AddExpand("latest_charge.balance_transaction");
                                        var piAud = await piServiceAud.GetAsync(sessionAud.PaymentIntentId, optionsPI);

                                        if (piAud.LatestCharge?.BalanceTransaction != null)
                                            netoReal = piAud.LatestCharge.BalanceTransaction.Net / 100.0m;
                                    }
                                }
                                else if (refPasarela.StartsWith("pi_"))
                                {
                                    var optionsPI = new Stripe.PaymentIntentGetOptions();
                                    optionsPI.AddExpand("latest_charge.balance_transaction");
                                    var piAud = await piServiceAud.GetAsync(refPasarela, optionsPI);

                                    if (piAud.LatestCharge?.BalanceTransaction != null)
                                        netoReal = piAud.LatestCharge.BalanceTransaction.Net / 100.0m;
                                }

                                if (netoReal > 0)
                                {
                                    string sqlFix = @"UPDATE ""Tienda_Pedidos"" SET ""Total_Neto"" = @neto WHERE ""Id_Pedido"" = @id";
                                    using (var cmdFix = new NpgsqlCommand(sqlFix, conexionAud))
                                    {
                                        cmdFix.Parameters.AddWithValue("@neto", netoReal);
                                        cmdFix.Parameters.AddWithValue("@id", p.IdPedido);
                                        await cmdFix.ExecuteNonQueryAsync();
                                    }
                                    Console.WriteLine($"Auditoría Tienda: Pedido {p.IdPedido} corregido con neto real de {netoReal}");
                                }
                            }
                        }
                    }
                }
                catch (Exception exAud) { Console.WriteLine("Error en auditoría silenciosa Tienda: " + exAud.Message); }

                var candidatos = new List<int>();

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Buscamos todo lo pendiente reciente de 3 días a hoy
                    string sqlCandidatos = @"
                SELECT ""Id_Pedido"" 
                FROM ""Tienda_Pedidos"" 
                WHERE ""Id_Estatus"" = 15 
                AND (""Ref_Pasarela"" LIKE 'cs_%' OR ""Ref_Pasarela"" LIKE 'pi_%')
                AND ""Fecha_Solicitud"" >= (NOW() - INTERVAL '3 days')
                ORDER BY ""Id_Pedido"" ASC";

                    using (var cmd = new NpgsqlCommand(sqlCandidatos, conexion))
                    {
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync()) candidatos.Add(r.GetInt32(0));
                        }
                    }
                }

                if (!candidatos.Any()) return;

                var sessionService = new Stripe.Checkout.SessionService();
                var piService = new Stripe.PaymentIntentService();

                foreach (var idPedido in candidatos)
                {
                    using (var conexion = new NpgsqlConnection(_cadenaConexion))
                    {
                        await conexion.OpenAsync();

                        // 2. CLAIM ATÓMICO (IDEMPOTENCIA)
                        // "Apartamos" el pedido marcándolo como 98. Si ya estaba apartado o pagado, esto falla y no hacemos nada.
                        // Esto evita que el Webhook y esta función choquen.
                        string sqlClaim = @"UPDATE ""Tienda_Pedidos"" 
                                    SET ""Id_Estatus"" = 98 
                                    WHERE ""Id_Pedido"" = @id AND ""Id_Estatus"" = 15 
                                    RETURNING ""Ref_Pasarela"", ""External_Reference"", ""Total_Estimado"", ""Id_Cupon"", ""Id_Usuario_Solicita""";

                        string sessionId = null;
                        string refEsperada = null;
                        decimal totalEsperado = 0;
                        int? idCupon = null;
                        int? idUser = null;

                        using (var cmdClaim = new NpgsqlCommand(sqlClaim, conexion))
                        {
                            cmdClaim.Parameters.AddWithValue("@id", idPedido);
                            using (var r = await cmdClaim.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    sessionId = r["Ref_Pasarela"].ToString();
                                    refEsperada = r["External_Reference"]?.ToString();
                                    totalEsperado = (decimal)r["Total_Estimado"];
                                    idCupon = r["Id_Cupon"] as int?;
                                    idUser = r["Id_Usuario_Solicita"] as int?;
                                }
                                else continue; // Ya lo tomó otro proceso, saltamos.
                            }
                        }

                        // 3. CONSULTA A STRIPE
                        bool pagoConfirmado = false;
                        bool debeEliminarse = false;

                        try
                        {
                            // Soporte para validar tanto Checkout Sessions como PaymentIntents directos
                            if (sessionId.StartsWith("cs_"))
                            {
                                var session = await sessionService.GetAsync(sessionId);

                                if (session.PaymentStatus == "paid")
                                {
                                    if (refEsperada != null && session.ClientReferenceId == refEsperada)
                                    {
                                        decimal pagadoReal = (session.AmountTotal ?? 0) / 100m;
                                        if (Math.Abs(pagadoReal - totalEsperado) <= 1.0m) pagoConfirmado = true;
                                    }
                                }
                                else if (session.Status == "expired")
                                {
                                    debeEliminarse = true;
                                }
                            }
                            else if (sessionId.StartsWith("pi_"))
                            {
                                var intent = await piService.GetAsync(sessionId);

                                if (intent.Status == "succeeded")
                                {
                                    decimal pagadoReal = intent.AmountReceived / 100m;
                                    if (Math.Abs(pagadoReal - totalEsperado) <= 1.0m) pagoConfirmado = true;
                                }
                                else if (intent.Status == "canceled")
                                {
                                    debeEliminarse = true;
                                }
                            }
                        }
                        catch (StripeException ex)
                        {
                            // Si Stripe no encuentra la sesión, es basura local.
                            if (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound) debeEliminarse = true;
                        }
                        catch
                        {
                            // Error de red ajeno -> Soltar Claim
                            await LiberarClaim(conexion, idPedido); continue;
                        }

                        // 4. ACCIONES FINALES
                        if (pagoConfirmado)
                        {
                            // Aprobamos el pedido (Regresamos a 15 temporalmente para que ProcesarPagoInterno funcione estándar)
                            await LiberarClaim(conexion, idPedido);
                            // Pasamos el totalEsperado como montoPagadoReal para la validación interna
                            await ProcesarPagoInterno(idPedido, sessionId, idUser ?? 1, "Auto-Check 3Days", totalEsperado, totalEsperado);
                        }
                        else if (debeEliminarse)
                        {
                            // Limpieza ACID (Devolver stock y cupón)
                            using (var trans = await conexion.BeginTransactionAsync())
                            {
                                try
                                {
                                    // A. Devolver Cupón (VERSION MEJORADA CON ID_PEDIDO)
                                    if (idCupon.HasValue)
                                    {
                                        // 1. Devolver Stock al contador global
                                        await new NpgsqlCommand("UPDATE \"Sist_Cupones\" SET \"Conteo_Usados\" = GREATEST(\"Conteo_Usados\" - 1, 0) WHERE \"Id_Cupon\" = @c", conexion, trans)
                                        { Parameters = { new NpgsqlParameter("@c", idCupon.Value) } }.ExecuteNonQueryAsync();

                                        // 2. Eliminar el uso ESPECÍFICO de este pedido
                                        // Ya no necesitamos validar usuario ni nulos, el ID del pedido es único.
                                        await new NpgsqlCommand("DELETE FROM \"Sist_Cupones_Uso\" WHERE \"Id_Pedido_Tienda\" = @idPed", conexion, trans)
                                        { Parameters = { new NpgsqlParameter("@idPed", idPedido) } }.ExecuteNonQueryAsync();
                                    }

                                    // B. Devolver Stock (Item por Item)
                                    var items = new List<dynamic>();
                                    using (var cmdDet = new NpgsqlCommand("SELECT \"Id_Producto_Venta\", \"Cantidad\", \"Instrucciones_Especiales\" FROM \"Tienda_Detalles_Pedido\" WHERE \"Id_Pedido\" = @id", conexion, trans))
                                    {
                                        cmdDet.Parameters.AddWithValue("@id", idPedido);
                                        using (var rDet = await cmdDet.ExecuteReaderAsync())
                                            while (await rDet.ReadAsync()) items.Add(new { Pid = (int)rDet[0], Cant = (int)rDet[1], Inst = rDet[2].ToString() });
                                    }

                                    foreach (var item in items)
                                    {
                                        string talla = "Unitalla";
                                        if (item.Inst.Contains("Talla: ")) talla = item.Inst.Replace("Talla: ", "").Trim();

                                        // Devolución segura
                                        await new NpgsqlCommand("UPDATE \"Tienda_Productos_Medidas\" SET \"Stock\" = \"Stock\" + @cant WHERE \"Id_Producto\" = @pid AND \"Nombre\" = @t", conexion, trans)
                                        {
                                            Parameters = { new NpgsqlParameter("@cant", item.Cant), new NpgsqlParameter("@pid", item.Pid), new NpgsqlParameter("@t", talla) }
                                        }.ExecuteNonQueryAsync();
                                    }

                                    // C. Matar Pedido
                                    await new NpgsqlCommand("UPDATE \"Tienda_Pedidos\" SET \"Id_Estatus\" = 99 WHERE \"Id_Pedido\" = @id", conexion, trans)
                                    { Parameters = { new NpgsqlParameter("@id", idPedido) } }.ExecuteNonQueryAsync();

                                    await trans.CommitAsync();
                                }
                                catch { await trans.RollbackAsync(); await LiberarClaim(conexion, idPedido); }
                            }
                        }
                        else
                        {
                            // CASO "VIVO": Está 'open' o 'unpaid'. 
                            // Simplemente liberamos el bloqueo para que el usuario pueda seguir interactuando.
                            await LiberarClaim(conexion, idPedido);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error verificación: {ex.Message}");
            }
        }

        // Helper para soltar el bloqueo (Status 98 -> 15)
        private async Task LiberarClaim(NpgsqlConnection con, int idPedido)
        {
            try
            {
                using var cmd = new NpgsqlCommand("UPDATE \"Tienda_Pedidos\" SET \"Id_Estatus\" = 15 WHERE \"Id_Pedido\" = @id AND \"Id_Estatus\" = 98", con);
                cmd.Parameters.AddWithValue("@id", idPedido);
                await cmd.ExecuteNonQueryAsync();
            }
            catch { }
        }

        // =========================================================
        // 9. MIS COMPRAS (VISTA CLIENTE)
        // =========================================================
        [Authorize]
        public async Task<IActionResult> MisCompras()
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            await VerificarPedidosPendientes();

            var lista = new List<PedidoEntregaViewModel>();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // CAMBIO: "Ref_Pasarela" en lugar de "Id_Pago_MercadoPago"
                    string sql = @"
                SELECT p.""Id_Pedido"", p.""Fecha_Estimada"", p.""Fecha_Solicitud"", p.""Total_Estimado"", 
                       p.""Id_Estatus"", p.""Codigo_Entrega"", p.""Ref_Pasarela"", 
                       e.""Nombre"" as ""NomEst"",
                       (SELECT COUNT(*) FROM ""Tienda_Detalles_Pedido"" WHERE ""Id_Pedido"" = p.""Id_Pedido"") as ""CantItems"",
                       
                       (SELECT prod.""Nombre_Comercial"" 
                        FROM ""Tienda_Detalles_Pedido"" d2 
                        JOIN ""Tienda_Productos_Venta"" prod ON d2.""Id_Producto_Venta"" = prod.""Id_Producto"" 
                        WHERE d2.""Id_Pedido"" = p.""Id_Pedido"" 
                        ORDER BY d2.""Id_Detalle"" ASC LIMIT 1) as ""PrimerProd"",

                       (SELECT COALESCE(dis.""Imagen_Previo_Url"", m.""UrlImagen"")
                         FROM ""Tienda_Detalles_Pedido"" d3 
                         LEFT JOIN ""Tienda_Multimedia_Productos"" m ON d3.""Id_Producto_Venta"" = m.""Id_Producto"" AND m.""Es_Principal"" = TRUE 
                         LEFT JOIN ""Tienda_Solicitudes_Diseno"" dis ON d3.""Id_Solicitud_Diseno"" = dis.""Id_Solicitud""
                         WHERE d3.""Id_Pedido"" = p.""Id_Pedido"" 
                         ORDER BY d3.""Id_Detalle"" ASC LIMIT 1) as ""ImgPrevUrl"",
                       (SELECT COUNT(*) FROM ""Tienda_Pedidos_Mensajes"" m 
                        WHERE m.""Id_Pedido"" = p.""Id_Pedido"" AND m.""Es_Admin"" = TRUE 
                        AND m.""Leido"" = FALSE) as ""MsjSinLeer""

                FROM ""Tienda_Pedidos"" p
                JOIN ""Tienda_Cat_Estatus"" e ON p.""Id_Estatus"" = e.""Id_Estatus""
                WHERE p.""Id_Usuario_Solicita"" = @uid 
                AND p.""Id_Estatus"" IN (15, 30, 50) 
                AND p.""Ref_Pasarela"" IS NOT NULL 
                AND EXISTS (SELECT 1 FROM ""Tienda_Detalles_Pedido"" d WHERE d.""Id_Pedido"" = p.""Id_Pedido"")
                ORDER BY p.""Id_Pedido"" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int totalItems = Convert.ToInt32(r["CantItems"]);
                                string primerProd = r["PrimerProd"]?.ToString() ?? "Producto";
                                string resumen = totalItems > 1 ? $"{primerProd} y {totalItems - 1} más" : primerProd;

                                lista.Add(new PedidoEntregaViewModel
                                {
                                    IdPedido = (int)r["Id_Pedido"],
                                    Fecha = (DateTime)r["Fecha_Solicitud"],
                                    Total = (decimal)r["Total_Estimado"],
                                    IdEstatus = (int)r["Id_Estatus"],
                                    NombreEstatus = r["NomEst"].ToString(),
                                    CodigoEntrega = r["Codigo_Entrega"]?.ToString() ?? "---",
                                    ImagenPreviewUrl = r["ImgPrevUrl"]?.ToString(),
                                    ResumenCompra = resumen,
                                    CantidadArticulos = totalItems,
                                    FechaEstimada = r["Fecha_Estimada"] != DBNull.Value ? (DateTime)r["Fecha_Estimada"] : new DateTime(1900, 1, 1),
                                    RefPasarela = r["Ref_Pasarela"]?.ToString(),
                                    MensajesSinLeer = Convert.ToInt32(r["MsjSinLeer"])
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return View(lista);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActualizarFechaEstimada(int idPedido, DateTime fechaEstimada)
        {
            Parametros.Modulo Modulo = Parametros.Modulos.Entregas;
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permiso de edición en ésta ventana", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. VALIDACIÓN: ¿En qué estatus está el pedido?
                    int estatusActual = 0;
                    string sqlCheck = @"SELECT ""Id_Estatus"" FROM ""Tienda_Pedidos"" WHERE ""Id_Pedido"" = @id";

                    using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@id", idPedido);
                        object result = await cmdCheck.ExecuteScalarAsync();
                        if (result != null) estatusActual = Convert.ToInt32(result);
                    }

                    // 2. REGLA DE NEGOCIO: Si es 50 (Entregado) o mayor, no se toca.
                    if (estatusActual >= 50)
                    {
                        MostrarMensaje("Acción Bloqueada", "No puedes modificar la fecha estimada de un pedido que ya fue entregado.", TipoMensaje.Alerta);
                        return RedirectToAction("DetalleEntregaAdmin", new { id = idPedido });
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // 3. ACTUALIZACIÓN (Si pasó la validación)
                        string sqlUpdate = @"UPDATE ""Tienda_Pedidos"" SET ""Fecha_Estimada"" = @fecha WHERE ""Id_Pedido"" = @id";

                        using (var cmd = new NpgsqlCommand(sqlUpdate, conexion,trans))
                        {
                            cmd.Parameters.AddWithValue("@fecha", fechaEstimada);
                            cmd.Parameters.AddWithValue("@id", idPedido);
                            await cmd.ExecuteNonQueryAsync();

                            var idAdmin = int.Parse(User.FindFirst("IdUsuario").Value); // Asegúrate de tener esta variable
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            string detalle = $"Pedido #{idPedido}: Fecha estimada actualizada a {fechaEstimada:dd/MM/yyyy}";

                            await Funciones.RegistrarBitacora(conexion, idAdmin, Parametros.Modulos.Entregas, Parametros.AccionesBitacora.Editar, detalle, ip, trans);
                            await trans.CommitAsync();

                            try
                            {
                                string emailCliente = "";
                                string nombreCliente = "";

                                string sqlDatos = @"SELECT u.""Email"", u.""NombreCompleto"" 
                        FROM ""Tienda_Pedidos"" p
                        JOIN ""Sist_Usuarios"" u ON p.""Id_Usuario_Solicita"" = u.""Id_Usuario""
                        WHERE p.""Id_Pedido"" = @id";
                                using (var cmdD = new NpgsqlCommand(sqlDatos, conexion))
                                {
                                    cmdD.Parameters.AddWithValue("@id", idPedido);
                                    using (var r = await cmdD.ExecuteReaderAsync())
                                    {
                                        if (await r.ReadAsync())
                                        {
                                            emailCliente = r["Email"]?.ToString();
                                            nombreCliente = r["NombreCompleto"]?.ToString();
                                        }
                                    }
                                }

                                if (!string.IsNullOrEmpty(emailCliente))
                                {
                                    string asunto = $"Actualización de tu Pedido #{idPedido}";
                                    string html = $@"
            <h2 style='color:#00B8D4;'>Hola {nombreCliente},</h2>
            <p>Te informamos que la fecha estimada de entrega para tu pedido <strong>#{idPedido}</strong> ha sido programada para el <strong>{fechaEstimada:dd/MM/yyyy}</strong>.</p>
            <p>Recuerda llevar tu código de entrega para poder recibir tus artículos.</p>";

                                    await Funciones.EnviarCorreo(_configuration, emailCliente, asunto, html);
                                }
                            }
                            catch { }
                        }
                    }
                }

                MostrarMensaje("Fecha Actualizada", "El cliente ahora podrá ver la fecha tentativa.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("DetalleEntregaAdmin", new { id = idPedido });
        }

        // =========================================================
        // 10. ADMINISTRAR ENTREGAS (ADMIN)
        // =========================================================
        [Authorize]
        public async Task<IActionResult> EntregasAdmin()
        {
            //Sobreescribe esta funcion pues la ventana es otra con otro permiso
            Parametros.Modulo Modulo = Parametros.Modulos.Entregas;
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para gestionar entregas.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            // 1. AUTO-VERIFICACIÓN GLOBAL
            // Al entrar aquí, revisamos si cayeron pagos nuevos de CUALQUIER usuario.
            await VerificarPedidosPendientes();

            var lista = new List<PedidoEntregaViewModel>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = @"
    SELECT 
        p.""Id_Pedido"", 
        p.""Fecha_Solicitud"", 
        p.""Total_Estimado"", 
        p.""Codigo_Entrega"", 
        p.""Telefono_Contacto"", 
        p.""Comentarios_Cliente"",
        p.""Fecha_Estimada"", -- ¡AQUÍ ESTÁ LA FECHA QUE FALTABA!
        u.""NombreCompleto"" as ""Cliente"",
        
        (SELECT COUNT(*) FROM ""Tienda_Detalles_Pedido"" WHERE ""Id_Pedido"" = p.""Id_Pedido"") as ""CantItems"",

        -- AHORA CONCATENAMOS TAMBIÉN LA TALLA (Instrucciones_Especiales) PARA EL RESUMEN
        (SELECT string_agg(CONCAT(prod.""Nombre_Comercial"", ' | ', COALESCE(d2.""Instrucciones_Especiales"", 'Unitalla'), ' | x', d2.""Cantidad""), '||') 
         FROM ""Tienda_Detalles_Pedido"" d2 
         JOIN ""Tienda_Productos_Venta"" prod ON d2.""Id_Producto_Venta"" = prod.""Id_Producto"" 
         WHERE d2.""Id_Pedido"" = p.""Id_Pedido"") as ""ListaProductos"",

        (SELECT COALESCE(dis.""Imagen_Previo_Url"", m.""UrlImagen"")
         FROM ""Tienda_Detalles_Pedido"" d3 
         LEFT JOIN ""Tienda_Multimedia_Productos"" m ON d3.""Id_Producto_Venta"" = m.""Id_Producto"" AND m.""Es_Principal"" = TRUE 
         LEFT JOIN ""Tienda_Solicitudes_Diseno"" dis ON d3.""Id_Solicitud_Diseno"" = dis.""Id_Solicitud""
         WHERE d3.""Id_Pedido"" = p.""Id_Pedido"" 
         ORDER BY d3.""Id_Detalle"" ASC LIMIT 1) as ""ImgPrevUrl"",
        (SELECT COUNT(*) FROM ""Tienda_Pedidos_Mensajes"" m 
        WHERE m.""Id_Pedido"" = p.""Id_Pedido"" AND m.""Es_Admin"" = FALSE 
        AND m.""Leido"" = FALSE) as ""MsjSinLeer""

    FROM ""Tienda_Pedidos"" p
    JOIN ""Sist_Usuarios"" u ON p.""Id_Usuario_Solicita"" = u.""Id_Usuario""
    WHERE p.""Id_Estatus"" = 30
    ORDER BY p.""Id_Pedido"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            int totalItems = Convert.ToInt32(r["CantItems"]);
                            string listaProd = r["ListaProductos"]?.ToString() ?? "";
                            // Hacemos el resumen corto más limpio para la vista
                            string resumen = listaProd.Replace("||", ", ").Replace(" | ", " ");
                            if (resumen.Length > 50) resumen = resumen.Substring(0, 47) + "...";

                            lista.Add(new PedidoEntregaViewModel
                            {
                                IdPedido = (int)r["Id_Pedido"],
                                Fecha = (DateTime)r["Fecha_Solicitud"],
                                Total = (decimal)r["Total_Estimado"],
                                CodigoEntrega = r["Codigo_Entrega"]?.ToString() ?? "---",
                                NombreCliente = r["Cliente"].ToString(),
                                ImagenPreviewUrl = r["ImgPrevUrl"]?.ToString(),
                                CantidadArticulos = totalItems,
                                Telefono = r["Telefono_Contacto"]?.ToString() ?? "No registrado",
                                Comentarios = r["Comentarios_Cliente"]?.ToString() ?? "",
                                DetalleProductos = listaProd, // Pasamos la data cruda estructurada para que JS la lea
                                ResumenCompra = resumen,
                                MensajesSinLeer = Convert.ToInt32(r["MsjSinLeer"]),
                                // ¡AGREGAMOS LA LECTURA DE LA FECHA!
                                FechaEstimada = r["Fecha_Estimada"] != DBNull.Value ? (DateTime)r["Fecha_Estimada"] : new DateTime(1900, 1, 1)
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return View(lista);
        }
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AsignarFechaGrupal(string idsPedidos, DateTime fechaAsignada)
        {
            // 1. Validar permisos del módulo de Entregas
            Parametros.Modulo moduloEntregas = Parametros.Modulos.Entregas;
            if (!User.TienePermiso(moduloEntregas, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permiso de edición para gestionar entregas.", TipoMensaje.Alerta);
                return RedirectToAction("EntregasAdmin");
            }

            if (string.IsNullOrEmpty(idsPedidos) || fechaAsignada == default)
            {
                MostrarMensaje("Error", "Faltan datos o la fecha seleccionada no es válida.", TipoMensaje.Error);
                return RedirectToAction("EntregasAdmin");
            }

            // 2. Sanitizar y convertir los IDs a una lista de enteros segura
            var listaIds = idsPedidos.Split(',')
                                    .Select(s => int.TryParse(s, out int id) ? id : 0)
                                    .Where(id => id > 0)
                                    .ToList();

            if (!listaIds.Any())
            {
                MostrarMensaje("Error", "No se encontraron pedidos válidos seleccionados.", TipoMensaje.Error);
                return RedirectToAction("EntregasAdmin");
            }

            var idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 3. Actualizar masivamente en la base de datos (Solo los que estén en estatus 30 - Pagados)
                            // Usamos la seguridad de parámetros para evitar SQL Injection inyectando la lista limpia
                            string sqlUpdate = $@"
                        UPDATE ""Tienda_Pedidos"" 
                        SET ""Fecha_Estimada"" = @fecha 
                        WHERE ""Id_Pedido"" IN ({string.Join(",", listaIds)}) AND ""Id_Estatus"" < 50";

                            using (var cmd = new NpgsqlCommand(sqlUpdate, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@fecha", fechaAsignada);
                                int filasAfectadas = await cmd.ExecuteNonQueryAsync();

                                if (filasAfectadas == 0)
                                {
                                    throw new Exception("Ninguno de los pedidos seleccionados cumple con el estado requerido para modificar su fecha.");
                                }
                            }

                            // 4. Registrar la acción en la Bitácora del sistema
                            string detalleBitacora = $"Asignación masiva de fecha estimada ({fechaAsignada:dd/MM/yyyy}) a los siguientes Pedidos: #{string.Join(", #", listaIds)}";
                            await Funciones.RegistrarBitacora(conexion, idAdmin, moduloEntregas, Parametros.AccionesBitacora.Editar, detalleBitacora, ip, trans);

                            await trans.CommitAsync();

                            // 5. NOTIFICACIONES EN SEGUNDO PLANO (Envío de correos individual a cada cliente)
                            // Lo corremos en un hilo secundario de fondo para que el Admin no experimente lentitud al procesar
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    using (var conMail = new NpgsqlConnection(_cadenaConexion))
                                    {
                                        await conMail.OpenAsync();
                                        string sqlClientes = $@"
                                    SELECT p.""Id_Pedido"", u.""Email"", u.""NombreCompleto"" 
                                    FROM ""Tienda_Pedidos"" p
                                    JOIN ""Sist_Usuarios"" u ON p.""Id_Usuario_Solicita"" = u.""Id_Usuario""
                                    WHERE p.""Id_Pedido"" IN ({string.Join(",", listaIds)})";

                                        using (var cmdMail = new NpgsqlCommand(sqlClientes, conMail))
                                        using (var r = await cmdMail.ExecuteReaderAsync())
                                        {
                                            while (await r.ReadAsync())
                                            {
                                                string emailCliente = r["Email"]?.ToString();
                                                string nombreCliente = r["NombreCompleto"]?.ToString();
                                                string idPed = r["Id_Pedido"].ToString();

                                                if (!string.IsNullOrEmpty(emailCliente))
                                                {
                                                    string asunto = $"Actualización de tu Pedido #{idPed}";
                                                    string html = $@"
                                                <h2 style='color:#00B8D4;'>Hola {nombreCliente},</h2>
                                                <p>Te informamos que la fecha estimada de entrega para tu pedido <strong>#{idPed}</strong> ha sido programada o actualizada para el <strong>{fechaAsignada:dd/MM/yyyy}</strong>.</p>
                                                <p>Recuerda tener a la mano tu código seguro de entrega al momento de recibir tus artículos.</p>";

                                                    await Funciones.EnviarCorreo(_configuration, emailCliente, asunto, html);
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { /* Silenciamos errores de SMTP de fondo para resguardar el flujo principal */ }
                            });

                            MostrarMensaje("Asignación Exitosa", $"Se ha actualizado la fecha a {listaIds.Count} pedidos correctamente.", TipoMensaje.Exito);
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo realizar la actualización masiva: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("EntregasAdmin");
        }
        [HttpGet]
        public async Task<IActionResult> ObtenerResumenEntregas(string ids = "")
        {
            var listaResumen = new List<object>();

            using (var conexion = new NpgsqlConnection(_cadenaConexion))
            {
                await conexion.OpenAsync();

                string filtroIds = "";
                if (!string.IsNullOrEmpty(ids))
                {
                    var idList = ids.Split(',')
                                    .Select(s => int.TryParse(s, out int n) ? n : 0)
                                    .Where(n => n > 0)
                                    .ToList();

                    if (idList.Any())
                    {
                        filtroIds = $" AND p.\"Id_Pedido\" IN ({string.Join(",", idList)})";
                    }
                }

                // AGREGAMOS EL ID_PRODUCTO_VENTA PARA EVITAR CAOS POR NOMBRES DUPLICADOS
                string sql = $@"
            SELECT 
                d.""Id_Producto_Venta"" AS IdProducto,
                prod.""Nombre_Comercial"" AS Producto,
                COALESCE(d.""Instrucciones_Especiales"", 'Unitalla') AS Especificacion,
                SUM(d.""Cantidad"") AS Cantidad
            FROM ""Tienda_Pedidos"" p
            JOIN ""Tienda_Detalles_Pedido"" d ON p.""Id_Pedido"" = d.""Id_Pedido""
            JOIN ""Tienda_Productos_Venta"" prod ON d.""Id_Producto_Venta"" = prod.""Id_Producto""
            WHERE p.""Id_Estatus"" = 30 {filtroIds}
            GROUP BY d.""Id_Producto_Venta"", prod.""Nombre_Comercial"", COALESCE(d.""Instrucciones_Especiales"", 'Unitalla')
            ORDER BY prod.""Nombre_Comercial"" ASC, d.""Id_Producto_Venta"" ASC";

                using (var cmd = new NpgsqlCommand(sql, conexion))
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        listaResumen.Add(new
                        {
                            idProducto = Convert.ToInt32(r["IdProducto"]),
                            producto = r["Producto"].ToString(),
                            especificacion = r["Especificacion"].ToString().Replace("Talla:", "").Trim(),
                            cantidad = Convert.ToInt32(r["Cantidad"])
                        });
                    }
                }
            }

            return Json(listaResumen);
        }
        // =========================================================
        // 11. CONFIRMAR ENTREGA (ADMIN)
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmarEntrega([FromForm] int idPedido, [FromForm] string codigoInput)
        {
            Parametros.Modulo Modulo = Parametros.Modulos.Entregas;
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permiso de edición en el módulo de entregas", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            if (idPedido <= 0 || string.IsNullOrEmpty(codigoInput))
            {
                MostrarMensaje("Error", "Faltan datos.", TipoMensaje.Error);
                return RedirectToAction("DetalleEntregaAdmin", new { id = idPedido });
            }

            var idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string codigoLimpio = codigoInput.ToUpper().Trim();

                    // Obtenemos también quién es el dueño del pedido y su correo 
                    string sqlCheck = @"SELECT p.""Id_Usuario_Solicita"", u.""NombreCompleto"", u.""Email"" 
                                FROM ""Tienda_Pedidos"" p
                                JOIN ""Sist_Usuarios"" u ON p.""Id_Usuario_Solicita"" = u.""Id_Usuario""
                                WHERE p.""Id_Pedido"" = @id AND UPPER(p.""Codigo_Entrega"") = @cod AND p.""Id_Estatus"" = 30";

                    string nombreCliente = "";
                    string emailCliente = "";
                    int idClientePedido = 0;

                    using (var cmdC = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmdC.Parameters.AddWithValue("@id", idPedido);
                        cmdC.Parameters.AddWithValue("@cod", codigoLimpio);
                        using (var r = await cmdC.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                idClientePedido = (int)r["Id_Usuario_Solicita"];
                                nombreCliente = r["NombreCompleto"].ToString();
                                emailCliente = r["Email"]?.ToString();
                            }
                        }
                    }

                    if (idClientePedido > 0)
                    {
                        // 1. Marcar como entregado en BD
                        string sqlUpdate = @"UPDATE ""Tienda_Pedidos"" SET ""Id_Estatus"" = 50, ""Fecha_Entrega"" = NOW(), ""Id_Usuario_Entrega"" = @admin WHERE ""Id_Pedido"" = @id";
                        using (var cmd = new NpgsqlCommand(sqlUpdate, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", idPedido);
                            cmd.Parameters.AddWithValue("@admin", idAdmin);
                            await cmd.ExecuteNonQueryAsync();
                        }

                        await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.PedidoEntregado, $"Entregó pedido #{idPedido} físico al cliente: {nombreCliente} (ID:{idClientePedido})", "Local", null);

                        // 3. CORREO DE ACUSE DE RECIBO (En hilo secundario para no bloquear al cajero/admin)
                        if (!string.IsNullOrEmpty(emailCliente))
                        {
                            _ = Task.Run(async () => {
                                try
                                {
                                    string asunto = $"Tu Pedido #{idPedido} ha sido entregado";
                                    string html = $@"
                                <h2 style='color:#6c757d;'>¡Pedido Completado!</h2>
                                <p>Hola {nombreCliente}, este es un aviso automático para confirmar que los artículos de tu pedido <strong>#{idPedido}</strong> te han sido entregados físicamente el día de hoy.</p>
                                <p>Si tú no recibiste este pedido, por favor contacta a la directiva inmediatamente.</p>";
                                    await Funciones.EnviarCorreo(_configuration, emailCliente, asunto, html);
                                }
                                catch { /* Ignorar fallos de SMTP en el hilo de fondo */ }
                            });
                        }

                        MostrarMensaje("Entrega Exitosa", "Pedido marcado como entregado.", TipoMensaje.Exito);
                        return RedirectToAction("EntregasAdmin");
                    }
                    else
                    {
                        MostrarMensaje("Código Incorrecto", "El código no coincide o el pedido ya fue entregado.", TipoMensaje.Error);
                        return RedirectToAction("DetalleEntregaAdmin", new { id = idPedido });
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("EntregasAdmin");
            }
        }

        // =========================================================
        // 12. DETALLE COMPRA (CLIENTE)
        // =========================================================
        [Authorize]
        public async Task<IActionResult> DetalleCompra(int id)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new PedidoDetalleViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Cabecera del Pedido
                    // CAMBIO: p.* trae todas, pero si tu modelo mapeaba manual, ojo. 
                    // Aquí recuperamos Ref_Pasarela explícitamente si es necesario o confiamos en p.* si el modelo lo soporta.
                    // Para asegurar, accedemos por nombre de columna nuevo.
                    string sqlCab = @"SELECT p.*, e.""Nombre"" as ""NomEst"", 
                  (SELECT COUNT(*) FROM ""Tienda_Pedidos_Mensajes"" m WHERE m.""Id_Pedido"" = p.""Id_Pedido"" AND m.""Es_Admin"" = TRUE AND m.""Leido"" = FALSE) as ""MsjSinLeer""
                  FROM ""Tienda_Pedidos"" p 
                  JOIN ""Tienda_Cat_Estatus"" e ON p.""Id_Estatus"" = e.""Id_Estatus"" 
                  WHERE p.""Id_Pedido"" = @id AND p.""Id_Usuario_Solicita"" = @uid";

                    using (var cmd = new NpgsqlCommand(sqlCab, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.IdPedido = (int)r["Id_Pedido"];
                                modelo.Fecha = (DateTime)r["Fecha_Solicitud"];
                                modelo.Total = (decimal)r["Total_Estimado"];
                                modelo.IdEstatus = (int)r["Id_Estatus"];
                                modelo.NombreEstatus = r["NomEst"].ToString();
                                modelo.CodigoEntrega = r["Codigo_Entrega"] != DBNull.Value ? r["Codigo_Entrega"].ToString() : "---";
                                modelo.IdPagoMP = r["Ref_Pasarela"]?.ToString();
                                modelo.CodigoCupon = r["Codigo_Cupon_Aplicado"] != DBNull.Value ? r["Codigo_Cupon_Aplicado"].ToString() : null;
                                modelo.MontoDescuento = r["Monto_Descuento"] != DBNull.Value ? (decimal)r["Monto_Descuento"] : 0;
                                modelo.MensajesSinLeer = Convert.ToInt32(r["MsjSinLeer"]);
                            }
                            else return RedirectToAction("MisCompras");
                        }
                    }

                    // 2. Detalles del Pedido
                    string sqlDet = @"
        SELECT d.""Cantidad"", d.""Precio_Unitario"", d.""Instrucciones_Especiales"", d.""Es_Personalizado"", 
           d.""Id_Solicitud_Diseno"",
           prod.""Nombre_Comercial"", 
           m.""UrlImagen"" as ""ImgUrl"",
           dis.""Imagen_Previo_Url"" as ""ImgDiseno""
        FROM ""Tienda_Detalles_Pedido"" d 
        JOIN ""Tienda_Productos_Venta"" prod ON d.""Id_Producto_Venta"" = prod.""Id_Producto"" 
        LEFT JOIN ""Tienda_Multimedia_Productos"" m ON prod.""Id_Producto"" = m.""Id_Producto"" AND m.""Es_Principal"" = TRUE 
        LEFT JOIN ""Tienda_Solicitudes_Diseno"" dis ON d.""Id_Solicitud_Diseno"" = dis.""Id_Solicitud""
        WHERE d.""Id_Pedido"" = @id";

                    using (var cmd = new NpgsqlCommand(sqlDet, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string nombre = r["Nombre_Comercial"].ToString();
                                string imgFinal = r["ImgUrl"]?.ToString();

                                if (r["Id_Solicitud_Diseno"] != DBNull.Value && r["ImgDiseno"] != DBNull.Value)
                                {
                                    imgFinal = r["ImgDiseno"].ToString();
                                }

                                modelo.Productos.Add(new ItemDetalle
                                {
                                    Nombre = nombre,
                                    Cantidad = (int)r["Cantidad"],
                                    Precio = (decimal)r["Precio_Unitario"],
                                    Especificaciones = r["Instrucciones_Especiales"].ToString(),
                                    EsPersonalizado = (bool)r["Es_Personalizado"],
                                    ImagenUrl = imgFinal
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); return RedirectToAction("MisCompras"); }

            return View(modelo);
        }

        // =========================================================
        // 13. DETALLE ENTREGA (ADMIN)
        // =========================================================
        [Authorize]
        public async Task<IActionResult> DetalleEntregaAdmin(int id)
        {
            Parametros.Modulo Modulo = Parametros.Modulos.Entregas;
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Error", "No tienes permiso de lectura en el módulo de entregas", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }
            var modelo = new PedidoDetalleViewModel();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Cabecera (Agregamos Teléfono y Comentarios)
                    string sqlCab = @"SELECT p.*, p.""Fecha_Estimada"", e.""Nombre"" as ""NomEst"", u.""NombreCompleto"", u.""Email"",
                  (SELECT COUNT(*) FROM ""Tienda_Pedidos_Mensajes"" m WHERE m.""Id_Pedido"" = p.""Id_Pedido"" AND m.""Es_Admin"" = FALSE AND m.""Leido"" = FALSE) as ""MsjSinLeer""
                  FROM ""Tienda_Pedidos"" p 
                  JOIN ""Tienda_Cat_Estatus"" e ON p.""Id_Estatus"" = e.""Id_Estatus"" 
                  JOIN ""Sist_Usuarios"" u ON p.""Id_Usuario_Solicita"" = u.""Id_Usuario"" 
                  WHERE p.""Id_Pedido"" = @id";

                    using (var cmd = new NpgsqlCommand(sqlCab, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.IdPedido = (int)r["Id_Pedido"];
                                modelo.Fecha = (DateTime)r["Fecha_Solicitud"];
                                modelo.Total = (decimal)r["Total_Estimado"];
                                modelo.IdEstatus = (int)r["Id_Estatus"];
                                modelo.NombreEstatus = r["NomEst"].ToString();
                                modelo.NombreCliente = r["NombreCompleto"].ToString();
                                modelo.EmailCliente = r["Email"].ToString();
                                modelo.FechaEstimada = (DateTime)r["Fecha_Estimada"];
                                modelo.CodigoEntrega = r["Codigo_Entrega"] != DBNull.Value ? r["Codigo_Entrega"].ToString() : "---";
                                modelo.CodigoCupon = r["Codigo_Cupon_Aplicado"] != DBNull.Value ? r["Codigo_Cupon_Aplicado"].ToString() : null;
                                modelo.MontoDescuento = r["Monto_Descuento"] != DBNull.Value ? (decimal)r["Monto_Descuento"] : 0;
                                modelo.Telefono = r["Telefono_Contacto"]?.ToString() ?? "No registrado";
                                modelo.Comentarios = r["Comentarios_Cliente"]?.ToString() ?? "Ninguno";
                                modelo.MensajesSinLeer = Convert.ToInt32(r["MsjSinLeer"]);
                            }
                            else return RedirectToAction("EntregasAdmin");
                        }
                    }

                    // 2. Detalles (Con Lógica de Imagen Personalizada)
                    string sqlDet = @"
                SELECT d.""Cantidad"", d.""Precio_Unitario"", d.""Instrucciones_Especiales"", d.""Es_Personalizado"", 
                   d.""Id_Solicitud_Diseno"", prod.""Nombre_Comercial"", m.""UrlImagen"" as ""ImgUrl"",
                   dis.""Imagen_Previo_Url"" as ""ImgDiseno""
                FROM ""Tienda_Detalles_Pedido"" d 
                JOIN ""Tienda_Productos_Venta"" prod ON d.""Id_Producto_Venta"" = prod.""Id_Producto"" 
                LEFT JOIN ""Tienda_Multimedia_Productos"" m ON prod.""Id_Producto"" = m.""Id_Producto"" AND m.""Es_Principal"" = TRUE 
                LEFT JOIN ""Tienda_Solicitudes_Diseno"" dis ON d.""Id_Solicitud_Diseno"" = dis.""Id_Solicitud""
                WHERE d.""Id_Pedido"" = @id";

                    using (var cmd = new NpgsqlCommand(sqlDet, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string imgFinal = r["ImgUrl"]?.ToString();
                                if (r["Id_Solicitud_Diseno"] != DBNull.Value && r["ImgDiseno"] != DBNull.Value)
                                {
                                    imgFinal = r["ImgDiseno"].ToString();
                                }

                                modelo.Productos.Add(new ItemDetalle
                                {
                                    Nombre = r["Nombre_Comercial"].ToString(),
                                    Cantidad = (int)r["Cantidad"],
                                    Precio = (decimal)r["Precio_Unitario"],
                                    Especificaciones = r["Instrucciones_Especiales"].ToString(),
                                    EsPersonalizado = (bool)r["Es_Personalizado"],
                                    ImagenUrl = imgFinal// Imagen correcta asignada
                                });
                            }
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("EntregasAdmin"); 
            }
            return View(modelo);
        }

        // =========================================================
        // 15. DISEÑADOR (REQUIERE LOGIN)
        // =========================================================
        [Authorize]
        public async Task<IActionResult> Disenador(int idBase)
        {
            var modelo = new DisenadorViewModel { Id_Producto_Base = idBase };
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"SELECT ""Nombre_Comercial"", m.""UrlImagen"" as ""ImgUrl"",
                               p.""Area_X"", p.""Area_Y"", p.""Area_Ancho"", p.""Area_Alto""
                               FROM ""Tienda_Productos_Venta"" p
                               LEFT JOIN ""Tienda_Multimedia_Productos"" m ON p.""Id_Producto"" = m.""Id_Producto"" AND m.""Es_Principal"" = TRUE
                               WHERE p.""Id_Producto"" = @id";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idBase);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.Nombre_Base = r["Nombre_Comercial"].ToString();
                                modelo.Imagen_Base_Url = r["ImgUrl"]?.ToString();
                                modelo.Area_X = r["Area_X"] != DBNull.Value ? (int)r["Area_X"] : 25;
                                modelo.Area_Y = r["Area_Y"] != DBNull.Value ? (int)r["Area_Y"] : 20;
                                modelo.Area_Ancho = r["Area_Ancho"] != DBNull.Value ? (int)r["Area_Ancho"] : 50;
                                modelo.Area_Alto = r["Area_Alto"] != DBNull.Value ? (int)r["Area_Alto"] : 60;
                            }
                            else return RedirectToAction("Index");
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index"); 
            }
            return View(modelo);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarDiseno([FromForm] DisenadorViewModel form)
        {
            if (form.Id_Producto_Base <= 0)
            {
                MostrarMensaje("Error", "No hemos podido identificar el producto a diseñar", TipoMensaje.Error);
                return RedirectToAction("Index"); 
            }
            if (form.ArchivoOriginal == null || string.IsNullOrEmpty(form.ImagenPrevioBase64))
            {
                MostrarMensaje("Error", "Faltan datos del diseño.", TipoMensaje.Error);
                return RedirectToAction("Disenador", new { idBase = form.Id_Producto_Base });
            }

            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Desconocida";
            string folderPath = $"{sAmbiente}/Imágenes/Tienda/Disenos/Usuario_{idUser}";

            try
            {
                // 1. Subir Archivo Original a Cloudinary
                string urlOriginal = "";
                using (var ms = new MemoryStream())
                {
                    await form.ArchivoOriginal.CopyToAsync(ms);
                    ms.Position = 0;
                    var uploadParamsObj = new ImageUploadParams() { File = new FileDescription(form.ArchivoOriginal.FileName, ms), Folder = folderPath };
                    var resultObj = await _cloudinary.UploadAsync(uploadParamsObj);
                    urlOriginal = resultObj.SecureUrl.ToString();
                }

                // 2. Subir Imagen Previo (Canvas en Base64) a Cloudinary
                string urlPrevio = "";
                string previoLimpio = form.ImagenPrevioBase64.Contains(",") ? form.ImagenPrevioBase64.Split(',')[1] : form.ImagenPrevioBase64;
                byte[] prevBytes = Convert.FromBase64String(previoLimpio);
                using (var msPrev = new MemoryStream(prevBytes))
                {
                    var uploadParamsPrev = new ImageUploadParams() { File = new FileDescription("previo.png", msPrev), Folder = folderPath };
                    var resultPrev = await _cloudinary.UploadAsync(uploadParamsPrev);
                    urlPrevio = resultPrev.SecureUrl.ToString();
                }

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // Guardamos las URLs seguras generadas por Cloudinary
                            string sql = @"INSERT INTO ""Tienda_Solicitudes_Diseno"" 
                                 (""Id_Usuario"", ""Id_Producto_Base"", ""Imagen_Original_Url"", ""Imagen_Previo_Url"", ""Configuracion_Json"", ""Id_Estatus_Diseno"")
                                 VALUES (@uid, @prod, @orig, @prev, @conf, 1)
                                 RETURNING ""Id_Solicitud""";

                            int idSolicitud;
                            using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@uid", idUser);
                                cmd.Parameters.AddWithValue("@prod", form.Id_Producto_Base);
                                cmd.Parameters.AddWithValue("@orig", urlOriginal);
                                cmd.Parameters.AddWithValue("@prev", urlPrevio);
                                cmd.Parameters.AddWithValue("@conf", form.ConfiguracionJson ?? "{}");
                                idSolicitud = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                            }

                            string detalle = $"Solicitud de diseño {idSolicitud} creada";
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Crear, detalle, ip, trans);

                            await trans.CommitAsync();

                            // ========================================================================
                            // --- ALERTA POR CORREO: NUEVO DISEÑO SOLICITADO (ADMIN) ---
                            // ========================================================================
                            try
                            {
                                string nombreUsuario = User.FindFirst(ClaimTypes.Name)?.Value ?? "Un usuario";
                                string urlAdmin = Url.Action("Solicitudes", "TiendaDisenos", null, Request.Scheme);

                                string htmlSolicitud = $@"
                                <div style='font-family: Arial, Helvetica, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e0e0e0; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px rgba(0,0,0,0.05);'>
                                    <div style='background-color: #6f42c1; padding: 20px; text-align: center; color: #ffffff;'>
                                        <h2 style='margin: 0; font-size: 22px; font-weight: 600;'>🎨 Nuevo Diseño Recibido</h2>
                                    </div>
                                    <div style='padding: 30px; background-color: #ffffff; color: #333333;'>
                                        <p style='font-size: 16px; margin-top: 0;'>Hola, <strong>Equipo Revisor</strong>:</p>
                                        <p style='font-size: 16px; line-height: 1.6;'>Se ha recibido una nueva propuesta de diseño personalizado en la tienda y requiere revisión.</p>

                                        <div style='background-color: #f8f9fa; border-left: 5px solid #6f42c1; padding: 18px; margin: 25px 0; border-radius: 4px;'>
                                            <ul style='margin: 0; padding-left: 20px; line-height: 1.8; font-size: 15px;'>
                                                <li><strong>Folio Solicitud:</strong> #{idSolicitud}</li>
                                                <li><strong>ID Producto Base:</strong> #{form.Id_Producto_Base}</li>
                                                <li><strong>Solicitado por:</strong> {nombreUsuario} (ID: {idUser})</li>
                                                <li><strong>Fecha:</strong> {DateTime.Now.ToString("dd/MM/yyyy HH:mm")}</li>
                                            </ul>
                                        </div>
            
                                        <div style='text-align: center; margin-top: 30px; margin-bottom: 10px;'>
                                            <a href='{urlAdmin}' style='background-color: #343a40; color: #ffffff; padding: 14px 30px; text-decoration: none; border-radius: 6px; font-weight: bold; font-size: 15px; display: inline-block;'>
                                                Revisar Solicitud
                                            </a>
                                        </div>
                                    </div>
                                </div>";

                                await Funciones.EnviarAlertaPorBaseDatos(_configuration, "TIENDA_NUEVO_DISENO", $"Revisión requerida: Diseño #{idSolicitud}", htmlSolicitud);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error alerta diseño: {ex.Message}");
                            }
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
                MostrarMensaje("Diseño Enviado", "Pendiente de aprobación.", TipoMensaje.Exito);
                return RedirectToAction("MisDisenos", "Perfil");
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Disenador", new { idBase = form.Id_Producto_Base });
            }
        }
        [Authorize]
        public async Task<IActionResult> MensajesPedido(int id)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            return await ObtenerVistaMensajes(id, idUser, esAdmin: false);
        }

        [Authorize]
        public async Task<IActionResult> MensajesPedidoAdmin(int id)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            // Valida permiso de admin
            if (!User.TienePermiso(Parametros.Modulos.Entregas, PermisoLeer))
            {
                MostrarMensaje("Error", "No tienes permiso de lectura en el módulo de entregas.", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }
            return await ObtenerVistaMensajes(id, idUser, esAdmin: true);
        }

        private async Task<IActionResult> ObtenerVistaMensajes(int idPedido, int idUser, bool esAdmin)
        {
            var modelo = new PedidoDetalleViewModel { IdPedido = idPedido };
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Marcar mensajes como leídos
                    string updSql = esAdmin
                        ? @"UPDATE ""Tienda_Pedidos_Mensajes"" SET ""Leido"" = TRUE WHERE ""Id_Pedido"" = @id AND ""Es_Admin"" = FALSE AND ""Leido"" = FALSE"
                        : @"UPDATE ""Tienda_Pedidos_Mensajes"" SET ""Leido"" = TRUE WHERE ""Id_Pedido"" = @id AND ""Es_Admin"" = TRUE AND ""Leido"" = FALSE";

                    using (var cmdU = new NpgsqlCommand(updSql, conexion))
                    {
                        cmdU.Parameters.AddWithValue("@id", idPedido);
                        await cmdU.ExecuteNonQueryAsync();
                    }

                    // 2. Obtener estado y nombre del cliente para la cabecera del chat
                    using (var cmdC = new NpgsqlCommand(@"SELECT p.""Id_Estatus"", u.""NombreCompleto"" 
                                      FROM ""Tienda_Pedidos"" p
                                      JOIN ""Sist_Usuarios"" u ON p.""Id_Usuario_Solicita"" = u.""Id_Usuario""
                                      WHERE p.""Id_Pedido"" = @id", conexion))
                    {
                        cmdC.Parameters.AddWithValue("@id", idPedido);
                        using (var rC = await cmdC.ExecuteReaderAsync())
                        {
                            if (await rC.ReadAsync())
                            {
                                modelo.IdEstatus = Convert.ToInt32(rC["Id_Estatus"]);
                                modelo.NombreCliente = rC["NombreCompleto"].ToString();
                            }
                        }
                    }

                    // 3. Obtener Mensajes
                    string sqlMsj = @"SELECT m.""Id_Mensaje"", m.""Id_Usuario"", m.""Mensaje"", m.""Fecha_Registro"", m.""Es_Admin"", u.""NombreCompleto""
                              FROM ""Tienda_Pedidos_Mensajes"" m
                              JOIN ""Sist_Usuarios"" u ON m.""Id_Usuario"" = u.""Id_Usuario""
                              WHERE m.""Id_Pedido"" = @id 
                              ORDER BY m.""Fecha_Registro"" ASC";

                    using (var cmdM = new NpgsqlCommand(sqlMsj, conexion))
                    {
                        cmdM.Parameters.AddWithValue("@id", idPedido);
                        using (var rM = await cmdM.ExecuteReaderAsync())
                        {
                            while (await rM.ReadAsync())
                            {
                                modelo.Mensajes.Add(new MensajePedidoViewModel
                                {
                                    Id_Mensaje = (int)rM["Id_Mensaje"],
                                    NombreUsuario = rM["NombreCompleto"].ToString(),
                                    Mensaje = rM["Mensaje"].ToString(),
                                    Fecha = (DateTime)rM["Fecha_Registro"],
                                    Es_Admin = (bool)rM["Es_Admin"],
                                    Es_Propio = esAdmin ? (bool)rM["Es_Admin"] : !(bool)rM["Es_Admin"]
                                });
                            }
                        }
                    }

                    // 4. NUEVO: Obtener Productos para el Carrusel del Chat
                    string sqlDet = @"
                SELECT d.""Cantidad"", d.""Precio_Unitario"", d.""Instrucciones_Especiales"", d.""Es_Personalizado"", 
                       d.""Id_Solicitud_Diseno"", prod.""Nombre_Comercial"", m.""UrlImagen"" as ""ImgUrl"",
                       dis.""Imagen_Previo_Url"" as ""ImgDiseno""
                FROM ""Tienda_Detalles_Pedido"" d 
                JOIN ""Tienda_Productos_Venta"" prod ON d.""Id_Producto_Venta"" = prod.""Id_Producto"" 
                LEFT JOIN ""Tienda_Multimedia_Productos"" m ON prod.""Id_Producto"" = m.""Id_Producto"" AND m.""Es_Principal"" = TRUE 
                LEFT JOIN ""Tienda_Solicitudes_Diseno"" dis ON d.""Id_Solicitud_Diseno"" = dis.""Id_Solicitud""
                WHERE d.""Id_Pedido"" = @id";

                    using (var cmdP = new NpgsqlCommand(sqlDet, conexion))
                    {
                        cmdP.Parameters.AddWithValue("@id", idPedido);
                        using (var rP = await cmdP.ExecuteReaderAsync())
                        {
                            while (await rP.ReadAsync())
                            {
                                string imgFinal = rP["ImgUrl"]?.ToString();
                                if (rP["Id_Solicitud_Diseno"] != DBNull.Value && rP["ImgDiseno"] != DBNull.Value)
                                {
                                    imgFinal = rP["ImgDiseno"].ToString();
                                }

                                modelo.Productos.Add(new ItemDetalle
                                {
                                    Nombre = rP["Nombre_Comercial"].ToString(),
                                    Cantidad = (int)rP["Cantidad"],
                                    Precio = (decimal)rP["Precio_Unitario"],
                                    Especificaciones = rP["Instrucciones_Especiales"].ToString(),
                                    EsPersonalizado = (bool)rP["Es_Personalizado"],
                                    ImagenUrl = imgFinal
                                });
                            }
                        }
                    }
                }
            }
            catch { }

            ViewBag.EsAdmin = esAdmin;
            return View("MensajesPedido", modelo);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnviarMensajePedido(int idPedido, string mensaje, bool esAdmin)
        {
            if (string.IsNullOrWhiteSpace(mensaje))
            {
                MostrarMensaje("Error", "No se recibió el mensaje a enviar", TipoMensaje.Error);
                return Redirect(Request.Headers["Referer"].ToString());
            }

            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"INSERT INTO ""Tienda_Pedidos_Mensajes"" (""Id_Pedido"", ""Id_Usuario"", ""Mensaje"", ""Es_Admin"")
                           VALUES (@ped, @uid, @msj, @admin)";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@ped", idPedido);
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        cmd.Parameters.AddWithValue("@msj", mensaje.Trim());
                        cmd.Parameters.AddWithValue("@admin", esAdmin);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo enviar el mensaje.", TipoMensaje.Error);
            }

            // Retornamos a la vista correcta dependiendo de quién envió el mensaje
            if (esAdmin)
                return RedirectToAction("MensajesPedidoAdmin", new { id = idPedido });
            else
                return RedirectToAction("MensajesPedido", new { id = idPedido });
        }
    }
}