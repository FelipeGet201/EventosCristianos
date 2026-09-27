using System;
using System.Collections.Generic;

namespace RedAJP.Models
{
    public class StripeCuentasViewModel
    {
        public List<DetalleCuentaStripe> Cuentas { get; set; } = new List<DetalleCuentaStripe>();
    }

    public class DetalleCuentaStripe
    {
        public string IdCuenta { get; set; }
        public string NombreMostrar { get; set; }
        public string TipoUso { get; set; }
        public string Estado { get; set; }
        public string Moneda { get; set; }

        // Hacemos la fecha nullable para poder ocultar el "1970"
        public DateTime? FechaCreacion { get; set; }

        // Balances Reales
        public decimal SaldoDisponible { get; set; }
        public decimal SaldoPendiente { get; set; }

        // Parámetros Operativos Reales
        public string FrecuenciaPayouts { get; set; }
        public string DescriptorCargo { get; set; }
    }
}