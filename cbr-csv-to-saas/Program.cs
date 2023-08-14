using System;
using System.Configuration;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Net;
using System.IO;
using System.Diagnostics;


namespace cbr_csv_to_saas
{
    class Program
    {
        private static readonly HttpClient httpClient = new HttpClient();

        const string USERS_UPLOAD_URI = "/api/v2/users-import/csv";
        const string STRUCTURE_UPLOAD_URI = "/api/v2/orgstructure/import";

        const string APP_NAME = "Collaborator CSV sync";
        const string BROWSER_ID = "cbr-csv-sync-tool";


        private static readonly List<string> ALLOWED_IMPORT_TYPES = new List<string>() { "users", "structure" };

        static void Main(string[] args)
        {
            try
            {
                Console.WriteLine("Start");

                string filePath = ConfigurationManager.AppSettings["file-path"];
                int splitLargeFileSize = Int32.Parse(ConfigurationManager.AppSettings["split-large-file-size"]);

                string importType = "users";

                if (args.Length > 0)
                {
                    foreach (string arg in Environment.GetCommandLineArgs())
                    {
                        if (arg.IndexOf("--file-path") != -1)
                        {
                            filePath = arg.Split('=')[1];
                        }
                        if (arg.IndexOf("--import-type") != -1)
                        {
                            importType = arg.Split('=')[1];
                        }
                        if (arg.IndexOf("--split-large-file-size") != -1)
                        {
                            splitLargeFileSize = Int32.Parse(arg.Split('=')[1]);
                        }
                    }
                }

                if (!ALLOWED_IMPORT_TYPES.Contains(importType))
                {
                    throw new Exception("Unsuported import type:" + importType);
                }


                Console.WriteLine("Import type: '" + importType + "'");
                if (splitLargeFileSize > 0 && importType == "users")
                {
                    Console.WriteLine(String.Format("Split files bigger that {0} lines to multiple uploads", splitLargeFileSize));
                }

                string apiUrl;
                switch (importType)
                {
                    case "users":
                        apiUrl = USERS_UPLOAD_URI;
                        break;
                    case "structure":
                        apiUrl = STRUCTURE_UPLOAD_URI;
                        break;
                    default:
                        apiUrl = USERS_UPLOAD_URI;
                        break;
                }

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
                if (splitLargeFileSize > 0 && importType == "users")
                {
                    splitAndUploadFile(filePath, splitLargeFileSize, 
                        ConfigurationManager.AppSettings["cbr-server"] + apiUrl,
                        ConfigurationManager.AppSettings["cbr-token"]
                    );
                }
                else
                {
                    Console.WriteLine("Read file " + filePath + " ...");
                    byte[] csvContent = File.ReadAllBytes(filePath);
                    Console.WriteLine("Read file done");

                    Console.WriteLine("Upload csv file to the server ...");
                    Console.WriteLine(uploadFile(ConfigurationManager.AppSettings["cbr-server"] + apiUrl,
                        ConfigurationManager.AppSettings["cbr-token"], csvContent, "file.csv", "text/csv"));
                }

                Console.WriteLine("Upload csv file done");

                Console.WriteLine("All done");

                //Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());

                try
                {
                    if (!EventLog.SourceExists(APP_NAME))
                    {
                        EventLog.CreateEventSource(APP_NAME, "Application");
                    }
                    EventLog.WriteEntry(APP_NAME, ex.ToString(), EventLogEntryType.Error, 101);
                }
                catch (Exception innerEx)
                { }


                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        private static void splitAndUploadFile(string filePath, int splitAfterSize, string actionUrl, string authToken)
        {

            Console.WriteLine("Read file " + filePath + " ...");
            int i = 0;
            int l = 0;
            int f = 0;
            string line;
            string firstLine = String.Empty;
            List<string> lines = new List<string>();

            using (var infile = new System.IO.StreamReader(filePath))
            {
                while (!infile.EndOfStream)
                {
                    line = infile.ReadLine();
                    if (i == 0)
                    {
                        firstLine = line;
                    }
                    i++;
                    if (l++ > splitAfterSize)
                    {
                        string text = String.Join(Environment.NewLine, lines);
                        f++;
                        Console.WriteLine(String.Format("Upload csv file (part #{0}) to the server ...", f));
                        Console.WriteLine(uploadFile(actionUrl, authToken, System.Text.Encoding.UTF8.GetBytes(text), String.Format("file{0}.csv", f), "text/csv"));
                        lines.Clear();
                        lines.Add(firstLine);
                        l = 0;
                        continue;
                    }
                    lines.Add(line);
                }
            }

            if (lines.Count > 1)
            {
                f++;
                string text = String.Join(Environment.NewLine, lines);
                Console.WriteLine(String.Format("Upload csv file (part #{0}) to the server ...", f));
                Console.WriteLine(uploadFile(actionUrl, authToken, System.Text.Encoding.UTF8.GetBytes(text), String.Format("file{0}.csv", f), "text/csv"));
            }
        }

        private static string uploadFile(string actionUrl, string authToken, byte[] fileContent, string fileName, string fileMimeType, string uid = null)
        {
            Dictionary<string, object> postParameters = new Dictionary<string, object>();
            if (uid != null)
            {
                postParameters.Add("uid", uid);
            }
            postParameters.Add("file",
                new FormUpload.FileParameter(fileContent, fileName, fileMimeType));

            // Create request and receive response
            HttpWebResponse webResponse =
                FormUpload.MultipartFormDataPost(actionUrl, authToken, BROWSER_ID, postParameters);

            string fullResponse = String.Empty;
            if (webResponse != null)
            {
                // Process response
                StreamReader responseReader = new StreamReader(webResponse.GetResponseStream());
                fullResponse = responseReader.ReadToEnd();
                webResponse.Close();
                //Response.Write(fullResponse);
            }

            return fullResponse;
        }
    }

    // Implements multipart/form-data POST in C# http://www.ietf.org/rfc/rfc2388.txt
    // http://www.briangrinstead.com/blog/multipart-form-post-in-c
    public static class FormUpload
    {
        private static readonly Encoding encoding = Encoding.UTF8;
        public static HttpWebResponse MultipartFormDataPost(string postUrl, string authToken, string userAgent, Dictionary<string, object> postParameters)
        {
            string formDataBoundary = String.Format("----------{0:N}", Guid.NewGuid());
            string contentType = "multipart/form-data; boundary=" + formDataBoundary;

            byte[] formData = GetMultipartFormData(postParameters, formDataBoundary);

            return PostForm(postUrl, authToken, userAgent, contentType, formData);
        }

        private static HttpWebResponse PostForm(string postUrl, string authToken, string userAgent, string contentType, byte[] formData)
        {
            HttpWebRequest request = WebRequest.Create(postUrl) as HttpWebRequest;
            if (request == null)
            {
                throw new NullReferenceException("request is not a http request");
            }

            // Set up the request properties.
            request.Method = "POST";
            request.ContentType = contentType;
            request.UserAgent = userAgent;
            //request.CookieContainer = new CookieContainer();
            request.ContentLength = formData.Length;
            request.Timeout = 60 * 60 * 1000;
            request.Headers.Add("Cache-Control", "no-cache");
            request.Headers.Add("Pragma", "no-cache");
            request.Headers.Add("X-Cbr-Authorization", "Bearer " + authToken);

            // Send the form data to the request.
            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(formData, 0, formData.Length);
                requestStream.Close();
            }

            try
            {
                return request.GetResponse() as HttpWebResponse;
            }
            catch (WebException ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
                using (var stream = ex.Response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    Console.WriteLine(reader.ReadToEnd());
                }
            }

            return null;
        }

        private static byte[] GetMultipartFormData(Dictionary<string, object> postParameters, string boundary)
        {
            Stream formDataStream = new System.IO.MemoryStream();
            bool needsCLRF = false;

            foreach (var param in postParameters)
            {
                // Thanks to feedback from commenters, add a CRLF to allow multiple parameters to be added.
                // Skip it on the first parameter, add it to subsequent parameters.
                if (needsCLRF)
                    formDataStream.Write(encoding.GetBytes("\r\n"), 0, encoding.GetByteCount("\r\n"));

                needsCLRF = true;

                if (param.Value is FileParameter)
                {
                    FileParameter fileToUpload = (FileParameter)param.Value;

                    // Add just the first part of this param, since we will write the file data directly to the Stream
                    string header = string.Format("--{0}\r\nContent-Disposition: form-data; name=\"{1}\"; filename=\"{2}\"\r\nContent-Type: {3}\r\n\r\n",
                        boundary,
                        param.Key,
                        fileToUpload.FileName ?? param.Key,
                        fileToUpload.ContentType ?? "application/octet-stream");

                    formDataStream.Write(encoding.GetBytes(header), 0, encoding.GetByteCount(header));

                    // Write the file data directly to the Stream, rather than serializing it to a string.
                    formDataStream.Write(fileToUpload.File, 0, fileToUpload.File.Length);
                }
                else
                {
                    string postData = string.Format("--{0}\r\nContent-Disposition: form-data; name=\"{1}\"\r\n\r\n{2}",
                        boundary,
                        param.Key,
                        param.Value);
                    formDataStream.Write(encoding.GetBytes(postData), 0, encoding.GetByteCount(postData));
                }
            }

            // Add the end of the request.  Start with a newline
            string footer = "\r\n--" + boundary + "--\r\n";
            formDataStream.Write(encoding.GetBytes(footer), 0, encoding.GetByteCount(footer));

            // Dump the Stream into a byte[]
            formDataStream.Position = 0;
            byte[] formData = new byte[formDataStream.Length];
            formDataStream.Read(formData, 0, formData.Length);
            formDataStream.Close();

            return formData;
        }

        public class FileParameter
        {
            public byte[] File { get; set; }
            public string FileName { get; set; }
            public string ContentType { get; set; }
            public FileParameter(byte[] file) : this(file, null) { }
            public FileParameter(byte[] file, string filename) : this(file, filename, null) { }
            public FileParameter(byte[] file, string filename, string contenttype)
            {
                File = file;
                FileName = filename;
                ContentType = contenttype;
            }
        }
    }
}
