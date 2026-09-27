using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration; // Agregado para leer appsettings
using RedAJP.Globales;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace RedAJP.Clases
{
    public class FiltroGeograficoMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IConfiguration _configuration;

        // Inyectamos IConfiguration en el constructor
        public FiltroGeograficoMiddleware(RequestDelegate next, IConfiguration configuration)
        {
            _next = next;
            _configuration = configuration;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // 1. OBTENER LA CONFIGURACIÓN DEL FILTRO DESDE LA BD
            string cadenaConexion = _configuration.GetConnectionString("MiConexion");
            bool filtroActivo = true; // Por seguridad, asumimos que está encendido por defecto

            try
            {
                // Reutilizamos tu función existente para leer de Sist_Parametros
                filtroActivo = await Funciones.ObtenerParametroBool(cadenaConexion, "pFiltroGeograficoActivo");
            }
            catch (Exception)
            {
                // FAIL-CLOSED: Si la BD falla, mantenemos la seguridad activa para no quedar expuestos.
                filtroActivo = true;
            }

            // 2. SI EL FILTRO ESTÁ APAGADO, DEJAMOS PASAR EL TRÁFICO
            if (!filtroActivo)
            {
                await _next(context);
                return;
            }

            // 3. SI EL FILTRO ESTÁ ACTIVO, EJECUTAMOS LA VALIDACIÓN DE IP
            string ip = ObtenerIpSegura(context);
            bool accesoPermitido = false;

            try
            {
                accesoPermitido = await Funciones.EsIpMexicana(ip);
            }
            catch (Exception)
            {
                // FAIL-CLOSED: Si el Búnker/DB falla, asumimos que NO tiene permiso.
                accesoPermitido = false;
            }

            if (!accesoPermitido)
            {
                context.Response.StatusCode = 403; // Forbidden
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync("<h1>403 - Forbidden</h1>");
                return; // Bloqueo total, no pasa al siguiente middleware
            }

            await _next(context);
        }

        // Helper privado para extracción segura de IP
        private string ObtenerIpSegura(HttpContext context)
        {
            var forwardedHeader = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(forwardedHeader))
            {
                string ipPotencial = forwardedHeader.Split(',')[0].Trim();
                if (System.Net.IPAddress.TryParse(ipPotencial, out _))
                {
                    return ipPotencial;
                }
            }

            var remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp == null) return "127.0.0.1";

            if (remoteIp.IsIPv4MappedToIPv6)
            {
                return remoteIp.MapToIPv4().ToString();
            }

            return remoteIp.ToString();
        }
    }
}