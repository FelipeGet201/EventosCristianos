using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Models;
using RedAJP.Globales;
using QRCoder;
using System.Drawing.Imaging;
using SDColor = System.Drawing.Color;
using SDBitmap = System.Drawing.Bitmap;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using RedAJP.Servicios;

namespace RedAJP.Controllers
{
    public class CodigoQRViewModel
    {
        public int Id_QR { get; set; }
        public string Titulo { get; set; } = "";
        public string Url { get; set; } = "";
        public string ColorFrente { get; set; } = "";
        public string ColorFondo { get; set; } = "";
        public string ImagenBase64 { get; set; } = "";
        public string FormaOjos { get; set; } = "Cuadrado";
        public string FormaModulos { get; set; } = "Rellenar";
        public DateTime Fecha_Creacion { get; set; }
    }

    [Authorize]
    public class CodigosQRController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.GeneradorQR;
        private readonly IConfiguration _config;
        private readonly GeneradorQRService _qrService; // Instancia de la clase que extrajimos

        public CodigosQRController(IConfiguration configuration)
        {
            _config = configuration;
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
            _qrService = new GeneradorQRService(); // Inicialización de la clase
        }

        public async Task<IActionResult> Index()
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Error", "No tiene permisos de lectura en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            // =========================================================================================
            // FUNCIÓN PARA GENERAR IMÁGENES LOCALES
            // DESCOMENTA la siguiente línea UNA SOLA VEZ, entra a la pantalla para que se generen 
            // los PNG en wwwroot/img/qr-formas/, y luego vuelve a COMENTARLA para ahorrar RAM/CPU.
            // =========================================================================================
            //_qrService.GenerarYGuardarPrevisualizaciones();

            var lista = new List<CodigoQRViewModel>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"SELECT * FROM ""Sist_CodigosQR"" ORDER BY ""Fecha_Creacion"" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (reader.Read())
                        {
                            string formaOjos = "Cuadrado";
                            string formaModulos = "Rellenar";
                            try { formaOjos = reader["FormaOjos"].ToString(); } catch { }
                            try { formaModulos = reader["FormaModulos"].ToString(); } catch { }

                            lista.Add(new CodigoQRViewModel
                            {
                                Id_QR = (int)reader["Id_QR"],
                                Titulo = reader["Titulo"].ToString(),
                                Url = reader["Url"].ToString(),
                                ColorFrente = reader["ColorFrente"].ToString(),
                                ColorFondo = reader["ColorFondo"].ToString(),
                                ImagenBase64 = reader["ImagenBase64"].ToString(),
                                FormaOjos = formaOjos,
                                FormaModulos = formaModulos,
                                Fecha_Creacion = Convert.ToDateTime(reader["Fecha_Creacion"])
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", ex.Message, TipoMensaje.Error);
            }

            return View(lista);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Crear(string Titulo, string Url, string ColorFrente, string ColorFondo, IFormFile Logo, FormaOjos FormaOjos = FormaOjos.Cuadrado, FormaModulos FormaModulos = FormaModulos.Rellenar, bool GuardarDatos = false)
        {
            bool tienePermisosCrear = User.TienePermiso(Modulo, PermisoCrear);
            if (!tienePermisosCrear)
            {
                GuardarDatos = false;
            }

            var idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            ColorFrente = string.IsNullOrEmpty(ColorFrente) ? "#000000" : ColorFrente;
            ColorFondo = string.IsNullOrEmpty(ColorFondo) ? "#FFFFFF" : ColorFondo;

            try
            {
                // Uso de la clase especial para generar el QR pasando los Enums
                string base64QR = _qrService.GenerarImagenQR(Url, ColorFrente, ColorFondo, Logo, FormaOjos, FormaModulos);

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    if (GuardarDatos)
                    {
                        using (var transaccion = await conexion.BeginTransactionAsync())
                        {
                            try
                            {
                                string sql = @"INSERT INTO ""Sist_CodigosQR"" 
                                             (""Titulo"", ""Url"", ""ColorFrente"", ""ColorFondo"", ""FormaOjos"", ""FormaModulos"", ""ImagenBase64"", ""Id_Usuario"", ""Fecha_Creacion"") 
                                             VALUES (@tit, @url, @cfre, @cfon, @fojo, @fmod, @img, @idu, NOW()) RETURNING ""Id_QR""";

                                int idNuevoQR = 0;
                                using (var cmd = new NpgsqlCommand(sql, conexion, transaccion))
                                {
                                    cmd.Parameters.AddWithValue("@tit", Titulo);
                                    cmd.Parameters.AddWithValue("@url", Url);
                                    cmd.Parameters.AddWithValue("@cfre", ColorFrente);
                                    cmd.Parameters.AddWithValue("@cfon", ColorFondo);
                                    cmd.Parameters.AddWithValue("@fojo", FormaOjos.ToString()); // Guardamos como string
                                    cmd.Parameters.AddWithValue("@fmod", FormaModulos.ToString()); // Guardamos como string
                                    cmd.Parameters.AddWithValue("@img", base64QR);
                                    cmd.Parameters.AddWithValue("@idu", idAdmin);

                                    idNuevoQR = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                                }

                                await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Crear,
                                    $"Creó Código QR ID: {idNuevoQR} ('{Titulo}') redirigiendo a {Url}", ip, transaccion);

                                await transaccion.CommitAsync();
                                MostrarMensaje("QR Generado", "El código QR se generó y guardó exitosamente.", TipoMensaje.Exito);

                                return Json(new { exito = true, guardado = true, base64 = base64QR });
                            }
                            catch
                            {
                                await transaccion.RollbackAsync();
                                throw;
                            }
                        }
                    }
                    else
                    {
                        await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Crear,
                            $"Generó Vista Previa QR ('{Titulo}') redirigiendo a {Url}", ip);
                        return Json(new { exito = true, guardado = false, base64 = base64QR });
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Editar(int Id_QR, string Titulo, string Url, string ColorFrente, string ColorFondo, IFormFile Logo, FormaOjos FormaOjos = FormaOjos.Cuadrado, FormaModulos FormaModulos = FormaModulos.Rellenar)
        {
            if (!User.TienePermiso(Modulo, PermisoCrear))
            {

                MostrarMensaje("Error", "No tiene permisos de creación en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            ColorFrente = string.IsNullOrEmpty(ColorFrente) ? "#000000" : ColorFrente;
            ColorFondo = string.IsNullOrEmpty(ColorFondo) ? "#FFFFFF" : ColorFondo;

            try
            {
                // Uso de la clase especial para generar el QR pasando los Enums
                string base64QR = _qrService.GenerarImagenQR(Url, ColorFrente, ColorFondo, Logo, FormaOjos, FormaModulos);

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sql = @"UPDATE ""Sist_CodigosQR"" 
                                         SET ""Titulo""=@tit, ""Url""=@url, ""ColorFrente""=@cfre, ""ColorFondo""=@cfon, ""FormaOjos""=@fojo, ""FormaModulos""=@fmod, ""ImagenBase64""=@img
                                         WHERE ""Id_QR""=@id";

                            using (var cmd = new NpgsqlCommand(sql, conexion, transaccion))
                            {
                                cmd.Parameters.AddWithValue("@tit", Titulo);
                                cmd.Parameters.AddWithValue("@url", Url);
                                cmd.Parameters.AddWithValue("@cfre", ColorFrente);
                                cmd.Parameters.AddWithValue("@cfon", ColorFondo);
                                cmd.Parameters.AddWithValue("@fojo", FormaOjos.ToString()); // Guardamos como string
                                cmd.Parameters.AddWithValue("@fmod", FormaModulos.ToString()); // Guardamos como string
                                cmd.Parameters.AddWithValue("@img", base64QR);
                                cmd.Parameters.AddWithValue("@id", Id_QR);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Editar,
                                $"Editó Código QR ID: {Id_QR} ('{Titulo}')", ip, transaccion);

                            await transaccion.CommitAsync();
                            MostrarMensaje("QR Actualizado", "El código QR se actualizó exitosamente.", TipoMensaje.Exito);
                        }
                        catch
                        {
                            await transaccion.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error al Actualizar", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int idEliminar)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Permiso Denegado", "No tienes permisos para eliminar códigos QR.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string datosAnteriores = "";
                            using (var cmdGet = new NpgsqlCommand(@"SELECT ""Titulo"", ""Url"" FROM ""Sist_CodigosQR"" WHERE ""Id_QR""=@id", conexion, transaccion))
                            {
                                cmdGet.Parameters.AddWithValue("@id", idEliminar);
                                using (var r = await cmdGet.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync()) datosAnteriores = $"Tít: {r["Titulo"]}, Url: {r["Url"]}";
                                }
                            }

                            var cmdDel = new NpgsqlCommand(@"DELETE FROM ""Sist_CodigosQR"" WHERE ""Id_QR""=@id", conexion, transaccion);
                            cmdDel.Parameters.AddWithValue("@id", idEliminar);
                            await cmdDel.ExecuteNonQueryAsync();

                            await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Borrar,
                                $"Eliminó Código QR ID: {idEliminar} de forma definitiva. Datos: {datosAnteriores}", ip, transaccion);

                            await transaccion.CommitAsync();
                            MostrarMensaje("QR Eliminado", "El código QR fue eliminado del sistema.", TipoMensaje.Exito);
                        }
                        catch { await transaccion.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }
    }
}