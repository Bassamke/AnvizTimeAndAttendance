using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Anviz.Models
{
    public class EmployeePunches
    {
        public int Id { get; set; }
        public DateTime RawPunchTime { get; set; }
        public string PunchType { get; set; }
        public string PunchDevice { get; set; }
        public string EmployeeNumber { get; set; }
        public bool? UploadedToDayforce { get; set; } 
        public string? DayForceResponse { get; set; }
    }
}
