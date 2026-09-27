using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using RedAJP.Models;
using RedAJP.Globales;
using Stripe;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RedAJP.Controllers
{
    [Authorize]
    public class CuentasStripeController : GlobalController
    {
        private readonly IConfiguration _configuration;
        private readonly Parametros.Modulo _modulo = Parametros.Modulos.CuentasStripe;

        public CuentasStripeController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task<IActionResult> Index()
        {
            // CORRECCIÓN: Permiso de Lectura
            if (!User.TienePermiso(_modulo, Parametros.Permisos.Leer))
            {
                MostrarMensaje("Acceso Denegado", "No cuentas con las autorizaciones de lectura necesarias para auditar esta sección.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            // LISTA DINÁMICA: Agregamos las tres llaves (Eventos, Tienda y Rifas)
            var configuraciones = new List<(string Modulo, string Key)>
    {
        ("Eventos", _configuration["StripeEventos:SecretKey"]),
        ("Tienda", _configuration["StripeTienda:SecretKey"]),
        ("Rifas", _configuration["StripeRifas:SecretKey"])
    };

            var modelo = new StripeCuentasViewModel();
            var cuentasAgrupadas = new Dictionary<string, DetalleCuentaStripe>();

            try
            {
                foreach (var config in configuraciones)
                {
                    var datos = await ObtenerMetricasStripe(config.Key, config.Modulo);

                    if (datos != null && !string.IsNullOrEmpty(datos.IdCuenta))
                    {
                        if (cuentasAgrupadas.ContainsKey(datos.IdCuenta))
                        {
                            // Si el IdCuenta ya existe, concatenamos el nombre del módulo actual
                            cuentasAgrupadas[datos.IdCuenta].TipoUso += $" & {config.Modulo}";
                        }
                        else
                        {
                            // Si es una cuenta nueva, la añadimos al diccionario
                            cuentasAgrupadas.Add(datos.IdCuenta, datos);
                        }
                    }
                }

                // Pasamos el listado de cuentas ya unificadas a la vista
                modelo.Cuentas = cuentasAgrupadas.Values.ToList();
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudieron consultar los datos de Stripe: " + ex.Message, TipoMensaje.Error);
            }

            return View(modelo);
        }

        private async Task<DetalleCuentaStripe> ObtenerMetricasStripe(string secretKey, string contexto)
        {
            if (string.IsNullOrWhiteSpace(secretKey)) return null;

            var clienteEspecial = new StripeClient(secretKey);
            var info = new DetalleCuentaStripe { TipoUso = contexto };

            var accountService = new AccountService(clienteEspecial);
            var cuentaStripe = await accountService.GetAsync("self");

            info.IdCuenta = cuentaStripe.Id;
            info.NombreMostrar = cuentaStripe.Settings?.Dashboard?.DisplayName ?? cuentaStripe.BusinessProfile?.Name ?? "Pasarela Stripe";
            info.Moneda = cuentaStripe.DefaultCurrency?.ToUpper() ?? "MXN";
            info.Estado = cuentaStripe.ChargesEnabled ? "ACTIVA" : "RESTRINGIDA";

            // CORRECCIÓN: Si Stripe devuelve la fecha base (Epoch = 1970), la anulamos para no mostrar mentiras
            info.FechaCreacion = cuentaStripe.Created.Year > 1970 ? cuentaStripe.Created : (DateTime?)null;

            // Configuraciones Reales
            string intervalo = cuentaStripe.Settings?.Payouts?.Schedule?.Interval;
            info.FrecuenciaPayouts = intervalo switch
            {
                "daily" => "Diaria",
                "weekly" => "Semanal",
                "monthly" => "Mensual",
                "manual" => "Manual (Bajo Demanda)",
                _ => "No configurada"
            };

            info.DescriptorCargo = cuentaStripe.Settings?.CardPayments?.StatementDescriptorPrefix
                                ?? cuentaStripe.Settings?.Payments?.StatementDescriptor
                                ?? "No configurado";

            // Saldos Reales
            var balanceService = new BalanceService(clienteEspecial);
            var balanceReal = await balanceService.GetAsync();

            info.SaldoDisponible = (balanceReal.Available.FirstOrDefault()?.Amount ?? 0) / 100m;
            info.SaldoPendiente = (balanceReal.Pending.FirstOrDefault()?.Amount ?? 0) / 100m;

            return info;
        }
    }
}