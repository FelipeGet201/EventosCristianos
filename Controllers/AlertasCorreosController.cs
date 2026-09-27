using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RedAJP.Controllers
{
    [Authorize]
    public class AlertasCorreosController : GlobalController
    {
        private readonly IConfiguration _configuration;
        private Parametros.Modulo Modulo = Parametros.Modulos.AlertasCorreos;

        public AlertasCorreosController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task<IActionResult> Index()
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para ver las alertas.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }

            var lista = new List<AlertaCorreoViewModel>();

            using (var con = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
            {
                await con.OpenAsync();
                string sql = "SELECT * FROM \"Sist_EnvioCorreos\" ORDER BY \"Id_Alerta\" ASC";

                using (var cmd = new NpgsqlCommand(sql, con))
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        lista.Add(new AlertaCorreoViewModel
                        {
                            IdAlerta = (int)r["Id_Alerta"],
                            ClaveEvento = r["Clave_Evento"].ToString(),
                            Descripcion = r["Descripcion"]?.ToString() ?? "",
                            CorreosDestino = r["Correos_Destino"].ToString(),
                            Activo = (bool)r["Activo"]
                        });
                    }
                }
            }
            return View(lista);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Editar(int Id_Alerta, string Correos_Destino)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para editar.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario")!.Value);
            string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            string correosValidados = "";

            if (string.IsNullOrWhiteSpace(Correos_Destino))
            {
                MostrarMensaje("Atención", "Debe especificar al menos un correo electrónico de destino.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }
            else
            {
                // Separamos por comas o punto y coma
                var listaCorreos = Correos_Destino.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                var listaLimpia = new List<string>();

                // Regex oficial básica para validar formato de email
                var regexEmail = new System.Text.RegularExpressions.Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");

                foreach (var correo in listaCorreos)
                {
                    string c = correo.Trim().ToLower();

                    if (!regexEmail.IsMatch(c))
                    {
                        // Si UN SOLO correo está mal, rechazamos toda la operación
                        MostrarMensaje("Formato Inválido", $"El texto '{c}' no es un correo válido. Operación cancelada.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    // Si es válido, lo agregamos a nuestra lista segura
                    listaLimpia.Add(c);
                }

                // Volvemos a unir los correos limpios y validados separados por coma
                correosValidados = string.Join(",", listaLimpia);
            }

            try
            {
                using (var con = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
                {
                    await con.OpenAsync();
                    using (var trans = await con.BeginTransactionAsync())
                    {
                        // Usamos la variable 'correosValidados' que ya pasó por el filtro
                        string sql = "UPDATE \"Sist_EnvioCorreos\" SET \"Correos_Destino\" = @correos WHERE \"Id_Alerta\" = @id";
                        using (var cmd = new NpgsqlCommand(sql, con, trans))
                        {
                            cmd.Parameters.AddWithValue("@correos", correosValidados);
                            cmd.Parameters.AddWithValue("@id", Id_Alerta);
                            await cmd.ExecuteNonQueryAsync();
                        }

                        await Funciones.RegistrarBitacora(con, idUsuario, Modulo, Parametros.AccionesBitacora.Editar, $"Actualizó destinatarios de la alerta #{Id_Alerta}", ipUsuario, trans);
                        await trans.CommitAsync();

                        MostrarMensaje("Éxito", "Destinatarios actualizados correctamente.", TipoMensaje.Exito);
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleEstado(int id, string estadoActual)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Admin))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de Administrador.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            bool nuevoEstado = !(bool.Parse(estadoActual));
            int idUsuario = int.Parse(User.FindFirst("IdUsuario")!.Value);
            string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var con = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
                {
                    await con.OpenAsync();
                    using (var trans = await con.BeginTransactionAsync())
                    {
                        string sql = "UPDATE \"Sist_EnvioCorreos\" SET \"Activo\" = @estado WHERE \"Id_Alerta\" = @id";
                        using (var cmd = new NpgsqlCommand(sql, con, trans))
                        {
                            cmd.Parameters.AddWithValue("@estado", nuevoEstado);
                            cmd.Parameters.AddWithValue("@id", id);
                            await cmd.ExecuteNonQueryAsync();
                        }

                        string accion = nuevoEstado ? "Activó" : "Desactivó";
                        await Funciones.RegistrarBitacora(con, idUsuario, Modulo, Parametros.AccionesBitacora.Editar, $"{accion} el envío de la alerta #{id}", ipUsuario, trans);
                        await trans.CommitAsync();

                        MostrarMensaje("Éxito", $"Alerta {(nuevoEstado ? "activada" : "desactivada")} correctamente.", TipoMensaje.Exito);
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }
    }
}