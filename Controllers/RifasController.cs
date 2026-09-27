using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using Stripe;
using Stripe.Checkout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using static RedAJP.Globales.Funciones;
using RedAJP.Servicios;

namespace RedAJP.Controllers
{
    [Authorize]
    public class RifasController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly string _stripeSecretKey;
        private readonly IConfiguration _configuration;
        private Parametros.Modulo Modulo = Parametros.Modulos.Rifas;
        private readonly Cloudinary _cloudinary;
        private readonly IWebHostEnvironment _env;

        public RifasController(IConfiguration configuration, IWebHostEnvironment env)
        {
            _configuration = configuration;
            _cadenaConexion = _configuration.GetConnectionString("MiConexion");
            _stripeSecretKey = _configuration["StripeRifas:SecretKey"];
            StripeConfiguration.ApiKey = _stripeSecretKey;
            _env = env;

            CloudinaryDotNet.Account account = new CloudinaryDotNet.Account(
                _configuration["Cloudinary:CloudName"],
                _configuration["Cloudinary:ApiKey"],
                _configuration["Cloudinary:ApiSecret"]
            );
            _cloudinary = new Cloudinary(account);
            _cloudinary.Api.Secure = true;
        }

        /// <summary>
        /// Endpoint administrativo para limpiar la "basura" de las Rifas.
        /// Cancela de forma global todas las ventas abandonadas o expiradas en Stripe y libera los boletos.
        /// </summary>
        [HttpGet("Rifas/LimpiarExpirados")]
        [Authorize]
        public async Task<IActionResult> LimpiarExpiradosRifas()
        {
            // 1. CONTROL DE ACCESO: Solo administradores autorizados de Rifas
            //if (!User.TienePermiso(Modulo, Parametros.Permisos.Admin)) return Forbid();

            var resultados = new List<dynamic>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var ventasExpirables = new List<dynamic>();

                    // 2. BUSQUEDA GLOBAL: Traemos todas las ventas 'Pendientes' que superen los 30 minutos de tolerancia
                    string sqlGetPendientes = @"
                SELECT ""Id_Venta"", ""Ref_Pasarela_Id"", ""Metodo_Pago"", ""Cantidad_Boletos"", ""Ref_Stripe""
                FROM ""Rifas_Ventas""
                WHERE ""Estado"" = 'Pendiente'
                  AND ""Fecha_Creacion"" < (NOW() - INTERVAL '30 minutes')";

                    using (var cmd = new NpgsqlCommand(sqlGetPendientes, conexion))
                    {
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                ventasExpirables.Add(new
                                {
                                    IdVenta = (int)r["Id_Venta"],
                                    SessionId = r["Ref_Pasarela_Id"] != DBNull.Value ? r["Ref_Pasarela_Id"].ToString() : null,
                                    EsPlanilla = r["Metodo_Pago"].ToString() == "Planilla",
                                    Cantidad = (int)r["Cantidad_Boletos"],
                                    RefStripe = r["Ref_Stripe"].ToString()
                                });
                            }
                        }
                    }

                    var stripeSessionService = new SessionService();

                    // 3. EVALUACIÓN INDIVIDUAL CON LA API DE STRIPE
                    foreach (var venta in ventasExpirables)
                    {
                        string estatusStripeRaw = "Sin Sesión de Checkout";
                        string accionTomada = "Ninguna";
                        bool debeCancelarLocal = false;

                        try
                        {
                            if (!string.IsNullOrEmpty(venta.SessionId) && venta.SessionId.StartsWith("cs_"))
                            {
                                // Consultamos directamente el estado real en los servidores de Stripe
                                var session = await stripeSessionService.GetAsync(venta.SessionId);
                                estatusStripeRaw = session.Status; // open, complete, expired, canceled

                                if (session.Status == "expired" || session.Status == "canceled")
                                {
                                    debeCancelarLocal = true;
                                }
                                else if (session.PaymentStatus == "paid")
                                {
                                    accionTomada = "Ignorado (Registrado como pagado en Stripe. Dejado para reconciliación automática)";
                                    debeCancelarLocal = false;
                                }
                                else
                                {
                                    // Si pasaron los 30 minutos y la sesión sigue 'open', la cancelamos localmente
                                    // ya que nuestro negocio es más estricto con los tiempos que las 24 horas por defecto de Stripe
                                    debeCancelarLocal = true;

                                    // Opcional: Podrías expirar la sesión en Stripe mediante la API aquí si deseas invalidar el link viejo
                                    try { await stripeSessionService.ExpireAsync(venta.SessionId); } catch { /* silenciar si falla */ }
                                }
                            }
                            else
                            {
                                // No tiene identificador de pasarela (El usuario cerró la ventana antes de redirigirse a Stripe)
                                debeCancelarLocal = true;
                            }

                            // 4. TRANSACCIÓN ATÓMICA DE LIBERACIÓN
                            if (debeCancelarLocal)
                            {
                                using (var trans = await conexion.BeginTransactionAsync())
                                {
                                    try
                                    {
                                        // El guardián asegura que siga en 'Pendiente' en este exacto milisegundo por si acaso
                                        string qVenta = @"UPDATE ""Rifas_Ventas"" SET ""Estado"" = 'Cancelado' WHERE ""Id_Venta"" = @id AND ""Estado"" = 'Pendiente'";
                                        int afectadas = 0;
                                        using (var cmdV = new NpgsqlCommand(qVenta, conexion, trans))
                                        {
                                            cmdV.Parameters.AddWithValue("@id", venta.IdVenta);
                                            afectadas = await cmdV.ExecuteNonQueryAsync();
                                        }

                                        if (afectadas > 0)
                                        {
                                            // Liberar los boletos devolviéndolos al pool público
                                            string qBoletos = venta.EsPlanilla
                                                ? @"UPDATE ""Rifas_Boletos"" SET ""Id_Venta"" = NULL WHERE ""Id_Venta"" = @id AND ""Estado"" != 'Pagado'"
                                                : @"UPDATE ""Rifas_Boletos"" SET ""Id_Venta"" = NULL, ""Estado"" = 'Disponible' WHERE ""Id_Venta"" = @id AND ""Estado"" != 'Pagado'";

                                            using (var cmdB = new NpgsqlCommand(qBoletos, conexion, trans))
                                            {
                                                cmdB.Parameters.AddWithValue("@id", venta.IdVenta);
                                                await cmdB.ExecuteNonQueryAsync();
                                            }

                                            await trans.CommitAsync();
                                            accionTomada = $"Éxito: Venta #{venta.IdVenta} Cancelada. Se liberaron {venta.Cantidad} boletos.";
                                        }
                                        else
                                        {
                                            await trans.RollbackAsync();
                                            accionTomada = "Ignorado: El estado de la venta cambió concurrentemente.";
                                        }
                                    }
                                    catch
                                    {
                                        await trans.RollbackAsync();
                                        throw;
                                    }
                                }
                            }

                            resultados.Add(new { IdLocal = venta.IdVenta, RefStripe = venta.RefStripe, StripeDijo = estatusStripeRaw, Accion = accionTomada });
                        }
                        catch (Exception ex)
                        {
                            resultados.Add(new { IdLocal = venta.IdVenta, Error = ex.Message });
                        }
                    }
                }

                return Json(new { mensaje = "Limpieza masiva de rifas expiradas finalizada.", total_evaluados = resultados.Count, detalle = resultados });
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }
        private async Task ProcesarVentasPendientes(NpgsqlConnection conexion, string tokenEspecifico = null, int? idUsuario = null)
        {
            var listaPendientes = new List<(int Id, string SessionId, DateTime Fecha, bool EsPlanilla, string Email, string Token, decimal Total, string RefStripe)>();

            // Construcción dinámica del filtro: si ambos son nulos, filtroSQL se queda vacío y procesa TODO
            string filtroSQL = "";
            if (!string.IsNullOrEmpty(tokenEspecifico)) filtroSQL = "AND \"Token_Acceso\" = @p";
            else if (idUsuario.HasValue) filtroSQL = "AND (\"Id_Usuario_Comprador\" = @p OR \"Id_Usuario_Vendedor\" = @p)";

            try
            {
                string sql = $@"SELECT ""Id_Venta"", ""Ref_Pasarela_Id"", ""Fecha_Creacion"", ""Metodo_Pago"", ""Email_Contacto"", ""Token_Acceso"", ""Total"", ""Ref_Stripe""
                FROM ""Rifas_Ventas""
                WHERE ""Estado"" = 'Pendiente' {filtroSQL}";

                using (var cmd = new NpgsqlCommand(sql, conexion))
                {
                    if (!string.IsNullOrEmpty(tokenEspecifico)) cmd.Parameters.AddWithValue("@p", tokenEspecifico);
                    else if (idUsuario.HasValue) cmd.Parameters.AddWithValue("@p", idUsuario.Value);

                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            listaPendientes.Add((
                                Id: (int)r["Id_Venta"],
                                SessionId: r["Ref_Pasarela_Id"] != DBNull.Value ? r["Ref_Pasarela_Id"].ToString() : null,
                                Fecha: (DateTime)r["Fecha_Creacion"],
                                EsPlanilla: r["Metodo_Pago"].ToString() == "Planilla",
                                Email: r["Email_Contacto"].ToString(),
                                Token: r["Token_Acceso"].ToString(),
                                Total: (decimal)r["Total"],
                                RefStripe: r["Ref_Stripe"].ToString()
                            ));
                        }
                    }
                }

                var stripeService = new SessionService();

                foreach (var venta in listaPendientes)
                {
                    bool isPaid = false;

                    if (!string.IsNullOrEmpty(venta.SessionId))
                    {
                        try
                        {
                            var options = new SessionGetOptions();
                            options.AddExpand("payment_intent.latest_charge.balance_transaction");

                            var session = await stripeService.GetAsync(venta.SessionId, options);

                            if (session.PaymentStatus == "paid")
                            {
                                // Validación Cruzada y Criptográfica (Monto y Referencia)
                                decimal montoRecibidoStripe = (session.AmountTotal ?? 0) / 100.0m;
                                if (Math.Round(montoRecibidoStripe, 2) != Math.Round(venta.Total, 2))
                                {
                                    Console.WriteLine($"Fraude detectado en Rifa Venta {venta.Id}: Montos no coinciden.");
                                    continue;
                                }

                                if (session.ClientReferenceId != venta.RefStripe)
                                {
                                    Console.WriteLine($"Fraude detectado en Rifa Venta {venta.Id}: Referencia suplantada.");
                                    continue;
                                }

                                decimal montoNeto = 0;
                                try
                                {
                                    if (session.PaymentIntent != null && session.PaymentIntent.LatestCharge != null && session.PaymentIntent.LatestCharge.BalanceTransaction != null)
                                        montoNeto = session.PaymentIntent.LatestCharge.BalanceTransaction.Net / 100.0m;
                                    else
                                        montoNeto = montoRecibidoStripe;
                                }
                                catch { montoNeto = montoRecibidoStripe; }

                                await ConfirmarVentaPagada(venta.Id, session.Id, montoNeto, conexion);
                                isPaid = true;
                            }
                        }
                        catch (Exception exStripe) { Console.WriteLine("Stripe Error: " + exStripe.Message); }
                    }

                    // Si la orden no está pagada en Stripe y ya superó los 30 minutos, se libera automáticamente
                    if (!isPaid && (DateTime.Now - venta.Fecha).TotalMinutes > 30)
                    {
                        using (var trans = await conexion.BeginTransactionAsync())
                        {
                            try
                            {
                                string qVenta = $"UPDATE \"Rifas_Ventas\" SET \"Estado\"='Cancelado' WHERE \"Id_Venta\"={venta.Id} AND \"Estado\" = 'Pendiente'";
                                await new NpgsqlCommand(qVenta, conexion, trans).ExecuteNonQueryAsync();

                                string qBoletos = venta.EsPlanilla
                                    ? $"UPDATE \"Rifas_Boletos\" SET \"Id_Venta\" = NULL WHERE \"Id_Venta\" = {venta.Id}"
                                    : $"UPDATE \"Rifas_Boletos\" SET \"Id_Venta\" = NULL, \"Estado\" = 'Disponible' WHERE \"Id_Venta\" = {venta.Id}";

                                await new NpgsqlCommand(qBoletos, conexion, trans).ExecuteNonQueryAsync();
                                await trans.CommitAsync();
                            }
                            catch { await trans.RollbackAsync(); }
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"Error Validación: {ex.Message}"); }
        }

        public async Task<IActionResult> Index()
        {
            await LimpiarExpiradosRifas();

            var modelo = new RifasIndexViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Validación pasiva global de TODOS los pendientes en la base de datos local
                    await ProcesarVentasPendientes(conexion);

                    string sql = @"
                SELECT r.*,
                (SELECT COUNT(*) FROM ""Rifas_Boletos"" WHERE ""Id_Rifa"" = r.""Id_Rifa"" AND ""Estado"" = 'Pagado') as Vendidos,
                bg.""Numero"" as NumGanador, 
                COALESCE(bg.""Nombre_Cliente_Final"", vg.""Nombre_Contacto"", 'Anónimo') as NomGanador
                FROM ""Rifas"" r 
                LEFT JOIN ""Rifas_Boletos"" bg ON r.""Id_Boleto_Ganador"" = bg.""Id_Boleto""
                LEFT JOIN ""Rifas_Ventas"" vg ON bg.""Id_Venta"" = vg.""Id_Venta""
                ORDER BY r.""Estado"" ASC, r.""Fecha_Sorteo"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            // VALIDACIÓN COMPARTIDA DE VISIBILIDAD
                            bool visiblePublico = r["VisiblePublico"] != DBNull.Value ? (bool)r["VisiblePublico"] : true;

                            // Si no tiene acceso por las reglas de visibilidad, saltamos al siguiente registro
                            if (!PermitirAccesoPorVisibilidad(visiblePublico)) continue;

                            int idRifa = (int)r["Id_Rifa"];
                            modelo.Rifas.Add(new RifaIndexItem
                            {
                                Id_Rifa = idRifa,
                                IdRifaEncriptado = Funciones.EncriptarId(idRifa),
                                Titulo = r["Titulo"].ToString(),
                                CostoBoleto = (decimal)r["Costo_Boleto"],
                                MetaBoletos = (int)r["Meta_Boletos"],
                                Vendidos = Convert.ToInt32(r["Vendidos"]),
                                ImagenUrl = NormalizarUrlImagen(r["ImagenUrl"]?.ToString(), ModoVisualizacionImagen.Incrustado),
                                FechaSorteo = (DateTime)r["Fecha_Sorteo"],
                                Estado = r["Estado"].ToString(),
                                NumeroGanador = r["NumGanador"] != DBNull.Value ? (int)r["NumGanador"] : null,
                                NombreGanador = r["NomGanador"]?.ToString(),
                                VisiblePublico = visiblePublico
                            });
                        }
                    }

                    if (User.Identity.IsAuthenticated)
                    {
                        int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

                        string sqlComprados = @"SELECT COUNT(*) FROM ""Rifas_Boletos"" b
                                        JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
                                        WHERE v.""Id_Usuario_Comprador"" = @uid AND b.""Estado"" = 'Pagado'";
                        using (var cmd = new NpgsqlCommand(sqlComprados, conexion))
                        {
                            cmd.Parameters.AddWithValue("@uid", idUsuario);
                            modelo.TotalComprados = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                        }

                        string sqlPorVender = @"SELECT COUNT(*) FROM ""Rifas_Boletos"" WHERE ""Id_Usuario_Asignado"" = @uid AND ""Estado"" != 'Pagado'";
                        using (var cmd = new NpgsqlCommand(sqlPorVender, conexion))
                        {
                            cmd.Parameters.AddWithValue("@uid", idUsuario);
                            modelo.TotalPorVender = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return View(modelo);
        }

        [Authorize]
        public async Task<IActionResult> MisBoletos(string id)
        {
            int idRifaReal = Funciones.DesencriptarId(id);
            if (idRifaReal <= 0) return RedirectToAction("Index");

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new GestionTableroViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    await ProcesarVentasPendientes(conexion, null, idUsuario);

                    // VALIDACIÓN COMPARTIDA (Consulta Async)
                    if (!await PermitirAccesoPorVisibilidadAsync(idRifaReal, conexion))
                    {
                        MostrarMensaje("Acceso Restringido", "La dinámica se encuentra en modo privado o mantenimiento.", TipoMensaje.Alerta);
                        return RedirectToAction("Index");
                    }

                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);
                    if (!acceso.Existe) return RedirectToAction("Index");

                    modelo.InfoRifa = new RifaPublicaViewModel { Id_Rifa = idRifaReal, IdRifaEncriptado = id, Titulo = acceso.Titulo, Estado = acceso.Estado, CostoBoleto = acceso.CostoXBoleto };

                    var misCompras = new List<BoletoItem>();
                    string sqlCompras = @"SELECT b.*, r.""Titulo"", v.""Fecha_Pago""
                                  FROM ""Rifas_Boletos"" b
                                  JOIN ""Rifas"" r ON b.""Id_Rifa"" = r.""Id_Rifa""
                                  JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
                                  WHERE v.""Id_Usuario_Comprador"" = @uid AND b.""Id_Rifa"" = @idRifa AND b.""Estado"" = 'Pagado'";
                    using (var cmd = new NpgsqlCommand(sqlCompras, conexion))
                    {
                        cmd.Parameters.AddWithValue("@uid", idUsuario);
                        cmd.Parameters.AddWithValue("@idRifa", idRifaReal);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                misCompras.Add(new BoletoItem
                                {
                                    Id_Rifa = idRifaReal,
                                    IdRifaEncriptado = id,
                                    Numero = (int)r["Numero"],
                                    Comprador = r["Titulo"].ToString(),
                                    FechaVenta = (DateTime)r["Fecha_Pago"],
                                    Estado = "Pagado"
                                });
                            }
                        }
                    }
                    ViewBag.MisCompras = misCompras;

                    string condicionFiltro = acceso.EsCreador
                        ? @"(b.""Id_Usuario_Asignado"" = @uid OR (b.""Id_Usuario_Asignado"" IS NULL AND b.""NombrePromotorExterno"" IS NOT NULL))"
                        : @"b.""Id_Usuario_Asignado"" = @uid";

                    string sqlBols = $@"
                SELECT b.*, u.""NombreCompleto"" as Promotor 
                FROM ""Rifas_Boletos"" b
                LEFT JOIN ""Sist_Usuarios"" u ON b.""Id_Usuario_Asignado"" = u.""Id_Usuario""
                WHERE b.""Id_Rifa"" = @id AND {condicionFiltro}
                ORDER BY b.""FechaAsignacion"" DESC, b.""Numero"" ASC";

                    using (var cmd = new NpgsqlCommand(sqlBols, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifaReal);
                        cmd.Parameters.AddWithValue("@uid", idUsuario);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.Boletos.Add(new BoletoItem
                                {
                                    Id_Boleto = (int)r["Id_Boleto"],
                                    IdRifaEncriptado = id,
                                    Numero = (int)r["Numero"],
                                    Estado = r["Estado"].ToString() == "Fisico_En_Mano" ? "Disponible" : r["Estado"].ToString(),
                                    NombrePromotorExterno = r["NombrePromotorExterno"]?.ToString(),
                                    Comprador = r["Nombre_Cliente_Final"]?.ToString(),
                                    Telefono = r["Telefono_Cliente_Final"]?.ToString(),
                                    Comentarios = r["Comentarios"]?.ToString(),
                                    FechaAsignacion = r["FechaAsignacion"] != DBNull.Value ? (DateTime)r["FechaAsignacion"] : (DateTime?)null,
                                    MarcadorPersonal = r["MarcadorPersonal"] != DBNull.Value && (bool)r["MarcadorPersonal"]
                                });
                            }
                        }
                    }

                    string sqlRifaInfo = @"SELECT ""Banco"", ""CuentaClabe"", ""NumeroCuenta"", ""NumeroTarjeta"", ""TitularCuenta"", ""QrEnlaceCompraUrl"", ""EnlaceCompra"", ""Fecha_Sorteo"" FROM ""Rifas"" WHERE ""Id_Rifa"" = @id";
                    using (var cmdInfo = new NpgsqlCommand(sqlRifaInfo, conexion))
                    {
                        cmdInfo.Parameters.AddWithValue("@id", idRifaReal);
                        using (var r = await cmdInfo.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.InfoRifa.Banco = r["Banco"]?.ToString();
                                modelo.InfoRifa.CuentaClabe = r["CuentaClabe"]?.ToString();
                                modelo.InfoRifa.NumeroCuenta = r["NumeroCuenta"]?.ToString();
                                modelo.InfoRifa.NumeroTarjeta = r["NumeroTarjeta"]?.ToString();
                                modelo.InfoRifa.TitularCuenta = r["TitularCuenta"]?.ToString();
                                modelo.InfoRifa.EnlaceCompra = r["EnlaceCompra"]?.ToString();
                                modelo.InfoRifa.QrEnlaceCompraUrl = r["QrEnlaceCompraUrl"]?.ToString();
                                modelo.InfoRifa.FechaSorteo = r["Fecha_Sorteo"] != DBNull.Value ? Convert.ToDateTime(r["Fecha_Sorteo"]) : DateTime.MinValue;
                            }
                        }
                    }

                    string condicionPagos = acceso.EsCreador ? @"(""Id_Usuario_Vendedor"" = @uid OR ""Id_Usuario_Vendedor"" IS NULL)" : @"(""Id_Usuario_Vendedor"" = @uid)";
                    string sqlPagos = $@"SELECT * FROM ""Rifas_Pagos"" WHERE ""Id_Rifa"" = @id AND ""Estado"" IN ('Pendiente', 'Rechazado') AND {condicionPagos}";

                    HashSet<int> boletosBloqueados = new HashSet<int>();

                    using (var cmdP = new NpgsqlCommand(sqlPagos, conexion))
                    {
                        cmdP.Parameters.AddWithValue("@id", idRifaReal);
                        cmdP.Parameters.AddWithValue("@uid", idUsuario);
                        using (var r = await cmdP.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                var pago = new PagoPromotorRifaViewModel
                                {
                                    IdPago = (int)r["IdPago"],
                                    IdRifaEncriptado = id,
                                    IdUsuarioVendedor = r["Id_Usuario_Vendedor"] as int?,
                                    NombrePromotorExterno = r["NombrePromotorExterno"]?.ToString(),
                                    Monto = (decimal)r["Monto"],
                                    ComprobanteUrl = r["ComprobanteUrl"]?.ToString(),
                                    Nota = r["Nota"]?.ToString(),
                                    FechaPago = (DateTime)r["FechaPago"],
                                    BoletosPagados = r["BoletosPagados"]?.ToString()
                                };

                                if (!string.IsNullOrEmpty(pago.BoletosPagados))
                                {
                                    foreach (var idStr in pago.BoletosPagados.Split(','))
                                    {
                                        if (int.TryParse(idStr, out int idB)) boletosBloqueados.Add(idB);
                                    }
                                }

                                if (r["Estado"].ToString() == "Pendiente") modelo.PagosPendientes.Add(pago);
                                else modelo.PagosRechazados.Add(pago);
                            }
                        }
                    }

                    ViewBag.BoletosBloqueados = boletosBloqueados;
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); return RedirectToAction("Index"); }

            return View(modelo);
        }

        [Authorize]
        public async Task<IActionResult> Resultados(string id)
        {
            int idRifaReal = Funciones.DesencriptarId(id);
            if (idRifaReal <= 0) return RedirectToAction("Index");

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new GestionTableroViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    //VALIDACIÓN COMPARTIDA (Consulta Async)
                    if (!await PermitirAccesoPorVisibilidadAsync(idRifaReal, conexion))
                    {
                        MostrarMensaje("Acceso Restringido", "Esta dinámica es privada y los resultados no están disponibles al público.", TipoMensaje.Alerta);
                        return RedirectToAction("Index");
                    }

                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);

                    bool esComprador = false;
                    string sqlCheckCompra = @"SELECT COUNT(*) FROM ""Rifas_Boletos"" b
                              JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
                              WHERE v.""Id_Usuario_Comprador"" = @uid AND b.""Id_Rifa"" = @idr AND b.""Estado"" = 'Pagado'";

                    using (var cmdCheck = new NpgsqlCommand(sqlCheckCompra, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@uid", idUsuario);
                        cmdCheck.Parameters.AddWithValue("@idr", idRifaReal);
                        long count = (long)await cmdCheck.ExecuteScalarAsync();
                        esComprador = count > 0;
                    }

                    if (!acceso.Existe || (!acceso.EsCreador && !acceso.EsVendedor && !esComprador))
                    {
                        return RedirectToAction("Index");
                    }

                    ViewBag.EsAdminOStaff = acceso.EsCreador || acceso.EsVendedor;

                    var enlaceWpObj = await new NpgsqlCommand($@"SELECT ""EnlaceCompra"" FROM ""Rifas"" WHERE ""Id_Rifa"" = {idRifaReal}", conexion).ExecuteScalarAsync();
                    string enlaceWpFinal = (enlaceWpObj != null && enlaceWpObj != DBNull.Value) ? enlaceWpObj.ToString() : null;

                    modelo.InfoRifa = new RifaPublicaViewModel
                    {
                        Id_Rifa = idRifaReal,
                        IdRifaEncriptado = id,
                        Titulo = acceso.Titulo,
                        Estado = acceso.Estado,
                        FechaSorteo = acceso.fechasorteo,
                        EnlaceCompra = enlaceWpFinal
                    };

                    if (acceso.Estado == "Finalizada")
                    {
                        string sqlGanador = @"
            SELECT b.""Numero"", COALESCE(b.""Nombre_Cliente_Final"", v.""Nombre_Contacto"", 'Anónimo') as Comprador,
                   u.""NombreCompleto"" as Promotor
            FROM ""Rifas"" r
            JOIN ""Rifas_Boletos"" b ON r.""Id_Boleto_Ganador"" = b.""Id_Boleto""
            LEFT JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
            LEFT JOIN ""Sist_Usuarios"" u ON b.""Id_Usuario_Asignado"" = u.""Id_Usuario""
            WHERE r.""Id_Rifa"" = @id";

                        using (var cmd = new NpgsqlCommand(sqlGanador, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", idRifaReal);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    ViewBag.NumeroGanador = r["Numero"] != DBNull.Value ? Convert.ToInt32(r["Numero"]).ToString("000") : "000";
                                    ViewBag.NombreGanador = r["Comprador"] != DBNull.Value ? r["Comprador"].ToString() : "Anónimo";
                                    ViewBag.VendedorGanador = r["Promotor"] != DBNull.Value ? r["Promotor"].ToString() : "Venta Web / Directa";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", "No se pudieron procesar los resultados: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }

        [Authorize]
        public async Task<IActionResult> DatosRifa(string id)
        {
            if (!User.TienePermiso(Modulo, PermisoCrear))
            {
                MostrarMensaje("Error", "No tienes permiso de creación en ésta página.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idRifaReal = string.IsNullOrEmpty(id) ? 0 : Funciones.DesencriptarId(id);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new RifaPublicaViewModel
            {
                FechaSorteo = DateTime.Now.AddDays(30),
                MetaBoletos = 100,
                CostoBoleto = 50,
                InversionPremio = 0,
                VisiblePublico = true
            };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Cargar lista de encuestas activas para el selector
                    var encuestasDisponibles = new List<SelectListItem>();
                    string sqlEncuestas = "SELECT \"Id_Encuesta\", \"Titulo\" FROM \"Encuestas_Catalogo\" WHERE \"Activa\" = TRUE ORDER BY \"Fecha_Creacion\" DESC";
                    using (var cmdE = new NpgsqlCommand(sqlEncuestas, conexion))
                    using (var rE = await cmdE.ExecuteReaderAsync())
                    {
                        while (await rE.ReadAsync())
                        {
                            encuestasDisponibles.Add(new SelectListItem { Value = rE["Id_Encuesta"].ToString(), Text = rE["Titulo"].ToString() });
                        }
                    }
                    ViewBag.EncuestasDisponibles = encuestasDisponibles;

                    if (idRifaReal > 0)
                    {
                        var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);

                        if (!acceso.Existe)
                        {
                            MostrarMensaje("Error", "La rifa no existe.", TipoMensaje.Error);
                            return RedirectToAction("Index");
                        }
                        if (!acceso.EsCreador)
                        {
                            MostrarMensaje("Acceso Restringido", "Solo el administrador de la rifa puede editar su configuración.", TipoMensaje.Alerta);
                            return RedirectToAction("Index");
                        }

                        string sql = @"SELECT * FROM ""Rifas"" WHERE ""Id_Rifa"" = @id";
                        using (var cmd = new NpgsqlCommand(sql, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", idRifaReal);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    modelo.Id_Rifa = (int)r["Id_Rifa"];
                                    modelo.IdRifaEncriptado = id;
                                    modelo.IdUsuarioCreador = r["Id_Usuario_Creador"] != DBNull.Value ? (int)r["Id_Usuario_Creador"] : 0;
                                    modelo.Titulo = r["Titulo"].ToString();
                                    modelo.Descripcion = r["Descripcion"]?.ToString();
                                    modelo.CostoBoleto = (decimal)r["Costo_Boleto"];
                                    modelo.MetaBoletos = (int)r["Meta_Boletos"];
                                    modelo.FechaSorteo = (DateTime)r["Fecha_Sorteo"];
                                    modelo.ImagenUrl = r["ImagenUrl"]?.ToString();
                                    modelo.Estado = r["Estado"]?.ToString();

                                    modelo.InversionPremio = r["InversionPremio"] != DBNull.Value ? (decimal)r["InversionPremio"] : 0m;
                                    modelo.Banco = r["Banco"]?.ToString();
                                    modelo.CuentaClabe = r["CuentaClabe"]?.ToString();
                                    modelo.NumeroCuenta = r["NumeroCuenta"]?.ToString();
                                    modelo.NumeroTarjeta = r["NumeroTarjeta"]?.ToString();
                                    modelo.TitularCuenta = r["TitularCuenta"]?.ToString();
                                    modelo.EnlaceCompra = r["EnlaceCompra"]?.ToString();
                                    modelo.QrEnlaceCompraUrl = r["QrEnlaceCompraUrl"]?.ToString();
                                    modelo.VisiblePublico = r["VisiblePublico"] != DBNull.Value ? (bool)r["VisiblePublico"] : true;

                                    ViewBag.IdEdicion = id;
                                }
                            }
                        }

                        // NUEVO: Cargar las encuestas asociadas previamente a esta rifa
                        string sqlAsoc = "SELECT \"Id_Encuesta\" FROM \"Rifas_Encuestas\" WHERE \"Id_Rifa\"=@id";
                        using (var cmdAsoc = new NpgsqlCommand(sqlAsoc, conexion))
                        {
                            cmdAsoc.Parameters.AddWithValue("@id", idRifaReal);
                            using (var rA = await cmdAsoc.ExecuteReaderAsync())
                            {
                                while (await rA.ReadAsync())
                                {
                                    modelo.EncuestasAsociadas.Add((int)rA["Id_Encuesta"]);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo cargar la configuración: " + ex.Message, TipoMensaje.Error);
            }

            return View(modelo);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(RifaPublicaViewModel form, IFormFile fotoPortadaNueva, string idRifa = "", List<int> EncuestasAsociadas = null)
        {
            if (!User.TienePermiso(Modulo, PermisoCrear))
            {
                MostrarMensaje("Error", "No tienes permiso de creación en ésta página.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            int idRifaReal = string.IsNullOrEmpty(idRifa) ? 0 : Funciones.DesencriptarId(idRifa);

            try
            {
                if (string.IsNullOrWhiteSpace(form.Descripcion)) throw new Exception("La descripción de la rifa es obligatoria.");
                if (form.CostoBoleto <= 0) throw new Exception("El costo del boleto debe ser mayor a 0.");
                if (form.MetaBoletos <= 0) throw new Exception("La cantidad de boletos debe ser mayor a 0.");
                if (form.FechaSorteo <= DateTime.Now) throw new Exception("La fecha del sorteo debe ser en el futuro.");
                if (form.InversionPremio < 0) throw new Exception("El costo del premio no puede ser negativo.");

                if (idRifaReal == 0 && (fotoPortadaNueva == null || fotoPortadaNueva.Length == 0))
                    throw new Exception("La imagen de portada es obligatoria al crear una rifa.");

                bool tieneDatosBancarios = !string.IsNullOrWhiteSpace(form.Banco) || !string.IsNullOrWhiteSpace(form.TitularCuenta) || !string.IsNullOrWhiteSpace(form.CuentaClabe) || !string.IsNullOrWhiteSpace(form.NumeroCuenta) || !string.IsNullOrWhiteSpace(form.NumeroTarjeta);
                if (tieneDatosBancarios && string.IsNullOrWhiteSpace(form.CuentaClabe) && string.IsNullOrWhiteSpace(form.NumeroCuenta) && string.IsNullOrWhiteSpace(form.NumeroTarjeta))
                {
                    throw new Exception("Debes ingresar al menos la Cuenta CLABE, Número de Cuenta o Número de Tarjeta para guardar la información bancaria.");
                }

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    int metaActualEnBD = 0;
                    string urlVieja = "";
                    string imgUrlFinal = form.ImagenUrl;
                    string enlaceCompraActualEnBD = null;
                    string qrCompraActualEnBD = null;

                    if (idRifaReal > 0)
                    {
                        var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);
                        if (!acceso.EsCreador) throw new Exception("No tienes permisos de administrador sobre esta rifa.");

                        if (acceso.Estado == "Finalizada") throw new Exception("Bloqueo de Seguridad: El sorteo de esta dinámica ya fue realizado. No es posible editar su configuración.");
                        if (acceso.Estado != "Activa") throw new Exception("No puedes editar una rifa inactiva o cancelada.");

                        string sqlVal = @"SELECT ""Meta_Boletos"", ""ImagenUrl"", ""EnlaceCompra"", ""QrEnlaceCompraUrl"" FROM ""Rifas"" WHERE ""Id_Rifa"" = @id";
                        using (var cmdVal = new NpgsqlCommand(sqlVal, conexion))
                        {
                            cmdVal.Parameters.AddWithValue("@id", idRifaReal);
                            using (var r = await cmdVal.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    metaActualEnBD = (int)r["Meta_Boletos"];
                                    urlVieja = r["ImagenUrl"]?.ToString() ?? "";
                                    enlaceCompraActualEnBD = r["EnlaceCompra"]?.ToString();
                                    qrCompraActualEnBD = r["QrEnlaceCompraUrl"]?.ToString();
                                }
                            }
                        }

                        if (form.MetaBoletos < metaActualEnBD)
                            throw new Exception($"No puedes reducir la cantidad de boletos. El mínimo actual es {metaActualEnBD}.");
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            if (fotoPortadaNueva != null)
                            {
                                if (fotoPortadaNueva.Length > 10 * 1024 * 1024)
                                    throw new Exception("La imagen de portada supera el límite permitido de 10MB.");

                                var extension = Path.GetExtension(fotoPortadaNueva.FileName).ToLower();
                                var mimeType = fotoPortadaNueva.ContentType.ToLower();
                                var mimesValidos = new[] { "image/jpeg", "image/jpg", "image/png", "image/webp" };
                                var extsValidas = new[] { ".jpg", ".jpeg", ".png", ".webp" };

                                if (!mimesValidos.Contains(mimeType) || !extsValidas.Contains(extension))
                                    throw new Exception("El archivo de portada no es una imagen válida (Solo se permite JPG, PNG, WEBP).");

                                imgUrlFinal = await SubirImagenCloudinary(fotoPortadaNueva, $"{sAmbiente}/Imágenes/Rifas/{idUsuario}");
                                if (!string.IsNullOrEmpty(urlVieja)) await DestruirImagenCloudinary(urlVieja);
                            }

                            if (idRifaReal == 0)
                            {
                                string sql = @"INSERT INTO ""Rifas"" 
                    (""Titulo"", ""Descripcion"", ""Costo_Boleto"", ""Meta_Boletos"", ""Fecha_Creacion"", ""Fecha_Sorteo"", ""Estado"", ""Id_Usuario_Creador"", ""ImagenUrl"", 
                     ""Banco"", ""CuentaClabe"", ""NumeroCuenta"", ""NumeroTarjeta"", ""TitularCuenta"", ""InversionPremio"", ""VisiblePublico"")
                    VALUES (@tit, @desc, @costo, @meta, NOW(), @sorteo, 'Activa', @uid, @img, @banco, @clabe, @numcta, @numtar, @titular, @inversion, @visible)
                    RETURNING ""Id_Rifa""";

                                using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@tit", form.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", (object)form.Descripcion ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@costo", form.CostoBoleto);
                                    cmd.Parameters.AddWithValue("@meta", form.MetaBoletos);
                                    cmd.Parameters.AddWithValue("@sorteo", form.FechaSorteo);
                                    cmd.Parameters.AddWithValue("@uid", idUsuario);
                                    cmd.Parameters.AddWithValue("@img", (object)imgUrlFinal ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@banco", (object)form.Banco ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@clabe", (object)form.CuentaClabe ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@numcta", (object)form.NumeroCuenta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@numtar", (object)form.NumeroTarjeta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@titular", (object)form.TitularCuenta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@inversion", form.InversionPremio);
                                    cmd.Parameters.AddWithValue("@visible", form.VisiblePublico);

                                    idRifaReal = (int)await cmd.ExecuteScalarAsync();
                                }

                                string sqlGen = $@"INSERT INTO ""Rifas_Boletos"" (""Id_Rifa"", ""Numero"", ""Estado"")
                                   SELECT @idR, s.i, 'Disponible'
                                   FROM generate_series(1, @meta) AS s(i)";
                                using (var cmd = new NpgsqlCommand(sqlGen, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@idR", idRifaReal);
                                    cmd.Parameters.AddWithValue("@meta", form.MetaBoletos);
                                    await cmd.ExecuteNonQueryAsync();
                                }

                                string tokenRifa = Funciones.EncriptarId(idRifaReal);
                                string urlCompra = Url.Action("Seleccion", "Rifas", new { id = tokenRifa }, Request.Scheme);
                                var qrService = new GeneradorQRService();
                                string base64Qr = qrService.GenerarImagenQR(urlCompra, "#0d6efd", "#FFFFFF", null, FormaOjos.Redondeado, FormaModulos.Redondeado);
                                string nuevoQrCompraUrl = await SubirQrBase64Cloudinary(base64Qr, $"{sAmbiente}/Rifas/QRsCompra/{idUsuario}");

                                string sqlUpdQr = @"UPDATE ""Rifas"" SET ""EnlaceCompra"" = @enlace, ""QrEnlaceCompraUrl"" = @qrUrl WHERE ""Id_Rifa"" = @id";
                                using (var cmdQr = new NpgsqlCommand(sqlUpdQr, conexion, trans))
                                {
                                    cmdQr.Parameters.AddWithValue("@enlace", urlCompra);
                                    cmdQr.Parameters.AddWithValue("@qrUrl", nuevoQrCompraUrl);
                                    cmdQr.Parameters.AddWithValue("@id", idRifaReal);
                                    await cmdQr.ExecuteNonQueryAsync();
                                }
                            }
                            else
                            {
                                string nuevoQrCompraUrl = qrCompraActualEnBD;
                                string urlCompra = enlaceCompraActualEnBD;

                                if (string.IsNullOrEmpty(nuevoQrCompraUrl) || string.IsNullOrEmpty(urlCompra))
                                {
                                    string tokenRifa = Funciones.EncriptarId(idRifaReal);
                                    urlCompra = Url.Action("Seleccion", "Rifas", new { id = tokenRifa }, Request.Scheme);
                                    var qrService = new GeneradorQRService();
                                    string base64Qr = qrService.GenerarImagenQR(urlCompra, "#0d6efd", "#FFFFFF", null, FormaOjos.Redondeado, FormaModulos.Redondeado);
                                    nuevoQrCompraUrl = await SubirQrBase64Cloudinary(base64Qr, $"{sAmbiente}/Rifas/QRsCompra/{idUsuario}");
                                }

                                string sqlUpd = @"UPDATE ""Rifas"" SET 
                    ""Titulo""=@tit, ""Descripcion""=@desc, ""Costo_Boleto""=@costo, 
                    ""Meta_Boletos""=@meta, ""Fecha_Sorteo""=@sorteo, ""ImagenUrl""=COALESCE(@img, ""ImagenUrl""),
                    ""Banco""=@banco, ""CuentaClabe""=@clabe, ""NumeroCuenta""=@numcta, ""NumeroTarjeta""=@numtar, ""TitularCuenta""=@titular,
                    ""EnlaceCompra""=@enlace, ""QrEnlaceCompraUrl""=@qrUrl, ""InversionPremio""=@inversion, ""VisiblePublico""=@visible
                    WHERE ""Id_Rifa""=@id";

                                using (var cmd = new NpgsqlCommand(sqlUpd, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@tit", form.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", (object)form.Descripcion ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@costo", form.CostoBoleto);
                                    cmd.Parameters.AddWithValue("@meta", form.MetaBoletos);
                                    cmd.Parameters.AddWithValue("@sorteo", form.FechaSorteo);
                                    cmd.Parameters.AddWithValue("@img", (object)imgUrlFinal ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@banco", (object)form.Banco ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@clabe", (object)form.CuentaClabe ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@numcta", (object)form.NumeroCuenta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@numtar", (object)form.NumeroTarjeta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@titular", (object)form.TitularCuenta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@enlace", (object)urlCompra ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@qrUrl", (object)nuevoQrCompraUrl ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@inversion", form.InversionPremio);
                                    cmd.Parameters.AddWithValue("@visible", form.VisiblePublico);
                                    cmd.Parameters.AddWithValue("@id", idRifaReal);

                                    await cmd.ExecuteNonQueryAsync();
                                }

                                if (form.MetaBoletos > metaActualEnBD)
                                {
                                    string sqlGenExtra = $@"INSERT INTO ""Rifas_Boletos"" (""Id_Rifa"", ""Numero"", ""Estado"")
                                            SELECT @idR, s.i, 'Disponible'
                                            FROM generate_series(@inicio, @fin) AS s(i)";
                                    using (var cmd = new NpgsqlCommand(sqlGenExtra, conexion, trans))
                                    {
                                        cmd.Parameters.AddWithValue("@idR", idRifaReal);
                                        cmd.Parameters.AddWithValue("@inicio", metaActualEnBD + 1);
                                        cmd.Parameters.AddWithValue("@fin", form.MetaBoletos);
                                        await cmd.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            // GESTIÓN DE LAS ENCUESTAS ASOCIADAS
                            string sqlDelEncAsoc = "DELETE FROM \"Rifas_Encuestas\" WHERE \"Id_Rifa\" = @id";
                            using (var cmdDE = new NpgsqlCommand(sqlDelEncAsoc, conexion, trans))
                            {
                                cmdDE.Parameters.AddWithValue("@id", idRifaReal);
                                await cmdDE.ExecuteNonQueryAsync();
                            }

                            if (EncuestasAsociadas != null && EncuestasAsociadas.Any())
                            {
                                foreach (var idE in EncuestasAsociadas)
                                {
                                    string sqlInsEncAsoc = "INSERT INTO \"Rifas_Encuestas\" (\"Id_Rifa\", \"Id_Encuesta\") VALUES (@idR, @idE)";
                                    using (var cmdIE = new NpgsqlCommand(sqlInsEncAsoc, conexion, trans))
                                    {
                                        cmdIE.Parameters.AddWithValue("@idR", idRifaReal);
                                        cmdIE.Parameters.AddWithValue("@idE", idE);
                                        await cmdIE.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            var accion = metaActualEnBD == 0 ? Parametros.AccionesBitacora.Crear : Parametros.AccionesBitacora.Editar;
                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, accion, $"Gestor General: Rifa {idRifaReal} guardada con estatus de visibilidad: {form.VisiblePublico}.", ip, trans);

                            await trans.CommitAsync();
                            MostrarMensaje("Éxito", "Configuración de la rifa guardada correctamente.", TipoMensaje.Exito);
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                if (idRifaReal > 0) ViewBag.IdEdicion = idRifa;
                return View("DatosRifa", form);
            }

            return RedirectToAction("Index");
        }
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarRifa(string idRifaEliminar)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Crear))
            {
                MostrarMensaje("Error", "No posees los privilegios administrativos requeridos para eliminar registros.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idRifaReal = string.IsNullOrEmpty(idRifaEliminar) ? 0 : Funciones.DesencriptarId(idRifaEliminar);
            if (idRifaReal <= 0) return RedirectToAction("Index");

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // VALIDACIÓN ESTRICTA: Validar que no existan boletos alterados, pagos reportados ni ventas inicializadas
                    string sqlValidar = @"
                SELECT 
                    (SELECT COUNT(*) FROM ""Rifas_Boletos"" WHERE ""Id_Rifa"" = @id AND ""Estado"" != 'Disponible') as BoletosOcupados,
                    (SELECT COUNT(*) FROM ""Rifas_Pagos"" WHERE ""Id_Rifa"" = @id) as TotalPagos,
                    (SELECT COUNT(*) FROM ""Rifas_Ventas"" WHERE ""Id_Rifa"" = @id) as TotalVentas";

                    using (var cmdVal = new NpgsqlCommand(sqlValidar, conexion))
                    {
                        cmdVal.Parameters.AddWithValue("@id", idRifaReal);
                        using (var r = await cmdVal.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                long boletosOcupados = Convert.ToInt64(r["BoletosOcupados"]);
                                long totalPagos = Convert.ToInt64(r["TotalPagos"]);
                                long totalVentas = Convert.ToInt64(r["TotalVentas"]);

                                if (boletosOcupados > 0 || totalPagos > 0 || totalVentas > 0)
                                {
                                    throw new Exception("Operación Abortada: La dinámica no puede eliminarse porque ya contiene boletos vendidos/apartados, intentos de compra de boletos o planillas vinculadas.");
                                }
                            }
                        }
                    }

                    // Obtener las URLs de Cloudinary asociadas para evitar archivos huérfanos
                    string urlImagen = "";
                    string urlQr = "";
                    string sqlImgs = @"SELECT ""ImagenUrl"", ""QrEnlaceCompraUrl"" FROM ""Rifas"" WHERE ""Id_Rifa"" = @id";
                    using (var cmdImgs = new NpgsqlCommand(sqlImgs, conexion))
                    {
                        cmdImgs.Parameters.AddWithValue("@id", idRifaReal);
                        using (var r = await cmdImgs.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                urlImagen = r["ImagenUrl"]?.ToString();
                                urlQr = r["QrEnlaceCompraUrl"]?.ToString();
                            }
                        }
                    }

                    // Ejecución transaccional limpia del borrado físico en cascada coordinada
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sqlDelBoletos = @"DELETE FROM ""Rifas_Boletos"" WHERE ""Id_Rifa"" = @id";
                            using (var cmdDelB = new NpgsqlCommand(sqlDelBoletos, conexion, trans))
                            {
                                cmdDelB.Parameters.AddWithValue("@id", idRifaReal);
                                await cmdDelB.ExecuteNonQueryAsync();
                            }

                            string sqlDelDesc = @"DELETE FROM ""Rifas_Descartes"" WHERE ""Id_Rifa"" = @id";
                            using (var cmdDelD = new NpgsqlCommand(sqlDelDesc, conexion, trans))
                            {
                                cmdDelD.Parameters.AddWithValue("@id", idRifaReal);
                                await cmdDelD.ExecuteNonQueryAsync();
                            }

                            string sqlDelRifa = @"DELETE FROM ""Rifas"" WHERE ""Id_Rifa"" = @id";
                            using (var cmdDelR = new NpgsqlCommand(sqlDelRifa, conexion, trans))
                            {
                                cmdDelR.Parameters.AddWithValue("@id", idRifaReal);
                                await cmdDelR.ExecuteNonQueryAsync();
                            }

                            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Borrar, $"Rifas Sistema: Eliminación física e irrevocable de Rifa ID {idRifaReal}.", ip, trans);

                            await trans.CommitAsync();
                        }
                        catch
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }

                    // Borrado físico de activos digitales en la nube
                    if (!string.IsNullOrEmpty(urlImagen)) await DestruirImagenCloudinary(urlImagen);
                    if (!string.IsNullOrEmpty(urlQr)) await DestruirImagenCloudinary(urlQr);

                    MostrarMensaje("Éxito", "La dinámica y todo su inventario de boletos vacíos han sido eliminados del servidor de forma permanente.", TipoMensaje.Exito);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Bloqueo de Seguridad", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }
        private async Task<string> SubirImagenCloudinary(IFormFile foto, string folderPath)
        {
            using var memoryStream = new MemoryStream();
            using (var image = await SixLabors.ImageSharp.Image.LoadAsync(foto.OpenReadStream()))
            {
                const int MaxWidth = 1200;
                if (image.Width > MaxWidth)
                {
                    int newHeight = (int)((double)image.Height / image.Width * MaxWidth);
                    image.Mutate(x => x.Resize(MaxWidth, newHeight));
                }
                var encoder = new JpegEncoder { Quality = 75 };
                await image.SaveAsync(memoryStream, encoder);
            }

            memoryStream.Position = 0;
            var uploadParams = new ImageUploadParams()
            {
                File = new FileDescription(foto.FileName, memoryStream),
                Folder = folderPath,
                Transformation = new Transformation().FetchFormat("auto"),
                UseFilename = false,
                UniqueFilename = true
            };

            var uploadResult = await _cloudinary.UploadAsync(uploadParams);
            return uploadResult.SecureUrl.ToString();
        }

        [AllowAnonymous]
        [HttpGet("Rifas/TuPedido/{token}")]
        public async Task<IActionResult> TuPedido(string token, string session_id = null)
        {
            if (string.IsNullOrEmpty(token))
            {
                MostrarMensaje("Error", "No se ha recibido el token del pedido.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            var modelo = new EstatusPedidoViewModel { Token = token };
            bool esPlanilla = false;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    await ProcesarVentasPendientes(conexion, token, null);

                    string sqlVenta = @"SELECT v.*, r.""Titulo"" as TituloRifa, r.""Costo_Boleto"", r.""VisiblePublico""
                        FROM ""Rifas_Ventas"" v
                        JOIN ""Rifas"" r ON v.""Id_Rifa"" = r.""Id_Rifa""
                        WHERE v.""Token_Acceso"" = @tok";

                    using (var cmd = new NpgsqlCommand(sqlVenta, conexion))
                    {
                        cmd.Parameters.AddWithValue("@tok", token);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                // VALIDACIÓN COMPARTIDA (Ya tenemos el dato del JOIN en 'r')
                                bool visiblePublico = r["VisiblePublico"] != DBNull.Value ? (bool)r["VisiblePublico"] : true;
                                if (!PermitirAccesoPorVisibilidad(visiblePublico))
                                {
                                    MostrarMensaje("Acceso Restringido", "El acceso a la dinámica está pausado. Si tienes boletos pagados, están seguros y podrás verlos cuando la dinámica sea pública nuevamente.", TipoMensaje.Alerta);
                                    return RedirectToAction("Index");
                                }

                                modelo.IdVenta = (int)r["Id_Venta"];
                                modelo.TituloRifa = r["TituloRifa"].ToString();
                                modelo.Estado = r["Estado"].ToString();
                                modelo.RefStripe = r["Ref_Stripe"]?.ToString();
                                modelo.Email = r["Email_Contacto"].ToString();
                                esPlanilla = r["Metodo_Pago"].ToString() == "Planilla";

                                if (modelo.Estado == "Cancelado") modelo.Total = 0;
                                else modelo.Total = (decimal)r["Total"];
                            }
                            else return NotFound("Pedido no encontrado.");
                        }
                    }

                    modelo.Boletos = new List<int>();
                    if (modelo.IdVenta > 0)
                    {
                        string sqlBols = @"SELECT ""Numero"" FROM ""Rifas_Boletos"" WHERE ""Id_Venta"" = @idV ORDER BY ""Numero"" ASC";
                        using (var cmd = new NpgsqlCommand(sqlBols, conexion))
                        {
                            cmd.Parameters.AddWithValue("@idV", modelo.IdVenta);
                            using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) modelo.Boletos.Add((int)r["Numero"]);
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            ViewBag.EsPlanilla = esPlanilla;
            return View(modelo);
        }

        [AllowAnonymous]
        public async Task<IActionResult> Seleccion(string id)
        {
            int idRifaReal = Funciones.DesencriptarId(id);
            var modelo = new RifaPublicaViewModel();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = @"SELECT * FROM ""Rifas"" WHERE ""Id_Rifa"" = @pId";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@pId", idRifaReal);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                bool visiblePublico = r["VisiblePublico"] != DBNull.Value ? (bool)r["VisiblePublico"] : true;
                                if (!PermitirAccesoPorVisibilidad(visiblePublico))
                                {
                                    MostrarMensaje("Acceso Restringido", "Esta dinámica es privada y no admite visitas al público en este momento.", TipoMensaje.Alerta);
                                    return RedirectToAction("Index");
                                }

                                modelo.Id_Rifa = (int)r["Id_Rifa"];
                                modelo.IdRifaEncriptado = id;
                                modelo.Titulo = r["Titulo"].ToString();
                                modelo.Descripcion = r["Descripcion"].ToString();
                                modelo.ImagenUrl = NormalizarUrlImagen(r["ImagenUrl"]?.ToString(), ModoVisualizacionImagen.Incrustado);
                                modelo.CostoBoleto = (decimal)r["Costo_Boleto"];
                                modelo.FechaSorteo = (DateTime)r["Fecha_Sorteo"];
                                modelo.Estado = r["Estado"].ToString();

                                int? idBolGanador = r["Id_Boleto_Ganador"] as int?;
                                if (idBolGanador.HasValue) ViewBag.IdBoletoGanador = idBolGanador.Value;

                                if (modelo.Estado == "Cancelada")
                                {
                                    MostrarMensaje("Lo sentimos", "Esta dinámica fue cancelada.", TipoMensaje.Alerta);
                                    return RedirectToAction("Index");
                                }
                            }
                            else
                            {
                                MostrarMensaje("Error", "La rifa que buscas no existe.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    // Validar si esta rifa tiene encuestas promocionales asociadas
                    using (var cmdE = new NpgsqlCommand("SELECT COUNT(*) FROM \"Rifas_Encuestas\" WHERE \"Id_Rifa\" = @id", conexion))
                    {
                        cmdE.Parameters.AddWithValue("@id", idRifaReal);
                        ViewBag.TieneEncuestasAsociadas = (long)await cmdE.ExecuteScalarAsync() > 0;
                    }

                    if (modelo.Estado == "Finalizada" && ViewBag.IdBoletoGanador != null)
                    {
                        int idBolGanador = (int)ViewBag.IdBoletoGanador;
                        var cmdBol = new NpgsqlCommand("SELECT \"Numero\" FROM \"Rifas_Boletos\" WHERE \"Id_Boleto\" = @idB", conexion);
                        cmdBol.Parameters.AddWithValue("@idB", idBolGanador);
                        var numObj = await cmdBol.ExecuteScalarAsync();
                        if (numObj != null && numObj != DBNull.Value)
                        {
                            ViewBag.NumeroGanador = Convert.ToInt32(numObj).ToString("000");
                        }
                    }

                    modelo.NumerosDisponibles = new List<int>();
                    if (modelo.Estado == "Activa")
                    {
                        string sqlBol = @"SELECT ""Numero"" FROM ""Rifas_Boletos"" WHERE ""Id_Rifa"" = @pId AND ""Estado"" = 'Disponible' ORDER BY ""Numero"" ASC";

                        using (var cmd = new NpgsqlCommand(sqlBol, conexion))
                        {
                            cmd.Parameters.AddWithValue("@pId", idRifaReal);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync()) modelo.NumerosDisponibles.Add((int)r["Numero"]);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", "Detalle: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }

        // =========================================================================
        // MÉTODO NUEVO PARA CANJEAR FOLIOS POR BOLETOS
        // =========================================================================
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CanjearFolio(string idRifa, string folio)
        {
            try
            {
                int idRifaReal = Funciones.DesencriptarId(idRifa);
                int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
                string nombreUsuario = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Usuario";
                string emailUsuario = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "sin_correo@sistema.com";

                if (!int.TryParse(folio, out int idRespuestaFolio))
                    return Json(new { exito = false, mensaje = "El formato del folio ingresado es incorrecto." });

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Utilizamos IsolationLevel.Serializable para garantizar que nadie más tome el mismo boleto al mismo tiempo
                    using (var trans = await conexion.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
                    {
                        try
                        {
                            // 1. Validar que no haya canjeado ya en esta rifa (Regla 4)
                            var cmdCheckU = new NpgsqlCommand("SELECT COUNT(*) FROM \"Rifas_Folios_Canjeados\" WHERE \"Id_Rifa\"=@idR AND \"Id_Usuario\"=@idU", conexion, trans);
                            cmdCheckU.Parameters.AddWithValue("@idR", idRifaReal);
                            cmdCheckU.Parameters.AddWithValue("@idU", idUsuario);
                            if ((long)await cmdCheckU.ExecuteScalarAsync() > 0)
                                throw new Exception("Ya has reclamado un boleto gratuito para esta dinámica. Solo se permite 1 por usuario.");

                            // 2. Validar Folio existente y obtener Id_Encuesta (Regla 5)
                            var cmdGetEnc = new NpgsqlCommand("SELECT \"Id_Encuesta\" FROM \"Encuestas_Respuestas_Header\" WHERE \"Id_Respuesta\"=@idR", conexion, trans);
                            cmdGetEnc.Parameters.AddWithValue("@idR", idRespuestaFolio);
                            var objEnc = await cmdGetEnc.ExecuteScalarAsync();
                            if (objEnc == null)
                                throw new Exception("El folio ingresado no existe en nuestro registro de encuestas.");
                            int idEncuestaFolio = (int)objEnc;

                            // 3. Validar que la encuesta esté asociada a la rifa (Regla 5)
                            var cmdCheckAsoc = new NpgsqlCommand("SELECT COUNT(*) FROM \"Rifas_Encuestas\" WHERE \"Id_Rifa\"=@idR AND \"Id_Encuesta\"=@idE", conexion, trans);
                            cmdCheckAsoc.Parameters.AddWithValue("@idR", idRifaReal);
                            cmdCheckAsoc.Parameters.AddWithValue("@idE", idEncuestaFolio);
                            if ((long)await cmdCheckAsoc.ExecuteScalarAsync() == 0)
                                throw new Exception("Este folio pertenece a una encuesta que no tiene promoción activa en esta dinámica.");

                            // 4. Validar que el folio no se haya usado ya globalmente (Regla 6)
                            var cmdCheckFolio = new NpgsqlCommand("SELECT COUNT(*) FROM \"Rifas_Folios_Canjeados\" WHERE \"Id_Respuesta\"=@idR", conexion, trans);
                            cmdCheckFolio.Parameters.AddWithValue("@idR", idRespuestaFolio);
                            if ((long)await cmdCheckFolio.ExecuteScalarAsync() > 0)
                                throw new Exception("Lo sentimos, este folio ya fue canjeado anteriormente por un boleto.");

                            // 5. Obtener un boleto disponible aleatorio y bloquearlo (FOR UPDATE)
                            var cmdGetTicket = new NpgsqlCommand(@"SELECT ""Id_Boleto"", ""Numero"" FROM ""Rifas_Boletos"" 
                                WHERE ""Id_Rifa""=@idR AND ""Estado""='Disponible' 
                                ORDER BY RANDOM() LIMIT 1 FOR UPDATE", conexion, trans);
                            cmdGetTicket.Parameters.AddWithValue("@idR", idRifaReal);

                            int idBoleto = 0;
                            int numeroBoleto = 0;
                            using (var rT = await cmdGetTicket.ExecuteReaderAsync())
                            {
                                if (await rT.ReadAsync())
                                {
                                    idBoleto = (int)rT["Id_Boleto"];
                                    numeroBoleto = (int)rT["Numero"];
                                }
                                else throw new Exception("No quedan boletos disponibles en esta rifa para realizar el canje.");
                            }

                            // 6. Crear la Venta (Total 0)
                            string tokenAcceso = Guid.NewGuid().ToString("N");
                            string refStripe = $"FOLIO_{idRespuestaFolio}_{Guid.NewGuid().ToString("N").Substring(0, 4)}";

                            string sqlVenta = @"INSERT INTO ""Rifas_Ventas"" 
                                (""Id_Rifa"", ""Total"", ""Total_Neto"", ""Cantidad_Boletos"", ""Estado"", ""Metodo_Pago"", ""Token_Acceso"", ""Email_Contacto"", ""Nombre_Contacto"", ""Id_Usuario_Comprador"", ""Ref_Stripe"", ""Fecha_Pago"")
                                VALUES (@idR, 0, 0, 1, 'Pagado', 'Folio_Encuesta', @tok, @mail, @nom, @uid, @ref, NOW()) RETURNING ""Id_Venta""";

                            int idVenta = 0;
                            using (var cmdV = new NpgsqlCommand(sqlVenta, conexion, trans))
                            {
                                cmdV.Parameters.AddWithValue("@idR", idRifaReal);
                                cmdV.Parameters.AddWithValue("@tok", tokenAcceso);
                                cmdV.Parameters.AddWithValue("@mail", emailUsuario);
                                cmdV.Parameters.AddWithValue("@nom", nombreUsuario);
                                cmdV.Parameters.AddWithValue("@uid", idUsuario);
                                cmdV.Parameters.AddWithValue("@ref", refStripe);
                                idVenta = (int)await cmdV.ExecuteScalarAsync();
                            }

                            // 7. Actualizar Boleto asignado (Regla 3)
                            string sqlUpdBol = @"UPDATE ""Rifas_Boletos"" SET ""Estado""='Pagado', ""Id_Venta""=@idV, ""Nombre_Cliente_Final""=@nom WHERE ""Id_Boleto""=@idB";
                            using (var cmdU = new NpgsqlCommand(sqlUpdBol, conexion, trans))
                            {
                                cmdU.Parameters.AddWithValue("@idV", idVenta);
                                cmdU.Parameters.AddWithValue("@nom", nombreUsuario);
                                cmdU.Parameters.AddWithValue("@idB", idBoleto);
                                await cmdU.ExecuteNonQueryAsync();
                            }

                            // 8. Registrar canje para historial (Regla 7)
                            string sqlCanje = @"INSERT INTO ""Rifas_Folios_Canjeados"" (""Id_Rifa"", ""Id_Usuario"", ""Id_Respuesta"", ""Id_Boleto"") VALUES (@idR, @idU, @idResp, @idB)";
                            using (var cmdC = new NpgsqlCommand(sqlCanje, conexion, trans))
                            {
                                cmdC.Parameters.AddWithValue("@idR", idRifaReal);
                                cmdC.Parameters.AddWithValue("@idU", idUsuario);
                                cmdC.Parameters.AddWithValue("@idResp", idRespuestaFolio);
                                cmdC.Parameters.AddWithValue("@idB", idBoleto);
                                await cmdC.ExecuteNonQueryAsync();
                            }

                            // 9. Bitácora de Auditoría
                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Crear, $"Canje de folio de encuesta #{folio} por boleto #{numeroBoleto.ToString("000")} en rifa {idRifaReal}", HttpContext.Connection.RemoteIpAddress?.ToString(), trans);

                            await trans.CommitAsync();

                            return Json(new { exito = true, numero = numeroBoleto.ToString("000"), token = tokenAcceso, mensaje = $"¡Felicidades! Has reclamado con éxito el boleto #{numeroBoleto.ToString("000")}." });
                        }
                        catch (Exception innerEx)
                        {
                            await trans.RollbackAsync();
                            throw innerEx;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = ex.Message });
            }
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> IniciarPagoStripe(string token)
        {
            try
            {
                string urlPago = "";
                string sessionIdGenerado = "";

                int idVenta = 0;
                decimal total = 0;
                string refSegura = "";
                int idRifa = 0;
                int cantidadBoletosEsperada = 0;
                string emailContacto = "";

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. OBTENEMOS LOS DATOS DE LA VENTA PENDIENTE
                    var cmd = new NpgsqlCommand(@"SELECT ""Id_Venta"", ""Total"", ""Ref_Stripe"", ""Id_Rifa"", ""Cantidad_Boletos"", ""Email_Contacto"" 
                                          FROM ""Rifas_Ventas"" WHERE ""Token_Acceso"" = @tok AND ""Estado"" = 'Pendiente'", conexion);
                    cmd.Parameters.AddWithValue("@tok", token);

                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        if (await r.ReadAsync())
                        {
                            idVenta = (int)r["Id_Venta"];
                            total = (decimal)r["Total"];
                            refSegura = r["Ref_Stripe"].ToString();
                            idRifa = (int)r["Id_Rifa"];
                            cantidadBoletosEsperada = (int)r["Cantidad_Boletos"];
                            emailContacto = r["Email_Contacto"].ToString();
                        }
                    }

                    // 2. BLINDAJE: VERIFICAMOS QUE LOS BOLETOS SIGAN RESERVADOS
                    if (idVenta > 0)
                    {
                        string sqlValidarBoletos = @"SELECT COUNT(*) FROM ""Rifas_Boletos"" WHERE ""Id_Venta"" = @idV AND ""Estado"" = 'Reservado'";
                        using (var cmdVal = new NpgsqlCommand(sqlValidarBoletos, conexion))
                        {
                            cmdVal.Parameters.AddWithValue("@idV", idVenta);
                            long boletosAunReservados = (long)await cmdVal.ExecuteScalarAsync();

                            if (boletosAunReservados != cantidadBoletosEsperada)
                            {
                                throw new Exception("Tu tiempo de reserva expiró y algunos de tus boletos fueron liberados. Por favor, selecciona nuevos boletos.");
                            }
                        }

                        // Verificar si ya existe una sesión de Stripe activa antes de crear una nueva.
                        // Esto previene que un doble clic genere dos sesiones de pago distintas para el mismo pedido.
                        string urlRetorno = $"{Request.Scheme}://{Request.Host}/Rifas/TuPedido/{token}";

                        using (var cmdCheckRef = new NpgsqlCommand(@"SELECT ""Ref_Pasarela_Id"" FROM ""Rifas_Ventas"" WHERE ""Id_Venta"" = @id AND ""Estado"" = 'Pendiente'", conexion))
                        {
                            cmdCheckRef.Parameters.AddWithValue("@id", idVenta);
                            var objRef = await cmdCheckRef.ExecuteScalarAsync();
                            if (objRef != null && objRef != DBNull.Value)
                            {
                                string refExistente = objRef.ToString();
                                if (refExistente.StartsWith("cs_"))
                                {
                                    try
                                    {
                                        var svcCheck = new SessionService();
                                        var sesionActiva = await svcCheck.GetAsync(refExistente);
                                        if (sesionActiva.Status == "open")
                                        {
                                            urlPago = sesionActiva.Url;
                                            sessionIdGenerado = sesionActiva.Id;
                                        }
                                    }
                                    catch { /* Si falla la consulta a Stripe, continuamos creando una nueva sesión */ }
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(urlPago))
                        {
                            // 3. GENERAMOS LA SESIÓN DE STRIPE SOLO SI NO HAY UNA ACTIVA
                            var options = new SessionCreateOptions
                            {
                                PaymentMethodTypes = new List<string> { "card" },
                                Mode = "payment",
                                CustomerEmail = emailContacto,
                                ClientReferenceId = refSegura,
                                LineItems = new List<SessionLineItemOptions>
                        {
                            new SessionLineItemOptions
                            {
                                PriceData = new SessionLineItemPriceDataOptions
                                {
                                    UnitAmountDecimal = total * 100,
                                    Currency = "mxn",
                                    ProductData = new SessionLineItemPriceDataProductDataOptions
                                    {
                                        Name = $"Rifa #{idRifa} - {cantidadBoletosEsperada} boletos",
                                        Description = $"Ref: {refSegura}"
                                    }
                                },
                                Quantity = 1
                            }
                        },
                                SuccessUrl = $"{urlRetorno}?session_id={{CHECKOUT_SESSION_ID}}",
                                CancelUrl = urlRetorno
                            };

                            var service = new SessionService();
                            var session = await service.CreateAsync(options);
                            urlPago = session.Url;
                            sessionIdGenerado = session.Id;

                            // 4. ACTUALIZAMOS LA REFERENCIA
                            var cmdUpd = new NpgsqlCommand(@"UPDATE ""Rifas_Ventas"" SET ""Ref_Pasarela_Id"" = @sid WHERE ""Id_Venta"" = @id", conexion);
                            cmdUpd.Parameters.AddWithValue("@sid", sessionIdGenerado);
                            cmdUpd.Parameters.AddWithValue("@id", idVenta);
                            await cmdUpd.ExecuteNonQueryAsync();
                        }
                    }
                }

                if (!string.IsNullOrEmpty(urlPago)) return Redirect(urlPago);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("TuPedido", new { token = token });
        }

        private async Task ConfirmarVentaPagada(int idVenta, string pasarelaId, decimal montoNeto, NpgsqlConnection conexion)
        {
            string emailContacto = "";
            string tokenAcceso = "";
            bool fueActualizado = false;

            using (var trans = await conexion.BeginTransactionAsync())
            {
                try
                {
                    // EL GUARDIÁN ATÓMICO: Solo actualiza si NO estaba pagado y nos devuelve los datos
                    string sqlV = @"UPDATE ""Rifas_Ventas"" 
                    SET ""Estado"" = 'Pagado', ""Fecha_Pago"" = NOW(), ""Ref_Pasarela_Id"" = @pid, ""Total_Neto"" = @neto 
                    WHERE ""Id_Venta"" = @id AND ""Estado"" != 'Pagado'
                    RETURNING ""Email_Contacto"", ""Token_Acceso""";

                    using (var cmd = new NpgsqlCommand(sqlV, conexion, trans))
                    {
                        cmd.Parameters.AddWithValue("@pid", pasarelaId);
                        cmd.Parameters.AddWithValue("@neto", montoNeto);
                        cmd.Parameters.AddWithValue("@id", idVenta);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                emailContacto = r["Email_Contacto"].ToString();
                                tokenAcceso = r["Token_Acceso"].ToString();
                                fueActualizado = true;
                            }
                        }
                    }

                    // Si fueActualizado es falso, Stripe envió el evento duplicado. Abortamos para no mandar doble correo.
                    if (!fueActualizado)
                    {
                        await trans.RollbackAsync();
                        return;
                    }

                    string sqlB = @"UPDATE ""Rifas_Boletos"" SET ""Estado"" = 'Pagado' WHERE ""Id_Venta"" = @id";
                    using (var cmd = new NpgsqlCommand(sqlB, conexion, trans))
                    {
                        cmd.Parameters.AddWithValue("@id", idVenta);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    await trans.CommitAsync();
                }
                catch { await trans.RollbackAsync(); throw; }
            }

            // ENVÍO DE CORREO ELECTRÓNICO
            if (!string.IsNullOrEmpty(emailContacto))
            {
                List<int> boletosComprados = new List<int>();
                string tituloRifa = "tu rifa";
                int idRifaDB = 0; // Variable para capturar el ID de la rifa

                string sqlInfo = @"
    SELECT b.""Numero"", r.""Titulo"", r.""Id_Rifa"" 
    FROM ""Rifas_Boletos"" b
    JOIN ""Rifas"" r ON b.""Id_Rifa"" = r.""Id_Rifa""
    WHERE b.""Id_Venta"" = @idVenta 
    ORDER BY b.""Numero"" ASC";

                using (var cmdInfo = new NpgsqlCommand(sqlInfo, conexion))
                {
                    cmdInfo.Parameters.AddWithValue("@idVenta", idVenta);
                    using (var r = await cmdInfo.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            boletosComprados.Add((int)r["Numero"]);
                            if (tituloRifa == "tu rifa") tituloRifa = r["Titulo"].ToString();
                            idRifaDB = (int)r["Id_Rifa"]; // Guardamos el ID real de la rifa
                        }
                    }
                }

                string htmlBoletos = "";
                foreach (var num in boletosComprados)
                {
                    htmlBoletos += $"<span style='display:inline-block; background-color:#f8f9fa; border:1px solid #ced4da; border-radius:4px; padding:8px 15px; margin:4px; font-weight:bold; font-size:16px; color:#333;'>#{num}</span>";
                }

                // 1. Enlace al recibo privado del usuario
                string enlaceRecibo = Url.Action("TuPedido", "Rifas", new { token = tokenAcceso }, Request.Scheme);

                // 2. NUEVO: Enlace a la página pública de la rifa (Para comprar más o ver al ganador)
                string idRifaEncriptado = Funciones.EncriptarId(idRifaDB);
                string enlacePublicoRifa = Url.Action("Seleccion", "Rifas", new { id = idRifaEncriptado }, Request.Scheme);

                string html = $@"
<div style='font-family: Arial, sans-serif; color: #333; max-width: 600px; margin: 0 auto; border: 1px solid #e0e0e0; border-radius: 8px; padding: 20px;'>
    <h2 style='color:#198754; text-align: center;'>¡Pago Confirmado!</h2>
    <p>Muchas gracias por tu compra. Tus boletos para <strong>{tituloRifa}</strong> están asegurados.</p>
    
    <div style='background-color: #f1f8f5; border-left: 4px solid #198754; padding: 15px; margin: 20px 0;'>
        <p style='margin-top: 0; font-weight: bold;'>Tus números asignados:</p>
        <div style='text-align: center;'>{htmlBoletos}</div>
    </div>
    
    <p align='center' style='margin-top: 30px;'>
        <a href='{enlaceRecibo}' style='background:#198754;color:white;padding:12px 25px;text-decoration:none;border-radius:5px;font-weight:bold; display:inline-block; margin-bottom: 10px;'>
            Ver mi Recibo y Boletos
        </a>
    </p>
    
    <p align='center' style='margin-top: 10px;'>
        <a href='{enlacePublicoRifa}' style='background:#0d6efd;color:white;padding:12px 25px;text-decoration:none;border-radius:5px;font-weight:bold; display:inline-block;'>
            Comprar Más o Ver Resultados
        </a>
    </p>
    
    <p style='font-size: 12px; color: #666; text-align: center; margin-top: 20px;'>
        Guarda este correo. Podrás usar los enlaces en cualquier momento para revisar el estado de la dinámica.
    </p>
</div>";

                await Funciones.EnviarCorreo(_configuration, emailContacto, $"¡Boletos Confirmados! - {tituloRifa}", html);
            }
        }

        [Authorize]
        public async Task<IActionResult> Asignaciones(string id)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de administrador.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idRifaReal = Funciones.DesencriptarId(id);
            if (idRifaReal <= 0) return RedirectToAction("Index");

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new GestionTableroViewModel();
            var usuariosPlataforma = new List<SelectListItem>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);
                    if (!acceso.Existe || !acceso.EsCreador) return RedirectToAction("Index");

                    modelo.InfoRifa = new RifaPublicaViewModel { Id_Rifa = idRifaReal, IdRifaEncriptado = id, Titulo = acceso.Titulo, Estado = acceso.Estado, CostoBoleto = acceso.CostoXBoleto };

                    string sqlUsr = @"SELECT ""Id_Usuario"", ""NombreCompleto"" FROM ""Sist_Usuarios"" WHERE ""Activo"" = TRUE AND ""Email_Verificado"" = TRUE ORDER BY ""NombreCompleto"" ASC";
                    using (var cmdUsr = new NpgsqlCommand(sqlUsr, conexion))
                    using (var r = await cmdUsr.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            usuariosPlataforma.Add(new SelectListItem { Value = r["Id_Usuario"].ToString(), Text = r["NombreCompleto"].ToString() });
                        }
                    }
                    ViewBag.Usuarios = usuariosPlataforma;

                    string sqlLibres = @"SELECT COUNT(*) FROM ""Rifas_Boletos"" WHERE ""Id_Rifa"" = @id AND ""Estado"" = 'Disponible'";
                    using (var cmd = new NpgsqlCommand(sqlLibres, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifaReal);
                        modelo.BoletosLibresParaAsignar = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                    }

                    string sqlPromotores = @"
                SELECT 
                    b.""Id_Usuario_Asignado"", 
                    b.""NombrePromotorExterno"",
                    u.""NombreCompleto"",
                    COUNT(b.""Id_Boleto"") as TotalAsignados,
                    SUM(CASE WHEN b.""Estado"" IN ('Pagado', 'Vendido_Sin_Pagar') THEN 1 ELSE 0 END) as Vendidos,
                    SUM(CASE WHEN b.""Estado"" = 'Fisico_En_Mano' THEN 1 ELSE 0 END) as Disponibles,
                    (SELECT COALESCE(SUM(p.""Monto""), 0) FROM ""Rifas_Pagos"" p 
                     WHERE p.""Id_Rifa"" = b.""Id_Rifa"" AND p.""Estado"" = 'Aprobado'
                     AND ((p.""Id_Usuario_Vendedor"" = b.""Id_Usuario_Asignado"" AND b.""Id_Usuario_Asignado"" IS NOT NULL) 
                       OR (p.""NombrePromotorExterno"" = b.""NombrePromotorExterno"" AND b.""NombrePromotorExterno"" IS NOT NULL))
                    ) as TotalPagado
                FROM ""Rifas_Boletos"" b
                LEFT JOIN ""Sist_Usuarios"" u ON b.""Id_Usuario_Asignado"" = u.""Id_Usuario""
                WHERE b.""Id_Rifa"" = @id AND (b.""Id_Usuario_Asignado"" IS NOT NULL OR b.""NombrePromotorExterno"" IS NOT NULL)
                GROUP BY b.""Id_Rifa"", b.""Id_Usuario_Asignado"", b.""NombrePromotorExterno"", u.""NombreCompleto""";

                    using (var cmd = new NpgsqlCommand(sqlPromotores, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifaReal);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int? idAsignado = r["Id_Usuario_Asignado"] != DBNull.Value ? (int)r["Id_Usuario_Asignado"] : null;
                                string nombreExterno = r["NombrePromotorExterno"]?.ToString();
                                string nombre = r["NombreCompleto"]?.ToString();

                                if (idAsignado == idUsuario) nombre = "Yo (Venta Directa)";
                                else if (idAsignado == null && !string.IsNullOrEmpty(nombreExterno)) nombre = nombreExterno + " (Externo)";

                                var p = new PromotorStatItem
                                {
                                    IdUsuario = idAsignado,
                                    NombrePromotorExterno = nombreExterno,
                                    Nombre = nombre,
                                    Asignados = Convert.ToInt32(r["TotalAsignados"]),
                                    VendidosSinPagar = Convert.ToInt32(r["Vendidos"]),
                                    Restantes = Convert.ToInt32(r["Disponibles"]),
                                    TotalPagado = Convert.ToDecimal(r["TotalPagado"]),
                                    CostoBoletoUnitario = acceso.CostoXBoleto
                                };
                                modelo.Promotores.Add(p);
                            }
                        }
                    }

                    string sqlPendientes = @"
                SELECT p.*, u.""NombreCompleto"" 
                FROM ""Rifas_Pagos"" p 
                LEFT JOIN ""Sist_Usuarios"" u ON p.""Id_Usuario_Vendedor"" = u.""Id_Usuario""
                WHERE p.""Id_Rifa"" = @id AND p.""Estado"" = 'Pendiente'";
                    using (var cmdP = new NpgsqlCommand(sqlPendientes, conexion))
                    {
                        cmdP.Parameters.AddWithValue("@id", idRifaReal);
                        using (var r = await cmdP.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.PagosPendientes.Add(new PagoPromotorRifaViewModel
                                {
                                    IdPago = (int)r["IdPago"],
                                    IdUsuarioVendedor = r["Id_Usuario_Vendedor"] as int?,
                                    NombrePromotorExterno = r["NombrePromotorExterno"]?.ToString() ?? r["NombreCompleto"]?.ToString(),
                                    Monto = (decimal)r["Monto"],
                                    ComprobanteUrl = r["ComprobanteUrl"]?.ToString(),
                                    FechaPago = (DateTime)r["FechaPago"]
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); return RedirectToAction("Index"); }

            return View(modelo);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AsignarBoletos(string idRifa, int? IdUsuarioDestino, string NombrePromotorExterno, int CantidadBoletos)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar)) return RedirectToAction("Index");
            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
            int idRifaReal = Funciones.DesencriptarId(idRifa);

            try
            {
                if (CantidadBoletos <= 0) throw new Exception("La cantidad de boletos a repartir debe ser mayor a cero.");

                if (IdUsuarioDestino.HasValue && IdUsuarioDestino.Value > 0) NombrePromotorExterno = null;
                else if (!string.IsNullOrWhiteSpace(NombrePromotorExterno))
                {
                    NombrePromotorExterno = NombrePromotorExterno.Trim();
                    IdUsuarioDestino = null;
                }
                else throw new Exception("Es obligatorio seleccionar un usuario registrado o tipificar el nombre del promotor externo.");

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuarioActual, conexion);
                    if (!acceso.EsCreador || acceso.Estado != "Activa") throw new Exception("Operación denegada. La rifa está cerrada o no posees el rol Admin.");

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        string sqlAsignar = @"
                            UPDATE ""Rifas_Boletos""
                            SET ""Id_Usuario_Asignado"" = @idDest, 
                                ""NombrePromotorExterno"" = @nomExt,
                                ""Estado"" = 'Fisico_En_Mano',
                                ""FechaAsignacion"" = CURRENT_TIMESTAMP
                            WHERE ""Id_Boleto"" IN (
                                SELECT ""Id_Boleto"" FROM ""Rifas_Boletos""
                                WHERE ""Id_Rifa"" = @idRifa AND ""Estado"" = 'Disponible'
                                ORDER BY ""Numero"" ASC
                                LIMIT @cantidad
                                FOR UPDATE SKIP LOCKED
                            )";

                        using (var cmd = new NpgsqlCommand(sqlAsignar, conexion, trans))
                        {
                            cmd.Parameters.AddWithValue("@idDest", (object)IdUsuarioDestino ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@nomExt", (object)NombrePromotorExterno ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@idRifa", idRifaReal);
                            cmd.Parameters.AddWithValue("@cantidad", CantidadBoletos);

                            int afectados = await cmd.ExecuteNonQueryAsync();
                            if (afectados == 0) throw new Exception("No hay suficientes boletos libres en la mesa general para cumplir la asignación.");

                            MostrarMensaje("Planilla Repartida", $"Se asignaron con éxito {afectados} números al promotor.", TipoMensaje.Exito);
                        }
                        await trans.CommitAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error de Reparto", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Asignaciones", new { id = idRifa });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VincularPromotorExterno(string idRifa, string NombrePromotorExterno, int IdUsuarioDestino)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar)) return RedirectToAction("Index");
            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
            int idRifaReal = Funciones.DesencriptarId(idRifa);

            try
            {
                NombrePromotorExterno = NombrePromotorExterno?.Trim();
                if (string.IsNullOrEmpty(NombrePromotorExterno) || IdUsuarioDestino <= 0) throw new Exception("Parámetros de vinculación corruptos.");

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuarioActual, conexion);
                    if (!acceso.EsCreador) throw new Exception("Privilegios insuficientes.");

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        string sqlBol = @"UPDATE ""Rifas_Boletos"" 
                                          SET ""Id_Usuario_Asignado"" = @idDest, ""NombrePromotorExterno"" = NULL 
                                          WHERE ""Id_Rifa"" = @idRifa AND ""NombrePromotorExterno"" = @nomExt";
                        int boletosVinculados;
                        using (var cmdBol = new NpgsqlCommand(sqlBol, conexion, trans))
                        {
                            cmdBol.Parameters.AddWithValue("@idDest", IdUsuarioDestino);
                            cmdBol.Parameters.AddWithValue("@idRifa", idRifaReal);
                            cmdBol.Parameters.AddWithValue("@nomExt", NombrePromotorExterno);
                            boletosVinculados = await cmdBol.ExecuteNonQueryAsync();
                        }

                        if (boletosVinculados == 0) throw new Exception("No se localizó ningún boleto bajo el nombre del promotor externo especificado.");

                        string sqlPagos = @"UPDATE ""Rifas_Pagos"" 
                                            SET ""Id_Usuario_Vendedor"" = @idDest, ""NombrePromotorExterno"" = NULL 
                                            WHERE ""Id_Rifa"" = @idRifa AND ""NombrePromotorExterno"" = @nomExt";
                        using (var cmdPagos = new NpgsqlCommand(sqlPagos, conexion, trans))
                        {
                            cmdPagos.Parameters.AddWithValue("@idDest", IdUsuarioDestino);
                            cmdPagos.Parameters.AddWithValue("@idRifa", idRifaReal);
                            cmdPagos.Parameters.AddWithValue("@nomExt", NombrePromotorExterno);
                            await cmdPagos.ExecuteNonQueryAsync();
                        }

                        await trans.CommitAsync();
                        MostrarMensaje("Estructura Vinculada", $"Se han unificado {boletosVinculados} números y sus flujos contables a la cuenta del usuario verificado.", TipoMensaje.Exito);
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error de Vinculación", ex.Message, TipoMensaje.Error);
            }
            return RedirectToAction("Asignaciones", new { id = idRifa });
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ImprimirPlanilla(string idRifa, int? idDestino, string externo, string nombreMostrar)
        {
            int idRifaReal = Funciones.DesencriptarId(idRifa);
            return await GenerarVistaImpresion(idRifaReal, idRifa, idDestino, externo, nombreMostrar, "ImprimirPlanilla");
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ImprimirPlanillaLista(string idRifa, int? idDestino, string externo, string nombreMostrar)
        {
            int idRifaReal = Funciones.DesencriptarId(idRifa);
            return await GenerarVistaImpresion(idRifaReal, idRifa, idDestino, externo, nombreMostrar, "ImprimirPlanillaLista");
        }

        private async Task<IActionResult> GenerarVistaImpresion(int idRifaReal, string idRifaEncriptado, int? idDestino, string externo, string nombreMostrar, string nombreVista)
        {
            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
            var boletos = new List<BoletoItem>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuarioActual, conexion);
                    if (!acceso.Existe) return NotFound("La rifa no existe.");

                    if (!acceso.EsCreador) { idDestino = null; externo = null; }

                    // Traemos también el QrEnlaceCompraUrl en lugar de solo la descripción
                    using (var cmdInfo = new NpgsqlCommand(@"SELECT ""Descripcion"", ""QrEnlaceCompraUrl"" FROM ""Rifas"" WHERE ""Id_Rifa"" = @id", conexion))
                    {
                        cmdInfo.Parameters.AddWithValue("@id", idRifaReal);
                        using (var reader = await cmdInfo.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                ViewBag.DescripcionRifa = reader["Descripcion"]?.ToString();
                                ViewBag.QrCompraUrl = reader["QrEnlaceCompraUrl"]?.ToString();
                            }
                        }
                    }

                    string sql = @"
                SELECT ""Id_Boleto"", ""Numero"", ""Estado"", ""NombrePromotorExterno"", 
                       ""Nombre_Cliente_Final"", ""Telefono_Cliente_Final"", ""Comentarios"", ""FechaAsignacion""
                FROM ""Rifas_Boletos""
                WHERE ""Id_Rifa"" = @idRifa ";

                    if (idDestino.HasValue) sql += @" AND ""Id_Usuario_Asignado"" = @idDestino";
                    else if (!string.IsNullOrEmpty(externo)) sql += @" AND ""NombrePromotorExterno"" = @externo AND ""Id_Usuario_Asignado"" IS NULL";
                    else sql += @" AND ""Id_Usuario_Asignado"" = @idUsuarioActual";

                    sql += @" ORDER BY ""Numero"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idRifa", idRifaReal);
                        if (idDestino.HasValue) cmd.Parameters.AddWithValue("@idDestino", idDestino.Value);
                        if (!string.IsNullOrEmpty(externo)) cmd.Parameters.AddWithValue("@externo", externo);
                        cmd.Parameters.AddWithValue("@idUsuarioActual", idUsuarioActual);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                boletos.Add(new BoletoItem
                                {
                                    Id_Boleto = (int)r["Id_Boleto"],
                                    Numero = (int)r["Numero"],
                                    Estado = r["Estado"].ToString() == "Fisico_En_Mano" ? "Asignado" : "Vendido",
                                    Comprador = r["Nombre_Cliente_Final"]?.ToString(),
                                    Telefono = r["Telefono_Cliente_Final"]?.ToString(),
                                    Comentarios = r["Comentarios"]?.ToString(),
                                    FechaAsignacion = r["FechaAsignacion"] != DBNull.Value ? (DateTime)r["FechaAsignacion"] : (DateTime?)null
                                });
                            }
                        }
                    }

                    ViewBag.TituloRifa = acceso.Titulo;
                    ViewBag.NombreMostrar = string.IsNullOrEmpty(nombreMostrar) ? "Vendedor" : nombreMostrar;
                    ViewBag.CostoBoleto = acceso.CostoXBoleto;
                    ViewBag.FechaSorteo = acceso.fechasorteo.ToString("dd/MMMM/yyyy", new System.Globalization.CultureInfo("es-ES"));
                    ViewBag.IdRifaActiva = idRifaEncriptado;

                    string tokenRifa = idRifaEncriptado;
                    ViewBag.UrlCompra = Url.Action("Seleccion", "Rifas", new { id = tokenRifa }, Request.Scheme);
                }
            }
            catch (Exception ex) { return Content("Error: " + ex.Message); }

            if (!boletos.Any()) return Content("No hay boletos asignados para generar la planilla.");

            var gruposContinuos = new List<List<BoletoItem>>();
            var grupoActual = new List<BoletoItem> { boletos[0] };
            gruposContinuos.Add(grupoActual);

            for (int i = 1; i < boletos.Count; i++)
            {
                var actual = boletos[i];
                var anterior = boletos[i - 1];

                bool mismaFecha = actual.FechaAsignacion?.Date == anterior.FechaAsignacion?.Date;
                bool esConsecutivo = actual.Numero == anterior.Numero + 1;

                if (mismaFecha && esConsecutivo) grupoActual.Add(actual);
                else { grupoActual = new List<BoletoItem> { actual }; gruposContinuos.Add(grupoActual); }
            }

            return View(nombreVista, gruposContinuos);
        }

        [Authorize]
        public async Task<IActionResult> Tablero(string id)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
            {
                MostrarMensaje("Error", "Necesitas permisos de administrador para realizar ésta acción.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idRifaReal = Funciones.DesencriptarId(id);
            if (idRifaReal <= 0) return RedirectToAction("Index");

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            var modelo = new GestionTableroViewModel
            {
                BoletosRelevantes = new List<BoletoItem>(),
                Promotores = new List<PromotorStatItem>(),
                Stats = new EstadisticasRifa(),
                PagosPendientes = new List<PagoPromotorRifaViewModel>()
            };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);
                    if (!acceso.Existe || !acceso.EsCreador)
                    {
                        MostrarMensaje("Acceso Denegado", "No tienes permisos de administración sobre esta dinámica.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    string sqlRifa = @"SELECT * FROM ""Rifas"" WHERE ""Id_Rifa"" = @id";
                    using (var cmd = new NpgsqlCommand(sqlRifa, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifaReal);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.InfoRifa = new RifaPublicaViewModel
                                {
                                    Id_Rifa = (int)r["Id_Rifa"],
                                    IdRifaEncriptado = id,
                                    Titulo = r["Titulo"].ToString(),
                                    MetaBoletos = (int)r["Meta_Boletos"],
                                    CostoBoleto = (decimal)r["Costo_Boleto"],
                                    ImagenUrl = r["ImagenUrl"]?.ToString(),
                                    FechaSorteo = r["Fecha_Sorteo"] != DBNull.Value ? Convert.ToDateTime(r["Fecha_Sorteo"]) : DateTime.MinValue,
                                    InversionPremio = r["InversionPremio"] != DBNull.Value ? (decimal)r["InversionPremio"] : 0m,
                                    Banco = r["Banco"]?.ToString(),
                                    CuentaClabe = r["CuentaClabe"]?.ToString(),
                                    TitularCuenta = r["TitularCuenta"]?.ToString(),
                                    Estado = r["Estado"]?.ToString()
                                };
                            }
                            else return RedirectToAction("Index");
                        }
                    }

                    string sqlGlobal = @"
                SELECT b.*, 
                       v.""Nombre_Contacto"" as CompradorWeb, 
                       u.""NombreCompleto"" as Promotor
                FROM ""Rifas_Boletos"" b
                LEFT JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
                LEFT JOIN ""Sist_Usuarios"" u ON b.""Id_Usuario_Asignado"" = u.""Id_Usuario""
                WHERE b.""Id_Rifa"" = @id
                ORDER BY b.""Numero"" ASC";

                    var todosLosBoletos = new List<dynamic>();

                    using (var cmd = new NpgsqlCommand(sqlGlobal, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifaReal);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                var est = r["Estado"].ToString();
                                modelo.Stats.TotalBoletos++;
                                if (est == "Pagado") modelo.Stats.Pagados++;
                                else if (est == "Vendido_Sin_Pagar") modelo.Stats.PendientesPago++;
                                else if (est == "Fisico_En_Mano") modelo.Stats.EnMano++;

                                todosLosBoletos.Add(new
                                {
                                    Id = (int)r["Id_Boleto"],
                                    Num = (int)r["Numero"],
                                    Estado = est,
                                    IdPromotor = r["Id_Usuario_Asignado"] != DBNull.Value ? (int)r["Id_Usuario_Asignado"] : 0,
                                    NomPromotor = r["Promotor"]?.ToString(),
                                    Cliente = r["Nombre_Cliente_Final"]?.ToString() ?? r["CompradorWeb"]?.ToString()
                                });

                                if (est == "Pagado" || est == "Vendido_Sin_Pagar")
                                {
                                    string nombreMostrar = est == "Vendido_Sin_Pagar"
                                        ? $"{r["Promotor"]} (Debe)"
                                        : (r["Nombre_Cliente_Final"]?.ToString() ?? r["CompradorWeb"]?.ToString() ?? "Anónimo");

                                    modelo.BoletosRelevantes.Add(new BoletoItem
                                    {
                                        Id_Boleto = (int)r["Id_Boleto"],
                                        Numero = (int)r["Numero"],
                                        Estado = est,
                                        Comprador = nombreMostrar
                                    });
                                }
                            }
                        }
                    }

                    modelo.Stats.DineroRecaudado = modelo.Stats.Pagados * modelo.InfoRifa.CostoBoleto;
                    modelo.Stats.DineroPorCobrar = modelo.Stats.PendientesPago * modelo.InfoRifa.CostoBoleto;

                    var gruposPromotores = todosLosBoletos.Where(x => x.IdPromotor > 0).GroupBy(x => x.IdPromotor);
                    foreach (var g in gruposPromotores)
                    {
                        var primer = g.First();
                        var p = new PromotorStatItem
                        {
                            IdUsuario = g.Key,
                            Nombre = primer.NomPromotor,
                            Asignados = g.Count(),
                            VendidosPagados = g.Count(x => x.Estado == "Pagado"),
                            VendidosSinPagar = g.Count(x => x.Estado == "Vendido_Sin_Pagar"),
                            Restantes = g.Count(x => x.Estado == "Fisico_En_Mano")
                        };
                        p.DeudaActual = p.VendidosSinPagar * modelo.InfoRifa.CostoBoleto;
                        modelo.Promotores.Add(p);
                    }
                    modelo.Promotores = modelo.Promotores.OrderByDescending(x => x.DeudaActual).ThenByDescending(x => x.VendidosPagados).ToList();

                    string sqlPendientes = @"
                SELECT p.*, u.""NombreCompleto"" 
                FROM ""Rifas_Pagos"" p 
                LEFT JOIN ""Sist_Usuarios"" u ON p.""Id_Usuario_Vendedor"" = u.""Id_Usuario""
                WHERE p.""Id_Rifa"" = @id AND p.""Estado"" = 'Pendiente'";
                    using (var cmdP = new NpgsqlCommand(sqlPendientes, conexion))
                    {
                        cmdP.Parameters.AddWithValue("@id", idRifaReal);
                        using (var r = await cmdP.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string promotorExterno = r["NombrePromotorExterno"]?.ToString();
                                string nombreUsuario = r["NombreCompleto"]?.ToString();
                                string nombreFinal = !string.IsNullOrWhiteSpace(promotorExterno)
                                    ? promotorExterno
                                    : (!string.IsNullOrWhiteSpace(nombreUsuario) ? nombreUsuario : "Usuario Interno");

                                modelo.PagosPendientes.Add(new PagoPromotorRifaViewModel
                                {
                                    IdPago = (int)r["IdPago"],
                                    IdRifaEncriptado = id,
                                    IdUsuarioVendedor = r["Id_Usuario_Vendedor"] as int?,
                                    NombrePromotorExterno = nombreFinal,
                                    Monto = (decimal)r["Monto"],
                                    ComprobanteUrl = r["ComprobanteUrl"]?.ToString(),
                                    FechaPago = (DateTime)r["FechaPago"],
                                    BoletosPagados = r["BoletosPagados"]?.ToString()
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", "Ocurrió un error al procesar el tablero analítico: " + ex.Message, TipoMensaje.Error);
            }

            return View(modelo);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegistrarVentaMasiva(string idRifaOrigen, List<int> IdsBoletos, List<string> NombresCompradores, List<string> TelefonosCompradores, List<string> Comentarios, List<bool> MarcadoresPersonales)
        {
            if (IdsBoletos == null || !IdsBoletos.Any())
            {
                MostrarMensaje("Error", "No se ha recibido ningún identificador de boletos para procesar.", TipoMensaje.Error);
                return RedirectToAction("MisBoletos", new { id = idRifaOrigen });
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            int ventasExitosas = 0;
            int idRifaReal = Funciones.DesencriptarId(idRifaOrigen);

            try
            {
                // 1. PREVENCIÓN DE DEADLOCKS: Combinamos las listas en objetos temporales y ordenamos por IdBoleto
                var transaccionesOrdenadas = new List<dynamic>();
                for (int i = 0; i < IdsBoletos.Count; i++)
                {
                    transaccionesOrdenadas.Add(new
                    {
                        IdBoleto = IdsBoletos[i],
                        Nombre = NombresCompradores != null && NombresCompradores.Count > i ? NombresCompradores[i] : "Anónimo",
                        Telefono = TelefonosCompradores != null && TelefonosCompradores.Count > i ? TelefonosCompradores[i] : "",
                        Comentario = Comentarios != null && Comentarios.Count > i ? Comentarios[i] : "",
                        Marcador = MarcadoresPersonales != null && MarcadoresPersonales.Count > i ? MarcadoresPersonales[i] : false
                    });
                }

                // ORDENAMIENTO GARANTIZADO para evitar abrazos mortales (deadlocks) en PostgreSQL
                transaccionesOrdenadas = transaccionesOrdenadas.OrderBy(x => x.IdBoleto).ToList();

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);
                    if (acceso.Estado != "Activa")
                    {
                        throw new Exception("La dinámica oficial ya no se encuentra activa. No es posible registrar nuevas ventas.");
                    }

                    string condicionFiltro = acceso.EsCreador
                        ? @"(""Id_Usuario_Asignado"" = @uid OR (""Id_Usuario_Asignado"" IS NULL AND ""NombrePromotorExterno"" IS NOT NULL))"
                        : @"(""Id_Usuario_Asignado"" = @uid)";

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        foreach (var item in transaccionesOrdenadas)
                        {
                            string sqlUpd = $@"UPDATE ""Rifas_Boletos"" 
                              SET ""Estado"" = 'Vendido_Sin_Pagar', 
                                  ""Nombre_Cliente_Final"" = @nom, 
                                  ""Telefono_Cliente_Final"" = @tel,
                                  ""Comentarios"" = @com,
                                  ""MarcadorPersonal"" = @marc,
                                  ""FechaVenta"" = CURRENT_TIMESTAMP
                              WHERE ""Id_Boleto"" = @idB 
                                AND ""Estado"" IN ('Fisico_En_Mano', 'Vendido_Sin_Pagar')
                                AND {condicionFiltro}";

                            using (var cmd = new NpgsqlCommand(sqlUpd, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@nom", string.IsNullOrWhiteSpace(item.Nombre) ? "Anónimo" : item.Nombre.Trim());
                                cmd.Parameters.AddWithValue("@tel", string.IsNullOrWhiteSpace(item.Telefono) ? "" : item.Telefono.Trim());
                                cmd.Parameters.AddWithValue("@com", string.IsNullOrWhiteSpace(item.Comentario) ? "" : item.Comentario.Trim());
                                cmd.Parameters.AddWithValue("@marc", item.Marcador);
                                cmd.Parameters.AddWithValue("@idB", item.IdBoleto);
                                cmd.Parameters.AddWithValue("@uid", idUsuario);

                                int afectadas = await cmd.ExecuteNonQueryAsync();
                                if (afectadas > 0) ventasExitosas++;
                            }
                        }

                        if (ventasExitosas != IdsBoletos.Count)
                        {
                            await trans.RollbackAsync();
                            MostrarMensaje("Error", "Inconsistencia detectada: Uno o más boletos ya no te pertenecen o cambiaron de estado mientras operabas. Por favor, refresca la página e inténtalo de nuevo.", TipoMensaje.Error);
                            return RedirectToAction("MisBoletos", new { id = idRifaOrigen });
                        }

                        string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                        await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Editar, $"Rifas Oficiales: Venta masiva individualizada de {ventasExitosas} boletos en Rifa ID {idRifaReal}.", ip, trans);

                        await trans.CommitAsync();
                        TempData["VentaExitosa"] = true;
                        MostrarMensaje("Éxito", $"Se reportaron {ventasExitosas} boletos correctamente.", TipoMensaje.Exito);
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error al Procesar", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("MisBoletos", new { id = idRifaOrigen });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LiberarBoleto(int idBoletoLiberar, string idRifaOrigen)
        {
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            int idRifaReal = Funciones.DesencriptarId(idRifaOrigen);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sqlCheckPago = @"SELECT ""BoletosPagados"" FROM ""Rifas_Pagos"" 
                                            WHERE ""Id_Rifa"" = @idr 
                                            AND ""Estado"" IN ('Pendiente', 'Rechazado')
                                            AND ""BoletosPagados"" IS NOT NULL";

                    bool estaBloqueado = false;
                    using (var cmdCheck = new NpgsqlCommand(sqlCheckPago, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@idr", idRifaReal);
                        using (var rCheck = await cmdCheck.ExecuteReaderAsync())
                        {
                            while (await rCheck.ReadAsync())
                            {
                                string bp = rCheck["BoletosPagados"].ToString();
                                if (!string.IsNullOrWhiteSpace(bp))
                                {
                                    var ids = bp.Split(',').Select(s => int.TryParse(s.Trim(), out int val) ? val : 0).ToList();
                                    if (ids.Contains(idBoletoLiberar))
                                    {
                                        estaBloqueado = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (estaBloqueado)
                    {
                        throw new Exception("No puedes liberar este boleto porque está asociado a un comprobante de pago en revisión o rechazado. Elimina el reporte primero para poder liberarlo.");
                    }

                    string sqlUpd = @"UPDATE ""Rifas_Boletos"" 
                                      SET ""Estado"" = 'Fisico_En_Mano', 
                                          ""Nombre_Cliente_Final"" = NULL, 
                                          ""Telefono_Cliente_Final"" = NULL,
                                          ""Comentarios"" = NULL,
                                          ""MarcadorPersonal"" = FALSE,
                                          ""FechaVenta"" = NULL
                                      WHERE ""Id_Boleto"" = @id 
                                      AND ""Id_Usuario_Asignado"" = @uid
                                      AND ""Estado"" = 'Vendido_Sin_Pagar'";

                    using (var cmd = new NpgsqlCommand(sqlUpd, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idBoletoLiberar);
                        cmd.Parameters.AddWithValue("@uid", idUsuario);

                        int afectados = await cmd.ExecuteNonQueryAsync();
                        if (afectados > 0)
                        {
                            MostrarMensaje("Boleto Liberado", "La asignación fue cancelada y el número está disponible nuevamente en tu inventario físico.", TipoMensaje.Exito);
                        }
                        else
                        {
                            throw new Exception("Acción denegada. El boleto ya fue liquidado en pasarela o no cuentas con los permisos de asignación.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("MisBoletos", new { id = idRifaOrigen });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarPagoRechazado(string idRifa, int IdPago)
        {
            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
            int idRifaReal = Funciones.DesencriptarId(idRifa);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuarioActual, conexion);
                    if (!acceso.Existe) throw new Exception("Rifa no encontrada.");

                    string comprobanteUrl = null;
                    string conVendedor = acceso.EsCreador ? "" : @" AND ""Id_Usuario_Vendedor"" = @uid";

                    string sqlSel = $@"SELECT ""ComprobanteUrl"" FROM ""Rifas_Pagos"" 
                                       WHERE ""IdPago"" = @idp AND ""Id_Rifa"" = @idr AND ""Estado"" = 'Rechazado'{conVendedor}";

                    using (var cmdSel = new NpgsqlCommand(sqlSel, conexion))
                    {
                        cmdSel.Parameters.AddWithValue("@idp", IdPago);
                        cmdSel.Parameters.AddWithValue("@idr", idRifaReal);
                        if (!acceso.EsCreador) cmdSel.Parameters.AddWithValue("@uid", idUsuarioActual);

                        var objUrl = await cmdSel.ExecuteScalarAsync();
                        if (objUrl != null && objUrl != DBNull.Value) comprobanteUrl = objUrl.ToString();
                        else throw new Exception("El comprobante no existe o ya fue removido.");
                    }

                    string sqlDel = $@"DELETE FROM ""Rifas_Pagos"" 
                                       WHERE ""IdPago"" = @idp AND ""Id_Rifa"" = @idr AND ""Estado"" = 'Rechazado'{conVendedor}";
                    using (var cmd = new NpgsqlCommand(sqlDel, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idp", IdPago);
                        cmd.Parameters.AddWithValue("@idr", idRifaReal);
                        if (!acceso.EsCreador) cmd.Parameters.AddWithValue("@uid", idUsuarioActual);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    if (!string.IsNullOrEmpty(comprobanteUrl))
                    {
                        await DestruirImagenCloudinary(comprobanteUrl);
                    }

                    MostrarMensaje("Historial Limpio", "El registro rechazado y su archivo adjunto han sido eliminados de tu interfaz.", TipoMensaje.Info);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("MisBoletos", new { id = idRifa });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegistrarAbonoManual(string idRifa, int? IdUsuarioVendedor, string NombrePromotorExterno, decimal MontoAbono, string NotaAbono)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar))
            {
                MostrarMensaje("Acceso Denegado", "No cuentas con privilegios administrativos para realizar cobros manuales.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
            int idRifaReal = Funciones.DesencriptarId(idRifa);
            if (string.IsNullOrWhiteSpace(NombrePromotorExterno)) NombrePromotorExterno = null;

            try
            {
                if (MontoAbono <= 0) throw new Exception("El monto ingresado para el abono directo debe ser mayor a cero.");
                if (IdUsuarioVendedor == null && string.IsNullOrEmpty(NombrePromotorExterno)) throw new Exception("Vendedor o promotor externo no identificado.");

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuarioActual, conexion);
                    if (!acceso.EsCreador) throw new Exception("Operación restringida. Solo el administrador principal puede auditar la caja manual.");

                    // Iniciamos la transacción con nivel de aislamiento RepeatableRead para garantizar integridad financiera
                    using (var trans = await conexion.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead))
                    {
                        try
                        {
                            string sqlValidacionDeuda = @"
                        SELECT 
                            ((SELECT COUNT(*) FROM ""Rifas_Boletos"" WHERE ""Id_Rifa"" = @idr AND ""Estado"" = 'Vendido_Sin_Pagar' AND ((""Id_Usuario_Asignado"" = @idu AND @idu IS NOT NULL) OR (""Id_Usuario_Asignado"" IS NULL AND ""NombrePromotorExterno"" = @nomExt))) 
                            * (SELECT ""Costo_Boleto"" FROM ""Rifas"" WHERE ""Id_Rifa"" = @idr))
                            - 
                            COALESCE((SELECT SUM(""Monto"") FROM ""Rifas_Pagos"" WHERE ""Id_Rifa"" = @idr AND ""Estado"" IN ('Aprobado', 'Pendiente') AND (""Id_Usuario_Vendedor"" = @idu AND @idu IS NOT NULL) OR (""Id_Usuario_Vendedor"" IS NULL AND ""NombrePromotorExterno"" = @nomExt))), 0) 
                        AS DeudaRestante";

                            decimal deudareal = 0;
                            using (var cmdVal = new NpgsqlCommand(sqlValidacionDeuda, conexion, trans))
                            {
                                cmdVal.Parameters.AddWithValue("@idr", idRifaReal);
                                cmdVal.Parameters.AddWithValue("@idu", (object)IdUsuarioVendedor ?? DBNull.Value);
                                cmdVal.Parameters.AddWithValue("@nomExt", (object)NombrePromotorExterno ?? DBNull.Value);

                                deudareal = Convert.ToDecimal(await cmdVal.ExecuteScalarAsync());
                            }

                            if (MontoAbono > (deudareal + 0.01m))
                            {
                                throw new Exception($"El cobro solicitado (${MontoAbono:N2}) supera la deuda real pendiente (${deudareal:N2}) de este promotor.");
                            }

                            string sqlInsert = @"INSERT INTO ""Rifas_Pagos"" 
                                       (""Id_Rifa"", ""Id_Usuario_Vendedor"", ""NombrePromotorExterno"", ""Monto"", ""MetodoPago"", ""Estado"", ""Origen"", ""Nota"", ""FechaPago"") 
                                       VALUES (@idr, @idu, @nomExt, @monto, 'Efectivo', 'Aprobado', 'Administrador', @nota, CURRENT_TIMESTAMP)";

                            using (var cmd = new NpgsqlCommand(sqlInsert, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@idr", idRifaReal);
                                cmd.Parameters.AddWithValue("@idu", (object)IdUsuarioVendedor ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@nomExt", (object)NombrePromotorExterno ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@monto", MontoAbono);
                                cmd.Parameters.AddWithValue("@nota", (object)NotaAbono ?? DBNull.Value);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            // Solo marcar boletos como Pagados si el abono cubre la deuda total pendiente.
                            // Un pago parcial se registra en Rifas_Pagos pero NO cambia el estado de los boletos,
                            // evitando marcar como cobrados boletos que aún están pendientes de pago.
                            if (Math.Abs(MontoAbono - deudareal) <= 0.01m)
                            {
                                string condVendedor = IdUsuarioVendedor.HasValue
                                    ? "AND \"Id_Usuario_Asignado\" = @idu"
                                    : "AND \"Id_Usuario_Asignado\" IS NULL AND \"NombrePromotorExterno\" = @nomExt";

                                string sqlUpdBol = $@"UPDATE ""Rifas_Boletos"" SET ""Estado"" = 'Pagado' 
                                             WHERE ""Id_Rifa"" = @idr AND ""Estado"" = 'Vendido_Sin_Pagar' {condVendedor}";

                                using (var cmdBol = new NpgsqlCommand(sqlUpdBol, conexion, trans))
                                {
                                    cmdBol.Parameters.AddWithValue("@idr", idRifaReal);
                                    if (IdUsuarioVendedor.HasValue) cmdBol.Parameters.AddWithValue("@idu", IdUsuarioVendedor.Value);
                                    else cmdBol.Parameters.AddWithValue("@nomExt", NombrePromotorExterno);
                                    await cmdBol.ExecuteNonQueryAsync();
                                }
                            }

                            await trans.CommitAsync();
                        }
                        catch
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }

                    MostrarMensaje("Abono Aplicado", $"Se registraron ${MontoAbono:N2} en efectivo directamente a la caja de la dinámica.", TipoMensaje.Exito);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error Financiero", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Asignaciones", new { id = idRifa });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LiquidarVentas(List<int> idsBoletosALiquidar)
        {
            if (idsBoletosALiquidar == null || !idsBoletosALiquidar.Any())
            {
                MostrarMensaje("Error", "No se ha recibido ningún identificador de boletos.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            string emailPromotor = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
            string tokenAcceso = Guid.NewGuid().ToString("N");

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    if (string.IsNullOrEmpty(emailPromotor))
                    {
                        emailPromotor = (string)await new NpgsqlCommand($"SELECT \"Email\" FROM \"Sist_Usuarios\" WHERE \"Id_Usuario\"={idUsuario}", conexion).ExecuteScalarAsync();
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sqlCalc = @"SELECT SUM(r.""Costo_Boleto""), MAX(r.""Id_Rifa"")
                                       FROM ""Rifas_Boletos"" b
                                       JOIN ""Rifas"" r ON b.""Id_Rifa"" = r.""Id_Rifa""
                                       WHERE b.""Id_Boleto"" = ANY(@ids) 
                                       AND b.""Id_Usuario_Asignado"" = @uid
                                       AND b.""Estado"" = 'Vendido_Sin_Pagar'";

                            decimal total = 0;
                            int idRifaReal = 0;

                            using (var cmd = new NpgsqlCommand(sqlCalc, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@ids", idsBoletosALiquidar);
                                cmd.Parameters.AddWithValue("@uid", idUsuario);
                                using (var r = await cmd.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync() && r[0] != DBNull.Value)
                                    {
                                        total = (decimal)r[0];
                                        idRifaReal = (int)r[1];
                                    }
                                }
                            }

                            if (total == 0) throw new Exception("No hay boletos válidos para liquidar.");

                            string sqlVenta = @"
                        INSERT INTO ""Rifas_Ventas"" 
                        (""Id_Rifa"", ""Id_Usuario_Vendedor"", ""Total"", ""Cantidad_Boletos"", ""Estado"", ""Metodo_Pago"", ""Token_Acceso"", ""Email_Contacto"", ""Nombre_Contacto"")
                        VALUES 
                        (@idR, @uid, @tot, @cant, 'Pendiente', 'Planilla', @tok, @mail, 'Liquidación Promotor')
                        RETURNING ""Id_Venta""";

                            int idVenta;
                            using (var cmd = new NpgsqlCommand(sqlVenta, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@idR", idRifaReal);
                                cmd.Parameters.AddWithValue("@uid", idUsuario);
                                cmd.Parameters.AddWithValue("@tot", total);
                                cmd.Parameters.AddWithValue("@cant", idsBoletosALiquidar.Count);
                                cmd.Parameters.AddWithValue("@tok", tokenAcceso);
                                cmd.Parameters.AddWithValue("@mail", emailPromotor ?? "sin_email@sistema.com");
                                idVenta = (int)await cmd.ExecuteScalarAsync();
                            }

                            string refStripe = $"SOR_{idVenta}_LIQ_{Guid.NewGuid().ToString("N").Substring(0, 4).ToUpper()}";
                            await new NpgsqlCommand($"UPDATE \"Rifas_Ventas\" SET \"Ref_Stripe\"='{refStripe}' WHERE \"Id_Venta\"={idVenta}", conexion, trans).ExecuteNonQueryAsync();

                            string sqlUpd = @"UPDATE ""Rifas_Boletos"" SET ""Id_Venta"" = @idV WHERE ""Id_Boleto"" = ANY(@ids)";
                            using (var cmd = new NpgsqlCommand(sqlUpd, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@idV", idVenta);
                                cmd.Parameters.AddWithValue("@ids", idsBoletosALiquidar);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            await trans.CommitAsync();
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return RedirectToAction("TuPedido", new { token = tokenAcceso });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reservar(string idRifa, List<int> numerosSeleccionados, string email, string nombre, string telefono, string comentarios)
        {
            int idRifaReal = Funciones.DesencriptarId(idRifa);

            if (numerosSeleccionados == null || !numerosSeleccionados.Any())
            {
                MostrarMensaje("Error", "Selecciona al menos un boleto.", TipoMensaje.Alerta);
                return RedirectToAction("Seleccion", new { id = idRifa });
            }

            if (string.IsNullOrWhiteSpace(nombre) || string.IsNullOrWhiteSpace(email))
            {
                MostrarMensaje("Faltan Datos", "Tu nombre completo y correo electrónico son obligatorios.", TipoMensaje.Alerta);
                return RedirectToAction("Seleccion", new { id = idRifa });
            }

            string tokenAcceso = Guid.NewGuid().ToString("N");
            string tituloRifa = "";
            bool esAnonimo = !User.Identity.IsAuthenticated;

            int? idUsuarioComprador = null;
            if (!esAnonimo)
            {
                var idClaim = User.FindFirst("IdUsuario");
                if (idClaim != null && int.TryParse(idClaim.Value, out int id)) idUsuarioComprador = id;
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
                            // VALIDACIÓN COMPARTIDA (Consulta Async con Transacción)
                            if (!await PermitirAccesoPorVisibilidadAsync(idRifaReal, conexion, trans))
                            {
                                await trans.RollbackAsync();
                                MostrarMensaje("Acceso Restringido", "La dinámica es privada actualmente, no se pueden apartar nuevos boletos.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }

                            tituloRifa = (string)await new NpgsqlCommand($"SELECT \"Titulo\" FROM \"Rifas\" WHERE \"Id_Rifa\"={idRifaReal}", conexion, trans).ExecuteScalarAsync();
                            decimal costoUnitario = (decimal)await new NpgsqlCommand($"SELECT \"Costo_Boleto\" FROM \"Rifas\" WHERE \"Id_Rifa\"={idRifaReal}", conexion, trans).ExecuteScalarAsync();

                            string sqlVenta = @"
                INSERT INTO ""Rifas_Ventas"" 
                (""Id_Rifa"", ""Total"", ""Cantidad_Boletos"", ""Estado"", ""Metodo_Pago"", ""Token_Acceso"", ""Email_Contacto"", ""Nombre_Contacto"", ""Telefono_Contacto"", ""Id_Usuario_Comprador"")
                VALUES 
                (@idR, @tot, @cant, 'Pendiente', 'Stripe', @tok, @mail, @nom, @tel, @uid)
                RETURNING ""Id_Venta""";

                            int idVenta;
                            using (var cmd = new NpgsqlCommand(sqlVenta, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@idR", idRifaReal);
                                cmd.Parameters.AddWithValue("@tot", costoUnitario * numerosSeleccionados.Count);
                                cmd.Parameters.AddWithValue("@cant", numerosSeleccionados.Count);
                                cmd.Parameters.AddWithValue("@tok", tokenAcceso);
                                cmd.Parameters.AddWithValue("@mail", email.Trim());
                                cmd.Parameters.AddWithValue("@nom", nombre.Trim());
                                cmd.Parameters.AddWithValue("@tel", telefono ?? "");
                                cmd.Parameters.AddWithValue("@uid", (object)idUsuarioComprador ?? DBNull.Value);
                                idVenta = (int)await cmd.ExecuteScalarAsync();
                            }

                            string refStripe = $"SOR_{idVenta}_{Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper()}";
                            await new NpgsqlCommand($"UPDATE \"Rifas_Ventas\" SET \"Ref_Stripe\"='{refStripe}' WHERE \"Id_Venta\"={idVenta}", conexion, trans).ExecuteNonQueryAsync();

                            string sqlUpdBol = @"
                UPDATE ""Rifas_Boletos"" 
                SET ""Estado"" = 'Reservado', 
                    ""Id_Venta"" = @idV,
                    ""Nombre_Cliente_Final"" = @nom,    
                    ""Telefono_Cliente_Final"" = @tel,
                    ""Comentarios"" = @com
                WHERE ""Id_Rifa"" = @idR AND ""Numero"" = ANY(@nums) AND ""Estado"" = 'Disponible'
                RETURNING ""Id_Boleto""";

                            int afectados = 0;
                            using (var cmd = new NpgsqlCommand(sqlUpdBol, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@idV", idVenta);
                                cmd.Parameters.AddWithValue("@nom", nombre.Trim());
                                cmd.Parameters.AddWithValue("@tel", telefono ?? "");
                                cmd.Parameters.AddWithValue("@com", comentarios ?? "");
                                cmd.Parameters.AddWithValue("@idR", idRifaReal);
                                cmd.Parameters.AddWithValue("@nums", numerosSeleccionados);

                                using (var reader = await cmd.ExecuteReaderAsync())
                                {
                                    while (await reader.ReadAsync()) afectados++;
                                }
                            }

                            if (afectados != numerosSeleccionados.Count)
                            {
                                await trans.RollbackAsync();
                                MostrarMensaje("Lo sentimos", "Uno o más de los boletos seleccionados ya fueron apartados por otra persona. Por favor elige otros.", TipoMensaje.Alerta);
                                return RedirectToAction("Seleccion", new { id = idRifa });
                            }

                            int idUserBitacora = idUsuarioComprador ?? 0;
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            await Funciones.RegistrarBitacora(conexion, idUserBitacora, Modulo, Parametros.AccionesBitacora.Crear, $"Reserva Rifa #{idRifaReal}: {numerosSeleccionados.Count} boletos. Ref Venta: {idVenta}", ip, trans);

                            await trans.CommitAsync();
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }

                    if (!string.IsNullOrEmpty(email))
                    {
                        string enlace = Url.Action("TuPedido", "Rifas", new { token = tokenAcceso }, Request.Scheme);
                        string html = $@"
            <h2>Enlace de Compra</h2>
            <p>Hola <strong>{nombre}</strong>,</p>
            <p>Has iniciado la compra de {numerosSeleccionados.Count} boletos para <strong>{tituloRifa}</strong>.</p>
            <p>Si cierras la ventana, puedes retomar tu pago aquí:</p>
            <p align='center'>
                <a href='{enlace}' style='background:#0d6efd;color:white;padding:12px 24px;border-radius:4px;text-decoration:none;font-weight:bold;'>
                    Pagar Ahora
                </a>
            </p>
            <p><small>El enlace expira en <strong>30 minutos</strong> a partir de la selección de los boletos.</small></p>";
                        await Funciones.EnviarCorreo(_configuration, email, "Completa tu compra - " + tituloRifa, html);
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Seleccion", new { id = idRifa });
            }

            if (esAnonimo) return RedirectToAction("ConfirmacionEnvio", new { token = tokenAcceso, emailDestino = email });
            else return RedirectToAction("TuPedido", new { token = tokenAcceso });
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ConfirmacionEnvio(string token, string emailDestino)
        {
            ViewBag.Token = token;
            ViewBag.Email = emailDestino;
            return View();
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelarOrdenManual(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                MostrarMensaje("Error", "No se ha recibido el token para cancelar.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sqlGet = @"SELECT v.""Id_Venta"", v.""Metodo_Pago"", v.""Estado"" as EstadoVenta, r.""Estado"" as EstadoRifa
                                      FROM ""Rifas_Ventas"" v
                                      JOIN ""Rifas"" r ON v.""Id_Rifa"" = r.""Id_Rifa""
                                      WHERE v.""Token_Acceso"" = @tok";

                    int idVenta = 0;
                    bool esPlanilla = false;
                    string estadoVenta = "";
                    string estadoRifa = "";

                    using (var cmd = new NpgsqlCommand(sqlGet, conexion))
                    {
                        cmd.Parameters.AddWithValue("@tok", token);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                idVenta = (int)r["Id_Venta"];
                                esPlanilla = r["Metodo_Pago"].ToString() == "Planilla";
                                estadoVenta = r["EstadoVenta"].ToString();
                                estadoRifa = r["EstadoRifa"].ToString();
                            }
                        }
                    }

                    if (idVenta == 0)
                    {
                        MostrarMensaje("Error", "La orden no existe o el enlace es inválido.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    if (estadoVenta == "Pagado")
                    {
                        MostrarMensaje("Acción Denegada", "Esta orden ya fue pagada exitosamente. No se puede cancelar.", TipoMensaje.Alerta);
                        if (User.Identity.IsAuthenticated) return RedirectToAction("MisBoletos");
                        return RedirectToAction("TuPedido", new { token = token });
                    }

                    if (estadoVenta == "Cancelado")
                    {
                        MostrarMensaje("Info", "Esta orden ya había sido cancelada previamente.", TipoMensaje.Info);
                        return RedirectToAction("Index");
                    }

                    if (estadoRifa != "Activa")
                    {
                        MostrarMensaje("Rifa Cerrada", "La rifa ya ha finalizado. No se pueden realizar cambios en las órdenes.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // Guardia de estado — evita cancelar una orden que fue pagada concurrentemente
                            int filasCanceladas = await new NpgsqlCommand($"UPDATE \"Rifas_Ventas\" SET \"Estado\"='Cancelado' WHERE \"Id_Venta\"={idVenta} AND \"Estado\" = 'Pendiente'", conexion, trans).ExecuteNonQueryAsync();
                            if (filasCanceladas == 0) throw new Exception("La orden no pudo cancelarse: ya fue procesada (pagada u operada) por otra acción concurrente.");

                            string sqlLib = "";

                            if (esPlanilla)
                            {
                                sqlLib = @"UPDATE ""Rifas_Boletos"" SET ""Id_Venta"" = NULL WHERE ""Id_Venta"" = @id";
                            }
                            else
                            {
                                sqlLib = @"UPDATE ""Rifas_Boletos"" SET ""Id_Venta"" = NULL, ""Estado"" = 'Disponible' WHERE ""Id_Venta"" = @id";
                            }

                            using (var cmd = new NpgsqlCommand(sqlLib, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@id", idVenta);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            int idUserBit = User.Identity.IsAuthenticated ? int.Parse(User.FindFirst("IdUsuario").Value) : 0;
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            string detalle = $"Orden de boletos Cancelada (Venta #{idVenta}). Estado previo: {estadoVenta}";

                            await Funciones.RegistrarBitacora(conexion, idUserBit, Modulo, Parametros.AccionesBitacora.Borrar, detalle, ip, trans);

                            await trans.CommitAsync();

                            if (esPlanilla)
                            {
                                MostrarMensaje("Operación Cancelada", "El intento de pago fue cancelado. Los boletos siguen en tu lista pendientes de cobro.", TipoMensaje.Info);
                            }
                            else
                            {
                                MostrarMensaje("Orden Cancelada", "Los boletos han sido liberados correctamente.", TipoMensaje.Exito);
                            }
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            if (User.Identity.IsAuthenticated) return RedirectToAction("MisBoletos");
            else return RedirectToAction("Index");
        }

        [Authorize]
        public async Task<IActionResult> Sorteo(string id)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin)) return RedirectToAction("Index");
            int idRifaReal = Funciones.DesencriptarId(id);
            if (idRifaReal <= 0) return RedirectToAction("Index");

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            var participantes = new List<BoletoItem>();
            var descartesPrevios = new List<BoletoItem>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);
                    if (!acceso.EsCreador) return RedirectToAction("Tablero", new { id = id });

                    if (acceso.Estado != "Activa" || DateTime.Now < acceso.fechasorteo)
                    {
                        MostrarMensaje("Bloqueado", "La rifa no está activa o aún no es la fecha programada.", TipoMensaje.Alerta);
                        return RedirectToAction("Tablero", new { id = id });
                    }

                    string sqlGanador = @"SELECT ""Id_Boleto_Ganador"" FROM ""Rifas"" WHERE ""Id_Rifa"" = @id";
                    using (var cmd = new NpgsqlCommand(sqlGanador, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifaReal);
                        var ganador = await cmd.ExecuteScalarAsync();
                        if (ganador != DBNull.Value)
                        {
                            MostrarMensaje("Sorteo Bloqueado", "Esta rifa ya tiene un ganador registrado.", TipoMensaje.Alerta);
                            return RedirectToAction("Index");
                        }
                    }

                    var idsDescartados = new List<int>();
                    string sqlDescartes = @"SELECT ""BoletoToken"" FROM ""Rifas_Descartes"" WHERE ""Id_Rifa"" = @id ORDER BY ""FechaDescarte"" ASC";
                    using (var cmdDesc = new NpgsqlCommand(sqlDescartes, conexion))
                    {
                        cmdDesc.Parameters.AddWithValue("@id", idRifaReal);
                        using (var rDesc = await cmdDesc.ExecuteReaderAsync())
                        {
                            while (await rDesc.ReadAsync())
                            {
                                int idBoletoDesc = Funciones.DesencriptarId(rDesc["BoletoToken"].ToString());
                                if (idBoletoDesc > 0) idsDescartados.Add(idBoletoDesc);
                            }
                        }
                    }

                    string sql = @"
                SELECT b.""Id_Boleto"", b.""Numero"", COALESCE(b.""Nombre_Cliente_Final"", v.""Nombre_Contacto"", 'Anónimo') as Cliente
                FROM ""Rifas_Boletos"" b
                LEFT JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
                WHERE b.""Id_Rifa"" = @id AND b.""Estado"" = 'Pagado' ORDER BY b.""Numero"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifaReal);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int idBol = (int)r["Id_Boleto"];
                                var boleto = new BoletoItem { Id_Boleto = idBol, Numero = (int)r["Numero"], Comprador = r["Cliente"].ToString().ToUpper() };

                                if (idsDescartados.Contains(idBol)) descartesPrevios.Add(boleto);
                                else participantes.Add(boleto);
                            }
                        }
                    }

                    if (!participantes.Any() && !descartesPrevios.Any())
                    {
                        MostrarMensaje("Sin Participantes", "No hay boletos pagados en esta rifa.", TipoMensaje.Alerta);
                        return RedirectToAction("Tablero", new { id = id });
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); return RedirectToAction("Index"); }

            ViewBag.IdRifa = id;
            ViewBag.DescartesPrevios = descartesPrevios;
            return View(participantes);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarTiroSorteo(string idRifa, int modalidadTotal)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin)) return Json(new { exito = false, mensaje = "Sin permisos." });

            try
            {
                int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
                int idRifaReal = Funciones.DesencriptarId(idRifa);

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);
                    if (!acceso.EsCreador || acceso.Estado != "Activa")
                        return Json(new { exito = false, mensaje = "Rifa inactiva o sin permisos." });

                    // Iniciamos la transacción ANTES de la validación para serializar el sorteo con bloqueo.
                    // El FOR UPDATE sobre la fila de Rifas impide que dos solicitudes simultáneas (doble clic)
                    // pasen ambas la validación de "sin ganador" y registren dos ganadores distintos.
                    using (var transSort = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Bloquear fila de la rifa con FOR UPDATE (serializa acceso concurrente al sorteo)
                            string sqlLockRifa = @"SELECT ""Id_Boleto_Ganador"" FROM ""Rifas"" WHERE ""Id_Rifa"" = @id FOR UPDATE";
                            using (var cmdLock = new NpgsqlCommand(sqlLockRifa, conexion, transSort))
                            {
                                cmdLock.Parameters.AddWithValue("@id", idRifaReal);
                                var objGanador = await cmdLock.ExecuteScalarAsync();
                                if (objGanador != null && objGanador != DBNull.Value)
                                {
                                    await transSort.RollbackAsync();
                                    return Json(new { exito = false, mensaje = "Ya hay un ganador registrado." });
                                }
                            }

                            int totalVendidos = 0;
                            using (var cmdCount = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Rifas_Boletos"" WHERE ""Id_Rifa"" = @id AND ""Estado"" = 'Pagado'", conexion, transSort))
                            {
                                cmdCount.Parameters.AddWithValue("@id", idRifaReal);
                                totalVendidos = Convert.ToInt32(await cmdCount.ExecuteScalarAsync());
                            }

                            // 2. Cargar descartes legítimos directamente desde la Base de Datos
                            var idsDescartados = new List<int>();
                            string sqlDesc = @"SELECT ""BoletoToken"" FROM ""Rifas_Descartes"" WHERE ""Id_Rifa"" = @id";
                            using (var cmdDesc = new NpgsqlCommand(sqlDesc, conexion, transSort))
                            {
                                cmdDesc.Parameters.AddWithValue("@id", idRifaReal);
                                using (var rDesc = await cmdDesc.ExecuteReaderAsync())
                                {
                                    while (await rDesc.ReadAsync())
                                    {
                                        string tokenBol = rDesc["BoletoToken"].ToString();
                                        int decId = Funciones.DesencriptarId(tokenBol);
                                        if (decId <= 0) { await transSort.RollbackAsync(); return Json(new { exito = false, mensaje = "🔥 ALERTA SEGURIDAD: Cadena de encriptación rota en BD." }); }
                                        idsDescartados.Add(decId);
                                    }
                                }
                            }

                            // 3. DETERMINACIÓN ATÓMICA DE INTENTOS DESDE EL SERVIDOR
                            int descartesEnBD = idsDescartados.Count;
                            int intentoCalculado = descartesEnBD + 1;

                            if (descartesEnBD >= totalVendidos) { await transSort.RollbackAsync(); return Json(new { exito = false, codigoError = "EMPTY", mensaje = "Boletos agotados." }); }

                            // El servidor decide si es Ganador: Si alcanzó la meta enviada O si matemáticamente ya solo queda un boleto libre.
                            bool esGanadorReal = (intentoCalculado >= modalidadTotal) || (totalVendidos - descartesEnBD <= 1);

                            // 4. Cargar participantes restantes válidos
                            var disponibles = new List<BoletoItem>();
                            string sqlDisponibles = @"
        SELECT b.""Id_Boleto"", b.""Numero"", COALESCE(b.""Nombre_Cliente_Final"", v.""Nombre_Contacto"", 'Anónimo') as Cliente
        FROM ""Rifas_Boletos"" b
        LEFT JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
        WHERE b.""Id_Rifa"" = @id AND b.""Estado"" = 'Pagado'";

                            using (var cmdDisp = new NpgsqlCommand(sqlDisponibles, conexion, transSort))
                            {
                                cmdDisp.Parameters.AddWithValue("@id", idRifaReal);
                                using (var r = await cmdDisp.ExecuteReaderAsync())
                                {
                                    while (await r.ReadAsync())
                                    {
                                        int idB = (int)r["Id_Boleto"];
                                        if (!idsDescartados.Contains(idB))
                                        {
                                            disponibles.Add(new BoletoItem
                                            {
                                                Id_Boleto = idB,
                                                Numero = (int)r["Numero"],
                                                Comprador = r["Cliente"]?.ToString()
                                            });
                                        }
                                    }
                                }
                            }

                            if (!disponibles.Any()) { await transSort.RollbackAsync(); return Json(new { exito = false, mensaje = "No quedan participantes disponibles." }); }

                            // 5. Selección aleatoria criptográfica segura en servidor
                            int indiceGanador = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, disponibles.Count);
                            var seleccionado = disponibles[indiceGanador];

                            // 6. Guardado transaccional atómico (dentro de transSort que ya tiene el bloqueo de la fila de la rifa)
                            if (esGanadorReal)
                            {
                                await new NpgsqlCommand($@"UPDATE ""Rifas"" SET ""Estado"" = 'Finalizada', ""Id_Boleto_Ganador"" = {seleccionado.Id_Boleto} WHERE ""Id_Rifa"" = {idRifaReal}", conexion, transSort).ExecuteNonQueryAsync();
                                await new NpgsqlCommand($@"DELETE FROM ""Rifas_Descartes"" WHERE ""Id_Rifa"" = {idRifaReal}", conexion, transSort).ExecuteNonQueryAsync();
                            }
                            else
                            {
                                string tokenSeguro = Funciones.EncriptarId(seleccionado.Id_Boleto);
                                using (var cmdIns = new NpgsqlCommand(@"INSERT INTO ""Rifas_Descartes"" (""Id_Rifa"", ""BoletoToken"") VALUES (@idR, @tok)", conexion, transSort))
                                {
                                    cmdIns.Parameters.AddWithValue("@idR", idRifaReal);
                                    cmdIns.Parameters.AddWithValue("@tok", tokenSeguro);
                                    await cmdIns.ExecuteNonQueryAsync();
                                }
                            }
                            await transSort.CommitAsync();

                            return Json(new
                            {
                                exito = true,
                                idBoleto = seleccionado.Id_Boleto,
                                numero = seleccionado.Numero,
                                comprador = seleccionado.Comprador,
                                esGanadorFinal = esGanadorReal,
                                intentoProcesado = intentoCalculado
                            });
                        }
                        catch (Exception exTrans) { await transSort.RollbackAsync(); return Json(new { exito = false, mensaje = exTrans.Message }); }
                    }
                    // Ruta de control de seguridad — todas las rutas dentro de transSort retornan explícitamente
                    return Json(new { exito = false, mensaje = "Error inesperado en el proceso de sorteo." });

                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }
        /// <summary>
        /// Valida si el usuario actual tiene permiso de ver la dinámica basado en su visibilidad.
        /// Útil cuando ya tienes el valor de VisiblePublico desde una consulta previa.
        /// </summary>
        private bool PermitirAccesoPorVisibilidad(bool visiblePublico)
        {
            if (visiblePublico) return true;

            // Si no es visible al público, verificamos si es administrador
            return User.Identity.IsAuthenticated && User.TienePermiso(Modulo, Parametros.Permisos.Crear);
        }

        /// <summary>
        /// Consulta la base de datos y valida si el usuario tiene permiso de ver la dinámica.
        /// Útil cuando solo tienes el ID de la rifa.
        /// </summary>
        private async Task<bool> PermitirAccesoPorVisibilidadAsync(int idRifa, NpgsqlConnection conexion, NpgsqlTransaction trans = null)
        {
            bool visiblePublico = true;
            string sql = @"SELECT ""VisiblePublico"" FROM ""Rifas"" WHERE ""Id_Rifa"" = @id";

            using (var cmd = new NpgsqlCommand(sql, conexion, trans))
            {
                cmd.Parameters.AddWithValue("@id", idRifa);
                var obj = await cmd.ExecuteScalarAsync();
                if (obj != null && obj != DBNull.Value)
                {
                    visiblePublico = (bool)obj;
                }
            }

            return PermitirAccesoPorVisibilidad(visiblePublico);
        }
        private async Task<(bool Existe, bool EsCreador, bool EsVendedor, string Titulo, string Estado, DateTime fechasorteo, decimal CostoXBoleto)> ValidarAccesoRifa(int idRifa, int idUsuarioActual, NpgsqlConnection conexion)
        {
            bool existe = false, esVendedor = false;
            string titulo = "", estado = "", imagenUrl = "";
            DateTime fechasorteo = new DateTime(1900, 1, 1);
            decimal CostoBoleto = 0;

            bool esCreador = User.TienePermiso(Modulo, Parametros.Permisos.Crear);

            string sqlRifa = @"SELECT ""Titulo"", ""Estado"", ""ImagenUrl"", ""Fecha_Sorteo"", ""Costo_Boleto"" 
                       FROM ""Rifas"" WHERE ""Id_Rifa"" = @id";
            using (var cmd = new NpgsqlCommand(sqlRifa, conexion))
            {
                cmd.Parameters.AddWithValue("@id", idRifa);
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    if (await r.ReadAsync())
                    {
                        existe = true;
                        titulo = r["Titulo"].ToString();
                        estado = r["Estado"].ToString();
                        imagenUrl = r["ImagenUrl"]?.ToString();
                        CostoBoleto = (decimal)r["Costo_Boleto"];

                        if (!r.IsDBNull(r.GetOrdinal("Fecha_Sorteo")))
                        {
                            fechasorteo = Convert.ToDateTime(r["Fecha_Sorteo"]);
                        }
                    }
                }
            }

            if (existe && !esCreador)
            {
                string sqlBoletos = @"SELECT COUNT(*) FROM ""Rifas_Boletos"" WHERE ""Id_Rifa"" = @id AND ""Id_Usuario_Asignado"" = @uid";
                using (var cmdBol = new NpgsqlCommand(sqlBoletos, conexion))
                {
                    cmdBol.Parameters.AddWithValue("@id", idRifa);
                    cmdBol.Parameters.AddWithValue("@uid", idUsuarioActual);
                    long conteo = (long)await cmdBol.ExecuteScalarAsync();
                    esVendedor = conteo > 0;
                }
            }

            if (existe)
            {
                ViewBag.IdRifaActiva = Funciones.EncriptarId(idRifa);
                ViewBag.TituloRifa = titulo;
                ViewBag.EsCreador = esCreador;
                ViewBag.ImagenUrl = Funciones.NormalizarUrlImagen(imagenUrl, ModoVisualizacionImagen.Incrustado);
            }

            return (existe, esCreador, esVendedor, titulo, estado, fechasorteo, CostoBoleto);
        }

        private async Task DestruirImagenCloudinary(string urlImagen)
        {
            if (string.IsNullOrEmpty(urlImagen)) return;

            try
            {
                Uri uri = new Uri(urlImagen);
                string path = uri.AbsolutePath;

                int versionIndex = path.IndexOf("/v");
                int startPos = -1;

                if (versionIndex >= 0)
                {
                    startPos = path.IndexOf('/', versionIndex + 2);
                }

                if (startPos == -1) startPos = path.IndexOf('/', 1);

                if (startPos > 0)
                {
                    string publicIdConExt = path.Substring(startPos + 1);
                    int lastDot = publicIdConExt.LastIndexOf('.');
                    string publicId = lastDot > 0 ? publicIdConExt.Substring(0, lastDot) : publicIdConExt;

                    publicId = Uri.UnescapeDataString(publicId);

                    await _cloudinary.DestroyAsync(new DeletionParams(publicId));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADVERTENCIA] Falló el borrado en Cloudinary para la URL: {urlImagen}. Archivo huérfano generado. Razón: {ex.Message}");
            }
        }

        private async Task<string> SubirQrBase64Cloudinary(string base64Image, string folderPath)
        {
            var uploadParams = new ImageUploadParams()
            {
                File = new FileDescription(base64Image),
                Folder = folderPath,
                UseFilename = false,
                UniqueFilename = true
            };

            var uploadResult = await _cloudinary.UploadAsync(uploadParams);
            return uploadResult.SecureUrl.ToString();
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditarBoletoVendido(string idRifa, int IdBoleto, string NombreComprador, string TelefonoComprador, string Comentarios, bool MarcadorPersonal)
        {
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            int idRifaReal = Funciones.DesencriptarId(idRifa);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);
                    if (!acceso.Existe || (!acceso.EsCreador && !acceso.EsVendedor))
                        throw new Exception("No tienes autorización ni deudas asignadas en esta rifa.");

                    if (acceso.Estado != "Activa")
                        throw new Exception("La rifa no está activa, los datos comerciales han sido congelados.");

                    string condicionFiltro = acceso.EsCreador
                        ? @"(""Id_Usuario_Asignado"" = @uid OR (""Id_Usuario_Asignado"" IS NULL AND ""NombrePromotorExterno"" IS NOT NULL))"
                        : @"(""Id_Usuario_Asignado"" = @uid)";

                    string sqlUpd = $@"UPDATE ""Rifas_Boletos"" 
                                       SET ""Nombre_Cliente_Final"" = @nom, 
                                           ""Telefono_Cliente_Final"" = @tel, 
                                           ""Comentarios"" = @com,
                                           ""MarcadorPersonal"" = @marc
                                       WHERE ""Id_Boleto"" = @idBol 
                                         AND ""Estado"" IN ('Vendido_Sin_Pagar', 'Pagado') 
                                         AND {condicionFiltro}";

                    using (var cmd = new NpgsqlCommand(sqlUpd, conexion))
                    {
                        cmd.Parameters.AddWithValue("@nom", string.IsNullOrWhiteSpace(NombreComprador) ? "Anónimo" : NombreComprador.Trim());
                        cmd.Parameters.AddWithValue("@tel", string.IsNullOrWhiteSpace(TelefonoComprador) ? "" : TelefonoComprador.Trim());
                        cmd.Parameters.AddWithValue("@com", string.IsNullOrWhiteSpace(Comentarios) ? "" : Comentarios.Trim());
                        cmd.Parameters.AddWithValue("@marc", MarcadorPersonal);
                        cmd.Parameters.AddWithValue("@idBol", IdBoleto);
                        cmd.Parameters.AddWithValue("@uid", idUsuario);

                        int afectadas = await cmd.ExecuteNonQueryAsync();
                        if (afectadas > 0)
                        {
                            MostrarMensaje("Éxito", "Información del comprador actualizada correctamente.", TipoMensaje.Exito);
                        }
                        else
                        {
                            throw new Exception("No se pudo actualizar el registro. Verifica que el boleto te pertenezca y no haya sido modificado.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("MisBoletos", new { id = idRifa });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReportarPagoComprobante(string idRifa, int? IdUsuarioVendedor, string NombrePromotorExterno, decimal MontoPago, IFormFile comprobanteArchivo, List<int> idsBoletosAPagar, int? IdPagoPrevio)
        {
            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
            int idRifaReal = Funciones.DesencriptarId(idRifa);

            try
            {
                if (MontoPago <= 0) throw new Exception("El monto debe ser mayor a cero.");
                if (comprobanteArchivo == null) throw new Exception("Debes adjuntar la imagen de tu comprobante.");
                if (idsBoletosAPagar == null || !idsBoletosAPagar.Any()) throw new Exception("Debes seleccionar al menos un boleto para pagar.");

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuarioActual, conexion);

                    // Si no es el creador (admin), forzamos sus propios datos
                    if (!acceso.EsCreador)
                    {
                        IdUsuarioVendedor = idUsuarioActual;
                        NombrePromotorExterno = null;
                    }

                    string boletosPagadosStr = string.Join(",", idsBoletosAPagar);

                    // Búsqueda segura de Vendedor manejando correctamente los NULLs en SQL
                    string condVendedor = "";
                    if (IdUsuarioVendedor.HasValue && IdUsuarioVendedor.Value > 0)
                    {
                        condVendedor = "AND \"Id_Usuario_Vendedor\" = @idu";
                    }
                    else if (!string.IsNullOrWhiteSpace(NombrePromotorExterno))
                    {
                        condVendedor = "AND \"Id_Usuario_Vendedor\" IS NULL AND \"NombrePromotorExterno\" = @nomExt";
                    }
                    else
                    {
                        // Venta directa / Mi Cuenta
                        condVendedor = "AND \"Id_Usuario_Vendedor\" IS NULL AND \"NombrePromotorExterno\" IS NULL";
                    }

                    // BUSCAMOS PAGOS RECHAZADOS PARA BORRAR
                    string sqlRechazados = $@"SELECT ""IdPago"", ""ComprobanteUrl"", ""BoletosPagados"" 
                                      FROM ""Rifas_Pagos"" 
                                      WHERE ""Id_Rifa"" = @idr AND ""Estado"" = 'Rechazado' {condVendedor}";

                    var pagosAEliminar = new List<(int Id, string Url)>();

                    using (var cmdBusqueda = new NpgsqlCommand(sqlRechazados, conexion))
                    {
                        cmdBusqueda.Parameters.AddWithValue("@idr", idRifaReal);
                        if (IdUsuarioVendedor.HasValue && IdUsuarioVendedor.Value > 0)
                            cmdBusqueda.Parameters.AddWithValue("@idu", IdUsuarioVendedor.Value);
                        else if (!string.IsNullOrWhiteSpace(NombrePromotorExterno))
                            cmdBusqueda.Parameters.AddWithValue("@nomExt", NombrePromotorExterno.Trim());

                        using (var r = await cmdBusqueda.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int idPagoBd = (int)r["IdPago"];
                                string bolPagadosBd = r["BoletosPagados"]?.ToString();
                                bool eliminar = false;

                                // Condición 1: Coincidencia explícita de IdPagoPrevio
                                if (IdPagoPrevio.HasValue && IdPagoPrevio.Value == idPagoBd)
                                {
                                    eliminar = true;
                                }
                                // Condición 2: El comprobante rechazado contiene AL MENOS UNO de los boletos que estamos volviendo a intentar pagar
                                else if (!string.IsNullOrEmpty(bolPagadosBd))
                                {
                                    var idsBd = bolPagadosBd.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
                                    if (idsBd.Intersect(idsBoletosAPagar).Any())
                                    {
                                        eliminar = true;
                                    }
                                }

                                if (eliminar) pagosAEliminar.Add((idPagoBd, r["ComprobanteUrl"]?.ToString()));
                            }
                        }
                    }

                    // EJECUTAMOS LA ELIMINACIÓN DE LOS COMPROBANTES VIEJOS
                    foreach (var pagoDel in pagosAEliminar)
                    {
                        using (var cmdDel = new NpgsqlCommand("DELETE FROM \"Rifas_Pagos\" WHERE \"IdPago\" = @idp", conexion))
                        {
                            cmdDel.Parameters.AddWithValue("@idp", pagoDel.Id);
                            await cmdDel.ExecuteNonQueryAsync();
                        }
                        if (!string.IsNullOrEmpty(pagoDel.Url)) await DestruirImagenCloudinary(pagoDel.Url);
                    }

                    if (comprobanteArchivo != null)
                    {
                        // VALIDACIÓN DE SEGURIDAD EN SERVIDOR (Error 3)
                        if (comprobanteArchivo.Length > 10 * 1024 * 1024)
                            throw new Exception("El archivo del comprobante supera el límite permitido de 10MB.");

                        var extension = Path.GetExtension(comprobanteArchivo.FileName).ToLower();
                        var mimeType = comprobanteArchivo.ContentType.ToLower();
                        var mimesValidos = new[] { "image/jpeg", "image/jpg", "image/png", "image/webp" };
                        var extsValidas = new[] { ".jpg", ".jpeg", ".png", ".webp" };

                        if (!mimesValidos.Contains(mimeType) || !extsValidas.Contains(extension))
                            throw new Exception("El archivo adjunto no es una imagen de comprobante válida (Solo JPG, PNG, WEBP).");
                    }

                    // CONTINUAMOS INSERTANDO EL NUEVO COMPROBANTE
                    string imgUrl = await SubirImagenCloudinary(comprobanteArchivo, $"{sAmbiente}/Rifas/Pagos/{idUsuarioActual}");

                    // FIX #14: Si falla el registro en BD, destruir la imagen subida para evitar archivos huérfanos en Cloudinary
                    try
                    {
                        string sqlInsert = @"INSERT INTO ""Rifas_Pagos"" 
               (""Id_Rifa"", ""Id_Usuario_Vendedor"", ""NombrePromotorExterno"", ""Monto"", ""MetodoPago"", ""ComprobanteUrl"", ""Estado"", ""Origen"", ""FechaPago"", ""BoletosPagados"") 
               VALUES (@idr, @idu, @nomExt, @monto, 'Transferencia', @img, 'Pendiente', 'Vendedor', CURRENT_TIMESTAMP, @bolPagados)";

                        using (var cmd = new NpgsqlCommand(sqlInsert, conexion))
                        {
                            cmd.Parameters.AddWithValue("@idr", idRifaReal);
                            cmd.Parameters.AddWithValue("@idu", (object)IdUsuarioVendedor ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@nomExt", string.IsNullOrWhiteSpace(NombrePromotorExterno) ? DBNull.Value : NombrePromotorExterno.Trim());
                            cmd.Parameters.AddWithValue("@monto", MontoPago);
                            cmd.Parameters.AddWithValue("@img", imgUrl);
                            cmd.Parameters.AddWithValue("@bolPagados", boletosPagadosStr);
                            await cmd.ExecuteNonQueryAsync();
                        }

                        MostrarMensaje("Comprobante Enviado", "Tu pago ha sido reportado y está en espera de validación.", TipoMensaje.Exito);
                    }
                    catch
                    {
                        // Revertir imagen en Cloudinary si falla el INSERT en BD
                        if (!string.IsNullOrEmpty(imgUrl)) await DestruirImagenCloudinary(imgUrl);
                        throw;
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("MisBoletos", new { id = idRifa });
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ObtenerBoletosParaPago(string idRifa, int? idUsuarioVendedor, string nombreExterno, int? idPagoPrevio)
        {
            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
            int idRifaReal = Funciones.DesencriptarId(idRifa);

            if (nombreExterno == "null" || nombreExterno == "undefined") nombreExterno = null;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuarioActual, conexion);
                    if (!acceso.Existe) return Json(new { exito = false, mensaje = "Rifa no encontrada." });

                    if (!idUsuarioVendedor.HasValue && string.IsNullOrWhiteSpace(nombreExterno))
                    {
                        idUsuarioVendedor = idUsuarioActual;
                    }

                    if (!acceso.EsCreador)
                    {
                        idUsuarioVendedor = idUsuarioActual;
                        nombreExterno = null;
                    }

                    string sqlBoletos = @"
                        SELECT b.""Id_Boleto"", b.""Numero"", COALESCE(b.""Nombre_Cliente_Final"", 'Anónimo') as ""Cliente""
                        FROM ""Rifas_Boletos"" b
                        WHERE b.""Id_Rifa"" = @idRifa 
                          AND b.""Estado"" = 'Vendido_Sin_Pagar'
                          AND (
                              (b.""Id_Usuario_Asignado"" = @idu AND @idu IS NOT NULL) 
                              OR 
                              (b.""Id_Usuario_Asignado"" IS NULL AND COALESCE(b.""NombrePromotorExterno"", '') = COALESCE(@nomExt, ''))
                          )";

                    var todosBoletos = new List<dynamic>();

                    using (var cmd = new NpgsqlCommand(sqlBoletos, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idRifa", idRifaReal);
                        cmd.Parameters.AddWithValue("@idu", (object)idUsuarioVendedor ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@nomExt", string.IsNullOrWhiteSpace(nombreExterno) ? DBNull.Value : nombreExterno.Trim());

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                todosBoletos.Add(new
                                {
                                    id = (int)r["Id_Boleto"],
                                    numero = (int)r["Numero"],
                                    comprador = r["Cliente"].ToString()
                                });
                            }
                        }
                    }

                    string sqlPagos = @"SELECT ""BoletosPagados"" FROM ""Rifas_Pagos"" 
                                        WHERE ""Id_Rifa"" = @idRifa 
                                          AND ""Estado"" = 'Pendiente' 
                                          AND ""IdPago"" != COALESCE(@idp, 0)
                                          AND ""BoletosPagados"" IS NOT NULL";

                    var bloqueados = new HashSet<int>();

                    using (var cmdP = new NpgsqlCommand(sqlPagos, conexion))
                    {
                        cmdP.Parameters.AddWithValue("@idRifa", idRifaReal);
                        cmdP.Parameters.AddWithValue("@idp", (object)idPagoPrevio ?? DBNull.Value);

                        using (var rP = await cmdP.ExecuteReaderAsync())
                        {
                            while (await rP.ReadAsync())
                            {
                                string bp = rP["BoletosPagados"].ToString();
                                if (!string.IsNullOrWhiteSpace(bp))
                                {
                                    foreach (var idStr in bp.Split(','))
                                    {
                                        if (int.TryParse(idStr.Trim(), out int idBloqueado))
                                        {
                                            bloqueados.Add(idBloqueado);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    var listaFinal = todosBoletos.Where(b => !bloqueados.Contains(b.id)).ToList();

                    return Json(new { exito = true, boletos = listaFinal });
                }
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = "Error en el servidor: " + ex.Message });
            }
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidarPagoTransferencia(string idRifa, int IdPago, string AccionValidacion, string NotaRechazo)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar)) return RedirectToAction("Index");

            var idClaim = User.FindFirst("IdUsuario");
            if (idClaim == null || !int.TryParse(idClaim.Value, out int idUsuarioActual)) return RedirectToAction("Index");

            int idRifaReal = Funciones.DesencriptarId(idRifa);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuarioActual, conexion);
                    if (!acceso.EsCreador) throw new Exception("No cuentas con autorización de auditoría financiera.");

                    string nuevoEstado = AccionValidacion == "Aprobar" ? "Aprobado" : "Rechazado";

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sqlCheck = "SELECT \"Estado\" FROM \"Rifas_Pagos\" WHERE \"IdPago\" = @idp FOR UPDATE";
                            using (var cmdC = new NpgsqlCommand(sqlCheck, conexion, trans))
                            {
                                cmdC.Parameters.AddWithValue("@idp", IdPago);
                                var estadoActual = await cmdC.ExecuteScalarAsync();
                                if (estadoActual == null || estadoActual.ToString() != "Pendiente")
                                {
                                    throw new Exception("El pago ya fue procesado anteriormente.");
                                }
                            }

                            string sqlUpdate = @"UPDATE ""Rifas_Pagos"" SET ""Estado"" = @est, ""Nota"" = @nota WHERE ""IdPago"" = @idp AND ""Id_Rifa"" = @idr";
                            using (var cmd = new NpgsqlCommand(sqlUpdate, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@est", nuevoEstado);
                                cmd.Parameters.AddWithValue("@nota", (object)NotaRechazo ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@idp", IdPago);
                                cmd.Parameters.AddWithValue("@idr", idRifaReal);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            if (nuevoEstado == "Aprobado")
                            {
                                int? idVendedor = null;
                                string promotorExt = null;
                                string boletosPagadosStr = null;

                                using (var cmdSel = new NpgsqlCommand($"SELECT \"Id_Usuario_Vendedor\", \"NombrePromotorExterno\", \"BoletosPagados\" FROM \"Rifas_Pagos\" WHERE \"IdPago\" = {IdPago}", conexion, trans))
                                {
                                    using (var r = await cmdSel.ExecuteReaderAsync())
                                    {
                                        if (await r.ReadAsync())
                                        {
                                            idVendedor = r["Id_Usuario_Vendedor"] as int?;
                                            promotorExt = r["NombrePromotorExterno"]?.ToString();
                                            boletosPagadosStr = r["BoletosPagados"]?.ToString();
                                        }
                                    }
                                }

                                if (!string.IsNullOrEmpty(boletosPagadosStr))
                                {
                                    List<int> idsToUpdate = boletosPagadosStr.Split(',').Select(int.Parse).ToList();

                                    // MODIFICACIÓN CRÍTICA: Se exige que el estado sea 'Vendido_Sin_Pagar' para proteger la consistencia
                                    string sqlUpdBol = @"UPDATE ""Rifas_Boletos"" SET ""Estado"" = 'Pagado' 
                                                 WHERE ""Id_Boleto"" = ANY(@idsToUpdate) AND ""Id_Rifa"" = @idr 
                                                 AND ""Estado"" = 'Vendido_Sin_Pagar'";

                                    using (var cmdBol = new NpgsqlCommand(sqlUpdBol, conexion, trans))
                                    {
                                        cmdBol.Parameters.AddWithValue("@idr", idRifaReal);
                                        cmdBol.Parameters.AddWithValue("@idsToUpdate", idsToUpdate);
                                        int afectadas = await cmdBol.ExecuteNonQueryAsync();

                                        if (afectadas != idsToUpdate.Count)
                                        {
                                            throw new Exception("Inconsistencia detectada: Uno o más boletos ya no se encuentran en estado de cobro pendiente (podrían haber sido liberados o pagados por otra vía). Operación abortada.");
                                        }
                                    }
                                }
                                else
                                {
                                    string condVendedor = idVendedor.HasValue
                                        ? "AND \"Id_Usuario_Asignado\" = @uid"
                                        : "AND \"Id_Usuario_Asignado\" IS NULL AND \"NombrePromotorExterno\" = @nomExt";

                                    string sqlUpdBol = $@"UPDATE ""Rifas_Boletos"" SET ""Estado"" = 'Pagado' 
                                          WHERE ""Id_Rifa"" = @idr AND ""Estado"" = 'Vendido_Sin_Pagar' {condVendedor}";

                                    using (var cmdBol = new NpgsqlCommand(sqlUpdBol, conexion, trans))
                                    {
                                        cmdBol.Parameters.AddWithValue("@idr", idRifaReal);
                                        if (idVendedor.HasValue) cmdBol.Parameters.AddWithValue("@uid", idVendedor.Value);
                                        else cmdBol.Parameters.AddWithValue("@nomExt", promotorExt);
                                        await cmdBol.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            await trans.CommitAsync();
                            MostrarMensaje("Estatus Actualizado", $"El reporte de caja ha sido marcado como: {nuevoEstado}.", TipoMensaje.Exito);
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error de Validación", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Asignaciones", new { id = idRifa });
        }

        // =====================================================================
        // IMPRESIÓN DE BOLETOS — Tómbola física
        // =====================================================================

        [Authorize]
        public async Task<IActionResult> ImpresionBoletos(string id)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin)) return RedirectToAction("Index");

            int idRifaReal = Funciones.DesencriptarId(id);
            if (idRifaReal <= 0) return RedirectToAction("Index");

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new ImpresionBoletosViewModel { IdRifaEncriptado = id };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);
                    if (!acceso.Existe || !acceso.EsCreador) return RedirectToAction("Index");

                    modelo.InfoRifa = new RifaPublicaViewModel
                    {
                        Id_Rifa = idRifaReal,
                        IdRifaEncriptado = id,
                        Titulo = acceso.Titulo,
                        FechaSorteo = acceso.fechasorteo,
                        Estado = acceso.Estado
                    };

                    // Carga boletos Pagado cuya venta también esté Pagado
                    string sql = @"
                        SELECT b.""Id_Boleto"", b.""Numero"",
                               COALESCE(b.""Nombre_Cliente_Final"", v.""Nombre_Contacto"", 'Sin nombre') AS NombreTitular,
                               COALESCE(b.""Telefono_Cliente_Final"", v.""Telefono_Contacto"", '') AS Telefono
                        FROM ""Rifas_Boletos"" b
                        JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
                        WHERE b.""Id_Rifa"" = @id
                          AND b.""Estado"" = 'Pagado'
                          AND v.""Estado"" = 'Pagado'
                        ORDER BY b.""Numero"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifaReal);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.Boletos.Add(new BoletoImpresionItem
                                {
                                    Id_Boleto = (int)r["Id_Boleto"],
                                    Numero = (int)r["Numero"],
                                    NombreTitular = r["NombreTitular"].ToString().ToUpper(),
                                    Telefono = r["Telefono"].ToString()
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); return RedirectToAction("Index"); }

            return View(modelo);
        }

        // =====================================================================
        // SORTEO FORMAL CON DIRECTIVOS AJP
        // =====================================================================

        [Authorize]
        public async Task<IActionResult> SorteoDirectivos(string id)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin)) return RedirectToAction("Index");

            int idRifaReal = Funciones.DesencriptarId(id);
            if (idRifaReal <= 0) return RedirectToAction("Index");

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new SorteoDirectivosViewModel { IdRifaEncriptado = id };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifaReal, idUsuario, conexion);
                    if (!acceso.Existe || !acceso.EsCreador) return RedirectToAction("Index");

                    modelo.InfoRifa = new RifaPublicaViewModel
                    {
                        Id_Rifa = idRifaReal,
                        IdRifaEncriptado = id,
                        Titulo = acceso.Titulo,
                        FechaSorteo = acceso.fechasorteo,
                        Estado = acceso.Estado
                    };

                    // Leer estado actual de la rifa (ganador + modalidad)
                    string sqlRifa = @"SELECT ""Id_Boleto_Ganador"", ""Modalidad_Sorteo"", ""Fecha_Sorteo_Confirmado""
                                      FROM ""Rifas"" WHERE ""Id_Rifa"" = @id";
                    int? idBoletoBd = null;
                    using (var cmd = new NpgsqlCommand(sqlRifa, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifaReal);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                idBoletoBd = r["Id_Boleto_Ganador"] as int?;
                                modelo.Modalidad = r["Modalidad_Sorteo"]?.ToString();
                                modelo.FechaConfirmadoGanador = r["Fecha_Sorteo_Confirmado"] as DateTime?;
                            }
                        }
                    }

                    // Si ya hay ganador registrado, mostramos su información y terminamos
                    if (idBoletoBd.HasValue)
                    {
                        modelo.YaTieneGanador = true;
                        string sqlGan = @"SELECT b.""Numero"",
                                                 COALESCE(b.""Nombre_Cliente_Final"", v.""Nombre_Contacto"", 'Anónimo') AS Nombre
                                          FROM ""Rifas_Boletos"" b
                                          LEFT JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
                                          WHERE b.""Id_Boleto"" = @idB";
                        using (var cmd = new NpgsqlCommand(sqlGan, conexion))
                        {
                            cmd.Parameters.AddWithValue("@idB", idBoletoBd.Value);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    modelo.NumeroGanador = (int)r["Numero"];
                                    modelo.NombreGanador = r["Nombre"].ToString();
                                }
                            }
                        }

                        // Cargar quiénes confirmaron (para mostrar el historial)
                        await CargarConfirmaciones(idRifaReal, idUsuario, modelo, conexion);
                        return View(modelo);
                    }

                    // Cargar confirmaciones en proceso (si las hay)
                    await CargarConfirmaciones(idRifaReal, idUsuario, modelo, conexion);

                    if (modelo.HayProcesoEnCurso)
                    {
                        // Mostrar datos del candidato en curso
                        string sqlCand = @"SELECT b.""Numero"",
                                                  COALESCE(b.""Nombre_Cliente_Final"", v.""Nombre_Contacto"", 'Anónimo') AS Nombre
                                           FROM ""Rifas_Boletos"" b
                                           LEFT JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
                                           WHERE b.""Id_Boleto"" = @idB";
                        using (var cmd = new NpgsqlCommand(sqlCand, conexion))
                        {
                            cmd.Parameters.AddWithValue("@idB", modelo.IdBoletoCandidato);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    modelo.NumeroCandidato = (int)r["Numero"];
                                    modelo.NombreCandidato = r["Nombre"].ToString();
                                }
                            }
                        }
                    }
                    else
                    {
                        // Sin proceso en curso: cargar lista de boletos disponibles para seleccionar
                        string sqlDisp = @"
                            SELECT b.""Id_Boleto"", b.""Numero"",
                                   COALESCE(b.""Nombre_Cliente_Final"", v.""Nombre_Contacto"", 'Anónimo') AS Cliente
                            FROM ""Rifas_Boletos"" b
                            JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
                            WHERE b.""Id_Rifa"" = @id
                              AND b.""Estado"" = 'Pagado'
                              AND v.""Estado"" = 'Pagado'
                            ORDER BY b.""Numero"" ASC";
                        using (var cmd = new NpgsqlCommand(sqlDisp, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", idRifaReal);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    modelo.BoletosDisponibles.Add(new BoletoItem
                                    {
                                        Id_Boleto = (int)r["Id_Boleto"],
                                        Numero = (int)r["Numero"],
                                        Comprador = r["Cliente"].ToString()
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); return RedirectToAction("Index"); }

            return View(modelo);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmarSorteoDirectivos(string idRifa, int idBoleto)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
                return Json(new { exito = false, mensaje = "Sin permisos de administrador." });

            int idRifaReal = Funciones.DesencriptarId(idRifa);
            if (idRifaReal <= 0)
                return Json(new { exito = false, mensaje = "Rifa inválida." });

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Bloquear fila de la rifa (serializa acceso concurrente)
                            string sqlLock = @"SELECT ""Id_Boleto_Ganador"", ""Modalidad_Sorteo"", ""Estado""
                                              FROM ""Rifas"" WHERE ""Id_Rifa"" = @id FOR UPDATE";
                            using (var cmdLock = new NpgsqlCommand(sqlLock, conexion, trans))
                            {
                                cmdLock.Parameters.AddWithValue("@id", idRifaReal);
                                using (var r = await cmdLock.ExecuteReaderAsync())
                                {
                                    if (!await r.ReadAsync())
                                    { await trans.RollbackAsync(); return Json(new { exito = false, mensaje = "Rifa no encontrada." }); }

                                    if (r["Id_Boleto_Ganador"] != DBNull.Value)
                                    { await trans.RollbackAsync(); return Json(new { exito = false, mensaje = "Esta rifa ya tiene un ganador registrado." }); }

                                    string modalidad = r["Modalidad_Sorteo"]?.ToString();
                                    if (modalidad == "MANUAL")
                                    { await trans.RollbackAsync(); return Json(new { exito = false, mensaje = "Esta rifa ya fue sorteada de forma manual." }); }

                                    if (r["Estado"].ToString() != "Activa")
                                    { await trans.RollbackAsync(); return Json(new { exito = false, mensaje = "La rifa no está activa." }); }
                                }
                            }

                            // 2. Verificar que el boleto sea válido (pagado con venta pagada)
                            bool boletoValido = false;
                            using (var cmdBol = new NpgsqlCommand(
                                @"SELECT COUNT(*) FROM ""Rifas_Boletos"" b
                                  JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
                                  WHERE b.""Id_Boleto"" = @idB AND b.""Id_Rifa"" = @idR
                                    AND b.""Estado"" = 'Pagado' AND v.""Estado"" = 'Pagado'", conexion, trans))
                            {
                                cmdBol.Parameters.AddWithValue("@idB", idBoleto);
                                cmdBol.Parameters.AddWithValue("@idR", idRifaReal);
                                boletoValido = (long)await cmdBol.ExecuteScalarAsync() > 0;
                            }
                            if (!boletoValido)
                            { await trans.RollbackAsync(); return Json(new { exito = false, mensaje = "El boleto seleccionado no es válido o no está pagado." }); }

                            // 3. Verificar que si ya hay confirmaciones previas, el boleto candidato sea el mismo
                            int? candidatoExistente = null;
                            int conteoActual = 0;
                            using (var cmdCheck = new NpgsqlCommand(
                                @"SELECT ""Id_Boleto_Candidato"", COUNT(*) OVER() AS Total
                                  FROM ""Rifas_Sorteo_Confirmaciones""
                                  WHERE ""Id_Rifa"" = @idR
                                  LIMIT 1", conexion, trans))
                            {
                                cmdCheck.Parameters.AddWithValue("@idR", idRifaReal);
                                using (var r = await cmdCheck.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        candidatoExistente = (int)r["Id_Boleto_Candidato"];
                                        conteoActual = Convert.ToInt32(r["Total"]);
                                    }
                                }
                            }

                            if (candidatoExistente.HasValue && candidatoExistente.Value != idBoleto)
                            { await trans.RollbackAsync(); return Json(new { exito = false, mensaje = "El proceso ya tiene un boleto candidato fijo. Debes confirmar ese boleto." }); }

                            if (conteoActual >= 3)
                            { await trans.RollbackAsync(); return Json(new { exito = false, mensaje = "Este sorteo ya tiene 3 confirmaciones registradas." }); }

                            // 4. Insertar confirmación (el UNIQUE de BD rechaza duplicados del mismo usuario)
                            string ipCliente = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
                            using (var cmdIns = new NpgsqlCommand(
                                @"INSERT INTO ""Rifas_Sorteo_Confirmaciones""
                                  (""Id_Rifa"", ""Id_Boleto_Candidato"", ""Id_Usuario"", ""IP_Confirmacion"")
                                  VALUES (@idR, @idB, @idU, @ip)", conexion, trans))
                            {
                                cmdIns.Parameters.AddWithValue("@idR", idRifaReal);
                                cmdIns.Parameters.AddWithValue("@idB", idBoleto);
                                cmdIns.Parameters.AddWithValue("@idU", idUsuario);
                                cmdIns.Parameters.AddWithValue("@ip", ipCliente);
                                await cmdIns.ExecuteNonQueryAsync();
                            }

                            int nuevoConteo = conteoActual + 1;

                            // 5. Si se completan 3 confirmaciones: cerrar el sorteo
                            if (nuevoConteo >= 3)
                            {
                                using (var cmdCierre = new NpgsqlCommand(
                                    @"UPDATE ""Rifas"" SET
                                        ""Id_Boleto_Ganador""       = @idB,
                                        ""Estado""                  = 'Finalizada',
                                        ""Modalidad_Sorteo""        = 'PLATAFORMA',
                                        ""Fecha_Sorteo_Confirmado"" = NOW(),
                                        ""Id_Usuario_Sorteo""       = @idU
                                      WHERE ""Id_Rifa"" = @idR", conexion, trans))
                                {
                                    cmdCierre.Parameters.AddWithValue("@idB", idBoleto);
                                    cmdCierre.Parameters.AddWithValue("@idU", idUsuario);
                                    cmdCierre.Parameters.AddWithValue("@idR", idRifaReal);
                                    await cmdCierre.ExecuteNonQueryAsync();
                                }

                                // Bitácora
                                using (var cmdBit = new NpgsqlCommand(
                                    @"INSERT INTO ""Sist_Bitacora"" (""Id_Usuario"", ""Modulo"", ""Accion"", ""Detalle"", ""IP"")
                                      VALUES (@idU, 'Fin_Rifas', 'Sorteo_Plataforma_Completado',
                                              'Ganador: Boleto #' || @idB || ' en Rifa ' || @idR, @ip)", conexion, trans))
                                {
                                    cmdBit.Parameters.AddWithValue("@idU", idUsuario);
                                    cmdBit.Parameters.AddWithValue("@idB", idBoleto);
                                    cmdBit.Parameters.AddWithValue("@idR", idRifaReal);
                                    cmdBit.Parameters.AddWithValue("@ip", ipCliente);
                                    await cmdBit.ExecuteNonQueryAsync();
                                }
                            }

                            await trans.CommitAsync();

                            return Json(new
                            {
                                exito = true,
                                confirmacionesActuales = nuevoConteo,
                                esGanadorFinal = nuevoConteo >= 3,
                                mensaje = nuevoConteo >= 3
                                    ? "¡Sorteo completado! El ganador ha sido registrado."
                                    : $"Confirmación registrada ({nuevoConteo}/3)."
                            });
                        }
                        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23505")
                        {
                            // Violación del UNIQUE: el usuario ya confirmó esta rifa
                            await trans.RollbackAsync();
                            return Json(new { exito = false, mensaje = "Ya confirmaste este sorteo anteriormente." });
                        }
                        catch (Exception exTrans) { await trans.RollbackAsync(); return Json(new { exito = false, mensaje = exTrans.Message }); }
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegistrarSorteoManual(string idRifa, int idBoleto)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de administrador.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idRifaReal = Funciones.DesencriptarId(idRifa);
            if (idRifaReal <= 0) return RedirectToAction("Index");

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Bloquear y validar estado de la rifa
                            string sqlLock = @"SELECT ""Id_Boleto_Ganador"", ""Modalidad_Sorteo"", ""Estado""
                                              FROM ""Rifas"" WHERE ""Id_Rifa"" = @id FOR UPDATE";
                            using (var cmdLock = new NpgsqlCommand(sqlLock, conexion, trans))
                            {
                                cmdLock.Parameters.AddWithValue("@id", idRifaReal);
                                using (var r = await cmdLock.ExecuteReaderAsync())
                                {
                                    if (!await r.ReadAsync())
                                    { await trans.RollbackAsync(); MostrarMensaje("Error", "Rifa no encontrada.", TipoMensaje.Error); return RedirectToAction("SorteoDirectivos", new { id = idRifa }); }

                                    if (r["Id_Boleto_Ganador"] != DBNull.Value)
                                    { await trans.RollbackAsync(); MostrarMensaje("Bloqueado", "Esta rifa ya tiene un ganador registrado.", TipoMensaje.Alerta); return RedirectToAction("SorteoDirectivos", new { id = idRifa }); }

                                    string modalidad = r["Modalidad_Sorteo"]?.ToString();
                                    if (modalidad == "PLATAFORMA")
                                    { await trans.RollbackAsync(); MostrarMensaje("Bloqueado", "Esta rifa ya inició el proceso de sorteo por plataforma.", TipoMensaje.Alerta); return RedirectToAction("SorteoDirectivos", new { id = idRifa }); }

                                    if (r["Estado"].ToString() != "Activa")
                                    { await trans.RollbackAsync(); MostrarMensaje("Bloqueado", "La rifa no está activa.", TipoMensaje.Alerta); return RedirectToAction("SorteoDirectivos", new { id = idRifa }); }
                                }
                            }

                            // 2. Verificar que haya confirmaciones previas de plataforma (no deben existir para sorteo manual)
                            long confPrev = 0;
                            using (var cmdConf = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Rifas_Sorteo_Confirmaciones"" WHERE ""Id_Rifa"" = @id", conexion, trans))
                            {
                                cmdConf.Parameters.AddWithValue("@id", idRifaReal);
                                confPrev = (long)await cmdConf.ExecuteScalarAsync();
                            }
                            if (confPrev > 0)
                            { await trans.RollbackAsync(); MostrarMensaje("Bloqueado", "Ya existe un proceso de confirmación en curso. No se puede registrar un sorteo manual.", TipoMensaje.Alerta); return RedirectToAction("SorteoDirectivos", new { id = idRifa }); }

                            // 3. Verificar que el boleto esté pagado y pertenezca a la rifa
                            bool boletoValido = false;
                            using (var cmdBol = new NpgsqlCommand(
                                @"SELECT COUNT(*) FROM ""Rifas_Boletos"" b
                                  JOIN ""Rifas_Ventas"" v ON b.""Id_Venta"" = v.""Id_Venta""
                                  WHERE b.""Id_Boleto"" = @idB AND b.""Id_Rifa"" = @idR
                                    AND b.""Estado"" = 'Pagado' AND v.""Estado"" = 'Pagado'", conexion, trans))
                            {
                                cmdBol.Parameters.AddWithValue("@idB", idBoleto);
                                cmdBol.Parameters.AddWithValue("@idR", idRifaReal);
                                boletoValido = (long)await cmdBol.ExecuteScalarAsync() > 0;
                            }
                            if (!boletoValido)
                            { await trans.RollbackAsync(); MostrarMensaje("Error", "El boleto seleccionado no es válido o no está pagado.", TipoMensaje.Error); return RedirectToAction("SorteoDirectivos", new { id = idRifa }); }

                            // 4. Registrar ganador manual
                            string ipCliente = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
                            using (var cmdUpd = new NpgsqlCommand(
                                @"UPDATE ""Rifas"" SET
                                    ""Id_Boleto_Ganador""       = @idB,
                                    ""Estado""                  = 'Finalizada',
                                    ""Modalidad_Sorteo""        = 'MANUAL',
                                    ""Fecha_Sorteo_Confirmado"" = NOW(),
                                    ""Id_Usuario_Sorteo""       = @idU
                                  WHERE ""Id_Rifa"" = @idR", conexion, trans))
                            {
                                cmdUpd.Parameters.AddWithValue("@idB", idBoleto);
                                cmdUpd.Parameters.AddWithValue("@idU", idUsuario);
                                cmdUpd.Parameters.AddWithValue("@idR", idRifaReal);
                                await cmdUpd.ExecuteNonQueryAsync();
                            }

                            // 5. Bitácora
                            using (var cmdBit = new NpgsqlCommand(
                                @"INSERT INTO ""Sist_Bitacora"" (""Id_Usuario"", ""Modulo"", ""Accion"", ""Detalle"", ""IP"")
                                  VALUES (@idU, 'Fin_Rifas', 'Sorteo_Manual_Registrado',
                                          'Ganador manual: Boleto #' || @idB || ' en Rifa ' || @idR, @ip)", conexion, trans))
                            {
                                cmdBit.Parameters.AddWithValue("@idU", idUsuario);
                                cmdBit.Parameters.AddWithValue("@idB", idBoleto);
                                cmdBit.Parameters.AddWithValue("@idR", idRifaReal);
                                cmdBit.Parameters.AddWithValue("@ip", ipCliente);
                                await cmdBit.ExecuteNonQueryAsync();
                            }

                            await trans.CommitAsync();
                            MostrarMensaje("Sorteo Registrado", "El ganador manual ha sido registrado exitosamente.", TipoMensaje.Exito);
                        }
                        catch (Exception exTrans) { await trans.RollbackAsync(); MostrarMensaje("Error", exTrans.Message, TipoMensaje.Error); }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("SorteoDirectivos", new { id = idRifa });
        }

        /// <summary>
        /// Carga las confirmaciones actuales del sorteo en el ViewModel y determina si el proceso está en curso.
        /// </summary>
        private async Task CargarConfirmaciones(int idRifaReal, int idUsuarioActual, SorteoDirectivosViewModel modelo, NpgsqlConnection conexion)
        {
            string sql = @"SELECT sc.""Id_Boleto_Candidato"", sc.""Id_Usuario"", sc.""Fecha_Confirmacion"",
                                  u.""NombreCompleto""
                           FROM ""Rifas_Sorteo_Confirmaciones"" sc
                           JOIN ""Sist_Usuarios"" u ON sc.""Id_Usuario"" = u.""Id_Usuario""
                           WHERE sc.""Id_Rifa"" = @id
                           ORDER BY sc.""Fecha_Confirmacion"" ASC";

            using (var cmd = new NpgsqlCommand(sql, conexion))
            {
                cmd.Parameters.AddWithValue("@id", idRifaReal);
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        var conf = new ConfirmacionSorteoItem
                        {
                            IdBoletoCandidato = (int)r["Id_Boleto_Candidato"],
                            IdUsuario = (int)r["Id_Usuario"],
                            NombreUsuario = r["NombreCompleto"].ToString(),
                            FechaConfirmacion = (DateTime)r["Fecha_Confirmacion"]
                        };
                        modelo.Confirmaciones.Add(conf);

                        if (conf.IdUsuario == idUsuarioActual) modelo.UsuarioActualYaConfirmo = true;
                    }
                }
            }

            if (modelo.Confirmaciones.Any())
            {
                modelo.HayProcesoEnCurso = true;
                modelo.IdBoletoCandidato = modelo.Confirmaciones.First().IdBoletoCandidato;
            }
        }
    }
}