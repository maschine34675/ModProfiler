using System;
using System.IO;
using System.Text;
using System.Threading;

namespace ModProfiler.UI
{
    internal sealed class PointerFile
    {
        private readonly object gate = new object();
        private int lastIndex;

        internal static string NameFor(string machineName)
        {
            var name = new StringBuilder("latest-");
            if (string.IsNullOrEmpty(machineName))
                name.Append("unknown");
            else
                foreach (char c in machineName)
                    name.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return name.Append(".json").ToString();
        }
        internal bool Write(string path, string content, int index)
        {
            lock (gate)
            {
                if (index <= lastIndex)
                    return false;
                string temp = path + ".tmp";
                File.WriteAllText(temp, content, new UTF8Encoding(false));
                try
                {
                    Swap(temp, path);
                }
                catch
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (IOException)
                    {
                    }
                    throw;
                }
                lastIndex = index;
                return true;
            }
        }

        internal const int ReplaceAttempts = 6;
        internal const int ReplaceRetryMs = 50;
        private static void Swap(string temp, string path)
        {
            if (File.Exists(path))
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Replace(temp, path, null);
                        return;
                    }
                    catch (IOException) when (attempt < ReplaceAttempts)
                    {
                        Thread.Sleep(ReplaceRetryMs);
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
                File.Delete(path);
            }
            File.Move(temp, path);
        }
    }
}
