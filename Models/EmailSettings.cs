using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Anviz.Models
{
    public class EmailSettings
    {
        public int Id { get; set; }
        public string Emailaccount { get; set; }    
        public string Smtp { get; set; }    
        public int Port { get; set; }    
        public string Password { get; set; }    
        public bool UseSSL { get; set; }    
        public string ReceiverEmail { get; set; }   
    }
}
