using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Models;
using RedAJP.Globales;
using static RedAJP.Globales.Parametros;
using System.Data;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace RedAJP.Controllers
{
    [Authorize]
    public class TiendaProductosController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.TiendaConfig;
        private readonly Cloudinary _cloudinary;

        public TiendaProductosController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");

            // 1. Inicializar Cloudinary
            Account account = new Account(
                configuration["Cloudinary:CloudName"],
                configuration["Cloudinary:ApiKey"],
                configuration["Cloudinary:ApiSecret"]
            );
            _cloudinary = new Cloudinary(account);
            _cloudinary.Api.Secure = true;
        }

        // ==========================================
        // HELPER: CARGAR CATEGORÍAS (Para los Dropdowns)
        // ==========================================
        private async Task CargarCategoriasViewBag(NpgsqlConnection conexion)
        {
            var categorias = new List<dynamic>();
            bool abrirLocal = false;

            if (conexion.State != ConnectionState.Open) { await conexion.OpenAsync(); abrirLocal = true; }

            try
            {
                using (var cmd = new NpgsqlCommand("SELECT \"Id_Categoria\", \"Nombre\", \"Medidas_Default\" FROM \"Tienda_Cat_Categorias\" WHERE \"Activo\"=TRUE ORDER BY \"Nombre\"", conexion))
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    while (r.Read()) categorias.Add(new
                    {
                        Id = (int)r["Id_Categoria"],
                        Nombre = r["Nombre"].ToString(),
                        Medidas = r["Medidas_Default"].ToString()
                    });
                }
            }
            finally { if (abrirLocal) await conexion.CloseAsync(); }

            ViewBag.Categorias = categorias;
        }

        // ==========================================
        // 1. LISTADO (INDEX) - GET
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(string busqueda, int? idCategoria)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Error", "No tienes permiso de lectura en ésta ventana", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home"); 
            }

            var lista = new List<ProductoListadoItem>();

            // Guardamos filtros para la vista
            ViewData["Busqueda"] = busqueda;
            ViewData["IdCategoria"] = idCategoria;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    await CargarCategoriasViewBag(conexion);

                    // CAMBIO: Leemos UrlImagen en lugar de decodificar Base64
                    string sql = @"SELECT p.*, c.""Nombre"" as ""Categoria"",
                           m.""UrlImagen"" as ""ImgUrl"",
                           COALESCE((SELECT SUM(""Stock"") FROM ""Tienda_Productos_Medidas"" pm WHERE pm.""Id_Producto"" = p.""Id_Producto""), 0) as ""StockReal""
                           FROM ""Tienda_Productos_Venta"" p
                           INNER JOIN ""Tienda_Cat_Categorias"" c ON p.""Id_Categoria"" = c.""Id_Categoria""
                           LEFT JOIN ""Tienda_Multimedia_Productos"" m ON p.""Id_Producto"" = m.""Id_Producto"" AND m.""Es_Principal"" = TRUE
                           WHERE 1=1 ";

                    if (!string.IsNullOrEmpty(busqueda))
                    {
                        sql += @" AND (p.""Nombre_Comercial"" ILIKE @busqueda OR p.""Descripcion"" ILIKE @busqueda) ";
                    }

                    if (idCategoria.HasValue)
                    {
                        sql += @" AND p.""Id_Categoria"" = @idCategoria ";
                    }

                    sql += @" ORDER BY p.""Nombre_Comercial"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        if (!string.IsNullOrEmpty(busqueda)) cmd.Parameters.AddWithValue("@busqueda", $"%{busqueda}%");
                        if (idCategoria.HasValue) cmd.Parameters.AddWithValue("@idCategoria", idCategoria.Value);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (r.Read())
                            {
                                lista.Add(new ProductoListadoItem
                                {
                                    Id_Producto = (int)r["Id_Producto"],
                                    Nombre = r["Nombre_Comercial"].ToString(),
                                    Categoria = r["Categoria"].ToString(),
                                    Precio = (decimal)r["Precio_Venta"],
                                    Es_Personalizable = (bool)r["Es_Personalizable"],
                                    Stock = Convert.ToInt32(r["StockReal"]),
                                    Activo = (bool)r["Activo"],
                                    ImagenUrl = r["ImgUrl"]?.ToString() 
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return View(lista);
        }

        // ==========================================
        // 2. EDITOR (CARGAR FORMULARIO) - GET
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Editor(int id)
        {
            bool bNuevo = (id == 0);
            if (!User.TienePermiso(Modulo, bNuevo ? PermisoCrear : PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permiso para esta acción", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var modelo = new EditorProductoViewModel
            {
                Id_Producto = id,
                Activo = true,
                Usa_Medidas_Categoria = true,
                Area_X = 25,
                Area_Y = 20,
                Area_Ancho = 50,
                Area_Alto = 60
            };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    await CargarCategoriasViewBag(conexion);

                    if (id > 0)
                    {
                        // 1. Cargar Datos del Producto
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
                                    modelo.Nombre_Comercial = r["Nombre_Comercial"].ToString();
                                    modelo.Id_Categoria = (int)r["Id_Categoria"];
                                    modelo.Descripcion = r["Descripcion"]?.ToString();
                                    modelo.Precio_Venta = (decimal)r["Precio_Venta"];
                                    modelo.Es_Personalizable = (bool)r["Es_Personalizable"];
                                    modelo.Stock_Tienda = 0;
                                    modelo.Activo = (bool)r["Activo"];
                                    modelo.ImagenActualUrl = r["ImgUrl"]?.ToString();
                                    modelo.ImagenImpresionUrl = r["Url_Imagen_Impresion"] != DBNull.Value ? r["Url_Imagen_Impresion"].ToString() : null;
                                    modelo.ImagenImpresionPosteriorUrl = r["Url_Imagen_Impresion_Posterior"] != DBNull.Value ? r["Url_Imagen_Impresion_Posterior"].ToString() : null;
                                    modelo.Cobrar_Comision_Extra = r["Cobrar_Comision_Extra"] != DBNull.Value ? (bool)r["Cobrar_Comision_Extra"] : true;

                                    if (r["Usa_Medidas_Categoria"] != DBNull.Value)
                                        modelo.Usa_Medidas_Categoria = (bool)r["Usa_Medidas_Categoria"];

                                    modelo.Area_X = r["Area_X"] != DBNull.Value ? (int)r["Area_X"] : 25;
                                    modelo.Area_Y = r["Area_Y"] != DBNull.Value ? (int)r["Area_Y"] : 20;
                                    modelo.Area_Ancho = r["Area_Ancho"] != DBNull.Value ? (int)r["Area_Ancho"] : 50;
                                    modelo.Area_Alto = r["Area_Alto"] != DBNull.Value ? (int)r["Area_Alto"] : 60;
                                }
                            }
                        }

                        // 1.5 Galería Adicional (Solo las extras) - Leyendo UrlImagen
                        string sqlGaleria = @"SELECT ""UrlImagen"" FROM ""Tienda_Multimedia_Productos"" 
                                              WHERE ""Id_Producto"" = @id AND ""Es_Principal"" = FALSE 
                                              ORDER BY ""Id_Imagen"" ASC";

                        using (var cmdG = new NpgsqlCommand(sqlGaleria, conexion))
                        {
                            cmdG.Parameters.AddWithValue("@id", id);
                            using (var rG = await cmdG.ExecuteReaderAsync())
                            {
                                while (await rG.ReadAsync())
                                {
                                    modelo.GaleriaActualUrls.Add(rG["UrlImagen"].ToString());
                                }
                            }
                        }

                        // 2. Cargar Medidas y Calcular Stock Visual
                        string sqlMed = @"SELECT ""Nombre"", ""Stock"" FROM ""Tienda_Productos_Medidas"" WHERE ""Id_Producto"" = @id ORDER BY ""Id_Medida""";
                        using (var cmdM = new NpgsqlCommand(sqlMed, conexion))
                        {
                            cmdM.Parameters.AddWithValue("@id", id);
                            using (var r = await cmdM.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    int s = (int)r["Stock"];
                                    modelo.TallasStock.Add(new MedidaStockItem
                                    {
                                        Nombre = r["Nombre"].ToString(),
                                        Stock = s
                                    });
                                    modelo.Stock_Tienda += s; // Sumamos para mostrar en el input 'Total'
                                }
                            }
                        }

                        // 3. Validación de Integridad
                        bool enUso = false;
                        var cmdC1 = new NpgsqlCommand($"SELECT COUNT(*) FROM \"Tienda_Detalles_Pedido\" WHERE \"Id_Producto_Venta\"={id}", conexion);
                        if ((long)await cmdC1.ExecuteScalarAsync() > 0) enUso = true;

                        ViewBag.ProductoEnUso = enUso;
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(EditorProductoViewModel modelo)
        {
            bool bNuevo = (modelo.Id_Producto == 0);
            if (!User.TienePermiso(Modulo, bNuevo ? PermisoCrear : PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permiso para realizar esta acción", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            //Evitar que manden un producto con costo en 0
            if (modelo.Precio_Venta <= 0)
            {
                MostrarMensaje("Error", "El precio de venta debe ser mayor a 0", TipoMensaje.Error);
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await CargarCategoriasViewBag(conexion);
                }
                return View("Editor", modelo);
            }

            if (!modelo.Es_Personalizable)
            {
                modelo.Area_X = 0; modelo.Area_Y = 0; modelo.Area_Ancho = 0; modelo.Area_Alto = 0;
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            int idProd = modelo.Id_Producto;

                            // A. INSERT / UPDATE (Con columna Cobrar_Comision_Extra incluida)
                            if (idProd == 0)
                            {
                                string sql = @"INSERT INTO ""Tienda_Productos_Venta"" 
                            (""Id_Categoria"", ""Nombre_Comercial"", ""Descripcion"", ""Precio_Venta"", ""Es_Personalizable"", ""Activo"", ""Usa_Medidas_Categoria"", ""Area_X"", ""Area_Y"", ""Area_Ancho"", ""Area_Alto"", ""Cobrar_Comision_Extra"")
                            VALUES (@cat, @nom, @desc, @pre, @per, @act, @usa, @ax, @ay, @aw, @ah, @comision) 
                            RETURNING ""Id_Producto""";

                                using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                                {
                                    SetParams(cmd, modelo);
                                    idProd = (int)await cmd.ExecuteScalarAsync();
                                }
                                modelo.Id_Producto = idProd;
                            }
                            else
                            {
                                string sql = @"UPDATE ""Tienda_Productos_Venta"" 
                            SET ""Id_Categoria""=@cat, ""Nombre_Comercial""=@nom, ""Descripcion""=@desc, ""Precio_Venta""=@pre, ""Es_Personalizable""=@per, ""Activo""=@act, ""Usa_Medidas_Categoria""=@usa, ""Area_X""=@ax, ""Area_Y""=@ay, ""Area_Ancho""=@aw, ""Area_Alto""=@ah, ""Cobrar_Comision_Extra""=@comision 
                            WHERE ""Id_Producto""=@id";

                                using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                                {
                                    SetParams(cmd, modelo);
                                    cmd.Parameters.AddWithValue("@id", idProd);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }

                            string folderPath = $"{sAmbiente}/Imágenes/Tienda/Productos/{idProd}";

                            // B. IMAGEN PRINCIPAL EN CLOUDINARY (Se mantiene intacto)
                            if (modelo.ArchivoImagen != null)
                            {
                                string urlVieja = null;
                                using (var cmdO = new NpgsqlCommand($"SELECT \"UrlImagen\" FROM \"Tienda_Multimedia_Productos\" WHERE \"Id_Producto\"={idProd} AND \"Es_Principal\"=TRUE", conexion, trans))
                                {
                                    var result = await cmdO.ExecuteScalarAsync();
                                    if (result != null && result != DBNull.Value) urlVieja = result.ToString();
                                }

                                if (!string.IsNullOrEmpty(urlVieja)) await DestruirImagenCloudinary(urlVieja);

                                string urlNueva = await SubirImagenCloudinary(modelo.ArchivoImagen, folderPath);
                                await new NpgsqlCommand($"DELETE FROM \"Tienda_Multimedia_Productos\" WHERE \"Id_Producto\"={idProd} AND \"Es_Principal\"=TRUE", conexion, trans).ExecuteNonQueryAsync();

                                string sqlImgP = @"INSERT INTO ""Tienda_Multimedia_Productos"" (""Id_Producto"", ""UrlImagen"", ""Es_Principal"") VALUES (@id, @url, TRUE)";
                                using (var cmdImg = new NpgsqlCommand(sqlImgP, conexion, trans))
                                {
                                    cmdImg.Parameters.AddWithValue("@id", idProd);
                                    cmdImg.Parameters.AddWithValue("@url", urlNueva);
                                    await cmdImg.ExecuteNonQueryAsync();
                                }
                            }

                            // C. IMÁGENES DE GALERÍA EN CLOUDINARY (Se mantiene intacto)
                            if (modelo.ArchivosGaleria != null && modelo.ArchivosGaleria.Count > 0)
                            {
                                var urlsViejas = new List<string>();
                                using (var cmdOG = new NpgsqlCommand($"SELECT \"UrlImagen\" FROM \"Tienda_Multimedia_Productos\" WHERE \"Id_Producto\"={idProd} AND \"Es_Principal\"=FALSE", conexion, trans))
                                using (var rOG = await cmdOG.ExecuteReaderAsync())
                                {
                                    while (await rOG.ReadAsync()) urlsViejas.Add(rOG["UrlImagen"].ToString());
                                }

                                foreach (var url in urlsViejas) await DestruirImagenCloudinary(url);
                                await new NpgsqlCommand($"DELETE FROM \"Tienda_Multimedia_Productos\" WHERE \"Id_Producto\"={idProd} AND \"Es_Principal\"=FALSE", conexion, trans).ExecuteNonQueryAsync();

                                foreach (var archivo in modelo.ArchivosGaleria.Take(4))
                                {
                                    string urlNuevaG = await SubirImagenCloudinary(archivo, folderPath);
                                    string sqlImgG = @"INSERT INTO ""Tienda_Multimedia_Productos"" (""Id_Producto"", ""UrlImagen"", ""Es_Principal"") VALUES (@id, @url, FALSE)";
                                    using (var cmdImg = new NpgsqlCommand(sqlImgG, conexion, trans))
                                    {
                                        cmdImg.Parameters.AddWithValue("@id", idProd);
                                        cmdImg.Parameters.AddWithValue("@url", urlNuevaG);
                                        await cmdImg.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            // =========================================================
                            // D. GESTIÓN FLUIDA: DISEÑO IMPRESIÓN FRONTAL
                            // =========================================================
                            if (modelo.BorrarImagenImpresion || modelo.ArchivoImagenImpresion != null)
                            {
                                string urlViejaImp = null;
                                using (var cmdO = new NpgsqlCommand($@"SELECT ""Url_Imagen_Impresion"" FROM ""Tienda_Productos_Venta"" WHERE ""Id_Producto""={idProd}", conexion, trans))
                                {
                                    var result = await cmdO.ExecuteScalarAsync();
                                    if (result != null && result != DBNull.Value) urlViejaImp = result.ToString();
                                }

                                if (!string.IsNullOrEmpty(urlViejaImp)) await DestruirImagenCloudinary(urlViejaImp);

                                if (modelo.BorrarImagenImpresion && modelo.ArchivoImagenImpresion == null)
                                {
                                    await new NpgsqlCommand($@"UPDATE ""Tienda_Productos_Venta"" SET ""Url_Imagen_Impresion"" = NULL WHERE ""Id_Producto""={idProd}", conexion, trans).ExecuteNonQueryAsync();
                                }
                            }

                            if (modelo.ArchivoImagenImpresion != null)
                            {
                                string urlNuevaImp = await SubirImagenCloudinary(modelo.ArchivoImagenImpresion, folderPath);
                                string sqlUpdImp = @"UPDATE ""Tienda_Productos_Venta"" SET ""Url_Imagen_Impresion""=@url WHERE ""Id_Producto""=@id";
                                using (var cmdImp = new NpgsqlCommand(sqlUpdImp, conexion, trans))
                                {
                                    cmdImp.Parameters.AddWithValue("@id", idProd);
                                    cmdImp.Parameters.AddWithValue("@url", urlNuevaImp);
                                    await cmdImp.ExecuteNonQueryAsync();
                                }
                            }

                            // =========================================================
                            // E. NUEVO: GESTIÓN FLUIDA: DISEÑO IMPRESIÓN POSTERIOR
                            // =========================================================
                            if (modelo.BorrarImagenImpresionPosterior || modelo.ArchivoImagenImpresionPosterior != null)
                            {
                                string urlViejaImpPost = null;
                                using (var cmdO = new NpgsqlCommand($@"SELECT ""Url_Imagen_Impresion_Posterior"" FROM ""Tienda_Productos_Venta"" WHERE ""Id_Producto""={idProd}", conexion, trans))
                                {
                                    var result = await cmdO.ExecuteScalarAsync();
                                    if (result != null && result != DBNull.Value) urlViejaImpPost = result.ToString();
                                }

                                if (!string.IsNullOrEmpty(urlViejaImpPost)) await DestruirImagenCloudinary(urlViejaImpPost);

                                if (modelo.BorrarImagenImpresionPosterior && modelo.ArchivoImagenImpresionPosterior == null)
                                {
                                    await new NpgsqlCommand($@"UPDATE ""Tienda_Productos_Venta"" SET ""Url_Imagen_Impresion_Posterior"" = NULL WHERE ""Id_Producto""={idProd}", conexion, trans).ExecuteNonQueryAsync();
                                }
                            }

                            if (modelo.ArchivoImagenImpresionPosterior != null)
                            {
                                string urlNuevaImpPost = await SubirImagenCloudinary(modelo.ArchivoImagenImpresionPosterior, folderPath);
                                string sqlUpdImpPost = @"UPDATE ""Tienda_Productos_Venta"" SET ""Url_Imagen_Impresion_Posterior""=@url WHERE ""Id_Producto""=@id";
                                using (var cmdImpPost = new NpgsqlCommand(sqlUpdImpPost, conexion, trans))
                                {
                                    cmdImpPost.Parameters.AddWithValue("@id", idProd);
                                    cmdImpPost.Parameters.AddWithValue("@url", urlNuevaImpPost);
                                    await cmdImpPost.ExecuteNonQueryAsync();
                                }
                            }

                            // F. MEDIDAS Y STOCK (Se mantiene intacto)
                            await new NpgsqlCommand($"DELETE FROM \"Tienda_Productos_Medidas\" WHERE \"Id_Producto\"={idProd}", conexion, trans).ExecuteNonQueryAsync();
                            string sqlMed = @"INSERT INTO ""Tienda_Productos_Medidas"" (""Id_Producto"", ""Nombre"", ""Stock"") VALUES (@id, @nom, @stk)";

                            if (modelo.TallasStock == null || modelo.TallasStock.Count == 0)
                            {
                                using (var cmdM = new NpgsqlCommand(sqlMed, conexion, trans))
                                {
                                    cmdM.Parameters.AddWithValue("@id", idProd);
                                    cmdM.Parameters.AddWithValue("@nom", "Unitalla");
                                    cmdM.Parameters.AddWithValue("@stk", modelo.Stock_Tienda);
                                    await cmdM.ExecuteNonQueryAsync();
                                }
                            }
                            else
                            {
                                foreach (var t in modelo.TallasStock)
                                {
                                    if (!string.IsNullOrWhiteSpace(t.Nombre))
                                    {
                                        using (var cmdM = new NpgsqlCommand(sqlMed, conexion, trans))
                                        {
                                            cmdM.Parameters.AddWithValue("@id", idProd);
                                            cmdM.Parameters.AddWithValue("@nom", t.Nombre.Trim());
                                            cmdM.Parameters.AddWithValue("@stk", t.Stock);
                                            await cmdM.ExecuteNonQueryAsync();
                                        }
                                    }
                                }
                            }

                            // G. BITÁCORA
                            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, bNuevo ? Parametros.AccionesBitacora.Crear : Parametros.AccionesBitacora.Editar, $"Producto: {modelo.Nombre_Comercial}", HttpContext.Connection.RemoteIpAddress?.ToString(), trans);

                            await trans.CommitAsync();
                            MostrarMensaje("¡Producto Guardado!", $"Se ha guardado correctamente.", TipoMensaje.Exito);
                        }
                        catch
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error al Guardar", ex.Message, TipoMensaje.Error);
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await CargarCategoriasViewBag(conexion);
                }
                return View("Editor", modelo);
            }

            return RedirectToAction("Index");
        }

        private void SetParams(NpgsqlCommand cmd, EditorProductoViewModel m)
        {
            cmd.Parameters.AddWithValue("@cat", m.Id_Categoria);
            cmd.Parameters.AddWithValue("@nom", m.Nombre_Comercial);
            cmd.Parameters.AddWithValue("@desc", m.Descripcion ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@pre", m.Precio_Venta);
            cmd.Parameters.AddWithValue("@per", m.Es_Personalizable);
            cmd.Parameters.AddWithValue("@act", m.Activo);
            cmd.Parameters.AddWithValue("@usa", m.Usa_Medidas_Categoria);
            cmd.Parameters.AddWithValue("@ax", m.Area_X);
            cmd.Parameters.AddWithValue("@ay", m.Area_Y);
            cmd.Parameters.AddWithValue("@aw", m.Area_Ancho);
            cmd.Parameters.AddWithValue("@ah", m.Area_Alto);
            cmd.Parameters.AddWithValue("@comision", m.Cobrar_Comision_Extra);
        }

        // ==========================================
        // 4. ELIMINAR - POST (DESTRUCCIÓN FÍSICA EN CLOUDINARY)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Error", "No tienes permiso de borrado en ésta ventana", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var cmdCheck1 = new NpgsqlCommand("SELECT COUNT(*) FROM \"Tienda_Detalles_Pedido\" WHERE \"Id_Producto_Venta\"=@id", conexion);
                    cmdCheck1.Parameters.AddWithValue("@id", id);
                    if ((long)await cmdCheck1.ExecuteScalarAsync() > 0)
                    {
                        MostrarMensaje("No se puede eliminar", "El producto tiene historial de ventas.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    var cmdCheck2 = new NpgsqlCommand("SELECT COUNT(*) FROM \"Tienda_Carrito\" WHERE \"Id_Producto\"=@id", conexion);
                    cmdCheck2.Parameters.AddWithValue("@id", id);
                    if ((long)await cmdCheck2.ExecuteScalarAsync() > 0)
                    {
                        MostrarMensaje("No se puede eliminar", "Alguien tiene este producto en su carrito.", TipoMensaje.Alerta);
                        return RedirectToAction("Index");
                    }

                    var cmdCheck3 = new NpgsqlCommand("SELECT COUNT(*) FROM \"Tienda_Solicitudes_Diseno\" WHERE \"Id_Producto_Base\"=@id", conexion);
                    cmdCheck3.Parameters.AddWithValue("@id", id);
                    if ((long)await cmdCheck3.ExecuteScalarAsync() > 0)
                    {
                        MostrarMensaje("No se puede eliminar", "Existen diseños basados en este producto.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            await new NpgsqlCommand($"DELETE FROM \"Tienda_Productos_Medidas\" WHERE \"Id_Producto\"={id}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Tienda_Multimedia_Productos\" WHERE \"Id_Producto\"={id}", conexion, trans).ExecuteNonQueryAsync();

                            var cmd = new NpgsqlCommand("DELETE FROM \"Tienda_Productos_Venta\" WHERE \"Id_Producto\"=@id", conexion, trans);
                            cmd.Parameters.AddWithValue("@id", id);
                            await cmd.ExecuteNonQueryAsync();

                            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Borrar, $"Eliminó producto ID {id}", null, trans);

                            await trans.CommitAsync();

                            // CLOUDINARY: Borrar carpeta entera (fuera de transacción por si falla)
                            try
                            {
                                string folderPath = $"{sAmbiente}/Imágenes/Tienda/Productos/{id}";
                                await _cloudinary.DeleteResourcesByPrefixAsync($"{folderPath}/");
                                await _cloudinary.DeleteFolderAsync(folderPath);
                            }
                            catch { /* Ignoramos fallos externos */ }

                            MostrarMensaje("Eliminado", "Producto eliminado correctamente.", TipoMensaje.Exito);
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Index");
        }

        // ==========================================
        // HELPERS PRIVADOS DE CLOUDINARY
        // ==========================================
        private async Task<string> SubirImagenCloudinary(IFormFile foto, string folderPath)
        {
            using var memoryStream = new MemoryStream();
            using (var image = await Image.LoadAsync(foto.OpenReadStream()))
            {
                const int MaxWidth = 1200;
                if (image.Width > MaxWidth)
                {
                    int newHeight = (int)((double)image.Height / image.Width * MaxWidth);
                    image.Mutate(x => x.Resize(MaxWidth, newHeight));
                }

                // Validamos si la imagen original es PNG
                bool esPng = foto.ContentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
                             foto.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

                if (esPng)
                {
                    // Usamos PngEncoder para conservar la transparencia
                    var encoder = new SixLabors.ImageSharp.Formats.Png.PngEncoder();
                    await image.SaveAsync(memoryStream, encoder);
                }
                else
                {
                    // Usamos JpegEncoder para fotos normales y ahorrar espacio
                    var encoder = new JpegEncoder { Quality = 75 };
                    await image.SaveAsync(memoryStream, encoder);
                }
            }

            memoryStream.Position = 0;

            var uploadParams = new ImageUploadParams()
            {
                File = new FileDescription(foto.FileName, memoryStream),
                Folder = folderPath,
                Transformation = new Transformation().FetchFormat("auto")
            };

            var uploadResult = await _cloudinary.UploadAsync(uploadParams);
            return uploadResult.SecureUrl.ToString();
        }

        private async Task DestruirImagenCloudinary(string urlImg)
        {
            try
            {
                var uri = new Uri(urlImg);
                var segments = uri.Segments;
                int uploadIndex = Array.IndexOf(segments, "upload/");

                if (uploadIndex >= 0 && segments.Length > uploadIndex + 2)
                {
                    string publicIdWithExtension = string.Join("", segments.Skip(uploadIndex + 2));
                    string publicId = Path.ChangeExtension(publicIdWithExtension, null).Replace("%20", " ").Trim('/');
                    await _cloudinary.DestroyAsync(new DeletionParams(publicId));
                }
            }
            catch { /* Ignorar si no se puede borrar */ }
        }
    }
}