using System;
using System.Configuration;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Threading.Tasks;
using log4net;
using SharpCompress.Compressors.Xz;

namespace MissionPlanner.Utilities
{
    public static class ParameterMetaDataRepositoryAPMpdef
    {
        private static readonly ILog log =
            LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static Dictionary<string,XDocument> _parameterMetaDataXML = new Dictionary<string, XDocument>();

        private static XDocument _localParameterMetaDataXML;

        internal const string LocalParameterMetaDataFileName = "ParameterMetaDataLocal.xml";

        private static string[] vehicles = new[]
        {
             "SITL", "AP_Periph", "ArduSub", "Rover", "ArduCopter",
            "ArduPlane", "AntennaTracker", "Blimp", "Heli"      
        };

        private static string[] vehicles_versioned = new[] 
        {
            "Copter", "Plane", "Rover", "Sub", "Tracker"
        };

        static string url = "https://autotest.ardupilot.org/Parameters/{0}/apm.pdef.xml.gz";

        static string urlversioned = "https://autotest.ardupilot.org/Parameters/versioned/{0}/stable-{1}/apm.pdef.xml";

        static ParameterMetaDataRepositoryAPMpdef()
        {
            ReloadLocal();
            _ = GetMetaData();
        }

        private static void ReloadLocal()
        {
            var fileName = Path.Combine(Settings.GetRunningDirectory(), LocalParameterMetaDataFileName);

            try
            {
                if (File.Exists(fileName))
                    _localParameterMetaDataXML = XDocument.Load(fileName);
            }
            catch (Exception ex)
            {
                log.Error(fileName);
                log.Error(ex);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ParameterMetaDataRepository"/> class.
        /// </summary>
        public static void CheckLoad(string vehicle = "")
        {
            if (!_parameterMetaDataXML.ContainsKey(vehicle))
                Reload(vehicle);
        }

        public static async Task GetMetaDataVersioned(Version version)
        {
            List<Task> tlist = new List<Task>();

            vehicles_versioned.ForEach(a =>
            {
                try
                {
                    var newurl = String.Format(urlversioned, a, version.ToString());
                    var file = Path.Combine(Settings.GetDataDirectory(), a + version.ToString() + ".apm.pdef.xml");
                    if (File.Exists(file))
                        if (new FileInfo(file).LastWriteTime.AddDays(7) > DateTime.Now)
                            return;
                    var dltask = Download.getFilefromNetAsync(newurl, file);
                    tlist.Add(dltask);
                }
                catch (Exception ex) { log.Error(ex); }
            });

            await Task.WhenAll(tlist);

            vehicles_versioned.ForEach(a =>
            {
                try
                {
                    Reload(a + version.ToString());

                    var veh = vehicles.First(b => b.Contains(a));

                    if(_parameterMetaDataXML.ContainsKey(a + version.ToString()))
                        _parameterMetaDataXML[veh] = _parameterMetaDataXML[a + version.ToString()];
                }
                catch (Exception ex) { log.Error(ex); }
            });
        }

        public static async Task GetMetaData(bool force = false)
        {
            List<Task> tlist = new List<Task>();

            vehicles.ForEach(a =>
            {
                try
                {
                    var newurl = String.Format(url, a);
                    // try the gzipped version first
                    var file = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml.gz");
                    if(File.Exists(file))
                        if (new FileInfo(file).LastWriteTime.AddDays(7) > DateTime.Now && !force)
                            return;
                    // try just the xml
                    var file2 = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml");
                    if (File.Exists(file2))
                        if (new FileInfo(file2).LastWriteTime.AddDays(7) > DateTime.Now && !force)
                            return;
                    var dltask = Download.getFilefromNetAsync(newurl, file);
                    tlist.Add(dltask);
                }
                catch (Exception ex) { log.Error(ex); }
            });

            await Task.WhenAll(tlist);

            vehicles.ForEach(a =>
            {
                try
                {
                    var fileout = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml");
                    var fileouttemp = Path.Combine(Path.GetTempFileName());
                    var file = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml.gz");
                    if (File.Exists(file))
                    {
                        // drop out to prevent unnessary fileio at startup
                        if (File.Exists(fileout) && new FileInfo(fileout).LastWriteTime.AddDays(7) > DateTime.Now && !force)
                            return;
                        using (var read = File.OpenRead(file))
                        {
                            //if (XZStream.IsXZStream(read))
                            {
                                read.Position = 0;
                                var stream = new GZipStream(read, CompressionMode.Decompress);
                                //var stream = new XZStream(read);
                                using (var outst = File.Open(fileouttemp, FileMode.Create))
                                {
                                    stream.CopyTo(outst);
                                }
                                // move after good decompress
                                File.Delete(fileout);
                                File.Move(fileouttemp, fileout);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                }
            });

            Reset();
        }

        public static void Reset()
        {
            _parameterMetaDataXML.Clear();
        }

        public static void Reload(string vehicle = "")
        {
            string paramMetaDataXMLFileName =
                String.Format("{0}{1}", Settings.GetDataDirectory(), vehicle + ".apm.pdef.xml");

            try
            {
                if (File.Exists(paramMetaDataXMLFileName))
                {
                    _parameterMetaDataXML[vehicle] = XDocument.Load(paramMetaDataXMLFileName);
                }

            }
            catch (System.Xml.XmlException ex) 
            {
                try
                {
                    if (File.Exists(paramMetaDataXMLFileName))
                        File.Delete(paramMetaDataXMLFileName);
                }
                catch { }
                log.Error(paramMetaDataXMLFileName);
                log.Error(ex);
            }
            catch (Exception ex)
            {
                log.Error(paramMetaDataXMLFileName);
                log.Error(ex);
            }
        }

        /// <summary>
        /// Gets the parameter meta data.
        /// </summary>
        /// <param name="nodeKey">The node key.</param>
        /// <param name="metaKey">The meta key.</param>
        /// <returns></returns>
        public static string GetParameterMetaData(string nodeKey, string metaKey, string vechileType)
        {
            // remap names
            if (vechileType == "ArduCopter2")
                vechileType = "ArduCopter";
            if (vechileType == "ArduRover")
                vechileType = "Rover";
            if (vechileType == "ArduTracker")
                vechileType = "AntennaTracker";

            CheckLoad(vechileType);

            // remap keys
            if (metaKey == ParameterMetaDataConstants.DisplayName)
                metaKey = "humanName";
            if (metaKey == ParameterMetaDataConstants.Description)
                metaKey = "documentation";
            if (metaKey == ParameterMetaDataConstants.User)
                metaKey = "user";

            _parameterMetaDataXML.TryGetValue(vechileType, out var downloadedParameterMetaData);

            try
            {
                return ParameterMetaDataPdefReader.ResolveParameterMetaData(
                    _localParameterMetaDataXML,
                    downloadedParameterMetaData,
                    nodeKey,
                    metaKey,
                    vechileType);
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }

            return string.Empty;
        }
    }

    internal static class ParameterMetaDataPdefReader
    {
        internal static string ResolveParameterMetaData(
            XDocument localParameterMetaData,
            XDocument downloadedParameterMetaData,
            string nodeKey,
            string metaKey,
            string vehicleType)
        {
            var localAnswer = ReadParameterMetaData(
                localParameterMetaData, nodeKey, metaKey, vehicleType);
            return localAnswer != string.Empty
                ? localAnswer
                : ReadParameterMetaData(downloadedParameterMetaData, nodeKey, metaKey, vehicleType);
        }

        private static string ReadParameterMetaData(
            XDocument parameterMetaData,
            string nodeKey,
            string metaKey,
            string vehicleType)
        {
            var root = parameterMetaData?.Element("paramfile");
            if (root == null)
                return string.Empty;

            var vehicleKey = vehicleType + ":" + nodeKey;
            foreach (var param in root.Elements()
                         .SelectMany(section => section.Elements())
                         .Where(parameters => parameters.HasAttributes)
                         .SelectMany(parameters => parameters.Elements()))
            {
                var name = param.Attribute("name")?.Value;
                if (name != vehicleKey && name != nodeKey)
                    continue;

                var attribute = param.Attribute(metaKey);
                if (attribute != null)
                    return attribute.Value;

                if (metaKey == ParameterMetaDataConstants.Values)
                {
                    return string.Join(",", param.Elements("values")
                        .Elements("value")
                        .Select(value => $"{value.Attribute("code")?.Value}:{value.Value}"));
                }

                var field = param.Elements("field")
                    .FirstOrDefault(element => element.Attribute("name")?.Value == metaKey);
                if (field != null)
                    return field.Value;
            }

            return string.Empty;
        }
    }
}
