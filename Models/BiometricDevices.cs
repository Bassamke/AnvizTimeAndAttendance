using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Anviz.Models
{
    public class BiometricDevices
    {
        public int Id { get; set; }
        public string IPAddress { get; set; }   
        public string Location { get; set; }
        public DateTime LastFailDateTime { get; set; }
    }
}
