using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Globales;
using System.Security.Claims;
using System.Text.RegularExpressions;
using static RedAJP.Globales.Parametros;

namespace RedAJP.Controllers
{
    [Authorize]
    public class MaterialesController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly Modulo _modulo = Modulos.Material;

        public MaterialesController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        // --- MODELOS ---
        public class MaterialViewModel
        {
            public int Id { get; set; }
            public string Titulo { get; set; }
            public string Descripcion { get; set; }
            public string IconoClase { get; set; }
            public string ColorClase { get; set; }
            public string UsuarioCarga { get; set; }
            public DateTime Fecha { get; set; }
            public int Descargas { get; set; }

            // NUEVO: Propiedad para decidir renderizado
            public bool EsImagen { get; set; }
        }

        public class EditorMaterialViewModel
        {
            public int Id_Archivo { get; set; }
            public string Titulo { get; set; }
            public string Descripcion { get; set; }
            public IFormFile Archivo { get; set; }
            public string NombreArchivoActual { get; set; }
        }

        // --- ACCIONES PÚBLICAS A USUARIOS REGISTRADOS ---

        public async Task<IActionResult> Index()
        {
            var lista = new List<MaterialViewModel>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    if (!await Funciones.EsModuloActivo(_cadenaConexion, _modulo))
                        return RedirigirAtras("La biblioteca de materiales está en mantenimiento.");

                    // FIX: Added Fecha_Modificacion to the query
                    string sql = @"
                        SELECT a.""Id_Archivo"", a.""Titulo"", a.""Descripcion"", a.""Tipo"", 
                               a.""Fecha_Creacion"", a.""Fecha_Modificacion"", a.""Descargas"", u.""NombreCompleto""
                        FROM ""Rec_Archivos"" a
                        LEFT JOIN ""Sist_Usuarios"" u ON a.""Id_Usuario_Carga"" = u.""Id_Usuario""
                        WHERE a.""Origen"" = 'Materiales'
                        ORDER BY COALESCE(a.""Fecha_Modificacion"", a.""Fecha_Creacion"") DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            string tipoMime = r["Tipo"].ToString().ToLower();
                            var estilo = ObtenerEstiloPorMime(tipoMime);

                            // FIX: Determine correct date for cache busting
                            DateTime fechaDisplay = r["Fecha_Modificacion"] != DBNull.Value
                                ? (DateTime)r["Fecha_Modificacion"]
                                : (DateTime)r["Fecha_Creacion"];

                            lista.Add(new MaterialViewModel
                            {
                                Id = (int)r["Id_Archivo"],
                                Titulo = r["Titulo"].ToString(),
                                Descripcion = r["Descripcion"].ToString(),
                                IconoClase = estilo.Icono,
                                ColorClase = estilo.Color,
                                UsuarioCarga = r["NombreCompleto"]?.ToString() ?? "Anónimo",
                                Fecha = fechaDisplay, // Used for ?v= ticks
                                Descargas = (int)r["Descargas"],
                                EsImagen = tipoMime.Contains("image") || tipoMime.Contains("jpg") || tipoMime.Contains("png")
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return View(lista);
        }

        // Cacheamos por 1 hora (3600 seg) en el cliente.
        // Gracias al truco del "?v=" en la vista, si la imagen cambia, la URL cambia 
        // y el navegador ignorará esta caché vieja automáticamente.
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
        public async Task<IActionResult> Imagen(int id)
        {
            if (id <= 0) return NotFound();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"SELECT ""Contenido_Binario"", ""Tipo"" 
                                   FROM ""Rec_Archivos"" 
                                   WHERE ""Id_Archivo"" = @id AND ""Origen"" = 'Materiales'";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync() && r["Contenido_Binario"] != DBNull.Value)
                            {
                                byte[] bytes = (byte[])r["Contenido_Binario"];
                                string mime = r["Tipo"].ToString();
                                return File(bytes, mime);
                            }
                        }
                    }
                }
            }
            catch { }

            return NotFound();
        }

        public async Task<IActionResult> Descargar(int id)
        {
            if (id <= 0)
            {
                MostrarMensaje("Error", "No se ha encontrado el identificador del recurso.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            try
            {
                byte[] archivoBytes = null;
                string nombreArchivo = "";
                string tipoMime = "";

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sql = @"SELECT ""Contenido_Binario"", ""Titulo"", ""Tipo"" 
                                           FROM ""Rec_Archivos"" 
                                           WHERE ""Id_Archivo"" = @id AND ""Origen"" = 'Materiales'";

                            using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@id", id);
                                using (var r = await cmd.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync() && r["Contenido_Binario"] != DBNull.Value)
                                    {
                                        archivoBytes = (byte[])r["Contenido_Binario"];
                                        nombreArchivo = r["Titulo"].ToString();
                                        tipoMime = r["Tipo"].ToString();
                                    }
                                }
                            }

                            if (archivoBytes == null)
                            {
                                await trans.RollbackAsync();
                                MostrarMensaje("No encontrado", "El material no existe.", TipoMensaje.Alerta);
                                return RedirectToAction("Index");
                            }

                            await new NpgsqlCommand($"UPDATE \"Rec_Archivos\" SET \"Descargas\" = \"Descargas\" + 1 WHERE \"Id_Archivo\" = {id}", conexion, trans).ExecuteNonQueryAsync();
                            await Funciones.RegistrarBitacora(conexion, idUsuario, _modulo, AccionesBitacora.Leer, $"Descarga Material: {nombreArchivo}", ip, trans);

                            await trans.CommitAsync();
                        }
                        catch
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }

                string extension = ObtenerExtensionPorMime(tipoMime);
                string nombreDescarga = $"{Regex.Replace(nombreArchivo, @"[^\w\d-]", "_")}{extension}";
                return File(archivoBytes, tipoMime, nombreDescarga);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Error al descargar: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }
        }

        // --- ACCIONES ADMINISTRATIVAS ---

        public async Task<IActionResult> Editor(int? id)
        {
            var modelo = new EditorMaterialViewModel();
            bool esNuevo = (id == null || id == 0);

            if (esNuevo)
            {
                if (!User.TienePermiso(_modulo, PermisoCrear))
                {
                    MostrarMensaje("Acceso Denegado", "Se requieren permisos para publicar material.", TipoMensaje.Alerta);
                    return RedirectToAction("Index");
                }
                return View(modelo);
            }
            else
            {
                if (!User.TienePermiso(_modulo, PermisoEditar))
                {
                    MostrarMensaje("Acceso Denegado", "Se requieren permisos para editar material.", TipoMensaje.Alerta);
                    return RedirectToAction("Index");
                }
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"SELECT ""Titulo"", ""Descripcion"" FROM ""Rec_Archivos"" 
                                   WHERE ""Id_Archivo"" = @id AND ""Origen"" = 'Materiales'";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.Id_Archivo = id.Value;
                                modelo.Titulo = r["Titulo"].ToString();
                                modelo.Descripcion = r["Descripcion"].ToString();
                                modelo.NombreArchivoActual = "Material cargado actualmente.";
                            }
                            else return RedirectToAction("Index");
                        }
                    }
                }
            }
            catch { return RedirectToAction("Index"); }

            return View(modelo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(EditorMaterialViewModel form)
        {
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            bool esNuevo = form.Id_Archivo == 0;

            if (esNuevo && !User.TienePermiso(_modulo, PermisoCrear))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }
            if (!esNuevo && !User.TienePermiso(_modulo, PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            if (esNuevo && (form.Archivo == null || form.Archivo.Length == 0))
            {
                MostrarMensaje("Atención", "Debes subir el archivo.", TipoMensaje.Alerta);
                return View("Editor", form);
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
                            byte[] bytes = null;
                            string mime = "";

                            if (form.Archivo != null)
                            {
                                using (var ms = new MemoryStream())
                                {
                                    await form.Archivo.CopyToAsync(ms);
                                    bytes = ms.ToArray();
                                    mime = form.Archivo.ContentType;
                                }
                            }

                            if (esNuevo)
                            {
                                string sql = @"INSERT INTO ""Rec_Archivos"" 
                                    (""Titulo"", ""Descripcion"", ""Tipo"", ""Contenido_Binario"", ""Descargas"", ""Origen"", ""Fecha_Creacion"", ""Id_Usuario_Carga"")
                                    VALUES (@tit, @desc, @mime, @bin, 0, 'Materiales', NOW(), @uid)";

                                using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@tit", form.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", form.Descripcion ?? "");
                                    cmd.Parameters.AddWithValue("@mime", mime);
                                    cmd.Parameters.AddWithValue("@bin", bytes);
                                    cmd.Parameters.AddWithValue("@uid", idUsuario);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                                await Funciones.RegistrarBitacora(conexion, idUsuario, _modulo, AccionesBitacora.Crear, $"Publicó material: {form.Titulo}", ip, trans);
                            }
                            else
                            {
                                string sql = @"UPDATE ""Rec_Archivos"" SET 
                                    ""Titulo"" = @tit, 
                                    ""Descripcion"" = @desc, 
                                    ""Fecha_Modificacion"" = NOW(), 
                                    ""Id_Usuario_Carga"" = @uid";

                                if (bytes != null) sql += @", ""Tipo"" = @mime, ""Contenido_Binario"" = @bin";

                                sql += @" WHERE ""Id_Archivo"" = @id AND ""Origen"" = 'Materiales'";

                                using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@tit", form.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", form.Descripcion ?? "");
                                    cmd.Parameters.AddWithValue("@uid", idUsuario);
                                    cmd.Parameters.AddWithValue("@id", form.Id_Archivo);
                                    if (bytes != null)
                                    {
                                        cmd.Parameters.AddWithValue("@mime", mime);
                                        cmd.Parameters.AddWithValue("@bin", bytes);
                                    }
                                    await cmd.ExecuteNonQueryAsync();
                                }
                                await Funciones.RegistrarBitacora(conexion, idUsuario, _modulo, AccionesBitacora.Editar, $"Editó material: {form.Titulo}", ip, trans);
                            }

                            await trans.CommitAsync();
                            MostrarMensaje("Éxito", "Material publicado correctamente.", TipoMensaje.Exito);
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return View("Editor", form);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int id)
        {
            if (!User.TienePermiso(_modulo, PermisoBorrar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"DELETE FROM ""Rec_Archivos"" WHERE ""Id_Archivo"" = @id AND ""Origen"" = 'Materiales'";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        await cmd.ExecuteNonQueryAsync();
                    }
                    await Funciones.RegistrarBitacora(conexion, idUsuario, _modulo, AccionesBitacora.Borrar, $"Eliminó material ID {id}", "Local");
                }
                MostrarMensaje("Eliminado", "Material eliminado.", TipoMensaje.Exito);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Index");
        }

        // Helpers Actualizados con soporte para Excel
        private (string Icono, string Color) ObtenerEstiloPorMime(string mime)
        {
            if (string.IsNullOrEmpty(mime)) return ("fa-file", "text-secondary");

            if (mime.Contains("pdf")) return ("fa-file-pdf", "text-danger");
            if (mime.Contains("image")) return ("fa-file-image", "text-primary");
            if (mime.Contains("word") || mime.Contains("document")) return ("fa-file-word", "text-primary");
            // Soporte Excel
            if (mime.Contains("excel") || mime.Contains("sheet") || mime.Contains("csv")) return ("fa-file-excel", "text-success");

            if (mime.Contains("presentation") || mime.Contains("powerpoint")) return ("fa-file-powerpoint", "text-warning");
            if (mime.Contains("audio")) return ("fa-file-audio", "text-info");
            if (mime.Contains("zip") || mime.Contains("rar")) return ("fa-file-zipper", "text-secondary");

            return ("fa-book-bible", "text-success");
        }

        private string ObtenerExtensionPorMime(string mime)
        {
            if (mime.Contains("pdf")) return ".pdf";
            if (mime.Contains("image") || mime.Contains("jpeg") || mime.Contains("jpg")) return ".jpg";
            if (mime.Contains("png")) return ".png";
            if (mime.Contains("word") || mime.Contains("doc")) return ".docx";
            if (mime.Contains("excel") || mime.Contains("sheet")) return ".xlsx"; // Extension Excel
            if (mime.Contains("zip")) return ".zip";
            return ".dat";
        }
    }
}