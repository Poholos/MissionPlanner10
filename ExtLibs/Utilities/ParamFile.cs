using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using log4net;

namespace MissionPlanner.Utilities
{
    public class ParamFile
    {
        private static readonly ILog log =
            LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static string FileMask = "Parameter File|*.param;*.parm|All Files|*.*";

        public static Dictionary<string, double> loadParamFile(string Filename)
        {
            Dictionary<string, double> param = new Dictionary<string, double>();

            using (StreamReader sr = new StreamReader(Filename))
            {
                while (!sr.EndOfStream)
                {
                    string line = sr.ReadLine();

                    if (line.StartsWith("#"))
                        continue;

                    string[] items = line.Split(new char[] {' ', ',', '\t'}, StringSplitOptions.RemoveEmptyEntries);

                    if (items.Length < 2)
                        continue;

                    string name = items[0];
                    double value = 0;
                    try
                    {
                        value = double.Parse(items[1], CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex);
                        throw new FormatException("Invalid number on param " + name + " : " + items[1].ToString());
                    }

                    if (name == "SYSID_SW_MREV")
                        continue;
                    if (name == "WP_TOTAL")
                        continue;
                    if (name == "CMD_TOTAL")
                        continue;
                    if (name == "FENCE_TOTAL")
                        continue;
                    if (name == "SYS_NUM_RESETS")
                        continue;
                    if (name == "ARSPD_OFFSET")
                        continue;
                    if (name == "GND_ABS_PRESS")
                        continue;
                    if (name == "GND_TEMP")
                        continue;
                    if (name == "BARO1_GND_PRESS")
                        continue;
                    if (name == "BARO2_GND_PRESS")
                        continue;
                    if (name == "BARO3_GND_PRESS")
                        continue;
                    if (name == "BARO_GND_TEMP")
                        continue;
                    if (name == "CMD_INDEX")
                        continue;
                    if (name == "LOG_LASTFILE")
                        continue;
                    if (name == "FORMAT_VERSION")
                        continue;

                    param[name] = value;
                }
            }

            return param;
        }

        /// <summary>
        /// Loads a parameter file without forcing values to be numeric. DroneCAN parameters can
        /// legitimately contain strings (including spaces, commas and an empty value), so only
        /// the first separator belongs to the file structure.
        /// </summary>
        public static Dictionary<string, string> LoadTextParamFile(string filename)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var reader = new StreamReader(filename))
            {
                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine();
                    if (String.IsNullOrWhiteSpace(line) ||
                        line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        continue;

                    int separator = line.IndexOfAny(new[] { ',', '\t', ' ' });
                    if (separator <= 0)
                        continue;

                    string name = line.Substring(0, separator).Trim();
                    if (name.Length == 0)
                        continue;

                    string value = line.Substring(separator + 1);
                    if (line[separator] != ',')
                        value = value.TrimStart(' ', '\t');
                    parameters[name] = value;
                }
            }
            return parameters;
        }

        public static void SaveParamFile(string fn, Hashtable paramlist)
        {
            using (StreamWriter sw = new StreamWriter(File.Open(fn, FileMode.Create)))
            {
                var list = new SortedList(paramlist);

                foreach (var item in list.Keys)
                {
                    double value = double.Parse(paramlist[item].ToString());

                    string valueasstring = value.ToString(CultureInfo.InvariantCulture);

                    sw.WriteLine(item + "," + valueasstring);
                }
            }
        }

        /// <summary>
        /// Saves numeric and string parameters in the conventional NAME,value format. Newlines
        /// are rejected because they would create additional parameter records on import.
        /// </summary>
        public static void SaveTextParamFile(string filename,
            IEnumerable<KeyValuePair<string, string>> parameters)
        {
            var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> parameter in parameters)
            {
                string name = parameter.Key;
                string value = parameter.Value ?? String.Empty;
                if (String.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { ',', '\t', ' ', '\r', '\n' }) >= 0)
                    throw new InvalidDataException("Invalid parameter name: " + name);
                if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new InvalidDataException("Parameter " + name + " contains a newline.");
                sorted[name] = value;
            }

            using (var writer = new StreamWriter(File.Open(filename, FileMode.Create)))
            {
                foreach (KeyValuePair<string, string> parameter in sorted)
                    writer.WriteLine(parameter.Key + "," + parameter.Value);
            }
        }
    }
}
