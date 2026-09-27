using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Models;
using RedAJP.Globales;
using static RedAJP.Globales.Parametros;
using System.Data;

namespace RedAJP.Controllers
{
    [Authorize]
    public class TiendaConfigController : GlobalController 
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.TiendaConfig;

        public TiendaConfigController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        // ==========================================
        // 1. LISTADO DE CATEGORÍAS
        // ==========================================
        public async Task<IActionResult> Categorias()
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Error", "Necesitas permisos de lectura para realizar ésta acción.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }

            var lista = new List<CategoriaViewModel>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = @"SELECT c.*, 
                                   (SELECT COUNT(*) FROM ""Tienda_Productos_Venta"" p WHERE p.""Id_Categoria"" = c.""Id_Categoria"") as ""Refs""
                                   FROM ""Tienda_Cat_Categorias"" c
                                   ORDER BY c.""Nombre"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            lista.Add(new CategoriaViewModel
                            {
                                Id_Categoria = (int)r["Id_Categoria"],
                                Nombre = r["Nombre"].ToString(),
                                Descripcion = r["Descripcion"]?.ToString() ?? "",
                                Icono = r["Icono"]?.ToString() ?? "fa-box-open",
                                Activo = (bool)r["Activo"],
                                Medidas_Default = r["Medidas_Default"]?.ToString() ?? "N/A",
                                TotalProductos = Convert.ToInt32(r["Refs"])
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Usamos el mensaje global
                MostrarMensaje("Error de Carga", ex.Message, TipoMensaje.Error);
            }

            return View(lista);
        }

        // ==========================================
        // 2. GUARDAR (CREAR / EDITAR)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarCategoria(CategoriaViewModel modelo)
        {
            bool bNuevo = modelo.Id_Categoria == 0;

            if (!User.TienePermiso(Modulo, bNuevo ? PermisoCrear : PermisoEditar))
            {
                MostrarMensaje("Error", "Necesitas permisos para realizar ésta acción.", TipoMensaje.Error);
                return RedirectToAction("Categorias");
            }

            if (string.IsNullOrEmpty(modelo.Nombre))
            {
                // Alerta Global
                MostrarMensaje("Atención", "El nombre es obligatorio.", TipoMensaje.Alerta);
                return RedirectToAction("Categorias");
            }

            if (string.IsNullOrEmpty(modelo.Medidas_Default)) modelo.Medidas_Default = "N/A";
            if (string.IsNullOrEmpty(modelo.Icono)) modelo.Icono = "fa-box-open";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            if (modelo.Id_Categoria == 0)
                            {
                                string sql = @"INSERT INTO ""Tienda_Cat_Categorias"" 
                                             (""Nombre"", ""Descripcion"", ""Icono"", ""Activo"", ""Medidas_Default"") 
                                             VALUES (@n, @d, @i, @a, @m)";
                                using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@n", modelo.Nombre);
                                    cmd.Parameters.AddWithValue("@d", modelo.Descripcion ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@i", modelo.Icono);
                                    cmd.Parameters.AddWithValue("@a", modelo.Activo);
                                    cmd.Parameters.AddWithValue("@m", modelo.Medidas_Default);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }
                            else
                            {
                                string sql = @"UPDATE ""Tienda_Cat_Categorias"" 
                                             SET ""Nombre""=@n, ""Descripcion""=@d, ""Icono""=@i, ""Activo""=@a, ""Medidas_Default""=@m 
                                             WHERE ""Id_Categoria""=@id";
                                using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@n", modelo.Nombre);
                                    cmd.Parameters.AddWithValue("@d", modelo.Descripcion ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@i", modelo.Icono);
                                    cmd.Parameters.AddWithValue("@a", modelo.Activo);
                                    cmd.Parameters.AddWithValue("@m", modelo.Medidas_Default);
                                    cmd.Parameters.AddWithValue("@id", modelo.Id_Categoria);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }

                            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, bNuevo ? Parametros.AccionesBitacora.Crear : Parametros.AccionesBitacora.Editar, $"Categoría: {modelo.Nombre}", HttpContext.Connection.RemoteIpAddress?.ToString(), trans);

                            await trans.CommitAsync();

                            // ÉXITO GLOBAL
                            MostrarMensaje("¡Guardado!", "La categoría se ha guardado correctamente.", TipoMensaje.Exito);
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
                // ERROR GLOBAL
                MostrarMensaje("Error al Guardar", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Categorias");
        }

        // ==========================================
        // 3. ELIMINAR
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarCategoria(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Error", "Necesitas permisos de borrado para realizar ésta acción.", TipoMensaje.Error);
                return RedirectToAction("Categorias");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var cmdCheck = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Tienda_Productos_Venta"" WHERE ""Id_Categoria""=@id", conexion);
                    cmdCheck.Parameters.AddWithValue("@id", id);

                    long count = (long)await cmdCheck.ExecuteScalarAsync();

                    if (count > 0)
                    {
                        // ERROR DE LÓGICA (NO SE PUEDE BORRAR)
                        MostrarMensaje("No se puede eliminar", "Existen productos asignados a esta categoría. Primero reasígnalos o elimínalos.", TipoMensaje.Error);
                        return RedirectToAction("Categorias");
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            var cmd = new NpgsqlCommand(@"DELETE FROM ""Tienda_Cat_Categorias"" WHERE ""Id_Categoria""=@id", conexion, trans);
                            cmd.Parameters.AddWithValue("@id", id);
                            await cmd.ExecuteNonQueryAsync();

                            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Borrar, $"Eliminó categoría ID {id}", null, trans);

                            await trans.CommitAsync();

                            // ÉXITO GLOBAL
                            MostrarMensaje("Eliminado", "La categoría ha sido eliminada.", TipoMensaje.Exito);
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
                // ERROR TÉCNICO
                MostrarMensaje("Error Técnico", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Categorias");
        }
    }
}