using Anviz.Models;
using Anviz.SDK;
using Anviz.SDK.Responses;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Mail;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Anviz.SDK.Utils;

namespace Anviz
{
    class Program
    {
        private const ulong DEVICE_ID = 1;
        private const string DEVICE_HOST = "192.168.167.222";
        public static AnvizDevice device;
        public static List<BiometricDevices> BiometricDevices = new List<BiometricDevices>();
        static async Task Main(string[] args)
        {
            await connectDevices();
        }

        private static async Task connectDevices()
        {
            var currentDeviceIp = "";
            try
            {
                await getDevices();
                foreach (BiometricDevices biometricDevice in BiometricDevices)
                {
                    currentDeviceIp = biometricDevice.IPAddress;
                    Console.WriteLine(biometricDevice.IPAddress);
                    var manager = new AnvizManager();
                    device = await manager.Connect(biometricDevice.IPAddress);

                    if (device.DeviceId != 0)
                    {
                        Console.WriteLine("Connected to reader." + device.DeviceId);
                        //var users = await device.GetEmployeesData();
                        await deleteDeactivatedEmployees(biometricDevice.Location);
                        await deleteTransferedEmployees(biometricDevice.Location);
                        await getRawPunches(biometricDevice.IPAddress);
                        await checkNewEmployees(biometricDevice.IPAddress, biometricDevice.Location);
                        await uploadEmployeesToMultipleLocations(biometricDevice.Location);
                        await refreshFingerPrints();
                        await enrollFingerPrints(biometricDevice.Location);
                        await enrollFingerPrintsMultipleDevices(biometricDevice.Location);
                        await checkFaceTemplates();
                        device.Dispose();
                    }
                    else
                    {
                        Console.WriteLine("Failed to connect to reader.");
                    }
                }
                Thread.Sleep(300000);
            }

            catch (Exception ex)
            {
                await updateLastFailTime(currentDeviceIp);
                Console.WriteLine(ex.Message + "STACKTRACE " + ex.StackTrace);
            }

            await connectDevices();
        }
        public static async Task refreshFingerPrints()
        {
            try
            {
                var users = await device.GetEmployeesData();
                Console.WriteLine("Registered on Biometric " + users.Count);
                foreach (UserInfo user in users)
                {
                    using (DbDataContext db = new DbDataContext())
                    {
                        foreach (var f in user.EnrolledFingerprints)
                        {
                            var fp = await device.GetFingerprintTemplate(user.Id, f);
                            string finger = f.ToString();
                            bool found = (from s in db.employeeFingerPrints.Where(f => f.EmployeeNumber == user.Id.ToString().PadLeft(5, '0') && f.Finger == finger) select s).Any();
                            if (!found)
                            {
                                EmployeeFingerPrints fingerPrints = new EmployeeFingerPrints();
                                fingerPrints.FingerPrint = Convert.ToBase64String(fp);
                                //Console.WriteLine($"-> {f} {Convert.ToBase64String(fp)}");
                                fingerPrints.EmployeeNumber = user.Id.ToString().PadLeft(5, '0');
                                fingerPrints.Finger = finger;
                                db.employeeFingerPrints.Add(fingerPrints);
                            }
                            else
                            {

                            }
                        }
                        await db.SaveChangesAsync();
                    }
                    //Console.WriteLine("Saved Fingerprints for " + user.Id);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to save fingerprints" + ex.Message);
            }

        }
        public static async Task checkFaceTemplates()
        {
            try
            {
                var users = await device.GetEmployeesData();
                foreach (UserInfo user in users)
                {

                    var faceTemplate = await device.GetFaceTemplate(user.Id);
                    using (DbDataContext db = new DbDataContext())
                    {
                        var employee = (from s in db.employeeData.Where(f => f.EmployeeNumber == user.Id.ToString().PadLeft(5, '0')) select s).FirstOrDefault();
                        if (employee != null)
                        {
                            employee.FaceTemplate = Convert.ToBase64String(faceTemplate);
                            await db.SaveChangesAsync();
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine("upload facetemplate failed " + ex.Message);
            }
        }
        public static async Task getRawPunches(string deviceIp)
        {
            var records = await device.DownloadRecords(true);
            await insertRawPunches(records, deviceIp);
            await device.ClearNewRecords();
        }
        public static async Task insertRawPunches(List<Record> records, string deviceIp)
        {
            try
            {
                Console.WriteLine("found " + records.Count + " attedance records for " + deviceIp);
                using (DbDataContext db = new DbDataContext())
                {
                    foreach (Record record in records)
                    {
                        EmployeePunches employeePunch = new EmployeePunches()
                        {
                            EmployeeNumber = record.UserCode.ToString().PadLeft(5, '0'),
                            RawPunchTime = record.DateTime,
                            PunchDevice = deviceIp,
                            PunchType = record.RecordType.ToString(),
                            UploadedToDayforce = false

                        };

                        db.employeePunches.Add(employeePunch);

                    }
                    await db.SaveChangesAsync();

                }
                Console.WriteLine("Succesfully saved " + records.Count + " attedance records for " + deviceIp);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Adding records failed" + ex.Message);
            }
            await countPunchesNotUploadedToDayforce();
        }
        public static async Task countPunchesNotUploadedToDayforce()
        {
            var notUploadedToDayforce = 0;
            using (DbDataContext db = new DbDataContext())
            {
                notUploadedToDayforce = (from b in db.employeePunches.Where(b => b.UploadedToDayforce == false) select b).Count();

            }
            if (notUploadedToDayforce > 100) {
                await sendEmail("NOT UPLOADING PUNCHES", "There are " + notUploadedToDayforce + " records not uplaoded to Dayforce. Kindly check the Dayforce Intgration application");
            }
        }
        public static async Task getDevices()
        {
            using (DbDataContext db = new DbDataContext())
            {
                bool found = (from s in db.biometricDevices select s).Any();
                if (found)
                {
                    BiometricDevices = (from b in db.biometricDevices.Where(b => b.LastFailDateTime < DateTime.Now.AddMinutes(-30))
                                        select b).ToList();
                }
                else
                {

                }
            }
        }
        public static async Task updateLastFailTime(string deviceIp)
        {
            var deviceLocation = "";
            using (DbDataContext db = new DbDataContext())
            {
                var BiometricDevice = (from b in db.biometricDevices.Where(b => b.IPAddress == deviceIp)
                                       select b).FirstOrDefault();
                deviceLocation = BiometricDevice.Location;
                BiometricDevice.LastFailDateTime = DateTime.Now;
                await db.SaveChangesAsync();


            }
            await sendEmail("BIOMETRIC FAILURE", "Could not connect to " + deviceLocation + " Kindly check on Biometric");
        }

        public static async void FacepassExample()
        {
            var manager = new AnvizManager();
            ulong employee_id = 5; // number of employee ID, Example 5
            using (var device = await manager.Accept())
            {
                var fp = await device.EnrollFingerprint(employee_id, 1);
                var employee = new SDK.Responses.UserInfo(employee_id, "name of employee");
                await device.SetEmployeesData(employee);
                await device.SetFaceTemplate(employee.Id, fp);
            }
        }
        public static async Task checkNewEmployees(string deviceIp, string location)
        {
            var employees = await getEmployeesByLocation(location);
            Console.WriteLine("found :" + employees.Count);
            List<UserInfo> usersToUpload = new List<UserInfo>();
            foreach (EmployeeData employee in employees)
            {
                long employeeNumber = long.Parse(employee.EmployeeNumber);
                string[] splitName = employee.DisplayName.Split(',');
                string name = splitName[0];
                name = name.Replace("'", "");
                var anvizEmployee = new UserInfo((ulong)(employeeNumber), name);
                usersToUpload.Add(anvizEmployee);
            }
            await uploadToAnviz(usersToUpload, deviceIp);
        }

        public async static Task<List<EmployeeData>> getEmployeesByLocation(string location)
        {
            List<EmployeeData> employees = new List<EmployeeData>();
            using (DbDataContext db = new DbDataContext())
            {
                employees = (from b in db.employeeData.Where(b => b.Location == location.ToUpper().Trim() && b.Uploaded == false && b.Active == true)
                             select b).ToList();

            }
            return employees;
        }
        public async static Task uploadEmployeesToMultipleLocations(string location)
        {
            List<EmployeeLocations> employees = new List<EmployeeLocations>();
            using (DbDataContext db = new DbDataContext())
            {
                employees = (from b in db.employeeLocations.Where(b => b.Location == location.ToUpper().Trim() && b.Uploaded == false)
                             select b).ToList();

            }

            List<UserInfo> usersToUpload = new List<UserInfo>();
            foreach (EmployeeLocations employee in employees)
            {
                long employeeNumber = long.Parse(employee.EmployeeNumber);
                string[] splitName = employee.Name.Split(',');
                string name = splitName[0];
                name = name.Replace("'", "");
                var anvizEmployee = new UserInfo((ulong)(employeeNumber), name);
                usersToUpload.Add(anvizEmployee);
            }
            await uploadToAnviz(usersToUpload, location);
        }
    

        public static async Task deleteDeactivatedEmployees(string location)
        {
            List<EmployeeData> employees = new List<EmployeeData>();
            using (DbDataContext db = new DbDataContext())
            {
                employees = (from b in db.employeeData.Where(b => b.Location == location.ToUpper().Trim() && b.Active == false)
                             select b).ToList();

            }
            foreach(EmployeeData employee in employees)
            {
                await device.DeleteEmployeesData((ulong)Convert.ToDecimal(employee.EmployeeNumber));
            }
        }
        public static async Task deleteTransferedEmployees(string location)
        {
            List<EmployeeData> employees = new List<EmployeeData>();
            using (DbDataContext db = new DbDataContext())
            {
                employees = (from b in db.employeeData.Where(b => b.OldLocation == location.ToUpper().Trim() && b.DeletedFromOldLocation == false)
                             select b).ToList();
                foreach (EmployeeData employee in employees)
                {
                    await device.DeleteEmployeesData((ulong)Convert.ToDecimal(employee.EmployeeNumber));
                    employee.DeletedFromOldLocation = true;
                }
                await db.SaveChangesAsync();
            }
            
        }
        public static async Task markAlreadyUploaded()
        {
            try
            {
                var users = await device.GetEmployeesData();
                Console.WriteLine("checking :   " + users.Count);

                foreach (UserInfo user in users)
                {
                    //Console.WriteLine("checking :   " + user.Id.ToString().Trim().PadLeft(5, '0'));
                    using (DbDataContext db = new DbDataContext())
                    {

                        Console.WriteLine("checking :   " + user.Id);
                        var userInDatabase = (from s in db.employeeData.Where(s => s.EmployeeNumber == (user.Id.ToString().Trim().PadLeft(5, '0'))) select s).SingleOrDefault();
                        if (userInDatabase != null)
                        {
                            userInDatabase.Uploaded = true;
                            await db.SaveChangesAsync();
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine ("Failed:   " + ex.Message + " StackTrace: " + ex.StackTrace);
            }
        }
        public static async Task uploadToAnviz(List<UserInfo> users, string IpAddress)
        {
            try
            {
                if(users.Count== 0) return; 
                await device.SetEmployeesData(users);
                Console.WriteLine("Successs " + IpAddress);
                //mark as uploaded after uploading the fingerprints
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed: " + IpAddress + " " + ex.Message);
            }
        }
        public static async Task enrollFingerPrints(string location)
        {
            var users = await getEmployeesByLocation(location);
            try
            {
                foreach(EmployeeData user in users) 
                {
                    Console.WriteLine("Enrolling Fingeprints to device for " + user.EmployeeNumber);
                    var fingerprints = await getFingerPrintsFromDatabase(user.EmployeeNumber);
                    Console.WriteLine("found " + fingerprints.Count +" fingers");
                    foreach(EmployeeFingerPrints fingerprint in fingerprints)
                    {
                        Console.WriteLine("Enrolling " + fingerprint.Finger + "Fingeprints to device for " + user.EmployeeNumber);
                        var byteFingerprint = Convert.FromBase64String(fingerprint.FingerPrint);
                        await device.SetFingerprintTemplate((ulong)Convert.ToDecimal(user.EmployeeNumber), (Finger)Enum.Parse(typeof (Finger),(fingerprint.Finger)),byteFingerprint);
                    }
                }
                await markAlreadyUploaded();
            }
            catch(Exception ex) 
            {
                Console.WriteLine("Failed to Enroll Fingeprints to device "+ ex.Message);
            }
        }
        public static async Task<List<EmployeeFingerPrints>> getFingerPrintsFromDatabase(string userId)
        {
            List<EmployeeFingerPrints> employeeFingerPrints = new List<EmployeeFingerPrints>();
            try
            {
                using (DbDataContext db = new DbDataContext())
                {
                    bool found = (from s in db.employeeFingerPrints select s).Any();
                    if (found)
                    {
                        employeeFingerPrints = (from b in db.employeeFingerPrints.Where(b => b.EmployeeNumber == (userId.Trim().PadLeft(5, '0')))
                                                select b).ToList();
                    }
                    else
                    {

                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("failed to get employee fingerprints from database " + ex.Message);
            }
            return employeeFingerPrints;
        }
        public static async Task enrollFingerPrintsMultipleDevices(string location)
        {
            List<EmployeeLocations> employees = new List<EmployeeLocations>();
            using (DbDataContext db = new DbDataContext())
            {
                employees = (from b in db.employeeLocations.Where(b => b.Location == location.ToUpper().Trim() && b.Uploaded == false)
                             select b).ToList();

            }
            try
            {
                foreach (EmployeeLocations user in employees)
                {
                    Console.WriteLine("Enrolling Fingeprints to device for " + user.EmployeeNumber);
                    var fingerprints = await getFingerPrintsFromDatabase(user.EmployeeNumber);
                    Console.WriteLine("found " + fingerprints.Count + " fingers");
                    foreach (EmployeeFingerPrints fingerprint in fingerprints)
                    {
                        Console.WriteLine("Enrolling " + fingerprint.Finger + "Fingeprints to device for " + user.EmployeeNumber);
                        var byteFingerprint = Convert.FromBase64String(fingerprint.FingerPrint);
                        await device.SetFingerprintTemplate((ulong)Convert.ToDecimal(user.EmployeeNumber), (Finger)Enum.Parse(typeof(Finger), (fingerprint.Finger)), byteFingerprint);
                    }
                }
                await markAlreadyUploadedMultipleDevices(location);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to Enroll Fingeprints to device " + ex.Message);
            }
        }
        public static async Task markAlreadyUploadedMultipleDevices(string location)
        {
            try
            {
                var users = await device.GetEmployeesData();
                Console.WriteLine("checking :   " + users.Count);

                foreach (UserInfo user in users)
                {
                    //Console.WriteLine("checking :   " + user.Id.ToString().Trim().PadLeft(5, '0'));
                    using (DbDataContext db = new DbDataContext())
                    {
                        var userInDatabase = (from s in db.employeeLocations.Where(s => s.EmployeeNumber == (user.Id.ToString().Trim().PadLeft(5, '0')) && s.Location==location) select s).SingleOrDefault();
                        if (userInDatabase != null)
                        {
                            userInDatabase.Uploaded = true;
                            await db.SaveChangesAsync();
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed:   " + ex.Message + " StackTrace: " + ex.StackTrace);
            }
        }
        public static async Task sendEmail( string subject, string emailMmessage)
        {
            try
            {
                MailMessage message = new MailMessage();
                string emailAccount = "";
                string password = "";
                int port = 25;
                string smtpServer = "";
                bool useSsl = false;
                string receiverEmail = "";

                using (DbDataContext db = new DbDataContext())
                {

                    var emailsettings = (from s in db.emailSettings select s).FirstOrDefault();
                    emailAccount = emailsettings.Emailaccount;
                    password = emailsettings.Password;
                    port = emailsettings.Port;
                    useSsl = emailsettings.UseSSL;
                    smtpServer = emailsettings.Smtp;
                    receiverEmail = emailsettings.ReceiverEmail;



                }

                SmtpClient smtp = new SmtpClient();
                message.From = new MailAddress(emailAccount, "BIOMETRIC DEVICE ALERT");
                message.To.Add(receiverEmail);
                message.Subject = subject;  
                message.IsBodyHtml = true; //to make message body as html  
                message.Body = emailMmessage; 
                smtp.UseDefaultCredentials = false;
                smtp.Port = port;
                smtp.Host = smtpServer; //for gmail host;
                smtp.EnableSsl = useSsl;
                smtp.Credentials = new NetworkCredential(emailAccount, password);
                smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                string userState = "test message1";

                await smtp.SendMailAsync(message);
            }
            catch(Exception ex)
            {
                Console.WriteLine("Email sending failed");
            }
        }
    }
}
